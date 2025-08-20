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

        // — values driven by settings (with literal fallbacks) ---------------
        // Use properties so there’s no duplicated state: if settings exist, use them; else use literals.
        int   LEDGE_CASTS    => settings?.ledgeCasts    ?? 60;
        float LEDGE_RADIUS   => settings?.ledgeRadius   ?? 1.0f;
        float LEDGE_HEIGHT   => settings?.ledgeHeight   ?? 1.5f;
        float LEDGE_MAX_DIST => settings?.ledgeMaxDist  ?? 4f;

        float DESPAWN_DIST        => settings?.despawnDistance ?? 100f;

        float STAM_REST_THRESH    => settings?.stamRest   ?? 0.15f;
        float STAM_SPRINT_THRESH  => settings?.stamSprint ?? 0.35f;
        float STAM_CLIMB_THRESH   => settings?.stamClimb  ?? 0.20f;
        float STAM_ATTACH_THRESH  => settings?.stamAttach ?? 0.40f;

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

        // — public entry -----------------------------------------------------
        internal void Init(Character target, float sprintDist, BotCloneSettings? s = null)
        {
            player = target;
            sprintRadius = sprintDist;
            settings = s; // if null, properties fall back to literals
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

            // ── force drop if hanging too long or out of stamina ───────────
            if (ch.data.isClimbing && (Low(STAM_REST_THRESH) || ch.data.sinceClimb > MAX_WALL_HANG))
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

            // ── Stamina State Machine ──────────────────────────────────────
            if (resting)
            {
                if (!Low(STAM_SPRINT_THRESH))
                    resting = false;
            }
            else if (Low(STAM_REST_THRESH))
            {
                resting = true;
            }
            else bot.IsSprinting = false;

            bot.LookDirection = navDir;

            // ── stuck detection / auto-climb poke ─────────────────────────
            if (Vector3.Distance(ch.Center, lastPos) < 0.2f)
                stuckTime += Time.deltaTime;
            else
                stuckTime = 0f;
            lastPos = ch.Center;

            if (stuckTime > 1.5f && !ch.data.isClimbing && !Low(STAM_CLIMB_THRESH))
            {
                Debug.LogWarning($"[AI] I've been stuck here for {stuckTime:F1}s. I am one with this fucking wall.");
                // poke a climb in case we're blocked by a knee-high ledge
                MI_TryClimb?.Invoke(ch.refs.climbing, null);
                current = null; // reset NavPoint path
                stuckTime = 0f;
            }
        }
    }
}
