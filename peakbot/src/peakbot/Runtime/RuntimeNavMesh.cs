// /Runtime/RuntimeNavMesh.cs
// Build NavMesh at runtime, optionally bounded to an AABB, and re-enable agents after bake.

using System.Collections;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    public class RuntimeNavMesh : MonoBehaviour
    {
        private NavMeshSurface surface = null!;
        private bool isBaking;
        // Store the handle (NavMeshDataInstance) returned by AddNavMeshData,
        // so we can remove it later without type mismatches.
        private NavMeshDataInstance lastNavData; // default = invalid

        private void Awake()
        {
            surface = GetComponent<NavMeshSurface>() ?? gameObject.AddComponent<NavMeshSurface>();
        }

        private void OnDestroy()
        {
            if (lastNavData.valid)
            {
                NavMesh.RemoveNavMeshData(lastNavData);
                lastNavData = default;
            }
        }

        // Backwards-compatible full bake
        public IEnumerator BakeNavMesh() => BakeNavMesh(null);

        /// <summary>
        /// Bake the NavMesh. When 'volume' is provided, bake is limited to that world-space AABB.
        /// </summary>
        public IEnumerator BakeNavMesh(Bounds? volume)
        {
            if (isBaking)
            {
                Debug.LogWarning("[RuntimeNavMesh] Bake already in progress.");
                yield break;
            }

            isBaking = true;

            // Configure surface
            SetupSurfaceForBounds(volume);

            // Yield once so scene/setup can settle this frame.
            yield return null;

            // Replace previous data to avoid accumulating NavMeshData objects
            if (lastNavData.valid)
            {
                NavMesh.RemoveNavMeshData(lastNavData);
                lastNavData = default;
            }

            var navData = new NavMeshData();
            surface.navMeshData = navData;                   // Surface needs the data object
            lastNavData = NavMesh.AddNavMeshData(navData);   // Keep the handle so we can remove it

            var op = surface.UpdateNavMesh(surface.navMeshData);
            while (!op.isDone)
            {
                // Optional: log progress if you like (can be noisy)
                // Debug.Log($"[RuntimeNavMesh] Bake progress: {op.progress:P0}");
                yield return null;
            }

            var tris = NavMesh.CalculateTriangulation();
            Debug.Log(volume.HasValue
                ? $"[RuntimeNavMesh] Bounded bake complete. Vertices={tris.vertices.Length} center={volume.Value.center} size={volume.Value.size}"
                : $"[RuntimeNavMesh] Full bake complete. Vertices={tris.vertices.Length}");

            ReenableAllAgents();
            isBaking = false;
        }

        /// <summary>
        /// Configure the NavMeshSurface to either bake the whole scene or a volume.
        /// NOTE: assumes the surface transform has no rotation or non-uniform scale.
        /// If it does, size conversion uses a best-effort lossyScale division.
        /// </summary>
        private void SetupSurfaceForBounds(Bounds? volume)
        {
            // Include typical geometry layers
            surface.layerMask = LayerMask.GetMask("Terrain", "Map", "Default");

            if (volume.HasValue)
            {
                surface.collectObjects = CollectObjects.Volume;

                // Convert world AABB center/size into the surface's local space.
                // center: exact via InverseTransformPoint
                Vector3 localCenter = surface.transform.InverseTransformPoint(volume.Value.center);

                // size: approximate by dividing by lossyScale magnitudes (handles uniform scale correctly).
                Vector3 ls = surface.transform.lossyScale;
                Vector3 safeScale = new Vector3(
                    Mathf.Approximately(ls.x, 0f) ? 1f : Mathf.Abs(ls.x),
                    Mathf.Approximately(ls.y, 0f) ? 1f : Mathf.Abs(ls.y),
                    Mathf.Approximately(ls.z, 0f) ? 1f : Mathf.Abs(ls.z)
                );
                Vector3 localSize = new Vector3(
                    volume.Value.size.x / safeScale.x,
                    volume.Value.size.y / safeScale.y,
                    volume.Value.size.z / safeScale.z
                );

                surface.center = localCenter;
                surface.size   = localSize;

                Debug.Log($"[RuntimeNavMesh] Using bounded bake: localCenter={localCenter} localSize={localSize}");
            }
            else
            {
                surface.collectObjects = CollectObjects.All;
            }
        }

        /// <summary>
        /// Re-enable any NavMeshAgents that might not be on the mesh after (re)bake.
        /// </summary>
        public void ReenableAllAgents()
        {
            Debug.Log("[RuntimeNavMesh] Re-enabling all NavMeshAgents.");
            NavMeshAgent[] allAgents =
#if UNITY_2023_1_OR_NEWER
                Object.FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None);
#else
                Object.FindObjectsOfType<NavMeshAgent>();
#endif
            foreach (var agent in allAgents)
            {
                if (!agent || !agent.gameObject.activeInHierarchy) continue;

                // If an agent lost its mesh link, briefly toggle it to snap back.
                if (!agent.isOnNavMesh)
                {
                    agent.enabled = false;
                    agent.enabled = true;

                    if (!agent.isOnNavMesh)
                        Debug.LogWarning($"[RuntimeNavMesh] Agent '{agent.name}' is still not on NavMesh after toggle.");
                }
            }
        }
    }
}
