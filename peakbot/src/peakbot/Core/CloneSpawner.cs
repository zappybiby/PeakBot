// /Core/CloneSpawner.cs
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using Peak.BotClone.Config;
using BepInEx.Logging;

namespace Peak.BotClone
{
    internal static class CloneSpawner
    {
        public static void Spawn(BotCloneSettings? s, ManualLogSource log)
        {
            if (!PrefabCache.Prefab) { log.LogError("No bot prefab cached."); return; }
            var me = Character.localCharacter; if (!me) { log.LogWarning("Player not ready."); return; }
            // Use default values if settings is null
            string botPrefabName = s?.botPrefabName ?? "Character_Bot";
            float speedMult = s?.speedMult ?? 0.65f;
            float sprintDistance = s?.sprintDistance ?? 18f;

            Vector3 pos = me.Center + Vector3.right * 2f;
            if (NavMesh.SamplePosition(pos, out var navHit, 2000f, NavMesh.AllAreas))
            { pos = navHit.position; log.LogInfo($"[SpawnClone] Projected spawn onto NavMesh at {pos}"); }
            else log.LogWarning($"[SpawnClone] Failed to project spawn onto NavMesh at {pos}; spawning anyways.");

            Quaternion rot = Quaternion.LookRotation(-me.data.lookDirection_Flat);
            var clone = PhotonNetwork.InstantiateRoomObject(botPrefabName, pos, rot, 0);
            clone.name = "[Bot Clone]";
            clone.GetComponent<Character>().isBot = true;

            var navAgent = clone.GetComponentInChildren<NavMeshAgent>();
            if (navAgent)
            {
                navAgent.enabled = false;
                bool warped = navAgent.Warp(pos);
                navAgent.enabled = true;
                log.LogInfo($"[SpawnClone] Agent warp returned {warped}, isOnNavMesh={navAgent.isOnNavMesh}");
                navAgent.SetDestination(me.Center);
            }

            foreach (var mv in clone.GetComponentsInChildren<BotMoverRagdoll>())
                mv.movementSpeed *= speedMult;

            var follower = clone.AddComponent<GraphFollower>();
            follower.Init(me, sprintDistance);
            clone.AddComponent<NavDiag>();

            var boar = clone.GetComponent<BotBoar>();
            if (boar) boar.enabled = false;

            // Cosmetics: attach a lightweight component that raises/handles the skin event.
            var cos = clone.AddComponent<CosmeticReplicator>();
            cos.BroadcastFromLocal();
        }
    }
}
