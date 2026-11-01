using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>Score contributed by one unit of a pickup kind. A table row, so a new kind is data (TECH_DOC §4.11).</summary>
    [Serializable]
    public sealed class KindScoreWeight
    {
        public CollectibleKind kind;
        [Min(0f)] public float weight = 1f;
    }

    /// <summary>Score composition weights and combo tuning (TECH_DOC §4.11, §5.13).</summary>
    [CreateAssetMenu(fileName = "ScoringConfig", menuName = "Skyrift/Config/Scoring")]
    public sealed class ScoringConfig : DefinitionBase
    {
        [Min(0f)] public float distanceWeight = 1f;

        [Tooltip("Score per collected unit, by kind. Kinds absent from this table contribute no score.")]
        public List<KindScoreWeight> kindWeights = new List<KindScoreWeight>
        {
            new KindScoreWeight { kind = CollectibleKind.Coin, weight = 10f },
            new KindScoreWeight { kind = CollectibleKind.Key, weight = 50f },
            new KindScoreWeight { kind = CollectibleKind.Gem, weight = 25f },
            new KindScoreWeight { kind = CollectibleKind.Letter, weight = 15f },
            new KindScoreWeight { kind = CollectibleKind.PowerUpPickup, weight = 0f },
            new KindScoreWeight { kind = CollectibleKind.MysteryBox, weight = 0f }
        };

        [Tooltip("Seconds between collections before the combo resets.")]
        [Min(0.1f)] public float comboWindow = 1.6f;
        [Min(1)] public int comboMax = 25;
        [Min(0f)] public float comboBonusPerStep = 4f;
        [Tooltip("Multiplier contributed by one completed mission set.")]
        [Min(0f)] public float missionSetMultiplierStep = 0.5f;

        /// <summary>Linear scan over a handful of rows; called on collection, never per frame.</summary>
        public float WeightFor(CollectibleKind kind)
        {
            if (kindWeights == null) return 0f;
            for (int i = 0; i < kindWeights.Count; i++)
            {
                KindScoreWeight row = kindWeights[i];
                if (row != null && row.kind == kind) return row.weight;
            }
            return 0f;
        }

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(ScoringConfig);

            if (comboWindow <= 0f) report.Error(t, Id, "combo_window", "comboWindow must be > 0.");
            if (comboMax < 1) report.Error(t, Id, "combo_max", "comboMax must be >= 1.");

            if (kindWeights == null) return;
            var seen = new HashSet<CollectibleKind>();
            for (int i = 0; i < kindWeights.Count; i++)
            {
                KindScoreWeight row = kindWeights[i];
                if (row == null) { report.Error(t, Id, "kind_null", $"kindWeights[{i}] is null."); continue; }
                if (!seen.Add(row.kind)) report.Error(t, Id, "kind_unique", $"Duplicate score weight for kind '{row.kind}'.");
                if (row.weight < 0f) report.Error(t, Id, "kind_weight", $"Score weight for '{row.kind}' must be >= 0.");
            }
        }
    }
}
