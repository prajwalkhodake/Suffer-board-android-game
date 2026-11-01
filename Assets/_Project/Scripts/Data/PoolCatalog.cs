using System.Collections.Generic;
using Skyrift.Core.Pooling;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// The single registry of pooled prefabs (TECH_DOC §4.6, §5.14 rule 10). Adding a pooled object is
    /// a row here — no code change, no per-type switch in the registry.
    /// </summary>
    [CreateAssetMenu(fileName = "PoolCatalog", menuName = "Skyrift/Catalogs/Pool Catalog")]
    public sealed class PoolCatalog : DefinitionBase
    {
        public List<PoolEntry> entries = new List<PoolEntry>();

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(PoolCatalog);

            var seen = new HashSet<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                PoolEntry e = entries[i];
                if (e == null) { report.Error(t, Id, "entry_null", $"entries[{i}] is null."); continue; }
                if (!StableIdValid(e.key)) report.Error(t, Id, "key_format", $"entries[{i}].key '{e.key}' is not a valid id.");
                if (!seen.Add(e.key)) report.Error(t, Id, "key_unique", $"Duplicate pool key '{e.key}'.");
                if (e.prefab == null) report.Error(t, Id, "prefab", $"Pool '{e.key}' has no prefab.");
                if (e.prewarm < 0) report.Error(t, Id, "prewarm", $"Pool '{e.key}' prewarm must be >= 0.");
            }
        }

        private static bool StableIdValid(string value) => Core.Ids.StableId.IsValidString(value);
    }
}
