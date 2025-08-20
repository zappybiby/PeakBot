// /Runtime/RuntimeNavMesh.cs
using System.Collections;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace Peak.BotClone
{
    public class RuntimeNavMesh : MonoBehaviour
    {
        private NavMeshSurface surface = null!;
        private bool isBaking = false;

        void Awake()
        {
            surface = gameObject.GetComponent<NavMeshSurface>();
            if (surface == null)
            {
                surface = gameObject.AddComponent<NavMeshSurface>();
            }
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

            surface.collectObjects = CollectObjects.All;
            surface.layerMask = LayerMask.GetMask("Terrain", "Map", "Default");

            yield return null;

            Debug.Log("[RuntimeNavMesh] Configuration set. Building NavMesh asynchronously...");

            var navData = new NavMeshData();
            NavMesh.AddNavMeshData(navData);
            surface.navMeshData = navData;

            AsyncOperation operation = surface.UpdateNavMesh(surface.navMeshData);
            while (!operation.isDone)
            {
                Debug.Log($"[RuntimeNavMesh] Bake progress: {operation.progress:P0}");
                yield return null;
            }

            NavMeshTriangulation tris = NavMesh.CalculateTriangulation();
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
