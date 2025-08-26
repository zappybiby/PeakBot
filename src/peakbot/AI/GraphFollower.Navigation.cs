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
        private const float CONE_WIDE_DOT  = -0.70710678f;

        // Greedy scoring weights
        private const float W_ALIGN  = 0.6f;   // alignment with vector to player
        private const float W_DIST   = 0.3f;   // relative distance improvement to player
        private const float W_SMOOTH = 0.1f;   // heading continuity vs last step
        private const float TIE_NOISE = 0.01f;

        // Optional logging controls
        private static readonly bool LOG_NAVMESH_FALLBACK = false;
        private const bool  LOG_FALLBACK_TRANSITIONS_ONLY = true;
        private const float NAVMESH_FALLBACK_LOG_PERIOD   = 5f;

        // Tiered cone policy: tight → wide → any (allow backtrack on final pass)
        private static readonly (float minDot, bool avoidBacktrack)[] CONE_TIERS = new[]
        {
            (CONE_TIGHT_DOT, true),
            (CONE_WIDE_DOT,  true),
            (-1f,            false),
        };

        // State to discourage immediate backtrack and promote smooth motion
        private NavPoint? previousNode;
        private Vector3   lastGraphStep;

        // Log throttling during fallback churn
        private NavMeshPathStatus? _lastPathStatusForLog;
        private bool   _lastHasPathForLog;
        private float  _nextFallbackLogAt;

        // ---------------------------------------------------------------------
        // NavMesh steering (primary)
        // ---------------------------------------------------------------------
        private Vector3 ComputeNavMeshDirection()
        {
            Vector3 navDir = Vector3.zero;
            var agent = bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                if (Time.time >= nextPathTime || !agent.hasPath)
                {
                    bot.targetPos_Set = player.Center;
                    nextPathTime = Time.time + pathRefresh;
                }

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

                if (LOG_NAVMESH_FALLBACK)
                {
                    bool statusChanged = (_lastPathStatusForLog != agent.pathStatus) || (_lastHasPathForLog != agent.hasPath);
                    bool timeOk = Time.time >= _nextFallbackLogAt;

                    if ((LOG_FALLBACK_TRANSITIONS_ONLY && statusChanged) ||
                        (!LOG_FALLBACK_TRANSITIONS_ONLY && (statusChanged || timeOk)))
                    {
#if UNITY_EDITOR
                        Debug.LogWarning($"[GraphFollower] NavMesh pathStatus={agent.pathStatus}, hasPath={agent.hasPath}. Falling back to graph");
#endif
                        _lastPathStatusForLog = agent.pathStatus;
                        _lastHasPathForLog = agent.hasPath;
                        _nextFallbackLogAt = Time.time + NAVMESH_FALLBACK_LOG_PERIOD;
                    }
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

            if (current && Vector3.Distance(ch.Center, current.transform.position) < NODE_REACH)
            {
                var from = current;
                var next = (from != null) ? PickNextNode(from) : null;
                if (next != null && from != null)
                {
                    previousNode = from;
                    lastGraphStep = (next.transform.position - from.transform.position).normalized;
                }
                current = next;
            }

            if (current)
                return (current.transform.position - ch.Center).normalized;

            return currentDir;
        }

        // ---------------------------------------------------------------------
        // NavPoint helpers
        // ---------------------------------------------------------------------
        private NavPoint? PickStartNode()
        {
            var origin       = ch.Center;
            var toPlayer     = (player.Center - origin);
            var toPlayerLen  = toPlayer.magnitude;
            var toPlayerNorm = toPlayerLen > 1e-4f ? (toPlayer / toPlayerLen) : Vector3.forward;

            for (int tier = 0; tier < CONE_TIERS.Length; tier++)
            {
                var (minDot, avoidBacktrack) = CONE_TIERS[tier];

                NavPoint? best = null;
                float bestD2 = float.PositiveInfinity;

                for (int i = 0; i < allNodes.Count; i++)
                {
                    var n = allNodes[i];
                    if (!n) continue;

                    if (avoidBacktrack && previousNode != null && n == previousNode)
                        continue;

                    var vec = n.transform.position - origin;
                    float d2 = vec.sqrMagnitude;
                    if (d2 < 1e-6f) continue;

                    float dot = Vector3.Dot(toPlayerNorm, vec / Mathf.Sqrt(d2));
                    if (minDot > -1f && dot < minDot) continue;

                    if (d2 < bestD2) { bestD2 = d2; best = n; }
                }

                if (best != null) return best;
            }

            return null;
        }

        private NavPoint? PickNextNode(NavPoint from)
        {
            if (from == null || from.connections == null || from.connections.Count == 0)
                return null;

            var fromPos      = from.transform.position;
            var toPlayer     = player.Center - fromPos;
            var toPlayerLen  = toPlayer.magnitude;
            var toPlayerNorm = toPlayerLen > 1e-4f ? (toPlayer / toPlayerLen) : Vector3.forward;

            var candidates = TieredNeighbors(from, toPlayerNorm);
            if (candidates.Count == 0)
                return null;

            float fromToPlayer = toPlayerLen;
            float bestScore = float.NegativeInfinity;
            NavPoint? best = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var n = candidates[i];
                if (!n) continue;

                var nPos = n.transform.position;
                var step = nPos - fromPos;
                float len = step.magnitude;
                if (len < 1e-4f) continue;

                var stepNorm = step / len;

                float alignDot   = Vector3.Dot(toPlayerNorm, stepNorm);
                float alignScore = Mathf.Max(0f, alignDot);

                float dImprovement = fromToPlayer - Vector3.Distance(nPos, player.Center);
                float distScore = (fromToPlayer > 1e-3f)
                    ? Mathf.Clamp(dImprovement / fromToPlayer, -1f, 1f)
                    : 0f;

                float smoothScore = (lastGraphStep.sqrMagnitude > 1e-4f)
                    ? Mathf.Max(0f, Vector3.Dot(lastGraphStep, stepNorm))
                    : 0.5f;

                float score = W_ALIGN * alignScore + W_DIST * distScore + W_SMOOTH * smoothScore;
                score += UnityEngine.Random.value * TIE_NOISE;

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

        private List<NavPoint> TieredNeighbors(NavPoint from, Vector3 toPlayerNorm)
        {
            for (int i = 0; i < CONE_TIERS.Length; i++)
            {
                var (minDot, avoid) = CONE_TIERS[i];
                var list = BuildCandidates(from, toPlayerNorm, minDot, avoid);
                if (list.Count > 0) return list;
            }
            return new List<NavPoint>(0);
        }

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

            if (list.Count == 0 && !avoidBacktrack && previousNode != null)
            {
                for (int i = 0; i < from.connections.Count; i++)
                    if (from.connections[i] == previousNode)
                        list.Add(previousNode);
            }

            return list;
        }
    }
}
