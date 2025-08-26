// /AI/GraphFollower.Movement.cs
// Slim locomotion shim. All action decisions (hop/attach/gap) are owned by BotBrain.

using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        // Legacy HandleMovement used to hop/attach/jump directly.
        // Now the brain owns that. Keep a no-op to preserve call sites.
        private void HandleMovement(Vector3 moveDir)
        {
            // Intentionally empty. Movement input is applied in Core after the brain runs.
            // Keep this if you later want to add purely kinematic helpers (no jumps here).
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
