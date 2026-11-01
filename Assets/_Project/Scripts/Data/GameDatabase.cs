using System.Collections.Generic;
using Skyrift.Core;
using Skyrift.Core.Ids;

namespace Skyrift.Data
{
    /// <summary>Read-only, id-indexed access to one content type (TECH_DOC §4.20).</summary>
    public interface IRepository<TDefinition> where TDefinition : class, IIdentifiable
    {
        bool TryGet(StableId id, out TDefinition definition);
        IReadOnlyList<TDefinition> All { get; }
        int Count { get; }
    }

    /// <summary>Dictionary-backed repository. Built once at boot; lookups are allocation-free.</summary>
    public sealed class Repository<TDefinition> : IRepository<TDefinition> where TDefinition : class, IIdentifiable
    {
        private readonly Dictionary<StableId, TDefinition> _byId = new Dictionary<StableId, TDefinition>();
        private readonly List<TDefinition> _all = new List<TDefinition>();

        public IReadOnlyList<TDefinition> All => _all;
        public int Count => _all.Count;

        public Repository(IEnumerable<TDefinition> definitions, ValidationReport report)
        {
            if (definitions == null) return;

            foreach (TDefinition def in definitions)
            {
                if (def == null)
                {
                    report?.Warn(typeof(TDefinition).Name, "-", "null_entry", "Null definition skipped.");
                    continue;
                }

                if (!def.Id.IsValid)
                {
                    report?.Error(typeof(TDefinition).Name, def.Id.Value, "id_format", "Invalid id; entry dropped.");
                    continue;
                }

                if (_byId.ContainsKey(def.Id))
                {
                    report?.Error(typeof(TDefinition).Name, def.Id.Value, "id_unique", "Duplicate id; second entry dropped.");
                    continue;
                }

                if (def is IValidatable validatable) validatable.Validate(report);
                _byId.Add(def.Id, def);
                _all.Add(def);
            }
        }

        public bool TryGet(StableId id, out TDefinition definition) => _byId.TryGetValue(id, out definition);
    }

    /// <summary>
    /// The validated index of all content. Consumers ask for a repository, never for an asset path.
    /// In PROD an invalid entry is dropped and reported; in DEV the editor build script blocks first.
    /// </summary>
    public interface IGameDatabase
    {
        IRepository<WorldDefinition> Worlds { get; }
        IRepository<ChunkDefinition> Chunks { get; }
        IRepository<ObstacleDefinition> Obstacles { get; }
        CatalogManifest Manifest { get; }
        BalanceConfig Balance { get; }
        TrackGenerationConfig TrackGeneration { get; }
        InputConfig Input { get; }
        ScoringConfig Scoring { get; }
        PoolCatalog Pools { get; }
        ContinueConfig Continue { get; }
        AudioConfig Audio { get; }
        bool IsValid { get; }
    }

    /// <inheritdoc cref="IGameDatabase"/>
    public sealed class GameDatabase : IGameDatabase
    {
        public IRepository<WorldDefinition> Worlds { get; }
        public IRepository<ChunkDefinition> Chunks { get; }
        public IRepository<ObstacleDefinition> Obstacles { get; }
        public CatalogManifest Manifest { get; }
        public BalanceConfig Balance => Manifest.balance;
        public TrackGenerationConfig TrackGeneration => Manifest.trackGeneration;
        public InputConfig Input => Manifest.input;
        public ScoringConfig Scoring => Manifest.scoring;
        public PoolCatalog Pools => Manifest.poolCatalog;
        public ContinueConfig Continue => Manifest.continueConfig;
        public AudioConfig Audio => Manifest.audio;
        public bool IsValid { get; }

        public GameDatabase(CatalogManifest manifest, ValidationReport report)
        {
            Manifest = manifest;
            Worlds = new Repository<WorldDefinition>(manifest?.worlds, report);
            Chunks = new Repository<ChunkDefinition>(manifest?.chunks, report);
            Obstacles = new Repository<ObstacleDefinition>(manifest?.obstacles, report);

            manifest?.Validate(report);
            IsValid = report != null && report.IsValid;
        }

        /// <summary>Cross-reference rules from TECH_DOC §5.14 that need more than one repository.</summary>
        public Result ValidateCrossReferences(ValidationReport report)
        {
            const string t = nameof(GameDatabase);

            for (int w = 0; w < Worlds.Count; w++)
            {
                WorldDefinition world = Worlds.All[w];
                bool hasFallback = false;
                bool hasAnyChunk = false;

                for (int c = 0; c < Chunks.Count; c++)
                {
                    ChunkDefinition chunk = Chunks.All[c];
                    if (!SharesTag(world.chunkTagFilter, chunk.worldTags)) continue;

                    hasAnyChunk = true;
                    if (chunk.safeFallback) hasFallback = true;

                    if (!string.IsNullOrEmpty(chunk.poolKey) && !PoolHasKey(chunk.poolKey))
                        report.Error(t, chunk.Id, "pool_ref", $"Chunk pool key '{chunk.poolKey}' is not in the PoolCatalog.");

                    if (chunk.obstacleSlots != null)
                    {
                        for (int s = 0; s < chunk.obstacleSlots.Length; s++)
                        {
                            ObstacleSlotData slot = chunk.obstacleSlots[s];
                            if (slot == null || string.IsNullOrEmpty(slot.obstacleTag)) continue;
                            if (!AnyObstacleWithTag(slot.obstacleTag))
                                report.Error(t, chunk.Id, "obstacle_ref", $"No obstacle matches tag '{slot.obstacleTag}'.");
                        }
                    }
                }

                if (!hasAnyChunk) report.Error(t, world.Id, "world_chunks", "World has no eligible chunks.");
                if (!hasFallback) report.Error(t, world.Id, "world_fallback", "World has no safeFallback chunk.");
            }

            // Obstacle → pool references are checked here rather than in ObstacleDefinition.Validate,
            // because a single definition cannot see the PoolCatalog.
            for (int o = 0; o < Obstacles.Count; o++)
            {
                ObstacleDefinition obstacle = Obstacles.All[o];
                if (!string.IsNullOrEmpty(obstacle.poolKey) && !PoolHasKey(obstacle.poolKey))
                    report.Error(t, obstacle.Id, "pool_ref", $"Obstacle pool key '{obstacle.poolKey}' is not in the PoolCatalog.");
            }

            if (Audio != null && !PoolHasKey(Audio.voicePoolKey))
                report.Error(t, Audio.Id, "pool_ref", $"Audio voice pool '{Audio.voicePoolKey}' is not in the PoolCatalog.");

            return report.IsValid ? Result.Ok() : Result.Fail(ErrorCodes.CatalogInvalid, $"{report.ErrorCount} catalog error(s).");
        }

        private bool PoolHasKey(string key)
        {
            if (Pools == null) return false;
            for (int i = 0; i < Pools.entries.Count; i++)
                if (Pools.entries[i] != null && Pools.entries[i].key == key) return true;
            return false;
        }

        private bool AnyObstacleWithTag(string tag)
        {
            for (int i = 0; i < Obstacles.Count; i++)
            {
                string[] tags = Obstacles.All[i].tagSet;
                if (tags == null) continue;
                for (int j = 0; j < tags.Length; j++) if (tags[j] == tag) return true;
            }
            return false;
        }

        private static bool SharesTag(string[] a, string[] b)
        {
            if (a == null || b == null) return false;
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (a[i] == b[j]) return true;
            return false;
        }
    }
}
