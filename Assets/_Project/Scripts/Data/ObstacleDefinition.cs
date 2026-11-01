using System;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// How an obstacle may be cleared (TECH_DOC §5.5). The collision resolver reads only this data plus
    /// the player's vertical state — adding a new clearance shape never requires code.
    /// </summary>
    [Serializable]
    public struct ClearanceProfile
    {
        [Tooltip("Can be jumped over.")]
        public bool jumpable;
        [Tooltip("Can be rolled under.")]
        public bool rollable;
        [Tooltip("Can be landed on (rooftop / ramp top).")]
        public bool mountable;
        [Tooltip("Feet must be at least this high to clear it by jumping.")]
        [Min(0f)] public float clearanceHeight;
        [Tooltip("Player height while rolling must be at most this to pass underneath.")]
        [Min(0f)] public float underClearance;
        [Tooltip("Bitmask of lanes physically occupied. Authoring + validation data: the collider is the ground " +
                 "truth for a contact, so this drives the traversability check, not the hit classification.")]
        public int laneOccupancyMask;
    }

    /// <summary>Authored motion for moving obstacles (trains, drones, sweepers).</summary>
    [Serializable]
    public struct ObstacleMotion
    {
        public enum MotionAxis { Lateral, Longitudinal, Vertical }

        public MotionAxis axis;
        [Min(0f)] public float amplitude;
        [Min(0.01f)] public float period;
        [Range(0f, 1f)] public float phase;
    }

    /// <summary>
    /// An obstacle archetype (TECH_DOC §5.5). <see cref="behaviourId"/> selects an
    /// <c>IObstacleBehaviour</c> from the behaviour catalog; reusing an existing behaviour id means
    /// a new obstacle is pure data (playbook §7.3).
    /// </summary>
    [CreateAssetMenu(fileName = "ObstacleDefinition", menuName = "Skyrift/Track/Obstacle")]
    public sealed class ObstacleDefinition : DefinitionBase
    {
        [Tooltip("Pool key for this obstacle's prefab.")]
        public string poolKey;

        [Tooltip("Addressables key of the obstacle prefab. M1 resolves it through the local catalog.")]
        public string prefabAddress;

        [Tooltip("Behaviour id resolved through ObstacleBehaviourCatalog (jumpable/rollable/full_blocker/...).")]
        public string behaviourId;

        [Tooltip("Tags matched by ChunkDefinition.obstacleSlots.")]
        public string[] tagSet = Array.Empty<string>();

        public ClearanceProfile clearance;

        [Tooltip("X = length along the track, Y = height.")]
        public Vector2 footprintMetres = new Vector2(2f, 1.2f);

        public bool isMoving;
        public ObstacleMotion motion;

        [Tooltip("Upward impulse multiplier applied when a ramp launches the player (0 = no launch).")]
        [Min(0f)] public float launchHeightMultiplier;

        [Tooltip("Sfx id played on impact. Resolved by the audio catalog; empty falls back to the generic hit.")]
        public string impactSfxId;

        [Tooltip("VFX key played on a crash against this obstacle. Resolved by the VFX catalog.")]
        public string crashFxKey;

        [Min(0.01f)] public float spawnWeightMultiplier = 1f;

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(ObstacleDefinition);

            if (string.IsNullOrEmpty(poolKey)) report.Error(t, Id, "pool_key", "poolKey is required.");
            if (string.IsNullOrEmpty(behaviourId)) report.Error(t, Id, "behaviour", "behaviourId is required.");
            if (tagSet == null || tagSet.Length == 0) report.Error(t, Id, "tags", "At least one tag is required.");
            if (clearance.laneOccupancyMask == 0) report.Error(t, Id, "lane_mask", "clearance.laneOccupancyMask must be non-zero.");
            if (footprintMetres.x <= 0f || footprintMetres.y <= 0f) report.Error(t, Id, "footprint", "footprintMetres must be positive.");
            if (isMoving && motion.period <= 0f) report.Error(t, Id, "motion", "Moving obstacles need motion.period > 0.");
            if (!clearance.jumpable && !clearance.rollable && !clearance.mountable && clearance.laneOccupancyMask != 0)
            {
                // A full blocker is legal and intentional; this is informational, not an error.
                report.Warn(t, Id, "full_blocker", "Obstacle is a full blocker: it can only be avoided by changing lane.");
            }
        }
    }
}
