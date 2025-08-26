// AI/State/Blackboard.cs
using UnityEngine;

namespace Peak.BotClone
{
    // ---------- Sensor DTOs ----------
    internal struct StepInfo
    {
        public bool  CanHop;        // small obstacle that a hop would clear
        public float Height;        // obstacle height above feet (m)
        public int   LateralAgree;  // -1=left prefers, 0=none, 1=right prefers, 2=both ok
    }

    internal struct WallAttachInfo
    {
        public bool     IsSteep;      // front surface is steep enough to climb
        public bool     CanAttach;    // close + steep + unobstructed
        public float    PlanarDist;   // planar distance from chest/head to surface
        public Vector3  Normal;       // wall normal at probe
    }

    internal struct GapInfo
    {
        public bool     HasLanding;   // we found a plausible landing
        public Vector3  Landing;      // landing position
        public float    Distance;     // horizontal distance to landing (m)
    }

    /// <summary>Read-only snapshot of everything the brain needs to decide.</summary>
    internal struct Blackboard
    {
        // Core pose
        public Vector3 SelfPos;
        public Vector3 PlayerPos;
        public float   DistToPlayer;

        // Movement / posture
        public bool IsGrounded;
        public bool IsClimbing;

        // Fatigue gates
        public bool RecentlyExhausted; // short-term stamina lockout

        // Stamina (regular-only mindset)
        public float StaminaRegular; // absolute
        public float StaminaFrac;    // 0..1

        // Steering suggestion (from navigation)
        public Vector3 MoveDir;

        // NavMesh status
        public bool HasNavMeshPath;
        public bool NavPathComplete;
        public float DetourRatio; // path length / straight-line (∞ if unknown)

        // Opportunities (perception)
        public StepInfo       Step;
        public WallAttachInfo Wall;
        public GapInfo        Gap;
    }
}
