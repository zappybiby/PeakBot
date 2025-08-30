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
        /// <summary>
        /// Spawns the bot clone next to the local player and initializes navigation + behavior.
        /// Returns the spawned GameObject (or null if spawn could not proceed).
        /// </summary>
        public static GameObject? Spawn(BotCloneSettings? s, ManualLogSource log)
        {
            if (!PrefabCache.Prefab) { log.LogError("No bot prefab cached."); return null; }

            var me = Character.localCharacter;
            if (!me) { log.LogWarning("Player not ready."); return null; }

            // Defaults if settings is null
            string botPrefabName = s?.botPrefabName ?? "Character_Bot";
            float  speedMult     = s?.speedMult     ?? 0.65f;
            float  sprintDistance= s?.sprintDistance?? 18f;

            // Pick a spot near the player; try to project onto the NavMesh
            Vector3 pos = me.Center + Vector3.right * 2f;
            if (NavMesh.SamplePosition(pos, out var navHit, 2000f, NavMesh.AllAreas))
            {
                pos = navHit.position;
                log.LogInfo($"[SpawnClone] Projected spawn onto NavMesh at {pos}");
            }
            else
            {
                log.LogWarning($"[SpawnClone] Failed to project spawn onto NavMesh at {pos}; spawning anyway.");
            }

            // Face roughly toward the player’s look
            Quaternion rot = Quaternion.LookRotation(-me.data.lookDirection_Flat);

            // Photon networked instantiate
            var clone = PhotonNetwork.InstantiateRoomObject(botPrefabName, pos, rot, 0);
            if (!clone) { log.LogError("[SpawnClone] Instantiate returned null."); return null; }

            clone.name = "[Bot Clone]";

            var ch = clone.GetComponent<Character>();
            if (ch) ch.isBot = true;

            // Ensure the agent is snapped to the NavMesh and heading toward the player
            var navAgent = clone.GetComponentInChildren<NavMeshAgent>();
            if (navAgent)
            {
                navAgent.enabled = false;
                bool warped = navAgent.Warp(pos);
                navAgent.enabled = true;
                log.LogInfo($"[SpawnClone] Agent warp returned {warped}, isOnNavMesh={navAgent.isOnNavMesh}");
                if (me) navAgent.SetDestination(me.Center);
            }

            // Speed multiplier across ragdoll movers
            foreach (var mv in clone.GetComponentsInChildren<BotMoverRagdoll>())
                mv.movementSpeed *= speedMult;

            // Follow/brain driver
            var follower = clone.AddComponent<GraphFollower>();
            follower.Init(me, sprintDistance, s);

            // Optional diagnostics
            clone.AddComponent<NavDiag>();

            // Disable boar AI if present on prefab
            var boar = clone.GetComponent<BotBoar>();
            if (boar) boar.enabled = false;

            // Cosmetics replicate from local
            var cos = clone.AddComponent<CosmeticReplicator>();
            cos.BroadcastFromLocal();

            return clone;
        }
    }
}