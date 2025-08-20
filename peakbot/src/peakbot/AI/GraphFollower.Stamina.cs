// /AI/GraphFollower.Stamina.cs
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower : MonoBehaviour
    {
        /// <summary>
        /// True if the character's stamina fraction is below <paramref name="frac"/>.
        /// </summary>
        bool Low(float frac) => ch.GetTotalStamina() < frac;

        /// <summary>
        /// Convenience accessor for current stamina fraction [0..1].
        /// </summary>
        float StaminaFrac() => ch.GetTotalStamina();
    }
}
