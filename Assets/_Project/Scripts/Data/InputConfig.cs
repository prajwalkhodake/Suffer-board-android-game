using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>Swipe/tap detection thresholds and intent buffering (TECH_DOC §4.3, §5.13).</summary>
    [CreateAssetMenu(fileName = "InputConfig", menuName = "Skyrift/Config/Input")]
    public sealed class InputConfig : DefinitionBase
    {
        [Tooltip("Minimum swipe travel as a fraction of the smaller screen dimension.")]
        [Range(0.01f, 0.5f)] public float swipeMinDistancePercent = 0.06f;
        [Tooltip("A gesture longer than this is a drag, not a swipe.")]
        [Min(0.05f)] public float swipeMaxDuration = 0.45f;
        [Min(0.02f)] public float tapMaxDuration = 0.22f;
        [Range(0.001f, 0.2f)] public float tapMaxMovePercent = 0.02f;
        [Tooltip("How much larger the dominant axis must be to resolve a diagonal swipe.")]
        [Min(1f)] public float axisDominanceRatio = 1.15f;
        [Tooltip("A buffered intent survives this long before it is dropped.")]
        [Min(0f)] public float bufferWindow = 0.2f;
        [Min(1)] public int bufferDepth = 1;

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            if (swipeMaxDuration <= tapMaxDuration)
                report.Error(nameof(InputConfig), Id, "durations", "swipeMaxDuration must exceed tapMaxDuration.");
            if (bufferDepth < 1) report.Error(nameof(InputConfig), Id, "buffer", "bufferDepth must be >= 1.");
        }
    }
}
