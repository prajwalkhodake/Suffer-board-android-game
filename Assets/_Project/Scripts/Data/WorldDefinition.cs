using System;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// A themed world / environment (TECH_DOC §5.3). Worlds select chunks by tag, drive the difficulty
    /// curve and palette, and ship as their own content pack so events can rotate without a build.
    /// </summary>
    [CreateAssetMenu(fileName = "WorldDefinition", menuName = "Skyrift/Worlds/World")]
    public sealed class WorldDefinition : DefinitionBase
    {
        [Tooltip("Addressables label / asset pack this world ships in.")]
        public string assetPackLabel = "world.default";

        [Tooltip("Only chunks whose worldTags intersect this filter are eligible.")]
        public string[] chunkTagFilter = Array.Empty<string>();

        [Tooltip("X = normalised run distance, Y = difficulty offset added to the ramp. Non-decreasing.")]
        public AnimationCurve difficultyCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);

        [Tooltip("Music id resolved by the audio catalog.")]
        public string musicId;

        [Header("Palette (placeholder-friendly)")]
        public Color fogColor = new Color(0.05f, 0.07f, 0.12f);
        public Color groundColor = new Color(0.16f, 0.18f, 0.24f);
        public Color accentColor = new Color(0.25f, 0.85f, 0.95f);
        public Color ambientColor = new Color(0.35f, 0.4f, 0.5f);

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(WorldDefinition);

            if (chunkTagFilter == null || chunkTagFilter.Length == 0)
                report.Error(t, Id, "tag_filter", "chunkTagFilter must contain at least one tag.");

            if (difficultyCurve != null && difficultyCurve.length > 1)
            {
                for (int i = 1; i < difficultyCurve.length; i++)
                {
                    if (difficultyCurve[i].value < difficultyCurve[i - 1].value)
                    {
                        report.Error(t, Id, "curve_monotonic", "difficultyCurve must be non-decreasing.");
                        break;
                    }
                }
            }
        }
    }
}
