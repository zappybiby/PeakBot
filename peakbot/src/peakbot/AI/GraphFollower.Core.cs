// /AI/GraphFollower.Core.cs
using Photon.Pun;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using Peak.BotClone.Config;

namespace Peak.BotClone
{
    /// <summary>
    /// Core partial for GraphFollower: references, setup, NavPoint discovery, and the main Update loop.
    /// Other behavior lives in the Navigation/Movement/DetourCost/Stamina partials.
    /// </summary>
    internal partial class GraphFollower : MonoBehaviour
    {
        // — references (assigned in Awake/Init) ------------------------------
        Character player = null!;
        Bot       bot    = null!;
        Character ch     = null!;
        BotCloneSettings? settings; // optional; when null we use literals below

        // — NavPoint graph ---------------------------------------------------
        readonly List<NavPoint> allNodes = new();
        NavPoint? current;                         // current graph node target
        const float NODE_REACH = 1f;               // keep literal by design

        float sprintRadius;                        // distance threshold from Init()
        float sprintEnterDist;          // when to start sprinting (from Init)
        float sprintExitDist;           // lower than enter → hysteresis
        float nextSprintToggle;         // debounce sprint toggles

        int   LEDGE_CASTS    => settings?.ledgeCasts    ?? 60;
        float LEDGE_RADIUS   => settings?.ledgeRadius   ?? 1.0f;
        float LEDGE_HEIGHT   => settings?.ledgeHeight   ?? 1.5f;
        float LEDGE_MAX_DIST => settings?.ledgeMaxDist  ?? 4f;

        float DESPAWN_DIST        => settings?.despawnDistance ?? 100f;

        // Threshold semantics (regular-only mindset):
        // - FRACTION fields are fractions of the regular bar (0..1, affected by afflictions).
        // - ABS fields are absolute regular units (0..RegularMax()).
        float STAM_REST_FRAC   => settings?.stamRest   ?? 0.30f; // begin resting at/under this regular fraction
        float STAM_SPRINT_FRAC => settings?.stamSprint ?? 0.25f; // need this regular fraction to sprint
        float STAM_CLIMB_FRAC  => settings?.stamClimb  ?? 0.20f; // need this regular fraction for simple climbs/jumps
        float STAM_ATTACH_ABS  => settings?.stamAttach ?? 0.40f; // absolute regular units required to attempt wall-attach jump

        int   MAX_NAV_EVAL_NODES  => settings?.maxNavEvalNodes ?? 200;
        float DETOUR_FACTOR       => settings?.detourFactor    ?? 1.4f;
        float MAX_WALL_HANG       => settings?.maxWallHang     ?? 3f;

        // — runtime state ----------------------------------------------------
        bool  resting;                              // true while intentionally idling to regen
        float nextWallAttempt;
        float attachFailDelay = 1f;                // doubles each fail, resets on success (1→2→4 s)

        static readonly MethodInfo? MI_TryClimb = typeof(CharacterClimbing)
            .GetMethod("TryClimb", BindingFlags.Instance | BindingFlags.NonPublic);

        // — update bookkeeping ----------------------------------------------
        float pathRefresh = 0.5f, nextPathTime;
        Vector3 lastPos; float stuckTime;
        int terrainMask;
        float nextLedgeAttempt;

        internal void Init(Character target, float sprintDist, BotCloneSettings? s = null)
        {
            player = target;
            sprintRadius = sprintDist;
            settings = s;

            // sprint hysteresis: enter at sprintRadius, exit at ~70% of it
            sprintEnterDist = sprintDist;
            sprintExitDist  = sprintDist * 0.7f;
            nextSprintToggle = 0f;
        }

        void Awake()
        {
            bot = GetComponentInChildren<Bot>();
            ch  = GetComponent<Character>();

            // Populate graph nodes (RuntimeNavPointGenerator should have created them)
            FindAndConnectNavPoints();

            // Use a more inclusive mask for raycasts to handle various surfaces.
            terrainMask = LayerMask.GetMask("Terrain", "Map", "Default");
        }

        /// <summary>
        /// Directly populates the NavPoint list. Assumes points have already been generated.
        /// </summary>
        void FindAndConnectNavPoints()
        {
            // Prefer the NavPoints singleton
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

            // Fallback: scan the scene
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

        static NavPoint[] GetAllNavPoints()
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindObjectsByType<NavPoint>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<NavPoint>(true);
#endif
        }

