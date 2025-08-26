// AI/GraphFollower.Actions.cs
using Photon.Pun;
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        /// <summary>
        /// Executes Hop / GapJump / WallAttach picked by the brain.
        /// Rest/Sprint/Follow are already applied in ApplyDecision.
        /// </summary>
        private void RunAction(in BotDecision d, in Blackboard bb)
        {
            switch (d.Type)
            {
                case BotActionType.Hop:        TryHop(bb);       break;
                case BotActionType.GapJump:    TryGapJump(bb);   break;
                case BotActionType.WallAttach: TryWallAttach(bb);break;
                default: break; // Follow/Sprint/Rest handled elsewhere
            }
        }


        private void TryHop(in Blackboard bb)
        {
            // Simple guardrails: only hop from ground and when not already climbing.
            if (!bb.IsGrounded || data.isClimbing) return;

            // A tiny forward jump; using networked JumpRpc keeps behavior consistent.
            SendJumpRpc();
        }

        private void TryGapJump(in Blackboard bb)
        {
            if (!bb.IsGrounded || data.isClimbing || !bb.Gap.HasLanding) return;

            // Look toward the landing to maximize latch / trajectory alignment.
            Vector3 toLanding = (bb.Gap.Landing - ch.Center);
            toLanding.y = 0f;
            if (toLanding.sqrMagnitude > 1e-4f)
                data.lookValues = DirToLook(toLanding.normalized);

            SendJumpRpc();
        }

        private void TryWallAttach(in Blackboard bb)
        {
            if (!bb.IsGrounded || data.isClimbing || !bb.Wall.CanAttach) return;

            // Face the wall (moveDir already faces movement; nudge if needed).
            Vector3 fwd = bb.MoveDir.sqrMagnitude > 1e-4f ? bb.MoveDir : (player.Center - ch.Center);
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f)
                data.lookValues = DirToLook(fwd.normalized);

            // Jump, then immediately try to climb (matches your “jump-then-latch” pattern).
            SendJumpRpc();

            // Use reflection to call CharacterClimbing.TryClimb (already cached).
            MI_TryClimb?.Invoke(ch.refs.climbing, null);
        }

        private void SendJumpRpc()
        {
            // Jump input is network-synced via RPC in this game; avoid raw input flags.
            if (ch.refs.view != null && ch.refs.view.IsMine)
                ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);
        }
    }
}
