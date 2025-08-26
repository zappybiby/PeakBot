// AI/Decision/BotBrain.cs
using System.Collections.Generic;

namespace Peak.BotClone
{
    internal enum BotActionType { Follow, Sprint, Rest }

    internal sealed class BotDecision
    {
        public BotActionType Type;
        public string Why = "";
        public Dictionary<string, float>? Scores; // optional for debugging
    }

    /// <summary>
    /// Minimal “utility pick”: Rest vs Sprint vs Follow, using your existing
    /// thresholds (regular-only stamina + sprint hysteresis).
    /// </summary>
    internal sealed class BotBrain
    {
        // Thresholds injected from GraphFollower so behavior matches current tuning.
        private readonly float _restFrac;     // e.g., STAM_REST_FRAC   (≈0.30)
        private readonly float _sprintFrac;   // e.g., STAM_SPRINT_FRAC (≈0.25)
        private readonly float _sprintEnter;  // sprintEnterDist
        private readonly float _sprintExit;   // sprintExitDist

        public BotBrain(float restFrac, float sprintFrac, float sprintEnter, float sprintExit)
        {
            _restFrac    = restFrac;
            _sprintFrac  = sprintFrac;
            _sprintEnter = sprintEnter;
            _sprintExit  = sprintExit;
        }

        public BotDecision Evaluate(in Blackboard bb, bool currentlySprinting)
        {
            // 1) Rest if stamina is low (regular-only mindset).
            if (bb.StaminaFrac <= _restFrac)
            {
                return new BotDecision
                {
                    Type = BotActionType.Rest,
                    Why  = $"stamina {bb.StaminaFrac:F2} ≤ rest { _restFrac:F2 }"
                };
            }

            // 2) Sprint when we have the stamina & distance (respect enter threshold).
            bool sprintable = !bb.IsClimbing && bb.StaminaFrac >= _sprintFrac;
            if (sprintable && bb.DistToPlayer >= _sprintEnter)
            {
                return new BotDecision
                {
                    Type = BotActionType.Sprint,
                    Why  = $"dist {bb.DistToPlayer:F1} ≥ enter {_sprintEnter:F1} & stamina {bb.StaminaFrac:F2} ≥ {_sprintFrac:F2}"
                };
            }

            // 3) Otherwise Follow (and if we were sprinting, exit when below exit threshold or stamina dips).
            if (currentlySprinting && (bb.DistToPlayer <= _sprintExit || !sprintable))
            {
                return new BotDecision
                {
                    Type = BotActionType.Follow,
                    Why  = $"exit sprint: dist {bb.DistToPlayer:F1} ≤ exit {_sprintExit:F1} or stamina/climb gate"
                };
            }

            return new BotDecision { Type = BotActionType.Follow, Why = "default follow" };
        }
    }
}
