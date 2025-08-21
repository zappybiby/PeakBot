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
        // References (assigned in Awake/Init)
        // ---------------------------------------------------------------------
        private Character player = null!;
        private Bot bot = null!;
        private Character ch = null!;
        private BotCloneSettings? settings; // Optional; when null we use literals below.

        // ---------------------------------------------------------------------
        // NavPoint graph
        // ---------------------------------------------------------------------
        private readonly List<NavPoint> allNodes = new();
        private NavPoint? current; // Current graph node target.
        private const float NODE_REACH = 1f; // Keep literal by design.

        private float sprintRadius; // Distance threshold from Init().
        private float sprintEnterDist; // When to start sprinting (from Init).
        private float sprintExitDist; // Lower than enter → hysteresis.
        private float nextSprintToggle; // Debounce sprint toggles.

        private int LEDGE_CASTS => settings?.ledgeCasts ?? 60;
        private float LEDGE_RADIUS => settings?.ledgeRadius ?? 1.0f;
        private float LEDGE_HEIGHT => settings?.ledgeHeight ?? 1.5f;
        private float LEDGE_MAX_DIST => settings?.ledgeMaxDist ?? 4f;

        private float DESPAWN_DIST => settings?.despawnDistance ?? 100f;

        // Threshold semantics (regular-only mindset):
        // - FRACTION fields are fractions of the regular bar (0..1, affected by afflictions).
        // - ABS fields are absolute regular units (0..RegularMax()).
        private float STAM_REST_FRAC => settings?.stamRest ?? 0.30f;  // Begin resting at/under this regular fraction.
        private float STAM_SPRINT_FRAC => settings?.stamSprint ?? 0.25f; // Need this regular fraction to sprint.
        private float STAM_CLIMB_FRAC => settings?.stamClimb ?? 0.20f; // Need this regular fraction for simple climbs/jumps.
        private float STAM_ATTACH_ABS => settings?.stamAttach ?? 0.40f; // Absolute regular units required to attempt wall-attach jump.

        private int MAX_NAV_EVAL_NODES => settings?.maxNavEvalNodes ?? 200;
        private float DETOUR_FACTOR => settings?.detourFactor ?? 1.4f;
        private float MAX_WALL_HANG => settings?.maxWallHang ?? 3f;

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
            sprintExitDist = sprintDist * 0.7f;
            nextSprintToggle = 0f;
        }

        private void Awake()
        {
            bot = GetComponentInChildren<Bot>();
            ch = GetComponent<Character>();

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
                    Debug.Log($"[Clone] NavPoints successfully initialized from manager: {allNodes.Count}");
                    return;
                }
            }

            // Fallback: scan the scene.
            Debug.LogWarning("[Clone] NavPoints.instance was not found. Using scene search fallback.");
            NavPoint[] fallbackNodes = GetAllNavPoints();
            if (fallbackNodes.Length > 0)
            {
                allNodes.AddRange(fallbackNodes);
                Debug.Log($"[Clone] NavPoints initialized via fallback: {fallbackNodes.Length}");
            }
            else
            {
                Debug.LogError("[Clone] CRITICAL: No NavPoints found in the scene. Graph navigation will fail.");
            }
        }

        private static NavPoint[] GetAllNavPoints()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindObjectsByType<NavPoint>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<NavPoint>(true);
#endif
        }

        // ---------------------------------------------------------------------
        // Per-frame update
        // ---------------------------------------------------------------------
        private void Update()
        {
            // Safety / despawn
            if (!player || !bot || !ch) return;
            if (Vector3.Distance(ch.Center, player.Center) >= DESPAWN_DIST)
            {
                if (PhotonNetwork.InRoom)
                    PhotonNetwork.Destroy(gameObject);
                else
                    Destroy(gameObject);

                return;
            }

            // Force drop if hanging too long or low regular stamina.
            if (ch.data.isClimbing && (RegularFrac() < STAM_CLIMB_FRAC || ch.data.sinceClimb > HangCap()))
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
            ch.data.lookValues = DirToLook(navDir);

            // Handle movement, climbing, gaps, etc.
            HandleMovement(navDir);

            // Baseline forward input (or zero if resting).
            ch.input.movementInput = resting ? Vector2.zero : Vector2.up;

            // Stamina State Machine (regular-only).
            if (resting)
            {
                // Leave rest with small hysteresis once we’re healthy again.
                if (RegularFrac() >= (STAM_REST_FRAC + 0.10f))
                    resting = false;
            }
            else if (RegularFrac() <= STAM_REST_FRAC)
            {
                resting = true;
            }

            // Sprint gating: only when not resting, not climbing, and regular bar healthy enough.
            bool sprinting = bot.IsSprinting;
            float distToPlayer = Vector3.Distance(ch.Center, player.Center);
            bool canToggle = Time.time >= nextSprintToggle;
            bool lowRegular = RegularFrac() < STAM_SPRINT_FRAC;

            if (resting || lowRegular || ch.data.isClimbing)
            {
                if (sprinting && canToggle)
                {
                    bot.IsSprinting = false;
                    nextSprintToggle = Time.time + 0.25f;
                }
            }
            else
            {
                bool wantSprint = navDir.sqrMagnitude > 1e-4f && distToPlayer >= sprintEnterDist && !lowRegular;
                bool stopSprint = distToPlayer <= sprintExitDist;

                if (!sprinting && wantSprint && canToggle)
                {
                    bot.IsSprinting = true;
                    nextSprintToggle = Time.time + 0.25f;
                }
                else if (sprinting && stopSprint && canToggle)
                {
                    bot.IsSprinting = false;
                    nextSprintToggle = Time.time + 0.25f;
                }
            }

            bot.LookDirection = navDir;

            // Stuck detection / auto-climb poke.
            if (Vector3.Distance(ch.Center, lastPos) < 0.2f)
                stuckTime += Time.deltaTime;
            else
                stuckTime = 0f;

            lastPos = ch.Center;

            if (stuckTime > 1.5f && !ch.data.isClimbing && RegularFrac() >= STAM_CLIMB_FRAC && !RecentlyExhausted())
            {
                Debug.LogWarning($"[AI] I've been stuck here for {stuckTime:F1}s. I am one with this fucking wall.");
                // Poke a climb in case we're blocked by a knee-high ledge.
                MI_TryClimb?.Invoke(ch.refs.climbing, null);
                current = null; // Reset NavPoint path.
                stuckTime = 0f;
            }

            // Regen correctness while resting.
            if (resting)
            {
                // Stop consuming.
                bot.IsSprinting = false;
                ch.input.movementInput = Vector2.zero;

                if (ch.data.currentClimbHandle == null && ch.data.isClimbing)
                    ch.refs.climbing.StopClimbing();
                // Else: when on a handle, keep inputs quiet to avoid canceling hang (regen allowed anywhere).
            }
        }

        /// <summary>
        /// Scale hang cap by low-stamina modifier so we drop sooner when nearly empty.
        /// </summary>
        private float HangCap()
        {
            // staminaMod ∈ [0.2, 1]; lower when empty.
            float mod01 = Mathf.InverseLerp(0.2f, 1f, ch.data.staminaMod);
            return Mathf.Lerp(1.5f, MAX_WALL_HANG, mod01);
        }
    }
}
