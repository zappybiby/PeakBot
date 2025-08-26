// /AI/GraphFollower.Movement.cs
// Slim locomotion shim. All action decisions (hop/attach/gap/strafe) are owned by BotBrain.

using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        private float _strafeBlend = 0f;
        private const float STRAFE_MAX   = 0.8f; // safety clamp
        private const float STRAFE_ACCEL = 6f;   // into a strafe
        private const float STRAFE_DECEL = 8f;   // back to zero

        /// <summary>
        /// Actuate a brain-provided strafe hint. No world/Blackboard reads here.
        /// </summary>
        private Vector2 HandleMovement(float targetStrafe, bool resting)
        {
            if (resting)
            {
                _strafeBlend = Mathf.MoveTowards(_strafeBlend, 0f, STRAFE_DECEL * Time.deltaTime);
                return Vector2.zero;
            }

            float accel = (Mathf.Abs(targetStrafe) > Mathf.Abs(_strafeBlend)) ? STRAFE_ACCEL : STRAFE_DECEL;
            _strafeBlend = Mathf.MoveTowards(_strafeBlend, targetStrafe, accel * Time.deltaTime);
            _strafeBlend = Mathf.Clamp(_strafeBlend, -STRAFE_MAX, STRAFE_MAX);

            // x = strafe, y = forward
            return new Vector2(_strafeBlend, 1f);
        }

        internal static Vector2 DirToLook(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-4f) return Vector2.zero;
            dir.Normalize();
            float yaw   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(dir.y) * Mathf.Rad2Deg;
            return new Vector2(yaw, pitch);
        }
    }
}
