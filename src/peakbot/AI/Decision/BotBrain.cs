// AI/Decision/BotBrain.cs
using System.Collections.Generic;
using UnityEngine;

namespace Peak.BotClone
{
    internal enum BotActionType { Follow, Sprint, Rest, Hop, WallAttach, GapJump }

    internal sealed class BotDecision
    {
        public BotActionType Type;
        public string Why = "";
        public Dictionary<string, float>? Scores;
        public Vector3? Target; // e.g., GapJump landing
    }

    internal sealed class BotBrain
    {
        // Thresholds come from GraphFollower so tuning stays consistent.
        private readonly float _restFrac;      // e.g., 0.30
        private readonly float _sprintFrac;    // e.g., 0.25
        private readonly float _sprintEnter;   // enter sprint dist
        private readonly float _sprintExit;    // exit sprint dist
        private readonly float _climbFrac;     // e.g., 0.20 (cap/hang)
        private readonly float _attachAbs;     // absolute stamina needed to try attach
        private readonly float _detourFactor;  // e.g., 1.4

        // Simple cooldowns per action
        private readonly Dictionary<string, float> _cd = new();

        public BotBrain(float restFrac, float sprintFrac, float sprintEnter, float sprintExit,
                        float climbFrac, float attachAbs, float detourFactor)
        {
            _restFrac     = restFrac;
            _sprintFrac   = sprintFrac;
            _sprintEnter  = sprintEnter;
            _sprintExit   = sprintExit;
            _climbFrac    = climbFrac;
            _attachAbs    = attachAbs;
            _detourFactor = detourFactor;
        }

        public BotDecision Evaluate(in Blackboard bb, bool currentlySprinting)
        {
            var scores = new Dictionary<string, float>(8);

            // --- Rest ---
            float rest = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(_restFrac + 0.10f, 0.0f, bb.StaminaFrac));
            if (!bb.IsGrounded) rest *= 0.5f;
            scores["Rest"] = rest;

            // --- Sprint ---
            bool sprintable = !bb.IsClimbing && bb.StaminaFrac >= _sprintFrac;
            float sprint = (sprintable && bb.DistToPlayer >= _sprintEnter) ? 0.8f : 0f;
            // If we’re already sprinting, keep a little inertia unless exit is clearly met.
            if (currentlySprinting && bb.DistToPlayer > _sprintExit && sprintable) sprint = Mathf.Max(sprint, 0.4f);
            scores["Sprint"] = sprint;

            // --- Hop (for small steps) ---
            float hop = 0f;
            if (bb.IsGrounded && !bb.IsClimbing && bb.Step.CanHop && !bb.RecentlyExhausted && CDReady("Hop"))
            {
                // Favor modest heights and some lateral agreement; keep it simple (0.6 baseline).
                float h = Mathf.InverseLerp(0.10f, 0.60f, bb.Step.Height);
                float lat = (bb.Step.LateralAgree == 0) ? 0.7f : 1f; // both sides or center → okay
                hop = 0.6f * h * lat;
            }
            scores["Hop"] = hop;

            // --- WallAttach (jump-then-attach) ---
            float wa = 0f;
            if (bb.IsGrounded && !bb.IsClimbing && bb.Wall.CanAttach && bb.StaminaRegular >= _attachAbs && CDReady("WallAttach"))
            {
                float detourCurve = Mathf.InverseLerp(_detourFactor, _detourFactor * 2f, bb.DetourRatio);
                wa = detourCurve;

                // Prefer ground if a complete nav path exists.
                if (bb.NavPathComplete) wa *= 0.2f;

                // If stamina is only barely above climb threshold, be cautious.
                if (bb.StaminaFrac < _climbFrac) wa *= 0.5f;
            }
            scores["WallAttach"] = wa;

            // --- GapJump (jump over a gap if landing looks valid) ---
            float gj = 0f;
            if (bb.IsGrounded && !bb.IsClimbing && bb.Gap.HasLanding && bb.StaminaFrac >= _sprintFrac && CDReady("GapJump"))
            {
                float detourCurve = Mathf.InverseLerp(_detourFactor, _detourFactor * 1.8f, bb.DetourRatio);
                float distPref    = Mathf.InverseLerp(0.8f, 4.0f, Mathf.Clamp(bb.Gap.Distance, 0.8f, 4f));
                gj = 0.5f * detourCurve + 0.5f * distPref;

                if (bb.NavPathComplete) gj *= 0.25f; // ground path exists; only jump if it's clearly better
            }
            scores["GapJump"] = gj;

            // --- Follow baseline ---
            scores["Follow"] = 0.1f;

            // Pick winner
            var (bestName, bestScore) = Max(scores);

            var d = new BotDecision { Scores = scores };
            switch (bestName)
            {
                case "Rest":
                    d.Type = BotActionType.Rest;
                    d.Why  = $"stamina {bb.StaminaFrac:F2} ≤ rest {_restFrac:F2}";
                    break;

                case "Sprint":
                    d.Type = BotActionType.Sprint;
                    d.Why  = $"dist {bb.DistToPlayer:F1} ≥ enter {_sprintEnter:F1} & stam {bb.StaminaFrac:F2} ≥ {_sprintFrac:F2}";
                    break;

                case "Hop":
                    d.Type = BotActionType.Hop;
                    d.Why  = $"step {bb.Step.Height:F2}m canHop; lateral={bb.Step.LateralAgree}";
                    SetCD("Hop", 0.25f);
                    break;

                case "WallAttach":
                    d.Type = BotActionType.WallAttach;
                    d.Why  = $"steep wall; detour={bb.DetourRatio:F2} worth it; stamAbs={bb.StaminaRegular:F2}";
                    SetCD("WallAttach", 1.0f);
                    break;

                case "GapJump":
                    d.Type = BotActionType.GapJump;
                    d.Target = bb.Gap.Landing;
                    d.Why  = $"landing ok @ {bb.Gap.Distance:F1}m; detour={bb.DetourRatio:F2}";
                    SetCD("GapJump", 0.5f);
                    break;

                default:
                    d.Type = BotActionType.Follow;
                    d.Why  = "default follow";
                    break;
            }

            return d;
        }

        private static KeyValuePair<string,float> Max(Dictionary<string,float> d)
        {
            KeyValuePair<string,float> best = default;
            float bestV = float.NegativeInfinity;
            foreach (var kv in d)
            {
                if (kv.Value >= bestV) { best = kv; bestV = kv.Value; }
            }
            return best;
        }

        private bool CDReady(string key) => !(_cd.TryGetValue(key, out var t) && Time.time < t);
        private void SetCD(string key, float seconds) => _cd[key] = Time.time + seconds;
    }
}
