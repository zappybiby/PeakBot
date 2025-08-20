// /AI/GraphFollower.Navigation.cs
using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        Vector3 ComputeNavMeshDirection()
        {
            Vector3 navDir = Vector3.zero;
            var agent = bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                // request a new path if our refresh timer hit _or_ if the agent has no path yet
                if (Time.time >= nextPathTime || !agent.hasPath)
                {
                    bot.targetPos_Set = player.Center;
                    nextPathTime = Time.time + pathRefresh;
                }
                // once it has a path, steer toward the next waypoint
                if (!agent.pathPending && agent.hasPath)
                    navDir = (agent.steeringTarget - ch.Center).normalized;
            }
            return navDir;
        }

        bool NeedsGraphFallback()
        {
            var agent = bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                if (agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathComplete)
                    return false;
                else
                    Debug.LogWarning($"[GraphFollower] NavMesh pathStatus={agent.pathStatus}, hasPath={agent.hasPath}. Falling back to graph");
            }
            return true;
        }

        Vector3 ComputeGraphDirection(Vector3 currentDir)
        {
            if (allNodes.Count == 0) return currentDir;

            if (current == null) current = PickStartNode();
            if (current && Vector3.Distance(ch.Center, current.transform.position) < NODE_REACH)
                current = PickNextNode(current);
            if (current)
                return (current.transform.position - ch.Center).normalized;

            return currentDir;
        }

        // NavPoint helpers
        NavPoint PickStartNode()
        {
            NavPoint best = null; float bestDist = float.MaxValue;
            Vector3 toPlayer = player.Center - ch.Center;
            foreach (var n in allNodes)
            {
                float d = Vector3.Distance(ch.Center, n.transform.position);
                if (d < bestDist && VectorBoxAngle(toPlayer, n.transform.position - ch.Center) <= 90f)
                {
                    bestDist = d; best = n;
                }
            }
            return best;
        }

        NavPoint PickNextNode(NavPoint from)
        {
            var opts = new List<NavPoint>();
            Vector3 toPlayer = player.Center - from.transform.position;
            foreach (var n in from.connections)
                if (VectorBoxAngle(toPlayer, n.transform.position - from.transform.position) < 90f)
                    opts.Add(n);
            return opts.Count == 0 ? null : opts[UnityEngine.Random.Range(0, opts.Count)];
        }

        static float VectorBoxAngle(Vector3 a, Vector3 b)
        {
            return Vector3.Angle(a, b);
        }
    }
}
