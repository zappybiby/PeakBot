// /Config/BotCloneSettings.cs
using UnityEngine;

namespace Peak.BotClone.Config
{
    [CreateAssetMenu(menuName = "PEAK/Bot Clone Settings")]
    public class BotCloneSettings : ScriptableObject
    {
        [Header("Clone")]
        public string botPrefabName = "Character_Bot";
        public float speedMult = 0.65f;
        public float sprintDistance = 18f;
        public float despawnDistance = 100f;

        [Header("Stamina thresholds")]
        public float stamRest = 0.15f;
        public float stamSprint = 0.35f;
        public float stamClimb = 0.20f;
        public float stamAttach = 0.40f;

        [Header("Climb/Detour")]
        public float detourFactor = 1.4f;
        public int   maxNavEvalNodes = 200;
        public float maxWallHang = 3f;

        [Header("Ledge scan")]
        public int   ledgeCasts = 60;
        public float ledgeRadius = 1.0f;
        public float ledgeHeight = 1.5f;
        public float ledgeMaxDist = 4f;
    }
}
