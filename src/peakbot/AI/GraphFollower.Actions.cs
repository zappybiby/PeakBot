// AI/GraphFollower.Actions.cs
using Photon.Pun;
using UnityEngine;
using System.Collections;

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
                case BotActionType.Hop: TryHop(bb); break;
                case BotActionType.GapJump: TryGapJump(bb); break;
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

            // Prefer aiming *into* the wall using the sensed normal.
            Vector3 fwd = Vector3.zero;
            if (bb.Wall.Normal != Vector3.zero)
            {
                // Face opposite the wall normal (flattened on XZ for clean yaw).
                fwd = -Vector3.ProjectOnPlane(bb.Wall.Normal, Vector3.up);
            }
            // Fallback to movement/player direction if normal was unavailable.
            if (fwd.sqrMagnitude < 1e-4f)
            {
                fwd = bb.MoveDir.sqrMagnitude > 1e-4f ? bb.MoveDir : (player.Center - ch.Center);
                fwd.y = 0f;
            }

            if (fwd.sqrMagnitude > 1e-4f)
            {
                data.lookValues = DirToLook(fwd.normalized);
                // IMPORTANT: update lookDirection/lookDirection_Flat right now,
                // since CharacterClimbing will read lookDirection_Flat this frame.
                ch.RecalculateLookDirections();
            }

            // Jump, then immediately try to climb (matches the game's "jump-then-grab" flow).
            SendJumpRpc();
            MI_TryClimb?.Invoke(ch.refs.climbing, null);

            // Optional: a tiny delayed re-poke helps if the first call races the jump.
            if (isActiveAndEnabled) StartCoroutine(CoClimbRetry(0.12f));
        }

        /// <summary>
        /// Mirror CharacterMovement.TryToJump gating so we don't force illegal jumps via RPC.
        /// </summary>
        private bool CanIssueJumpNow()
        {
            // Same conditions the game enforces before calling JumpRpc:
            if (ch.data.jumpsRemaining <= 0) return false; // no jumps left
            if (!ch.CheckJump()) return false;   // not allowed (climbing/rope/vine/handle/etc.)
            if (ch.data.sinceGrounded > 0.20f) return false; // been airborne too long
            if (ch.data.sinceJump < 0.30f) return false; // debounce jump spam
            if (ch.data.chargingJump) return false; // mid-charge

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
        private IEnumerator CoClimbRetry(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (!data.isClimbing && ch != null && ch.refs != null && ch.refs.climbing != null)
            {
                MI_TryClimb?.Invoke(ch.refs.climbing, null);
            }
        }

    }
}
