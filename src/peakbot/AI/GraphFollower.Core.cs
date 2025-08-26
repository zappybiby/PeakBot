// /AI/GraphFollower.Core.cs
// -----------------------------------------------------------------------------
// Core partial for GraphFollower: references, setup, NavPoint discovery, and
// the main Update loop. Other behavior lives in the Navigation/Movement/
// DetourCost/Stamina partials.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Reflection;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using Peak.BotClone.Config;

namespace Peak.BotClone
{
    /// <summary>
    /// Core partial for GraphFollower: references, setup, NavPoint discovery,
    /// and the main Update loop.
    /// </summary>
    internal partial class GraphFollower : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Runtime logging (no editor defines)
        // ---------------------------------------------------------------------
        // Flip to true if you want verbose logs in release/runtime builds.
        private const bool VERBOSE_LOGS = false;

        // ---------------------------------------------------------------------
        // References (assigned in Awake/Init)
        // ---------------------------------------------------------------------
        private Character player = null!;
        private Bot bot = null!;
        private Character ch = null!;
        private CharacterData data = null!;
        private BotCloneSettings? settings; // Optional; when null we use literals below.

        // ---------------------------------------------------------------------
        // Body-size awareness (computed from CharacterData + colliders)
        // ---------------------------------------------------------------------
        private float bodyHeight = 1.8f;   // meters
        private float bodyRadius = 0.30f;  // meters
        private float feetY, headY, chestY;

        [SerializeField, Range(0.45f, 0.70f)] private float chestHeightFrac = 0.60f;

        // Convenience accessors (used by Movement/other partials)
        private Vector3 FeetPos  => new Vector3(ch.Center.x, feetY,  ch.Center.z);
        private Vector3 HeadPos  => new Vector3(ch.Center.x, headY,  ch.Center.z);
        private Vector3 ChestPos => new Vector3(ch.Center.x, chestY, ch.Center.z);

        // ---------------------------------------------------------------------
        // NavPoint graph
        // ---------------------------------------------------------------------
        private readonly List<NavPoint> allNodes = new();
        private NavPoint? current; // Current graph node target.
        private const float NODE_REACH = 1f; // Keep literal by design.

        private float sprintRadius; // Distance threshold from Init().
        private float sprintEnterDist; // When to start sprinting (from Init).
        private float sprintExitDist;  // Lower than enter → hysteresis.
        private float nextSprintToggle; // Debounce sprint toggles.

        private int   LEDGE_CASTS    => settings?.ledgeCasts   ?? 60;
        private float LEDGE_RADIUS   => settings?.ledgeRadius  ?? 1.0f;
        private float LEDGE_HEIGHT   => settings?.ledgeHeight  ?? 1.5f;
        private float LEDGE_MAX_DIST => settings?.ledgeMaxDist ?? 4f;

        private float DESPAWN_DIST   => settings?.despawnDistance ?? 100f;

        // Threshold semantics (regular-only mindset):
        private float STAM_REST_FRAC   => settings?.stamRest   ?? 0.30f;
        private float STAM_SPRINT_FRAC => settings?.stamSprint ?? 0.25f;
        private float STAM_CLIMB_FRAC  => settings?.stamClimb  ?? 0.20f;
        private float STAM_ATTACH_ABS  => settings?.stamAttach ?? 0.40f;

        private int   MAX_NAV_EVAL_NODES => settings?.maxNavEvalNodes ?? 200;
        private float DETOUR_FACTOR       => settings?.detourFactor    ?? 1.4f;
        private float MAX_WALL_HANG       => settings?.maxWallHang     ?? 3f;

        // ---------------------------------------------------------------------
        // Runtime state
        // ---------------------------------------------------------------------
        private bool resting; // True while intentionally idling to regen.
        private float nextWallAttempt;
        private float attachFailDelay = 1f; // Doubles each fail, resets on success (1→2→4 s).

