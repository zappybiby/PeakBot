// AI/Perception/Perception.cs
using System;
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    internal sealed class Perception
    {
        private readonly GraphFollower _gf;
        private readonly Character _ch;
        private readonly CharacterData _data;
        private readonly Character _player;
        private readonly Bot _bot;

        private float _nextDetourAt;
        private float _cachedDetourRatio = float.PositiveInfinity;
        private Vector3 _lastDetourFrom, _lastDetourTo;
        private readonly NavMeshPath _tmpPath = new();

        private const float DETOUR_RECALC_PERIOD = 0.35f;

        private const float STEP_PROBE_DIST = 0.9f;
        private const float STEP_MAX_HOP = 0.60f;
        private const float STEP_MIN_HOP = 0.12f;
        private const float STEP_LATERAL = 0.30f;

        private const float WALL_MAX_RANGE = 1.3f;
        private const float WALL_STEEP_Y = 0.30f;

        private readonly float _ledgeRadius;
        private readonly float _ledgeHeight;
        private readonly float _ledgeMaxDist;
        private readonly int _terrainMask;

        public Perception(GraphFollower gf, Character ch, CharacterData data, Character player, Bot bot,
                          float ledgeRadius, float ledgeHeight, float ledgeMaxDist, int terrainMask)
        {
            _gf = gf;
            _ch = ch;
            _data = data;
            _player = player;
            _bot = bot;

            _ledgeRadius = ledgeRadius;
            _ledgeHeight = ledgeHeight;
            _ledgeMaxDist = ledgeMaxDist;
            _terrainMask = terrainMask;
        }

        public Blackboard BuildBlackboard(Vector3 moveDir)
        {
            Vector3 self = _ch.Center;
            Vector3 plyr = _player.Center;
            float dist = Vector3.Distance(self, plyr);

            float stamAbs = _gf.Regular();
            float stamFrac = _gf.RegularFrac();

            bool hasPath = false, complete = false;
            float detourRatio = float.PositiveInfinity;
            var agent = _bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                hasPath = agent.hasPath;
                complete = agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathComplete;
            }
            detourRatio = EstimateDetourRatioCached(self, plyr);

            var step = ProbeStep(self, moveDir);
            var wall = ProbeWall(self, moveDir);
            var gap = ProbeGap(self, moveDir);

            return new Blackboard
            {
                SelfPos = self,
                PlayerPos = plyr,
                DistToPlayer = dist,

                IsGrounded = _data.isGrounded,
                IsClimbing = _data.isClimbing,

                RecentlyExhausted = _gf.RecentlyExhausted(),

                StaminaRegular = stamAbs,
                StaminaFrac = stamFrac,

                MoveDir = moveDir,

                HasNavMeshPath = hasPath,
                NavPathComplete = complete,
                DetourRatio = detourRatio,

                Step = step,
                Wall = wall,
                Gap = gap
            };
        }

        // -------- Sensors --------

        private StepInfo ProbeStep(Vector3 origin, Vector3 moveDir)
        {
            if (moveDir.sqrMagnitude < 1e-6f) return default;

            // Derive body references from CharacterData (runtime-safe).
            float feetY = _data.isGrounded ? _data.groundPos.y : _ch.Center.y - Mathf.Max(_data.targetHeadHeight * 0.5f, 0.7f);
            float headH = _data.currentHeadHeight > 0 ? _data.currentHeadHeight
                       : (_data.targetHeadHeight > 0 ? _data.targetHeadHeight : 1.8f);
            float chestY = Mathf.Lerp(feetY, feetY + headH, _data.isCrouching ? 0.54f : 0.60f);

            Vector3 dir = new Vector3(moveDir.x, 0f, moveDir.z).normalized;
            Vector3 chest = new(origin.x, chestY, origin.z);

            // Center, left, right forward rays to detect obstacle at chest level.
            float bestHeight = 0f;
            int lateralAgree = 0;

            bool centerBlock = RayToObstacle(chest, dir, out float centerH);
            bool leftBlock = RayToObstacle(chest + Vector3.Cross(Vector3.up, dir) * -STEP_LATERAL, dir, out float leftH);
            bool rightBlock = RayToObstacle(chest + Vector3.Cross(Vector3.up, dir) * STEP_LATERAL, dir, out float rightH);

            if (centerBlock) bestHeight = Mathf.Max(bestHeight, centerH);
            if (leftBlock) bestHeight = Mathf.Max(bestHeight, leftH);
            if (rightBlock) bestHeight = Mathf.Max(bestHeight, rightH);

            if (leftBlock && rightBlock)
                lateralAgree = 2;
            else if (leftBlock && !rightBlock)
                lateralAgree = -1;
            else if (!leftBlock && rightBlock)
                lateralAgree = 1;
            else
                lateralAgree = 0;

            bool canHop = centerBlock || leftBlock || rightBlock;
            canHop = canHop && bestHeight >= STEP_MIN_HOP && bestHeight <= STEP_MAX_HOP;

            return new StepInfo { CanHop = canHop, Height = bestHeight, LateralAgree = lateralAgree };
        }

        private bool RayToObstacle(Vector3 start, Vector3 dir, out float height)
        {
            // Shoot a ray from chest, forward, down a bit to catch sloped lips.
            if (Physics.Raycast(start + Vector3.down * 0.05f, dir, out var hit, STEP_PROBE_DIST, _terrainMask, QueryTriggerInteraction.Ignore))
            {
                // Height above feet at contact point
                float feetY = _data.isGrounded ? _data.groundPos.y : _ch.Center.y;
                height = Mathf.Max(0f, hit.point.y - feetY);
                return true;
            }

            height = 0f;
            return false;
        }

        // Perception/Perception.cs (inside the Perception class)

        private WallAttachInfo ProbeWall(Vector3 origin, Vector3 moveDir)
        {
            if (moveDir.sqrMagnitude < 1e-6f) return default;

            float headH = _data.currentHeadHeight > 0 ? _data.currentHeadHeight
                    : (_data.targetHeadHeight > 0 ? _data.targetHeadHeight : 1.8f);
            float feetY = _data.isGrounded ? _data.groundPos.y : origin.y - Mathf.Max(_data.targetHeadHeight * 0.5f, 0.7f);
            float headY = feetY + headH;
            float chestY = Mathf.Lerp(feetY, headY, _data.isCrouching ? 0.54f : 0.60f);

            Vector3 dir = new Vector3(moveDir.x, 0f, moveDir.z).normalized;

            // Try ray from head, then chest.
            if (Physics.Raycast(new Vector3(origin.x, headY,   origin.z), dir, out var hit, WALL_MAX_RANGE, _terrainMask, QueryTriggerInteraction.Ignore) ||
                Physics.Raycast(new Vector3(origin.x, chestY,  origin.z), dir, out hit,    WALL_MAX_RANGE, _terrainMask, QueryTriggerInteraction.Ignore))
            {
                // Match the game's acceptance: slight underhangs and moderate overhangs are ok
                float climbAngle = Vector3.Angle(hit.normal, Vector3.up); // 0=flat up, 90=vertical
                bool acceptable  = AcceptableGrabAngle(climbAngle);

                // Must also be close enough in the plane
                float planar = Vector3.ProjectOnPlane(hit.point - new Vector3(origin.x, hit.point.y, origin.z), Vector3.up).magnitude;
                bool withinReach = planar <= 0.8f; // tighter than cast range

                bool canAttach = acceptable && withinReach;

                return new WallAttachInfo
                {
                    IsSteep   = climbAngle >= 70f,   // informational
                    CanAttach = canAttach,
                    PlanarDist= planar,
                    Normal    = hit.normal,
                    AngleDeg  = climbAngle
                };
            }

            return default;
        }


        private GapInfo ProbeGap(Vector3 origin, Vector3 moveDir)
        {
            if (moveDir.sqrMagnitude < 1e-6f) return default;

            // Sample along the move direction and look for a downward drop followed by ground.
            Vector3 dir = new Vector3(moveDir.x, 0f, moveDir.z).normalized;
            float step = Mathf.Max(0.3f, _ledgeRadius * 0.8f);
            int steps = Mathf.CeilToInt(_ledgeMaxDist / step);

            for (int i = 1; i <= steps; i++)
            {
                Vector3 sample = origin + dir * (i * step) + Vector3.up * 0.4f;

                // If there is ground right here (within a small height), it's not a gap.
                if (Physics.Raycast(sample, Vector3.down, out var ground, _ledgeHeight + 1.5f, _terrainMask, QueryTriggerInteraction.Ignore))
                {
                    // Prefer a landing slightly lower or level (avoid detecting current plateau).
                    float dh = ground.point.y - origin.y;
                    if (dh <= 0.4f) // landing not much higher than us
                    {
                        float horiz = Vector3.ProjectOnPlane(ground.point - origin, Vector3.up).magnitude;
                        if (horiz >= 0.8f) // meaningful jump
                            return new GapInfo { HasLanding = true, Landing = ground.point, Distance = horiz };
                    }
                }
            }

            return default;
        }

        private float EstimateDetourRatioCached(Vector3 from, Vector3 to)
        {
            float straight = Vector3.Distance(from, to);
            if (straight < 0.5f) return 1f;

            if (Time.time < _nextDetourAt && (from - _lastDetourFrom).sqrMagnitude < 0.25f && (to - _lastDetourTo).sqrMagnitude < 0.25f)
                return _cachedDetourRatio;

            _lastDetourFrom = from;
            _lastDetourTo = to;
            _nextDetourAt = Time.time + DETOUR_RECALC_PERIOD;

            float pathLen = float.PositiveInfinity;

            // Try NavMesh.CalculatePath as a cheap approximation of “detour vs direct”.
            if (NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _tmpPath) && _tmpPath.status != NavMeshPathStatus.PathInvalid)
            {
                var corners = _tmpPath.corners;
                if (corners != null && corners.Length >= 2)
                {
                    float len = 0f;
                    for (int i = 1; i < corners.Length; i++)
                        len += Vector3.Distance(corners[i - 1], corners[i]);
                    pathLen = len;
                }
            }

            // Fallback to NavPoint-graph A* (internal to GraphFollower) if NavMesh path is missing/invalid.
            if (pathLen == float.PositiveInfinity)
            {
                float approx = _gf.EstimateNavDistance(from, to);
                if (approx < float.PositiveInfinity)
                    pathLen = approx;
            }

            _cachedDetourRatio = (pathLen < float.PositiveInfinity && straight > 0.001f)
                ? Mathf.Max(1f, pathLen / straight)
                : float.PositiveInfinity;

            return _cachedDetourRatio;
        }
        // Perception/Perception.cs (inside the Perception class)

        private static bool AcceptableGrabAngle(float climbAngleDeg)
        {
            // Mirrors CharacterClimbing.AcceptableGrabAngle:
            // Overhang (angle > 90): allow up to ~80° past vertical → angle <= 170
            // Underhang (angle < 90): allow down to ~50° from vertical → angle >= 50
            float f = climbAngleDeg - 90f;
            if (f > 0f)  return Mathf.Abs(f) <= 80f;  // up to 170°
            else         return Mathf.Abs(f) <= 40f;  // down to 50°
        }

    }
}
