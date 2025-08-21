// /Runtime/RuntimeNavPointGenerator.cs
// Runtime generation of a lightweight NavPoint graph from scene geometry/NavMesh.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    public static class RuntimeNavPointGenerator
    {
        private const float NODE_SPACING = 15f;
        private const int POINTS_TO_PROCESS_PER_FRAME = 50;
        private const float MAX_CONNECTION_SEARCH_RADIUS = NODE_SPACING * 4f;
        private static readonly Collider[] overlapResults = new Collider[256];

        private const int NAVPOINT_LAYER = 30;
        private static readonly int navPointLayerMask = 1 << NAVPOINT_LAYER;

        public static IEnumerator GenerateAsync()
        {
            // Let the scene finish spawning before sampling.
            yield return null; yield return null; yield return null;

            Debug.Log("[RuntimeNavPointGenerator] Starting generation...");

            // Clear any previous graph.
            if (NavPoints.instance != null)
            {
                Object.Destroy(NavPoints.instance.gameObject);
                yield return null;
            }

            // Hidden NavPoint prefab (trigger sphere for overlap queries).
            var prefab = new GameObject("Runtime_NavPoint_Prefab") { layer = NAVPOINT_LAYER };
            prefab.AddComponent<NavPoint>();
            var sphere = prefab.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 0.5f;
            prefab.SetActive(false);

            // Collect scene colliders on geometry layers we'll raycast against.
            int geometryMask = LayerMask.GetMask("Terrain", "Map", "Default");
            var cols = Object.FindObjectsOfType<Collider>()
                              .Where(c => (geometryMask & (1 << c.gameObject.layer)) != 0)
                              .ToList();
            if (cols.Count == 0)
            {
                Debug.LogError("[RuntimeNavPointGenerator] No geometry colliders found. Aborting.");
                Object.Destroy(prefab);
                yield break;
            }

            // Sampling bounds + vertical ray distance for NavMesh.SamplePosition.
            var bounds = cols[0].bounds;
            for (int i = 1; i < cols.Count; i++) bounds.Encapsulate(cols[i].bounds);
            Vector3 min = bounds.min, max = bounds.max;
            float sampleDistance = (max.y - min.y) + 10f; // generous headroom
            Debug.Log($"[RuntimeNavPointGenerator] Sampling bounds min={min}, max={max}, sampleDistance={sampleDistance}");

            // Root container for all NavPoints.
            var managerGO = new GameObject("NavPoints");
            managerGO.AddComponent<NavPoints>();
            var allPoints = new List<NavPoint>();

            // Grid-sample the NavMesh.
            Debug.Log("[RuntimeNavPointGenerator] Sampling NavMesh for point placement...");
            for (float x = min.x; x <= max.x; x += NODE_SPACING)
            {
                for (float z = min.z; z <= max.z; z += NODE_SPACING)
                {
                    Vector3 origin = new(x, max.y + 5f, z);
                    if (NavMesh.SamplePosition(origin, out NavMeshHit hit, sampleDistance, NavMesh.AllAreas))
                    {
                        var go = Object.Instantiate(prefab);
                        go.transform.position = hit.position;
                        go.name = "Runtime NavPoint";
                        go.transform.SetParent(managerGO.transform, true);
                        allPoints.Add(go.GetComponent<NavPoint>());
                    }
                }
                yield return null;
            }

            // Fallback: NavMesh triangulation if grid produced nothing.
            if (allPoints.Count == 0)
            {
                Debug.LogWarning("[RuntimeNavPointGenerator] No grid hits; falling back to triangulation...");
                var tris = NavMesh.CalculateTriangulation();
                var seen = new HashSet<Vector2Int>();
                foreach (var v in tris.vertices)
                {
                    var cell = new Vector2Int(Mathf.FloorToInt(v.x / NODE_SPACING), Mathf.FloorToInt(v.z / NODE_SPACING));
                    if (!seen.Add(cell)) continue;
                    var go = Object.Instantiate(prefab);
                    go.transform.position = v;
                    go.name = "Runtime NavPoint";
                    go.transform.SetParent(managerGO.transform, true);
                    allPoints.Add(go.GetComponent<NavPoint>());
                }
                Debug.Log($"[RuntimeNavPointGenerator] Fallback created {allPoints.Count} points.");
            }

            if (allPoints.Count == 0)
            {
                Debug.LogError("[RuntimeNavPointGenerator] Still no points generated. Aborting.");
                Object.Destroy(prefab);
                yield break;
            }

            // Connect neighbors: LOS filter, then adaptive radius based on nearest neighbor.
            Debug.Log($"[RuntimeNavPointGenerator] Connecting {allPoints.Count} points...");
            var obstacleMask = HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap);
            var reachableNeighbors = new List<NavPoint>(64);
            var verticalOffset = Vector3.up * 0.1f;

            for (int i = 0; i < allPoints.Count; i++)
            {
                var current = allPoints[i];
                current.connections = new List<NavPoint>();
                reachableNeighbors.Clear();

                int hitCount = Physics.OverlapSphereNonAlloc(
                    current.transform.position,
                    MAX_CONNECTION_SEARCH_RADIUS,
                    overlapResults,
                    navPointLayerMask
                );

                if (hitCount > 0)
                {
                    // Pass 1: line-of-sight.
                    for (int k = 0; k < hitCount; k++)
                    {
                        var n = overlapResults[k].GetComponent<NavPoint>();
                        if (n == null || n == current) continue;
                        if (!Physics.Linecast(current.transform.position + verticalOffset,
                                              n.transform.position + verticalOffset,
                                              obstacleMask))
                        {
                            reachableNeighbors.Add(n);
                        }
                    }

                    // Pass 2: adaptive radius from closest neighbor.
                    if (reachableNeighbors.Count > 0)
                    {
                        float bestSq = float.PositiveInfinity;
                        foreach (var n in reachableNeighbors)
                        {
                            float dsq = (current.transform.position - n.transform.position).sqrMagnitude;
                            if (dsq < bestSq) bestSq = dsq;
                        }
                        float connSq = bestSq * (1.5f * 1.5f);
                        foreach (var n in reachableNeighbors)
                        {
                            if ((current.transform.position - n.transform.position).sqrMagnitude <= connSq)
                                current.connections.Add(n);
                        }
                    }
                }

                if (i > 0 && i % POINTS_TO_PROCESS_PER_FRAME == 0)
                    yield return null;
            }

            // Mirror connections for an undirected graph.
            Debug.Log("[RuntimeNavPointGenerator] Mirroring connections for two-way graph...");
            foreach (var pt in allPoints)
            {
                foreach (var c in pt.connections)
                {
                    if (!c.connections.Contains(pt))
                        c.connections.Add(pt);
                }
            }

            // Cleanup.
            Object.Destroy(prefab);
            Debug.Log($"[RuntimeNavPointGenerator] Generation complete. {allPoints.Count} nodes under /NavPoints.");
        }
    }
}
