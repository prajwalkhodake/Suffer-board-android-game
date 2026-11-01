using Skyrift.Core.Events;
using Skyrift.Core.Ids;

namespace Skyrift.Track
{
    /// <summary>A chunk became live ahead of the player (TECH_DOC §4.5). Published once per spawn.</summary>
    public readonly struct ChunkActivatedEvent : IGameEvent
    {
        public readonly StableId ChunkId;
        public readonly int SequenceIndex;
        public readonly double StartDistance;
        public readonly float LengthMetres;

        public ChunkActivatedEvent(StableId chunkId, int sequenceIndex, double startDistance, float lengthMetres)
        {
            ChunkId = chunkId;
            SequenceIndex = sequenceIndex;
            StartDistance = startDistance;
            LengthMetres = lengthMetres;
        }
    }

    /// <summary>A chunk fully behind the player was returned to its pool.</summary>
    public readonly struct ChunkRecycledEvent : IGameEvent
    {
        public readonly StableId ChunkId;
        public readonly int SequenceIndex;

        public ChunkRecycledEvent(StableId chunkId, int sequenceIndex)
        {
            ChunkId = chunkId;
            SequenceIndex = sequenceIndex;
        }
    }

    /// <summary>
    /// No chunk passed eligibility/weighting and the guaranteed safe set was used instead
    /// (telemetry <c>track_generation_fallback</c>). A rising rate means the authored chunk pool is
    /// too narrow for the current difficulty band.
    /// </summary>
    public readonly struct TrackFallbackEvent : IGameEvent
    {
        public readonly int SequenceIndex;
        public readonly float TargetDifficulty;

        public TrackFallbackEvent(int sequenceIndex, float targetDifficulty)
        {
            SequenceIndex = sequenceIndex;
            TargetDifficulty = targetDifficulty;
        }
    }

    /// <summary>
    /// The world was re-based (TECH_DOC §4.6). Absolute-position bookkeeping must subtract
    /// <see cref="DeltaMetres"/>; run distance itself is unaffected.
    /// </summary>
    public readonly struct OriginShiftedEvent : IGameEvent
    {
        public readonly double NewOffset;
        public readonly double DeltaMetres;

        public OriginShiftedEvent(double newOffset, double deltaMetres)
        {
            NewOffset = newOffset;
            DeltaMetres = deltaMetres;
        }
    }
}