        // — per-frame update -------------------------------------------------
        void Update()
        {
            // ── safety / despawn ───────────────────────────────────────────
            if (!player || !bot || !ch) return;
            if (Vector3.Distance(ch.Center, player.Center) >= DESPAWN_DIST)
            {
                if (PhotonNetwork.InRoom) PhotonNetwork.Destroy(gameObject);
                else Destroy(gameObject);
                return;
            }

            // ── force drop if hanging too long or low regular stamina ──────
            if (ch.data.isClimbing && (RegularFrac() < STAM_CLIMB_FRAC || ch.data.sinceClimb > HangCap()))
                ch.refs.climbing.StopClimbing();

            // ── 1. NavMesh steering (primary) ──────────────────────────────
            Vector3 navDir = ComputeNavMeshDirection();

            // ── 2. Graph fallback only if NavMesh path really isn’t usable ─
            if (NeedsGraphFallback())
                navDir = ComputeGraphDirection(navDir);

            // ── 3. direct fallback (straight line) ─────────────────────────
            if (navDir == Vector3.zero)
                navDir = (player.Center - ch.Center).normalized;

            // ── face movement direction ───────────────────────────────────
            ch.data.lookValues = DirToLook(navDir);

            // ── handle movement, climbing, gaps, etc. ─────────────────────
            HandleMovement(navDir);

            // ── baseline forward input (or zero if resting) ───────────────
            ch.input.movementInput = resting ? Vector2.zero : Vector2.up;

            // ── Stamina State Machine (regular-only) ───────────────────────
            if (resting)
            {
                // Leave rest with small hysteresis once we’re healthy again
                if (RegularFrac() >= (STAM_REST_FRAC + 0.10f))
                    resting = false;
            }
            else if (RegularFrac() <= STAM_REST_FRAC)
            {
                resting = true;
            }

            // Sprint gating: only when not resting, not climbing, and regular bar healthy enough
            bool sprinting   = bot.IsSprinting;
            float distToPlayer = Vector3.Distance(ch.Center, player.Center);
            bool canToggle   = Time.time >= nextSprintToggle;
            bool lowRegular  = RegularFrac() < STAM_SPRINT_FRAC;

            if (resting || lowRegular || ch.data.isClimbing)
            {
                if (sprinting && canToggle) { bot.IsSprinting = false; nextSprintToggle = Time.time + 0.25f; }
            }
            else
            {
                bool wantSprint = navDir.sqrMagnitude > 1e-4f && distToPlayer >= sprintEnterDist && !lowRegular;
                bool stopSprint = distToPlayer <= sprintExitDist;

                if (!sprinting && wantSprint && canToggle)      { bot.IsSprinting = true;  nextSprintToggle = Time.time + 0.25f; }
                else if (sprinting && stopSprint && canToggle)  { bot.IsSprinting = false; nextSprintToggle = Time.time + 0.25f; }
            }

            bot.LookDirection = navDir;

            // ── stuck detection / auto-climb poke ─────────────────────────
            if (Vector3.Distance(ch.Center, lastPos) < 0.2f)
                stuckTime += Time.deltaTime;
            else
                stuckTime = 0f;
            lastPos = ch.Center;

            if (stuckTime > 1.5f && !ch.data.isClimbing && RegularFrac() >= STAM_CLIMB_FRAC && !RecentlyExhausted())
            {
                Debug.LogWarning($"[AI] I've been stuck here for {stuckTime:F1}s. I am one with this fucking wall.");
                // poke a climb in case we're blocked by a knee-high ledge
                MI_TryClimb?.Invoke(ch.refs.climbing, null);
                current = null; // reset NavPoint path
                stuckTime = 0f;
            }

            // ── Regen correctness while resting ────────────────────────────
            if (resting)
            {
                // stop consuming
                bot.IsSprinting = false;
                ch.input.movementInput = Vector2.zero;

                if (ch.data.currentClimbHandle == null && ch.data.isClimbing)
                    ch.refs.climbing.StopClimbing();
                // else: when on a handle, keep inputs quiet to avoid canceling hang (regen allowed anywhere).
            }
        }

        // Scale hang cap by low-stamina modifier so we drop sooner when nearly empty
        float HangCap()
        {
            // staminaMod ∈ [0.2, 1]; lower when empty.
            float mod01 = Mathf.InverseLerp(0.2f, 1f, ch.data.staminaMod);
            return Mathf.Lerp(1.5f, MAX_WALL_HANG, mod01);
        }
    }
}
