// /AI/GraphFollower.DetourCost.cs
using System.Collections.Generic;
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        /// <summary>
        /// Returns an approximate path length through the NavPoint graph from 'from' to 'to'.
        /// Uses a capped BFS (MAX_NAV_EVAL_NODES) so it’s O(n) and GC-free.
        /// Returns Mathf.Infinity if no valid chain found under the cap.
        /// </summary>
        float EstimateNavDistance(Vector3 from, Vector3 to)
        {
            if (allNodes.Count == 0) return Mathf.Infinity;
            NavPoint start = NearestNode(from);
            NavPoint goal  = NearestNode(to);
            if (start == null || goal == null) return Mathf.Infinity;

            var queue = new Queue<NavPoint>();
            var cost  = new Dictionary<NavPoint, float>();
            queue.Enqueue(start);
            cost[start] = 0f;

            int nodes = 0;
            while (queue.Count > 0 && nodes < MAX_NAV_EVAL_NODES)
            {
                nodes++;
                var n = queue.Dequeue();
                if (n == goal)
                    return cost[n] + Vector3.Distance(n.transform.position, to);

                foreach (var c in n.connections)
                {
                    float nd = cost[n] + Vector3.Distance(n.transform.position, c.transform.position);
                    if (!cost.ContainsKey(c) || nd < cost[c])
                    {
                        cost[c] = nd;
                        queue.Enqueue(c);
                    }
                }
            }
            return Mathf.Infinity; // capped out or unattached graph
        }

        NavPoint NearestNode(Vector3 pos)
        {
            NavPoint best = null; float bestD = float.MaxValue;
            foreach (var n in allNodes)
            {
                float d = Vector3.Distance(pos, n.transform.position);
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }
    }
}
