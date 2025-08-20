// /AI/GraphFollower.Stamina.cs
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower : MonoBehaviour
    {
        // ───────────────────────────────────────────────────────────────────
        // Regular-only stamina helpers (ignore extra stamina for decisions)
        // ───────────────────────────────────────────────────────────────────

        /// <summary>Max of the regular bar (0..1, shrinks with afflictions). Always ≥ a tiny epsilon.</summary>
        float RegularMax() => Mathf.Max(ch.GetMaxStamina(), 0.0001f);

        /// <summary>Current regular stamina in absolute units (0..RegularMax()).</summary>
        float Regular() => ch.data.currentStamina;

        /// <summary>Regular stamina as a 0..1 fraction of RegularMax().</summary>
        float RegularFrac() => Regular() / RegularMax();

        /// <summary>True if the regular bar is effectively empty (matches game OutOfRegularStamina threshold).</summary>
        bool OutOfRegular() => Regular() < 0.005f;

        /// <summary>Short cooldown window after being empty to avoid thrashing hard maneuvers.</summary>
        bool RecentlyExhausted() => ch.data.outOfStaminaFor > 0.3f;
    }
}
