// /Runtime/RuntimeNavMesh.cs
// Build NavMesh at runtime and re-enable agents after bake.

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

        private void Awake()
        {
            surface = GetComponent<NavMeshSurface>() ?? gameObject.AddComponent<NavMeshSurface>();
        }

        public IEnumerator BakeNavMesh()
        {
            if (isBaking)
            {
                Debug.LogWarning("[RuntimeNavMesh] Bake already in progress.");
                yield break;
            }

            Debug.Log("[RuntimeNavMesh] Starting NavMesh bake process...");
            isBaking = true;

            // Configure what to include in the bake.
            surface.collectObjects = CollectObjects.All;
            surface.layerMask = LayerMask.GetMask("Terrain", "Map", "Default");

            // Yield once so scene setup settles this frame.
            yield return null;

            Debug.Log("[RuntimeNavMesh] Configuration set. Building NavMesh asynchronously...");

            var navData = new NavMeshData();
            NavMesh.AddNavMeshData(navData);
            surface.navMeshData = navData;

            AsyncOperation op = surface.UpdateNavMesh(surface.navMeshData);
            while (!op.isDone)
            {
                Debug.Log($"[RuntimeNavMesh] Bake progress: {op.progress:P0}");
                yield return null;
            }

            var tris = NavMesh.CalculateTriangulation();
            Debug.Log($"[RuntimeNavMesh] Bake complete! New NavMesh has {tris.vertices.Length} vertices.");

            ReenableAllAgents();
            isBaking = false;
        }

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
                if (agent.gameObject.activeInHierarchy && !agent.isOnNavMesh)
                {
                    agent.enabled = false;
                    agent.enabled = true;

                    if (agent.isOnNavMesh)
                        Debug.Log($"[RuntimeNavMesh] Successfully placed agent '{agent.name}' on NavMesh.");
                    else
                        Debug.LogWarning($"[RuntimeNavMesh] Failed to place agent '{agent.name}' on NavMesh after bake.");
                }
            }
        }
    }
}
