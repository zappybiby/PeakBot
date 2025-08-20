// /Core/NavDiag.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    internal class NavDiag : MonoBehaviour
    {
        NavMeshAgent agent = null!;
        float next;

        void Start()
        {
            agent = GetComponentInChildren<NavMeshAgent>();
            Debug.Log($"[Diag] Start – agent?{(agent != null)}");
            StartCoroutine(LogOneFrameLater());
        }

        IEnumerator LogOneFrameLater()
        {
            yield return null;
            Dump("t+1frame");
        }

        void Update()
        {
            if (Time.time >= next && Time.time < 30f)
            {
                next = Time.time + 0.5f;
                Dump($"t={Time.time:0.0}");
            }
        }

        void Dump(string tag)
        {
            int triVerts0 = NavMesh.CalculateTriangulation().vertices.Length;

            bool onMesh0 = agent && agent.isOnNavMesh;
            bool sample0 = NavMesh.SamplePosition(transform.position, out _, 3f, NavMesh.AllAreas);

            List<int> hits = new();
            int count = NavMesh.GetSettingsCount();
            for (int i = 0; i < count; i++)
            {
                var set = NavMesh.GetSettingsByIndex(i);
                var filter = new NavMeshQueryFilter
                {
                    agentTypeID = set.agentTypeID,
                    areaMask    = NavMesh.AllAreas
                };
                if (NavMesh.SamplePosition(transform.position, out _, 3f, filter))
                    hits.Add(set.agentTypeID);
            }

            int nodes =
#if UNITY_2023_1_OR_NEWER
                FindObjectsByType<NavPoint>(FindObjectsSortMode.None).Length;
#else
                FindObjectsOfType<NavPoint>(true).Length;
#endif

            Debug.Log($"[Diag] {tag} • agentType={agent?.agentTypeID} "
                    + $"triVerts0={triVerts0} onMesh0={onMesh0} sample0={sample0} "
                    + $"sampleAny=[{string.Join(", ", hits)}] nodes={nodes}");
        }
    }
}