        private static readonly MethodInfo? MI_TryClimb = typeof(CharacterClimbing)
            .GetMethod("TryClimb", BindingFlags.Instance | BindingFlags.NonPublic);

        // ---------------------------------------------------------------------
        // Update bookkeeping
        // ---------------------------------------------------------------------
        private float pathRefresh = 0.5f;
        private float nextPathTime;
        private Vector3 lastPos;
        private float stuckTime;
        private int terrainMask;
        private float nextLedgeAttempt;

        internal void Init(Character target, float sprintDist, BotCloneSettings? s = null)
        {
            player = target;
            sprintRadius = sprintDist;
            settings = s;

            // Sprint hysteresis: enter at sprintRadius, exit at ~70% of it.
            sprintEnterDist = sprintDist;
            sprintExitDist  = sprintDist * 0.7f;
            nextSprintToggle = 0f;

            // Ensure the brain uses the same thresholds.
            EnsureBrain();
        }

        private void Awake()
        {
            bot = GetComponentInChildren<Bot>();
            ch  = GetComponent<Character>();
            data = ch != null ? ch.data : GetComponent<CharacterData>();

            // Initial body-size compute (so other partials can read immediately).
            RecalcBodyDims();

            // Populate graph nodes (RuntimeNavPointGenerator should have created them).
            FindAndConnectNavPoints();

            // Use a more inclusive mask for raycasts to handle various surfaces.
            terrainMask = LayerMask.GetMask("Terrain", "Map", "Default");
        }

        /// <summary>
        /// Directly populates the NavPoint list. Assumes points have already been generated.
        /// </summary>
        private void FindAndConnectNavPoints()
        {
            // Prefer the NavPoints singleton.
            var navPointsManager = NavPoints.instance;
            if (navPointsManager != null)
            {
                var nodes = navPointsManager.GetComponentsInChildren<NavPoint>(true);
                if (nodes.Length > 0)
                {
                    allNodes.AddRange(nodes);
                    if (VERBOSE_LOGS) Debug.Log($"[Clone] NavPoints initialized from manager: {allNodes.Count}");
                    return;
                }
            }

            // Fallback: scan the scene.
            if (VERBOSE_LOGS) Debug.LogWarning("[Clone] NavPoints.instance not found. Using scene search fallback.");
            NavPoint[] fallbackNodes = GetAllNavPoints();
            if (fallbackNodes.Length > 0)
            {
                allNodes.AddRange(fallbackNodes);
                if (VERBOSE_LOGS) Debug.Log($"[Clone] NavPoints initialized via fallback: {fallbackNodes.Length}");
            }
            else
            {
                Debug.LogError("[Clone] CRITICAL: No NavPoints found in the scene. Graph navigation will fail.");
            }
        }

        private static NavPoint[] GetAllNavPoints()
        {
            // Runtime-safe: avoid compile-time UNITY_20xx conditionals.
            return Object.FindObjectsOfType<NavPoint>(true);
        }

