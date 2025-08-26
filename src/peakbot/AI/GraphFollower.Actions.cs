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
                case BotActionType.Hop:        TryHop(bb);        break;
                case BotActionType.GapJump:    TryGapJump(bb);    break;
                case BotActionType.WallAttach: TryWallAttach(bb); break;
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

        /// <summary>
        /// Mirror CharacterMovement.TryToJump gating so we don't force illegal jumps via RPC.
        /// </summary>
        private bool CanIssueJumpNow()
        {
            // Same conditions the game enforces before calling JumpRpc:
            if (ch.data.jumpsRemaining <= 0) return false; // no jumps left
            if (!ch.CheckJump())            return false;   // not allowed (climbing/rope/vine/handle/etc.)
            if (ch.data.sinceGrounded > 0.20f) return false; // been airborne too long
            if (ch.data.sinceJump < 0.30f)     return false; // debounce jump spam
            if (ch.data.chargingJump)          return false; // mid-charge

            return true;
        }

        private void SendJumpRpc()
        {
            // Jump input is network-synced via RPC in this game; avoid raw input flags.
            // IMPORTANT: Only issue if it would pass the game's own TryToJump checks.
            if (!CanIssueJumpNow()) return;

            if (ch.refs.view != null && ch.refs.view.IsMine)
                ch.refs.view.RPC("JumpRpc", RpcTarget.All, false);
        }
    }
}
