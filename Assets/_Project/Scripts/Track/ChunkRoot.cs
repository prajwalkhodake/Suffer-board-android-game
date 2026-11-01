using System.Collections.Generic;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Track
{
    /// <summary>
    /// What a prefab-declared anchor is for (TECH_DOC §4.5). Obstacle slots are authored in
    /// <see cref="ChunkDefinition"/> so one prefab can host many layouts; anchors are for content that is
    /// genuinely positional (collectible arcs, props, event decoration) and arrive with their milestone.
    /// </summary>
    public enum SpawnAnchorKind
    {
        Obstacle = 0,
        Collectible,
        Prop,
        Decoration
    }

    /// <summary>
    /// A named spawn point inside a chunk prefab. Caches its transform once; the populator reads
    /// <see cref="LocalPosition"/> rather than walking the hierarchy at spawn time.
    /// </summary>
    public sealed class SpawnAnchor : MonoBehaviour
    {
        [SerializeField] private SpawnAnchorKind kind = SpawnAnchorKind.Obstacle;
        [SerializeField] private int lane;
        [SerializeField] private string tag;

        public SpawnAnchorKind Kind => kind;
        public int Lane => lane;
        public string Tag => tag;
        public Transform CachedTransform { get; private set; }
        public Vector3 LocalPosition => CachedTransform != null ? CachedTransform.localPosition : Vector3.zero;

        private void Awake()
        {
            CachedTransform = transform;
        }
    }

    /// <summary>
    /// Root of a pooled track chunk (TECH_DOC §4.5, §5.4). Carries the definition it was spawned for, the
    /// absolute run distance of its start edge, and the pooled children the populator placed inside it.
    /// Geometry only — all gameplay content is spawned through <see cref="IChunkPopulator"/>.
    /// </summary>
    public sealed class ChunkRoot : PooledEntity
    {
        [Tooltip("Optional prefab-declared anchors. Obstacle slots come from the definition, not from here.")]
        [SerializeField] private SpawnAnchor[] anchors = System.Array.Empty<SpawnAnchor>();

        // Reused across spawns: the populator adds, Clear releases and empties. Never reallocated.
        private readonly List<PooledEntity> _children = new List<PooledEntity>(16);

        public ChunkDefinition Definition { get; private set; }
        public int SequenceIndex { get; private set; }
        public double StartDistance { get; private set; }
        public StableId Id => Definition != null ? Definition.Id : StableId.None;
        public float LengthMetres => Definition != null ? Definition.lengthMetres : 0f;
        public double EndDistance => StartDistance + LengthMetres;

        public int AnchorCount => anchors != null ? anchors.Length : 0;
        public int ChildCount => _children.Count;

        /// <summary>True when the populator forgot to release something — a pooling bug, reported by the generator.</summary>
        public bool HasLeakedChildren => _children.Count > 0;

        public SpawnAnchor GetAnchor(int index) => anchors[index];

        /// <summary>Called by the generator immediately after <c>pool.Get()</c>, before positioning.</summary>
        public void Bind(ChunkDefinition definition, int sequenceIndex, double startDistance)
        {
            Definition = definition;
            SequenceIndex = sequenceIndex;
            StartDistance = startDistance;
        }

        public void AddChild(PooledEntity child)
        {
            if (child != null) _children.Add(child);
        }

        /// <summary>Removes a child from the bookkeeping list without releasing it (the owner releases).</summary>
        public bool RemoveChild(PooledEntity child) => _children.Remove(child);

        public PooledEntity GetChild(int index) => _children[index];

        public void ClearChildren() => _children.Clear();

        public override void OnSpawned()
        {
            // A reused chunk must start empty; anything left over is a leak from the previous cycle.
            _children.Clear();
        }

        public override void OnDespawned()
        {
            Definition = null;
            SequenceIndex = 0;
            StartDistance = 0d;
        }
    }
}