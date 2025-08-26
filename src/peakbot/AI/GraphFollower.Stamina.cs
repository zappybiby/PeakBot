// /AI/GraphFollower.Stamina.cs
// Regular-only stamina helpers (tiny + consistent; no XML noise).

using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        // Regular stamina (ignore extra stamina for decisions).
        private float RegularMax()       => Mathf.Max(ch.GetMaxStamina(), 0.0001f);
        private float Regular()          => ch.data.currentStamina;
        private float RegularFrac()      => Regular() / RegularMax();
        private bool  OutOfRegular()     => Regular() < 0.005f;
        private bool  RecentlyExhausted()=> ch.data.outOfStaminaFor > 0.3f;
    }
}
