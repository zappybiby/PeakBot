// /Config/BotCloneSettings.cs
using UnityEngine;

namespace Peak.BotClone.Config
{
    [CreateAssetMenu(menuName = "PEAK/Bot Clone Settings")]
    public class BotCloneSettings : ScriptableObject
    {
        [Header("Clone")]
        [Tooltip("Photon prefab ID for the bot object.")]
        public string botPrefabName = "Character_Bot";

        [Tooltip("Multiplier applied to BotMoverRagdoll.movementSpeed.")]
        [Range(0.1f, 2f)]
        public float speedMult = 0.65f;

        [Tooltip("Distance at which the clone starts sprinting to catch the player.")]
        [Min(0f)]
        public float sprintDistance = 18f;

        [Tooltip("If the clone is farther than this from the player, it despawns.")]
        [Min(0f)]
        public float despawnDistance = 100f;

        [Header("Stamina thresholds")]
        [Tooltip("REGULAR FRACTION (0..1). When the regular bar falls to this fraction or below, the bot enters a regen-rest state.")]
        [Range(0f, 1f)] public float stamRest   = 0.30f;

        [Tooltip("REGULAR FRACTION (0..1). Minimum regular-bar fraction to start/keep sprinting.")]
        [Range(0f, 1f)] public float stamSprint = 0.25f;

        [Tooltip("REGULAR FRACTION (0..1). Minimum regular-bar fraction to attempt simple climbs, hops, and ledge jumps.")]
        [Range(0f, 1f)] public float stamClimb  = 0.20f;

        [Tooltip("REGULAR ABSOLUTE UNITS (0..1). Minimum regular stamina required to attempt a wall-attach jump (covers attach tax + one burst + headroom).")]
        [Range(0f, 1f)] public float stamAttach = 0.40f;

        [Header("Climb / Detour / Limits")]
        [Tooltip("Attempt climbs only if going around is this many times longer than direct.")]
        [Min(1f)]
        public float detourFactor = 1.4f;

        [Tooltip("Hard cap on nodes visited when estimating graph detours.")]
        [Min(1)]
        public int maxNavEvalNodes = 200;

        [Tooltip("Maximum time the clone will remain wall-hanging before forcing a drop (scaled down automatically when stamina is low).")]
        [Min(0f)]
        public float maxWallHang = 3f;

        [Header("Ledge scan (gap jumps)")]
        [Tooltip("Random downcasts sampled per attempt when looking for a landing.")]
        [Min(1)]
        public int ledgeCasts = 60;

        [Tooltip("Horizontal jitter radius for ledge landing samples.")]
        [Min(0f)]
        public float ledgeRadius = 1.0f;

        [Tooltip("Vertical height above the origin used when casting down for landings.")]
        [Min(0f)]
        public float ledgeHeight = 1.5f;

        [Tooltip("Maximum distance to consider a ledge landing valid.")]
        [Min(0f)]
        public float ledgeMaxDist = 4f;
    }
}
