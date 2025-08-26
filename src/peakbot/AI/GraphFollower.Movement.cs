// /AI/GraphFollower.Movement.cs
// Movement/interaction logic for GraphFollower: wall-attach jump, small-step hops/climbs,
// ledge-gap detection, and input translation.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        private float nextHopOkAt;
        private int consecutiveHops;

        // Adaptive hop gating
        private readonly float[] stepNoise = new float[60];
        private int stepNoiseIdx = 0, stepNoiseCount = 0;
        private Vector3 lastHopPos, lastHopFwd;
        private bool hasHopHistory = false;
        private Vector3 prevMoveDir = Vector3.zero;

        private void HandleMovement(Vector3 moveDir)
        {
            if (resting) return;

            bool staminaOK = RegularFrac() >= STAM_CLIMB_FRAC;

            // Wall-attach jump probe
            if (staminaOK && !RecentlyExhausted() && data.isGrounded && !data.isClimbing &&
                Time.time >= nextWallAttempt && Regular() >= STAM_ATTACH_ABS)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(moveDir, Vector3.up).normalized;
                const float wallProbeDist   = 2.2f;
                const float maxAttachHeight = 12.0f;
                float minHeightDiff = Mathf.Max(0.5f, bodyHeight * 0.33f);

                if (fwd.sqrMagnitude > 1e-4f &&
                    Physics.Raycast(HeadPos, fwd, out RaycastHit hit, wallProbeDist, terrainMask))
                {
                    float heightDiff = hit.point.y - ch.Center.y;
                    bool tallEnough = heightDiff > minHeightDiff && heightDiff < maxAttachHeight;
                    bool steepWall  = Vector3.Dot(hit.normal, fwd) < -0.5f;

                    if (tallEnough && steepWall)
                    {
                        float planar    = Vector3.ProjectOnPlane(hit.point - ch.Center, Vector3.up).magnitude;
                        float attachTax = 0.15f * planar;
                        float burst     = 0.20f;
                        float headroom  = 0.10f;

                        if (Regular() < (attachTax + burst + headroom))
                        {
                            nextWallAttempt = Time.time + 1f;
                        }
                        else
                        {
                            float around = EstimateNavDistance(ch.Center, player.Center);
                            float direct = Vector3.Distance(ch.Center, player.Center);
                            bool worthClimb = !float.IsInfinity(around) && (around > direct * DETOUR_FACTOR);

                            if (VERBOSE_LOGS) Debug.Log($"[WallAttach] around={around:F2}, direct={direct:F2}, worthClimb={worthClimb}");

                            if (worthClimb)
                            {
                                StartCoroutine(JumpAndAttach());
                                nextWallAttempt = Time.time + attachFailDelay;

                                if (VERBOSE_LOGS) Debug.Log($"[WallAttach] next attempt @ {nextWallAttempt:F2}");

                                attachFailDelay = Mathf.Min(attachFailDelay * 2f, 4f);
                                consecutiveHops = 0;
                            }
                            else
                            {
                                nextWallAttempt = Time.time + 1f;
                            }
                        }
                    }
                }
            }

            // Small/medium step handling
            if (staminaOK && !data.isClimbing && !RecentlyExhausted())
            {
                Vector3 fwd   = Vector3.ProjectOnPlane(moveDir, Vector3.up).normalized;
                Vector3 chest = ChestPos;

                if (VERBOSE_LOGS) Debug.Log($"[StepClimb] checking; climbing={data.isClimbing}");

                GetStepParams(out float STEP_MIN, out float STEP_MAX, out float STEP_RAY, out float STEP_RAD);

                if (fwd.sqrMagnitude > 1e-4f &&
                    Physics.SphereCast(chest, STEP_RAD, fwd, out RaycastHit hit, STEP_RAY, terrainMask))
                {
                    float step = hit.point.y - feetY;

                    if (VERBOSE_LOGS) Debug.Log($"[StepClimb] hit {hit.collider?.name ?? "col"} at {hit.point}, step={step:F2}, band=[{STEP_MIN:F2},{STEP_MAX:F2}]");

                    if (step >= STEP_MIN && step <= STEP_MAX)
                    {
                        if (VERBOSE_LOGS) Debug.Log("[HandleMovement] TryClimb (medium step)");
                        MI_TryClimb?.Invoke(ch.refs.climbing, null);
                        ch.input.jumpWasPressed = false;
                        consecutiveHops = 0;
                    }
                    else if (step < STEP_MIN)
                    {
                        bool okToHop = ShouldHopAfterGates(moveDir, fwd, STEP_RAD, STEP_RAY, step);

                        if (okToHop)
                        {
                            if (VERBOSE_LOGS) Debug.Log("[HandleMovement] Small hop (gated)");
                            ch.input.jumpWasPressed = true;

                            lastHopPos  = ch.Center;
                            lastHopFwd  = fwd;
                            hasHopHistory = true;

                            nextHopOkAt = Time.time + 0.25f;
                            consecutiveHops = 0;
                        }
                        else
                        {
                            ch.input.jumpWasPressed = false;
                            ObserveStepNoise(step);
                        }
                    }
                    else // step > STEP_MAX
                    {
                        if (VERBOSE_LOGS) Debug.Log("[HandleMovement] Big step → wall attach preference");
                        ch.input.jumpWasPressed = false;
                        nextWallAttempt = Mathf.Min(nextWallAttempt, Time.time);
                    }
                }
                else
                {
                    ch.input.jumpWasPressed = false;
                    ObserveStepNoise(0f);
                }
            }

            // Ledge/gap detection
            if (staminaOK && !RecentlyExhausted() && Time.time >= nextLedgeAttempt && data.isGrounded && !data.isClimbing)
            {
                Vector3 probe = ChestPos + moveDir.normalized * Mathf.Max(0.8f, bodyRadius * 2f);
                bool groundAhead = Physics.Raycast(
                    probe,
                    Vector3.down,
                    Mathf.Max(1.2f, bodyHeight * 1.0f),
                    terrainMask
                );

                if (!groundAhead && FindLedgeLanding(moveDir, out Vector3 landing))
                {
                    float around = EstimateNavDistance(ch.Center, player.Center);
                    float direct = Vector3.Distance(ch.Center, player.Center);

                    if (!float.IsInfinity(around) && (around > direct * DETOUR_FACTOR))
                    {
                        if (VERBOSE_LOGS) Debug.Log("[HandleMovement] Gap jump RPC");

                        data.lookValues = DirToLook((landing - ch.Center).normalized);
                        if (ch.refs.view.IsMine)
                            ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);

                        nextLedgeAttempt = Time.time + 0.5f;
                        consecutiveHops = 0;
                    }
                }
            }

            // Input conversion
            Vector3 local = ch.transform.InverseTransformDirection(moveDir);
            ch.input.movementInput = new Vector2(local.x * 0.75f, local.z).normalized;

            prevMoveDir = moveDir;
        }

        private void GetStepParams(out float min, out float max, out float ray, out float rad)
        {
            min = bodyHeight * 0.20f;
            max = bodyHeight * 0.80f;
            ray = Mathf.Max(1.0f, bodyHeight * 0.90f);
            rad = Mathf.Clamp(bodyRadius * 0.60f, 0.15f, 0.35f);
        }

        private bool FindLedgeLanding(Vector3 forwardDir, out Vector3 best)
        {
            best = Vector3.zero;
            var hits = new List<RaycastHit>();
            Vector3 origin = ch.Center;

            for (int i = 0; i < LEDGE_CASTS; i++)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * LEDGE_RADIUS;
                Vector3 start = origin + forwardDir.normalized * LEDGE_RADIUS + new Vector3(rnd.x, LEDGE_HEIGHT, rnd.y);
                if (Physics.Raycast(start, Vector3.down, out var h, LEDGE_HEIGHT * 2f, terrainMask))
                    hits.Add(h);
            }

            var flat    = hits.Where(h => Vector3.Angle(h.normal, Vector3.up) < 50f);
            var near    = flat.Where(h => Vector3.Distance(h.point, origin) < LEDGE_MAX_DIST);
            var forward = near.Where(h => Vector3.Dot(forwardDir, (h.point - origin).normalized) > 0.5f && h.point.y > origin.y);
            if (!forward.Any()) return false;

            best = forward
                .OrderByDescending(h => Vector3.Dot(forwardDir, h.point - origin))
                .First()
                .point;

            return true;
        }

        private static Vector2 DirToLook(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-4f) return Vector2.zero;
            dir.Normalize();
            float yaw   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(dir.y) * Mathf.Rad2Deg;
            return new Vector2(yaw, pitch);
        }

        private IEnumerator JumpAndAttach()
        {
            attachFailDelay = 1f;

            if (ch.refs.view.IsMine)
                ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);

            yield return new WaitForSeconds(0.15f);

            MI_TryClimb?.Invoke(ch.refs.climbing, null);
            consecutiveHops = 0;
        }

        // Adaptive noise & gating helpers

        private void ObserveStepNoise(float h)
        {
            stepNoise[stepNoiseIdx++ % stepNoise.Length] = Mathf.Max(0f, h);
            stepNoiseCount = Mathf.Min(stepNoiseCount + 1, stepNoise.Length);
        }

        private float StepNoiseP95()
        {
            int n = stepNoiseCount;
            if (n == 0) return 0f;
            var tmp = new float[n];
            System.Array.Copy(stepNoise, tmp, n);
            System.Array.Sort(tmp);
            int k = Mathf.Clamp(Mathf.CeilToInt(0.95f * (n - 1)), 0, n - 1);
            return tmp[k];
        }

        private bool ShouldHopAfterGates(Vector3 moveDir, Vector3 fwd, float STEP_RAD, float STEP_RAY, float stepHeight)
        {
            // Gap veto
            Vector3 probe = ChestPos + fwd * Mathf.Max(0.8f, bodyRadius * 2f);
            bool groundAhead = Physics.Raycast(
                probe,
                Vector3.down,
                Mathf.Max(1.2f, bodyHeight * 1.0f),
                terrainMask
            );
            if (!groundAhead)
            {
                if (VERBOSE_LOGS) Debug.Log("[HopGate] gap ahead");
                return false;
            }

            // Lateral vote at knee height
            float kneeY = feetY + bodyHeight * 0.25f;
            Vector3 knee = new Vector3(ChestPos.x, kneeY, ChestPos.z);
            Vector3 lat  = Vector3.Cross(Vector3.up, fwd).normalized * (bodyRadius * 0.8f);

            int agree = 0, walls = 0;
            for (int i = -1; i <= 1; i++)
            {
                Vector3 origin = knee + lat * i;
                if (Physics.SphereCast(origin, STEP_RAD, fwd, out RaycastHit h2, STEP_RAY, terrainMask))
                {
                    float up = Vector3.Dot(h2.normal, Vector3.up);
                    if (up >= 0.3f) agree++;
                    if (up <= 0.2f) walls++;
                }
            }
            if (agree < 2 || walls > 0)
            {
                if (VERBOSE_LOGS) Debug.Log($"[HopGate] lateral vote (agree={agree}, walls={walls})");
                return false;
            }

            // Heading stability
            Vector3 prevFwd = Vector3.ProjectOnPlane(prevMoveDir, Vector3.up).normalized;
            bool headingStable = (prevFwd.sqrMagnitude < 1e-6f) || (Vector3.Dot(prevFwd, fwd) >= 0.95f);
            if (!headingStable)
            {
                if (VERBOSE_LOGS) Debug.Log("[HopGate] heading unstable");
                return false;
            }

            // Noise floor
            float noise = StepNoiseP95();
            if (stepHeight < noise)
            {
                if (VERBOSE_LOGS) Debug.Log($"[HopGate] stepHeight({stepHeight:F2}) < noiseP95({noise:F2})");
                return false;
            }

            // Progress + time refractory
            bool timeOk = (Time.time >= nextHopOkAt);
            bool progressOk = true;
            if (hasHopHistory)
            {
                float planarAdvance = Vector3.ProjectOnPlane(ch.Center - lastHopPos, Vector3.up).magnitude;
                progressOk = planarAdvance >= (2f * bodyRadius);
                if (!progressOk && VERBOSE_LOGS) Debug.Log($"[HopGate] progress {planarAdvance:F2} < {(2f * bodyRadius):F2}");
            }

            if (VERBOSE_LOGS)
            {
                Debug.Log($"[HopGate] ground={groundAhead} agree={agree} walls={walls} step={stepHeight:F2} noiseP95={noise:F2} headStable={headingStable} timeOk={timeOk} progressOk={progressOk}");
            }

            return timeOk && progressOk;
        }
    }
}