        // ---------------------------------------------------------------------
        // Per-frame update
        // ---------------------------------------------------------------------
        private void Update()
        {
            // Safety / despawn
            if (!player || !bot || !ch) return;

            // Keep body metrics in sync with posture/crouch/grounding.
            RecalcBodyDims();

            if (Vector3.Distance(ch.Center, player.Center) >= DESPAWN_DIST)
            {
                if (PhotonNetwork.InRoom)
                    PhotonNetwork.Destroy(gameObject);
                else
                    Destroy(gameObject);
                return;
            }

            // Force drop if hanging too long or low regular stamina.
            if (data.isClimbing && (RegularFrac() < STAM_CLIMB_FRAC || data.sinceClimb > HangCap()))
                ch.refs.climbing.StopClimbing();

            // 1) NavMesh steering (primary)
            Vector3 navDir = ComputeNavMeshDirection();

            // 2) Graph fallback only if NavMesh path really isn’t usable.
            if (NeedsGraphFallback())
                navDir = ComputeGraphDirection(navDir);

            // 3) Direct fallback (straight line)
            if (navDir == Vector3.zero)
                navDir = (player.Center - ch.Center).normalized;

            // Face movement direction.
            data.lookValues = DirToLook(navDir);

            // Handle movement, climbing, gaps, etc.
            HandleMovement(navDir);

            // --- Brain skeleton: decide Rest/Sprint/Follow, then set baseline input.
            EnsureBrain();
            var bb  = BuildBlackboard(navDir);
            var dec = _brain.Evaluate(bb, currentlySprinting: bot.IsSprinting);
            ApplyDecision(bb, dec, ref resting);

            // Baseline forward input (or zero if resting).
            ch.input.movementInput = resting ? Vector2.zero : Vector2.up;

            bot.LookDirection = navDir;

            // Stuck detection / auto-climb poke.
            if (Vector3.Distance(ch.Center, lastPos) < 0.2f)
                stuckTime += Time.deltaTime;
            else
                stuckTime = 0f;

            lastPos = ch.Center;

            if (stuckTime > 1.5f && !data.isClimbing && RegularFrac() >= STAM_CLIMB_FRAC && !RecentlyExhausted())
            {
                if (VERBOSE_LOGS) Debug.LogWarning($"[AI] Stuck for {stuckTime:F1}s. Nudging a climb.");
                // Poke a climb in case we're blocked by a knee-high ledge.
                MI_TryClimb?.Invoke(ch.refs.climbing, null);
                current   = null; // Reset NavPoint path.
                stuckTime = 0f;
            }

            // Regen correctness while resting.
            if (resting)
            {
                bot.IsSprinting = false;
                ch.input.movementInput = Vector2.zero;

                if (data.currentClimbHandle == null && data.isClimbing)
                    ch.refs.climbing.StopClimbing();
            }
        }

        /// <summary>
        /// Scale hang cap by low-stamina modifier so we drop sooner when nearly empty.
        /// </summary>
        private float HangCap()
        {
            // staminaMod ∈ [0.2, 1]; lower when empty.
            float mod01 = Mathf.InverseLerp(0.2f, 1f, data.staminaMod);
            return Mathf.Lerp(1.5f, MAX_WALL_HANG, mod01);
        }

        // ---------------------------------------------------------------------
        // Body metrics recompute (runtime-safe)
        // ---------------------------------------------------------------------
        private void RecalcBodyDims()
        {
            // --- Vertical from CharacterData (authoritative for posture/crouch) ---
            float headH = (data != null && data.currentHeadHeight > 0f) ? data.currentHeadHeight
                         : (data != null && data.targetHeadHeight  > 0f) ? data.targetHeadHeight
                         : 1.8f;

            float hipH  = (data != null && data.targetHipHeight > 0f) ? data.targetHipHeight : headH * 0.5f;

            bodyHeight = Mathf.Clamp(headH, 1.0f, 2.6f);

            if (data != null && data.isGrounded)
                feetY = data.groundPos.y;
            else
                feetY = ch.Center.y - hipH;

            headY = feetY + bodyHeight;

            float chestFrac = chestHeightFrac;
            if (data != null && data.isCrouching)
                chestFrac = Mathf.Clamp(chestFrac - 0.06f, 0.45f, 0.70f);

            chestY = Mathf.Lerp(feetY, headY, chestFrac);

            // --- Horizontal radius from CharacterController/CapsuleCollider ---
            float r = 0.30f;

            var cc = GetComponent<CharacterController>();
            if (cc != null)
            {
                r = Mathf.Max(cc.radius, cc.bounds.extents.x, cc.bounds.extents.z);
            }
            else
            {
                var cap = GetComponentInChildren<CapsuleCollider>();
                if (cap != null)
                    r = Mathf.Max(cap.radius, cap.bounds.extents.x, cap.bounds.extents.z);
            }

            bodyRadius = Mathf.Clamp(r, 0.10f, 0.60f);
        }
    }
}
