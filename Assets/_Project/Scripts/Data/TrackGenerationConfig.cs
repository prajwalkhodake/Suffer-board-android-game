using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>Chunk streaming, difficulty ramp and floating-origin tuning (TECH_DOC §5.13).</summary>
    [CreateAssetMenu(fileName = "TrackGenerationConfig", menuName = "Skyrift/Config/Track Generation")]
    public sealed class TrackGenerationConfig : DefinitionBase
    {
        [Min(1)] public int chunksAhead = 6;
        [Min(0)] public int chunksBehind = 2;
        [Tooltip("Distance over which target difficulty ramps from 0 to 1.")]
        [Min(1f)] public float difficultyRampMetres = 2200f;
        [Tooltip("Multiplier applied to a chunk's weight for each recent use inside the repeat window.")]
        [Range(0f, 1f)] public float repeatPenalty = 0.35f;
        [Min(0)] public int repeatWindow = 4;
        [Tooltip("How much of the target difficulty comes from current speed rather than distance alone.")]
        [Range(0f, 1f)] public float speedDifficultyInfluence = 0.15f;
        [Tooltip("Lowest weight the difficulty-fit contribution may apply, so a far-band chunk is still pickable.")]
        [Range(0.01f, 1f)] public float difficultyFitFloor = 0.15f;
        [Tooltip("World is translated back by this many metres once the player passes it (ADR-0007).")]
        [Min(256f)] public float originShiftThreshold = 2048f;

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            if (chunksAhead < 1) report.Error(nameof(TrackGenerationConfig), Id, "chunks_ahead", "chunksAhead must be >= 1.");
            if (difficultyRampMetres <= 0f) report.Error(nameof(TrackGenerationConfig), Id, "ramp", "difficultyRampMetres must be > 0.");
            if (originShiftThreshold < 256f) report.Error(nameof(TrackGenerationConfig), Id, "origin", "originShiftThreshold must be >= 256 m.");
        }
    }
}
