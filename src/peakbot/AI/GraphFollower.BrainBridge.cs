// AI/GraphFollower.BrainBridge.cs
using UnityEngine;

namespace Peak.BotClone
{
    internal partial class GraphFollower
    {
        private BotBrain _brain = null!;
        private Perception _perception = null!;

        private void EnsureBrain()
        {
            if (_brain != null) return;
            _brain = new BotBrain(
                restFrac:    STAM_REST_FRAC,
                sprintFrac:  STAM_SPRINT_FRAC,
                sprintEnter: sprintEnterDist,
                sprintExit:  sprintExitDist,
                climbFrac:   STAM_CLIMB_FRAC,
                attachAbs:   STAM_ATTACH_ABS,
                detourFactor: DETOUR_FACTOR
            );
            EnsurePerception();
        }

        private void EnsurePerception()
        {
            if (_perception != null) return;
            _perception = new Perception(
                gf: this,
                ch: ch,
                data: data,
                player: player,
                bot: bot,
                ledgeRadius: LEDGE_RADIUS,
                ledgeHeight: LEDGE_HEIGHT,
                ledgeMaxDist: LEDGE_MAX_DIST,
                terrainMask: terrainMask
            );
        }

        private Blackboard BuildBlackboard(Vector3 navDir)
        {
            EnsurePerception();
            return _perception.BuildBlackboard(navDir);
        }

        private void ApplyDecision(in Blackboard bb, in BotDecision d, ref bool restingFlag)
        {
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

                default: // Follow / Hop / WallAttach / GapJump (actuation comes later)
                    restingFlag = false;

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
