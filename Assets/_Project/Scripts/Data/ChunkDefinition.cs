using System;
using Skyrift.Core.Ids;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// One authored obstacle placement inside a chunk (TECH_DOC §5.4). The populator resolves
    /// <see cref="obstacleTag"/> against obstacle definitions and spawns from the pool — the chunk
    /// prefab itself contains geometry only, so one prefab can host many obstacle layouts.
    /// </summary>
    [Serializable]
    public sealed class ObstacleSlotData
    {
        [Range(0, 7)] public int lane;
        [Tooltip("Metres from the chunk's start edge.")]
        [Min(0f)] public float zOffset;
        [Tooltip("Tag matched against ObstacleDefinition.tagSet.")]
        public string obstacleTag;
        [Tooltip("Required slots are always filled; optional slots are rolled against the spawn weight.")]
        public bool required = true;
        [Tooltip("Probability an optional slot is filled. Ignored when required is true.")]
        [Range(0f, 1f)] public float spawnChance = 1f;
    }

    /// <summary>
    /// A hand-authored track segment (TECH_DOC §5.4, ADR-0005). Selection is data-driven: the generator
    /// filters by world tag, difficulty band and lane masks, then weights the survivors.
    /// </summary>
    [CreateAssetMenu(fileName = "ChunkDefinition", menuName = "Skyrift/Track/Chunk")]
    public sealed class ChunkDefinition : DefinitionBase
    {
        [Tooltip("Addressables key of the chunk prefab. M1 resolves it through the local catalog.")]
        public string prefabAddress;

        [Tooltip("Pool key for this chunk's prefab. Must exist in the PoolCatalog.")]
        public string poolKey;

        [Tooltip("World tags this chunk belongs to. Intersected with WorldDefinition.chunkTagFilter.")]
        public string[] worldTags = Array.Empty<string>();

        [Min(1f)] public float lengthMetres = 40f;

        [Tooltip("Difficulty band (0..1) this chunk is eligible for.")]
        public Vector2 difficultyBand = new Vector2(0f, 1f);

        [Tooltip("Bitmask of lanes the player may be in when entering this chunk.")]
        public int entryLaneMask = 0b111;

        [Tooltip("Bitmask of lanes the player may be in when leaving this chunk.")]
        public int exitLaneMask = 0b111;

        [Min(0.01f)] public float baseWeight = 1f;
        [Min(0)] public int cooldownChunks;
        [Tooltip("Trivially traversable. At least one per world is mandatory (generator fallback).")]
        public bool safeFallback;

        public ObstacleSlotData[] obstacleSlots = Array.Empty<ObstacleSlotData>();

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(ChunkDefinition);

            if (string.IsNullOrEmpty(poolKey)) report.Error(t, Id, "pool_key", "poolKey is required.");
            if (lengthMetres <= 0f) report.Error(t, Id, "length", "lengthMetres must be > 0.");
            if (entryLaneMask == 0) report.Error(t, Id, "entry_mask", "entryLaneMask must be non-zero.");
            if (exitLaneMask == 0) report.Error(t, Id, "exit_mask", "exitLaneMask must be non-zero.");
            if (difficultyBand.x > difficultyBand.y) report.Error(t, Id, "band", "difficultyBand.x must be <= .y.");
            if (worldTags == null || worldTags.Length == 0) report.Error(t, Id, "world_tags", "At least one world tag is required.");
            if (baseWeight <= 0f) report.Error(t, Id, "weight", "baseWeight must be > 0.");

            if (obstacleSlots == null) return;
            for (int i = 0; i < obstacleSlots.Length; i++)
            {
                ObstacleSlotData slot = obstacleSlots[i];
                if (slot == null) { report.Error(t, Id, "slot_null", $"obstacleSlots[{i}] is null."); continue; }
                if (string.IsNullOrEmpty(slot.obstacleTag)) report.Error(t, Id, "slot_tag", $"obstacleSlots[{i}].obstacleTag is empty.");
                if (slot.zOffset < 0f || slot.zOffset > lengthMetres) report.Error(t, Id, "slot_z", $"obstacleSlots[{i}].zOffset is outside the chunk.");
            }
        }
    }
}
