// AI/GraphFollower.BrainBridge.cs
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        private BotBrain _brain = null!;

        // Call once after sprint hysteresis is set in Init(...)
        private void EnsureBrain()
        {
            if (_brain != null) return;
            _brain = new BotBrain(
                restFrac:   STAM_REST_FRAC,
                sprintFrac: STAM_SPRINT_FRAC,
                sprintEnter: sprintEnterDist,
                sprintExit:  sprintExitDist
            );
        }

        /// <summary>Builds the Blackboard from current runtime state.</summary>
        private Blackboard BuildBlackboard(Vector3 navDir)
        {
            return BlackboardUtil.Build(
                navDir,
                ch,
                player,
                data,
                bot,
                staminaAbs:  Regular(),
                staminaFrac: RegularFrac()
            );
        }

        /// <summary>
        /// Applies the minimal decision (Rest/Sprint/Follow) by only touching the
        /// existing 'resting' flag and the sprint toggle. Movement/steering stays as-is.
        /// </summary>
        private void ApplyDecision(in Blackboard bb, in BotDecision d, ref bool restingFlag)
        {
            // Respect your existing sprint toggle cooldown.
            bool canToggleSprint = Time.time >= nextSprintToggle;

            switch (d.Type)
            {
                case BotActionType.Rest:
                    restingFlag = true;

                    if (bot.IsSprinting && canToggleSprint)
                    {
                        bot.IsSprinting  = false;
                        nextSprintToggle = Time.time + 0.25f;
                    }
                    break;

                case BotActionType.Sprint:
                    restingFlag = false;

                    if (!bot.IsSprinting && canToggleSprint)
                    {
                        bot.IsSprinting  = true;
                        nextSprintToggle = Time.time + 0.25f;
                    }
                    break;

                default: // Follow
                    restingFlag = false;

                    // If sprint is on but exit conditions are implied by the brain's Why,
                    // safely turn it off (cooldown respected).
                    if (bot.IsSprinting)
                    {
                        bool shouldExit = (bb.DistToPlayer <= sprintExitDist) ||
                                          bb.IsClimbing ||
                                          (bb.StaminaFrac < STAM_SPRINT_FRAC);
                        if (shouldExit && canToggleSprint)
                        {
                            bot.IsSprinting  = false;
                            nextSprintToggle = Time.time + 0.25f;
                        }
                    }
                    break;
            }
        }
    }
}
