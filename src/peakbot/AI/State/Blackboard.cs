// AI/State/Blackboard.cs
using UnityEngine;
using UnityEngine.AI;

namespace Peak.BotClone
{
    /// <summary>Read-only snapshot of everything the brain needs to decide.</summary>
    internal struct Blackboard
    {
        // Core pose
        public Vector3 SelfPos;
        public Vector3 PlayerPos;
        public float   DistToPlayer;

        // Movement / posture
        public bool IsGrounded;
        public bool IsClimbing;

        // Stamina (regular-only mindset)
        public float StaminaRegular; // absolute
        public float StaminaFrac;    // 0..1

        // NavMesh status (for future decisions; harmless in the skeleton)
        public bool HasNavMeshPath;
        public bool NavPathComplete;

        // Optional: steering dir already computed by your navigation
        public Vector3 MoveDir;
    }

    internal static class BlackboardUtil
    {
        public static Blackboard Build(
            Vector3 moveDir,
            Character ch,
            Character player,
            CharacterData data,
            Bot bot,
            float staminaAbs,
            float staminaFrac)
        {
            var bb = new Blackboard
            {
                SelfPos      = ch.Center,
                PlayerPos    = player.Center,
                DistToPlayer = Vector3.Distance(ch.Center, player.Center),
                IsGrounded   = data != null && data.isGrounded,
                IsClimbing   = data != null && data.isClimbing,
                StaminaRegular = staminaAbs,
                StaminaFrac    = staminaFrac,
                MoveDir      = moveDir
            };

            var agent = bot.navigator?.agent;
            if (agent != null && agent.isOnNavMesh)
            {
                bb.HasNavMeshPath   = agent.hasPath;
                bb.NavPathComplete  = agent.hasPath && agent.pathStatus == NavMeshPathStatus.PathComplete;
            }

            return bb;
        }
    }
}
