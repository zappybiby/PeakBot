// /AI/GraphFollower.DetourCost.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        /// Returns an approximate path length through the NavPoint graph 
        /// A* binary heap, cap at MAX_NAV_EVAL_NODES 
        float EstimateNavDistance(Vector3 from, Vector3 to)
        {
            if (allNodes.Count == 0) return Mathf.Infinity;

            NavPoint start = NearestNode(from);
            NavPoint goal  = NearestNode(to);
            if (start == null || goal == null) return Mathf.Infinity;

            // A* open set with (f, g, node)
            var open = new MinHeap<OpenItem>((a, b) =>
            {
                int c = a.f.CompareTo(b.f);
                return c != 0 ? c : a.g.CompareTo(b.g);
            });

            var gCost = new Dictionary<NavPoint, float>(64);
            gCost[start] = 0f;

            open.Push(new OpenItem
            {
                node = start,
                g    = 0f,
                f    = Heuristic(start, goal)
            });

            int expansions = 0;
            while (open.Count > 0 && expansions < MAX_NAV_EVAL_NODES)
            {
                var cur = open.Pop();
                var n   = cur.node;
                expansions++;

                if (n == goal)
                    return gCost[n] + Vector3.Distance(n.transform.position, to);

                foreach (var c in n.connections)
                {
                    float tentativeG = gCost[n] + Vector3.Distance(n.transform.position, c.transform.position);
                    if (!gCost.TryGetValue(c, out float oldG) || tentativeG < oldG)
                    {
                        gCost[c] = tentativeG;
                        float f = tentativeG + Heuristic(c, goal);
                        open.Push(new OpenItem { node = c, g = tentativeG, f = f });
                    }
                }
            }

            return Mathf.Infinity;

            static float Heuristic(NavPoint a, NavPoint b)
                => Vector3.Distance(a.transform.position, b.transform.position);
        }

        NavPoint NearestNode(Vector3 pos)
        {
            NavPoint best = null;
            float bestD = float.MaxValue;

            foreach (var n in allNodes)
            {
                float d = Vector3.Distance(pos, n.transform.position);
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }

        struct OpenItem
        {
            public NavPoint node;
            public float g;
            public float f;
        }

        sealed class MinHeap<T>
        {
            readonly List<T> data = new List<T>(64);
            readonly Comparison<T> cmp;

            public MinHeap(Comparison<T> cmp) { this.cmp = cmp; }

            public int Count => data.Count;

            public void Push(T item)
            {
                data.Add(item);
                int i = data.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (cmp(data[i], data[p]) >= 0) break;
                    var tmp = data[i]; data[i] = data[p]; data[p] = tmp;
                    i = p;
                }
            }

            public T Pop()
            {
                var root = data[0];
                int last = data.Count - 1;
                data[0] = data[last];
                data.RemoveAt(last);
                int i = 0;
                for (;;)
                {
                    int l = (i << 1) + 1;
                    if (l >= data.Count) break;
                    int r = l + 1;
                    int m = (r < data.Count && cmp(data[r], data[l]) < 0) ? r : l;
                    if (cmp(data[m], data[i]) >= 0) break;
                    var tmp = data[i]; data[i] = data[m]; data[m] = tmp;
                    i = m;
                }
                return root;
            }
        }
    }
}
