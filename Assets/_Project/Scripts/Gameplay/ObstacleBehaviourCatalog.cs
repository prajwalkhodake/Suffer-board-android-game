using System;
using System.Collections.Generic;
using Skyrift.Core.Ids;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Base for a contact-classification strategy asset (TECH_DOC §4.7 extension point). Behaviours are
    /// stateless ScriptableObjects, so one asset is shared by every obstacle that uses it and resolving a
    /// contact never allocates a strategy instance.
    /// </summary>
    public abstract class ObstacleBehaviourAsset : ScriptableObject, IObstacleBehaviour
    {
        public abstract ContactOutcome Resolve(in ContactContext ctx);
    }

    /// <summary>
    /// Maps <c>ObstacleDefinition.behaviourId</c> to a behaviour asset (TECH_DOC §4.7, playbook §7.3).
    ///
    /// Placement: this lives in Gameplay, not Data, because Data may not reference Gameplay (§3.3). The
    /// definition therefore carries a plain string id and the composition root holds the catalog reference.
    /// Adding an obstacle that reuses an existing behaviour is a data row only; adding a genuinely new
    /// mechanic is one subclass plus one catalog row, and no core system changes.
    /// </summary>
    [CreateAssetMenu(fileName = "ObstacleBehaviourCatalog", menuName = "Skyrift/Catalogs/Obstacle Behaviour Catalog")]
    public sealed class ObstacleBehaviourCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("Stable behaviour id referenced by ObstacleDefinition.behaviourId.")]
            public string behaviourId;

            public ObstacleBehaviourAsset behaviour;
        }

        [Tooltip("One row per behaviour id. Adding a row here plus a subclass is the only code-side step for a new mechanic.")]
        public List<Entry> entries = new List<Entry>();

        // Built once on first use: a string-keyed lookup with no per-contact allocation.
        private Dictionary<string, ObstacleBehaviourAsset> _byId;

        public IReadOnlyList<Entry> Entries => entries;

        public bool TryGet(string behaviourId, out IObstacleBehaviour behaviour)
        {
            EnsureIndex();

            if (!string.IsNullOrEmpty(behaviourId) && _byId.TryGetValue(behaviourId, out ObstacleBehaviourAsset asset) && asset != null)
            {
                behaviour = asset;
                return true;
            }

            behaviour = null;
            return false;
        }

        /// <summary>Authoring-time check: every id valid and unique, every behaviour assigned.</summary>
        public void Validate(ValidationReport report)
        {
            const string type = nameof(ObstacleBehaviourCatalog);
            var seen = new HashSet<string>();

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null)
                {
                    report.Error(type, name, "entry_null", $"entries[{i}] is null.");
                    continue;
                }

                if (!StableId.IsValidString(entry.behaviourId))
                    report.Error(type, name, "id_format", $"entries[{i}].behaviourId '{entry.behaviourId}' is not a valid id.");
                else if (!seen.Add(entry.behaviourId))
                    report.Error(type, name, "id_unique", $"Duplicate behaviour id '{entry.behaviourId}'.");

                if (entry.behaviour == null)
                    report.Error(type, name, "behaviour", $"Behaviour '{entry.behaviourId}' has no asset assigned.");
            }
        }

        /// <summary>Drops the index so an editor-side edit is picked up without a domain reload.</summary>
        public void InvalidateIndex() => _byId = null;

        private void EnsureIndex()
        {
            if (_byId != null) return;

            _byId = new Dictionary<string, ObstacleBehaviourAsset>(entries.Count, StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.behaviourId) || entry.behaviour == null) continue;
                _byId[entry.behaviourId] = entry.behaviour;
            }
        }

#if UNITY_EDITOR
        private void OnValidate() => InvalidateIndex();
#endif
    }
}