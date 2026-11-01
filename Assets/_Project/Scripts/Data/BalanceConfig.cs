using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// Locomotion, collision-forgiveness and chaser tuning (TECH_DOC §5.13). Every number the player
    /// can feel lives here or in Remote Config — never in gameplay code.
    /// </summary>
    [CreateAssetMenu(fileName = "BalanceConfig", menuName = "Skyrift/Config/Balance")]
    public sealed class BalanceConfig : DefinitionBase
    {
        [Header("Lanes")]
        [Min(2)] public int laneCount = 3;
        [Min(0.5f)] public float laneWidth = 2.4f;
        [Min(0.02f)] public float laneChangeDuration = 0.14f;
        public AnimationCurve laneChangeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Speed")]
        [Tooltip("X = distance / speedRampMetres (0..1), Y = metres per second.")]
        public AnimationCurve speedCurve = AnimationCurve.Linear(0f, 12f, 1f, 28f);
        [Min(1f)] public float speedRampMetres = 2000f;
        [Min(1f)] public float maxSpeed = 30f;

        [Header("Jump / roll")]
        [Min(0.1f)] public float jumpHeight = 2.6f;
        [Min(0.1f)] public float jumpAirTime = 0.66f;
        [Min(0.1f)] public float rollDuration = 0.6f;
        [Tooltip("Extra height added to a jump when a jump modifier is active (e.g. super sneakers).")]
        [Min(0f)] public float jumpModifierBonus = 1.6f;

        [Header("Player collision volume")]
        [Min(0.1f)] public float playerStandHeight = 1.8f;
        [Min(0.1f)] public float playerRollHeight = 0.9f;
        [Min(0.05f)] public float playerRadius = 0.45f;

        [Header("Collision forgiveness")]
        [Tooltip("Vertical tolerance added to clearance checks so a jump 'counts' slightly early/late.")]
        [Min(0f)] public float clearanceForgiveness = 0.18f;
        [Tooltip("Extra downward tolerance when landing on a mountable roof. Trigger contacts are evaluated on " +
                 "the physics step rather than the render tick, so a fast descent can cover more than " +
                 "clearanceForgiveness between two evaluations. This window is what makes a roof landing " +
                 "frame-rate independent instead of a coin flip (§4.7).")]
        [Min(0f)] public float mountForgiveness = 0.6f;
        [Tooltip("Lateral offset (in lane widths) above which a contact is a side scrape rather than head-on.")]
        [Range(0.1f, 1f)] public float grazeLateralThreshold = 0.55f;

        [Header("Stumble")]
        [Range(0.1f, 1f)] public float stumbleSlowFactor = 0.55f;
        [Min(0.1f)] public float stumbleRecoveryTime = 1.1f;
        [Min(1)] public int maxStumblesBeforeCrash = 2;

        [Header("Chaser")]
        [Range(0f, 1f)] public float chaserProximityOnStumble = 0.9f;
        [Min(0f)] public float chaserDecayPerSecond = 0.22f;
        [Tooltip("How much speed scales the decay: at speed 0 the rate is (1 - influence), at maxSpeed it is (1 + influence).")]
        [Range(0f, 1f)] public float chaserDecaySpeedInfluence = 0.5f;
        [Range(0f, 1f)] public float chaserProximityOnRunStart = 0.25f;
        [Tooltip("Metres behind the player at proximity 0 (safe).")]
        [Min(1f)] public float chaserFarDistance = 16f;
        [Tooltip("Metres behind the player at proximity 1 (caught up). Must be lower than chaserFarDistance.")]
        [Min(0f)] public float chaserNearDistance = 2.5f;
        [Tooltip("How much of the player's lateral position the chaser mirrors, for presentation only.")]
        [Range(0f, 1f)] public float chaserLaneFollow = 0.65f;
        [Tooltip("Proximity at which the one-shot warning cue fires. Re-arms once proximity drops below it.")]
        [Range(0f, 1f)] public float chaserWarningProximity = 0.75f;

        /// <summary>Gravity implied by the authored jump height/airtime (h = g·t²/8).</summary>
        public float Gravity => 8f * jumpHeight / (jumpAirTime * jumpAirTime);

        /// <summary>Take-off velocity implied by the authored jump (v0 = 4h/t).</summary>
        public float JumpVelocity => 4f * jumpHeight / jumpAirTime;

        public float SpeedAtDistance(float distance, float scale)
        {
            float t = speedRampMetres > 0f ? Mathf.Clamp01(distance / speedRampMetres) : 1f;
            return Mathf.Min(speedCurve.Evaluate(t) * scale, maxSpeed * scale);
        }

        public float LaneX(int lane)
        {
            float centre = (laneCount - 1) * 0.5f;
            return (lane - centre) * laneWidth;
        }

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            if (laneCount < 2) report.Error(nameof(BalanceConfig), Id, "lane_count", "laneCount must be >= 2.");
            if (jumpAirTime <= 0f) report.Error(nameof(BalanceConfig), Id, "jump_airtime", "jumpAirTime must be > 0.");
            if (maxStumblesBeforeCrash < 1) report.Error(nameof(BalanceConfig), Id, "stumbles", "maxStumblesBeforeCrash must be >= 1.");
            if (playerRollHeight >= playerStandHeight) report.Error(nameof(BalanceConfig), Id, "player_height", "playerRollHeight must be lower than playerStandHeight.");
            if (chaserNearDistance >= chaserFarDistance) report.Error(nameof(BalanceConfig), Id, "chaser_distance", "chaserNearDistance must be lower than chaserFarDistance.");
            if (chaserDecaySpeedInfluence < 0f || chaserDecaySpeedInfluence > 1f) report.Error(nameof(BalanceConfig), Id, "chaser_influence", "chaserDecaySpeedInfluence must be within 0..1.");
            if (speedCurve == null || speedCurve.length == 0) report.Error(nameof(BalanceConfig), Id, "speed_curve", "speedCurve must have keys.");
        }
    }
}
