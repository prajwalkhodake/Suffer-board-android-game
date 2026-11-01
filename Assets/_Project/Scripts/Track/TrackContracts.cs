using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Core.Randomization;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Track
{
    /// <summary>
    /// A live track segment (TECH_DOC §4.5). A value type so the generator can keep an ordered list of
    /// them without allocating per frame. <see cref="StartDistance"/> is an absolute run distance in
    /// double precision; the world position is derived from it minus <c>IWorldOriginShifter.OriginOffset</c>.
    /// </summary>
    public readonly struct ChunkHandle
    {
        public readonly ChunkRoot Root;
        public readonly ChunkDefinition Definition;
        public readonly int SequenceIndex;
        public readonly double StartDistance;
        public readonly int EntryLaneMask;
        public readonly int ExitLaneMask;

        public ChunkHandle(ChunkRoot root, ChunkDefinition definition, int sequenceIndex, double startDistance,
            int entryLaneMask, int exitLaneMask)
        {
            Root = root;
            Definition = definition;
            SequenceIndex = sequenceIndex;
            StartDistance = startDistance;
            EntryLaneMask = entryLaneMask;
            ExitLaneMask = exitLaneMask;
        }

        public bool IsValid => Root != null && Definition != null;
        public StableId Id => Definition != null ? Definition.Id : StableId.None;
        public float LengthMetres => Definition != null ? Definition.lengthMetres : 0f;
        public double EndDistance => StartDistance + LengthMetres;

        /// <summary>True while the player's run distance is inside this chunk's span.</summary>
        public bool Contains(float playerDistance) => playerDistance >= StartDistance && playerDistance < EndDistance;
    }

    /// <summary>Allocation-free snapshot for the debug HUD and for generator tests.</summary>
    public readonly struct TrackDebugState
    {
        public readonly int LiveChunks;
        public readonly int GeneratedCount;
        public readonly int RecycledCount;
        public readonly int FallbackCount;
        public readonly float TargetDifficulty;
        public readonly double HeadDistance;
        public readonly StableId LastChunkId;

        public TrackDebugState(int liveChunks, int generatedCount, int recycledCount, int fallbackCount,
            float targetDifficulty, double headDistance, StableId lastChunkId)
        {
            LiveChunks = liveChunks;
            GeneratedCount = generatedCount;
            RecycledCount = recycledCount;
            FallbackCount = fallbackCount;
            TargetDifficulty = targetDifficulty;
            HeadDistance = headDistance;
            LastChunkId = lastChunkId;
        }
    }

    /// <summary>
    /// Everything the generator needs for one run. Bundled into a context so <c>Initialize</c> stays a
    /// single call and adding a dependency never changes the signature (TECH_DOC §4.5).
    /// </summary>
    public readonly struct TrackRunContext
    {
        public readonly TrackGenerationConfig Config;
        public readonly WorldDefinition World;
        public readonly IGameDatabase Database;
        public readonly IRandomProvider Random;
        public readonly IPoolRegistry Pools;
        public readonly IEventBus Bus;
        public readonly ILog Log;
        public readonly IWorldOriginShifter Origin;
        public readonly Transform TrackRoot;

        /// <summary>Optional in M1: obstacles are placed by the Gameplay populator through this seam.</summary>
        public readonly IChunkPopulator Populator;

        public TrackRunContext(TrackGenerationConfig config, WorldDefinition world, IGameDatabase database,
            IRandomProvider random, IPoolRegistry pools, IEventBus bus, ILog log, IWorldOriginShifter origin,
            Transform trackRoot, IChunkPopulator populator)
        {
            Config = config;
            World = world;
            Database = database;
            Random = random;
            Pools = pools;
            Bus = bus;
            Log = log;
            Origin = origin;
            TrackRoot = trackRoot;
            Populator = populator;
        }
    }

    /// <summary>
    /// Everything a rule needs to judge one candidate chunk. Readonly so rules cannot mutate generation
    /// state, and passed <c>in</c> so evaluating a candidate never copies or allocates.
    /// </summary>
    public readonly struct ChunkCandidateContext
    {
        public readonly ChunkDefinition Candidate;

        /// <summary>Index of <see cref="Candidate"/> inside <c>IGameDatabase.Chunks.All</c>.</summary>
        public readonly int RepositoryIndex;

        public readonly WorldDefinition World;
        public readonly TrackGenerationConfig Config;
        public readonly BalanceConfig Balance;

        /// <summary>0..1 difficulty the next chunk should target.</summary>
        public readonly float TargetDifficulty;

        /// <summary>Lanes the player may legally be in when entering the candidate.</summary>
        public readonly int PreviousExitLaneMask;

        /// <summary>Sequence number the candidate would take if chosen.</summary>
        public readonly int SequenceIndex;

        /// <summary>Sequence number at which this candidate was last chosen, or -1 if never.</summary>
        public readonly int LastUsedSequence;

        /// <summary>How many times this candidate appears inside the configured repeat window.</summary>
        public readonly int UsesInRepeatWindow;

        public readonly float PlayerSpeed;

        public ChunkCandidateContext(ChunkDefinition candidate, int repositoryIndex, WorldDefinition world,
            TrackGenerationConfig config, BalanceConfig balance, float targetDifficulty, int previousExitLaneMask,
            int sequenceIndex, int lastUsedSequence, int usesInRepeatWindow, float playerSpeed)
        {
            Candidate = candidate;
            RepositoryIndex = repositoryIndex;
            World = world;
            Config = config;
            Balance = balance;
            TargetDifficulty = targetDifficulty;
            PreviousExitLaneMask = previousExitLaneMask;
            SequenceIndex = sequenceIndex;
            LastUsedSequence = lastUsedSequence;
            UsesInRepeatWindow = usesInRepeatWindow;
            PlayerSpeed = playerSpeed;
        }
    }

    /// <summary>
    /// Hard filter on a candidate (TECH_DOC §4.5 extension point). Implementations must be stateless and
    /// allocation-free; they are registered once at composition time.
    /// </summary>
    public interface IChunkEligibilityRule
    {
        bool IsEligible(in ChunkCandidateContext ctx);
    }

    /// <summary>
    /// Multiplicative weight contribution (TECH_DOC §4.5 extension point). Returning 0 vetoes the
    /// candidate; returning 1 is a no-op. Contributions are multiplied, never added.
    /// </summary>
    public interface IChunkSelectionWeight
    {
        float Weight(in ChunkCandidateContext ctx);
    }

    /// <summary>
    /// Fills a freshly spawned chunk with pooled content and empties it before recycling
    /// (TECH_DOC §4.5 extension point). Implemented in Gameplay so Track never references obstacles,
    /// collectibles or any other gameplay type — the dependency arrow stays Track ← Gameplay.
    /// </summary>
    public interface IChunkPopulator
    {
        void Populate(in ChunkHandle chunk, IRandomProvider random);
        void Clear(in ChunkHandle chunk);
    }

    /// <summary>
    /// Endless, fair, procedurally varied track from hand-authored chunks (TECH_DOC §4.5).
    /// Selection is data-driven; the generator holds no gameplay logic and never calls Instantiate.
    /// </summary>
    public interface ITrackGenerator
    {
        /// <summary>Starts a run. Releases any previously live chunks first, so it is safe to call twice.</summary>
        void Initialize(in TrackRunContext ctx);

        /// <summary>
        /// Returns an already-generated chunk <paramref name="lookahead"/> positions ahead of the one the
        /// player is in (1 = the next chunk). Returns <c>default</c> when it has not been generated yet;
        /// this is a query and never mutates the track.
        /// </summary>
        ChunkHandle PeekNext(int lookahead = 1);

        /// <summary>Generates until <c>chunksAhead</c> chunks exist beyond the player. Allocation-free.</summary>
        void EnsureCoverage(float playerDistance);

        /// <summary>Returns chunks fully behind the player, keeping <c>chunksBehind</c> of them. Allocation-free.</summary>
        void Recycle(float playerDistance);

        /// <summary>Current player speed, used by the difficulty model. Pushed by the run controller.</summary>
        void SetPlayerSpeed(float metresPerSecond);

        /// <summary>
        /// Run-scoped difficulty scalar from <c>RunStartContext.DifficultyScale</c> (Remote Config / event
        /// rules). Pushed rather than written into the config asset, because that asset is shared by every
        /// run and must never be mutated at runtime. 1 = authored ramp. Reset to 1 by <see cref="Initialize"/>.
        /// </summary>
        void SetDifficultyScale(float scale);

        /// <summary>Releases every live chunk. Called on run end and on shutdown.</summary>
        void Shutdown();

        TrackDebugState Debug { get; }
        int LiveCount { get; }

        /// <summary>Ordered live chunks, oldest first. Index access is allocation-free.</summary>
        ChunkHandle GetLive(int index);

        void AddEligibilityRule(IChunkEligibilityRule rule);
        void AddSelectionWeight(IChunkSelectionWeight weight);
    }
}