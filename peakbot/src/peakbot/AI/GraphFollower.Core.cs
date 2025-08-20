// /AI/GraphFollower.Core.cs
using Photon.Pun;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    /// <summary>
    /// Core partial for GraphFollower: references, setup, NavPoint discovery, and the main Update loop.
    /// Other behavior lives in the Navigation/Movement/DetourCost/Stamina partials.
    /// </summary>
    internal partial class GraphFollower : MonoBehaviour
    {
        // — references (assigned in Awake) -----------------------------------
        Character player = null!;
        Bot       bot    = null!;
        Character ch     = null!;

        // — NavPoint graph ---------------------------------------------------
        readonly List<NavPoint> allNodes = new();
        NavPoint? current;                         // current graph node target
        const float NODE_REACH = 1f;

        float sprintRadius;                        // distance threshold from Init()

        // — ledge-gap sampling constants ------------------------------------
        const int   LEDGE_CASTS    = 60;
        const float LEDGE_RADIUS   = 1.0f;
        const float LEDGE_HEIGHT   = 1.5f;
        const float LEDGE_MAX_DIST = 4f;

        // — despawn distance -------------------------------------------------
        const float DESPAWN_DIST = 100f;

        // — stamina thresholds (kept here for now; can be moved to settings) -
        const float STAM_REST_THRESH   = 0.15f; // ≤15 % ⇒ rest
        const float STAM_SPRINT_THRESH = 0.35f; // need ≥35 % to sprint
        const float STAM_CLIMB_THRESH  = 0.20f; // require ≥20 % for simple climb / jump
        const float STAM_ATTACH_THRESH = 0.40f; // need ≥40 % to attempt wall-attach jump

        bool resting;                              // true while intentionally idling to regen

        // — wall-attach back-off --------------------------------------------
        float nextWallAttempt;
        float attachFailDelay = 1f;                // doubles each fail, resets on success (1→2→4 s)
        const float MAX_WALL_HANG = 3f;

        // — NavPoint detour evaluation --------------------------------------
        const int   MAX_NAV_EVAL_NODES = 200;      // hard cap for BFS
        const float DETOUR_FACTOR       = 1.4f;    // climb only if detour > 1.4× direct

        static readonly MethodInfo? MI_TryClimb = typeof(CharacterClimbing)
            .GetMethod("TryClimb", BindingFlags.Instance | BindingFlags.NonPublic);

        // — update bookkeeping ----------------------------------------------
        float pathRefresh = 0.5f, nextPathTime;
        Vector3 lastPos; float stuckTime;
        int terrainMask;
        float nextLedgeAttempt;

        // — public entry -----------------------------------------------------
        internal void Init(Character target, float sprintDist)
        { player = target; sprintRadius = sprintDist; }

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
