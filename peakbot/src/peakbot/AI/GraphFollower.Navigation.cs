// /AI/GraphFollower.Navigation.cs
// -----------------------------------------------------------------------------
// NavMesh-first navigation with graph fallback. Includes heuristics for neighbor
// selection, cone thresholds, greedy scoring, and candidate building utilities.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        // ---------------------------------------------------------------------
        // Heuristics for graph neighbor selection
        // ---------------------------------------------------------------------
        // cos(90°)  =  0.0   → forward cone
        // cos(135°) = -0.707 → wider cone allowing lateral/backward
        private const float CONE_TIGHT_DOT = 0f;
        private const float CONE_WIDE_DOT = -0.70710678f;

        // Greedy scoring weights
        private const float W_ALIGN = 0.6f;   // alignment with vector to player
        private const float W_DIST = 0.3f;    // relative distance improvement to player
        private const float W_SMOOTH = 0.1f;  // heading continuity vs last step
        private const float TIE_NOISE = 0.01f; // small randomness to break ties

        // State to discourage immediate backtrack and promote smooth motion
        private NavPoint? previousNode;
        private Vector3 lastGraphStep;

        // Log throttling during fallback churn
        private NavMeshPathStatus? _lastPathStatusForLog;
        private bool _lastHasPathForLog;
        private float _nextFallbackLogAt;

        // ---------------------------------------------------------------------
        // NavMesh steering (primary)
        // ---------------------------------------------------------------------
        private Vector3 ComputeNavMeshDirection()
        {
            Vector3 navDir = Vector3.zero;
            var agent = bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                // Request a new path when refresh elapses or if no path exists.
                if (Time.time >= nextPathTime || !agent.hasPath)
                {
                    bot.targetPos_Set = player.Center;
                    nextPathTime = Time.time + pathRefresh;
                }

                // Once a path exists, steer toward the next waypoint.
                if (!agent.pathPending && agent.hasPath)
                    navDir = (agent.steeringTarget - ch.Center).normalized;
            }
            return navDir;
        }

        private bool NeedsGraphFallback()
        {
            var agent = bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                if (agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathComplete)
                    return false;

                // Throttled log when status changes or ~1s has passed.
                if ((Time.time >= _nextFallbackLogAt) ||
                    (_lastPathStatusForLog != agent.pathStatus) ||
                    (_lastHasPathForLog != agent.hasPath))
                {
                    Debug.LogWarning($"[GraphFollower] NavMesh pathStatus={agent.pathStatus}, hasPath={agent.hasPath}. Falling back to graph");
                    _lastPathStatusForLog = agent.pathStatus;
                    _lastHasPathForLog = agent.hasPath;
                    _nextFallbackLogAt = Time.time + 1f;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // Graph steering (fallback when NavMesh path isn't complete)
        // ---------------------------------------------------------------------
        private Vector3 ComputeGraphDirection(Vector3 currentDir)
        {
            if (allNodes.Count == 0) return currentDir;

            if (current == null)
                current = PickStartNode();

            // Step to the next node once the current is reached.
            if (current && Vector3.Distance(ch.Center, current.transform.position) < NODE_REACH)
            {
                var from = current; // UnityEngine.Object null semantics are intentional
                var next = (from != null) ? PickNextNode(from) : null;
                if (next != null && from != null)
                {
                    previousNode = from;
                    lastGraphStep = (next.transform.position - from.transform.position).normalized;
                }
                current = next; // 'current' is NavPoint?
            }

            if (current)
                return (current.transform.position - ch.Center).normalized;

            // No graph target: preserve prior input (caller may use straight-line fallback).
            return currentDir;
        }

        // ---------------------------------------------------------------------
        // NavPoint helpers
        // ---------------------------------------------------------------------
        private NavPoint? PickStartNode()
        {
            // Nearest node with tiered preference toward the player:
            // 1) tight cone (≥ 0), 2) wide cone (≥ -0.707), 3) any.
            var origin = ch.Center;
            var toPlayer = (player.Center - origin);
            var toPlayerLen = toPlayer.magnitude;
            var toPlayerNorm = toPlayerLen > 1e-4f ? (toPlayer / toPlayerLen) : Vector3.forward;

            NavPoint? best = null;
            float bestDist = float.MaxValue;

            // Scan using a minimum dot threshold (optional)
            void Scan(float minDot, bool useDot)
            {
                for (int i = 0; i < allNodes.Count; i++)
                {
                    var n = allNodes[i];
                    var vec = n.transform.position - origin;
                    var d = vec.magnitude;
                    if (d < bestDist)
                    {
                        if (useDot)
                        {
                            float dot = Vector3.Dot(toPlayerNorm, vec.normalized);
                            if (dot < minDot) continue;
                        }
                        best = n; bestDist = d;
                    }
                }
            }

            // Tight cone → wide cone → any
            Scan(CONE_TIGHT_DOT, true);
            if (best != null) return best;

            Scan(CONE_WIDE_DOT, true);
            if (best != null) return best;

            Scan(0f, false);
            return best; // may be null if no nodes exist
        }

        private NavPoint? PickNextNode(NavPoint from)
        {
            if (from == null || from.connections == null || from.connections.Count == 0)
                return null;

            var fromPos = from.transform.position;
            var toPlayer = player.Center - fromPos;
            var toPlayerLen = toPlayer.magnitude;
            var toPlayerNorm = toPlayerLen > 1e-4f ? (toPlayer / toPlayerLen) : Vector3.forward;

            // Build candidates with tiered cones:
            // 1) tight (forward-ish), avoiding immediate backtrack
            // 2) wide
            // 3) any (allow backtrack as last resort)
            var candidates = BuildCandidates(from, toPlayerNorm, CONE_TIGHT_DOT, avoidBacktrack: true);
            if (candidates.Count == 0)
                candidates = BuildCandidates(from, toPlayerNorm, CONE_WIDE_DOT, avoidBacktrack: true);
            if (candidates.Count == 0)
                candidates = BuildCandidates(from, toPlayerNorm, minDot: -1f, avoidBacktrack: false);

            if (candidates.Count == 0)
                return null;

            // Greedy score: alignment + distance improvement + smoothness
            float fromToPlayer = toPlayerLen;
            float bestScore = float.NegativeInfinity;
            NavPoint? best = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var n = candidates[i];
                var nPos = n.transform.position;
                var step = nPos - fromPos;
                float len = step.magnitude;
                if (len < 1e-4f) continue;

                var stepNorm = step / len;

                // 1) Alignment [0..1]
                float alignDot = Vector3.Dot(toPlayerNorm, stepNorm);
                float alignScore = Mathf.Max(0f, alignDot);

                // 2) Relative distance improvement [-1..1]
                float dImprovement = fromToPlayer - Vector3.Distance(nPos, player.Center);
                float distScore = (fromToPlayer > 1e-3f)
                    ? Mathf.Clamp(dImprovement / fromToPlayer, -1f, 1f)
                    : 0f;

                // 3) Smoothness [0..1]
                float smoothScore = (lastGraphStep.sqrMagnitude > 1e-4f)
                    ? Mathf.Max(0f, Vector3.Dot(lastGraphStep, stepNorm))
                    : 0.5f;

                float score = W_ALIGN * alignScore + W_DIST * distScore + W_SMOOTH * smoothScore;
                score += UnityEngine.Random.value * TIE_NOISE;

                // Small penalty if backtracking is among the final-stage options
                if (previousNode != null && n == previousNode)
                    score -= 0.25f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = n;
                }
            }

            return best;
        }

        /// <summary>
        /// Build neighbor candidates with a minimum dot-to-player threshold.
        /// Optionally avoids immediate backtrack; if 'avoidBacktrack' is false and
        /// the result is empty with minDot = -1, allow previousNode as a last resort.
        /// </summary>
        private List<NavPoint> BuildCandidates(NavPoint from, Vector3 toPlayerNorm, float minDot, bool avoidBacktrack)
        {
            var list = new List<NavPoint>(from.connections.Count);
            for (int i = 0; i < from.connections.Count; i++)
            {
                var n = from.connections[i];
                if (n == null) continue;

                if (avoidBacktrack && previousNode != null && n == previousNode)
                    continue;

                var step = n.transform.position - from.transform.position;
                float len = step.magnitude;
                if (len < 1e-4f) continue;

                var stepNorm = step / len;
                float dot = Vector3.Dot(toPlayerNorm, stepNorm);
                if (dot >= minDot)
                    list.Add(n);
            }

            // If avoiding backtrack produced no options and this is the final fallback,
            // include previousNode when connected.
            if (list.Count == 0 && !avoidBacktrack && previousNode != null)
            {
                for (int i = 0; i < from.connections.Count; i++)
                    if (from.connections[i] == previousNode)
                        list.Add(previousNode);
            }

            return list;
        }

        // Retained for compatibility; current logic uses dot products directly.
        private static float VectorBoxAngle(Vector3 a, Vector3 b)
        {
            return Vector3.Angle(a, b);
        }
    }
}
