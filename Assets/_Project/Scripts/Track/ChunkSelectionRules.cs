using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Track
{
    /// <summary>
    /// The built-in eligibility filters (TECH_DOC §4.5 step 2). They are ordinary
    /// <see cref="IChunkEligibilityRule"/> implementations, not special cases inside the generator, so a
    /// project can remove or replace any of them at composition time without editing generation code.
    /// All are stateless and allocation-free.
    /// </summary>
    public static class ChunkEligibilityRules
    {
        /// <summary>Candidate must belong to the active world (tag intersection).</summary>
        public sealed class WorldTag : IChunkEligibilityRule
        {
            public bool IsEligible(in ChunkCandidateContext ctx)
            {
                string[] filter = ctx.World != null ? ctx.World.chunkTagFilter : null;
                string[] tags = ctx.Candidate.worldTags;
                if (filter == null || tags == null) return false;

                for (int i = 0; i < filter.Length; i++)
                    for (int j = 0; j < tags.Length; j++)
                        if (filter[i] == tags[j]) return true;

                return false;
            }
        }

        /// <summary>The target difficulty must fall inside the candidate's authored band.</summary>
        public sealed class DifficultyBand : IChunkEligibilityRule
        {
            public bool IsEligible(in ChunkCandidateContext ctx)
            {
                Vector2 band = ctx.Candidate.difficultyBand;
                return ctx.TargetDifficulty >= band.x && ctx.TargetDifficulty <= band.y;
            }
        }

        /// <summary>
        /// At least one lane the player can legally be in on entry must be shared with the previous
        /// chunk's exit mask. This is what makes an endless sequence traversable rather than merely valid.
        /// </summary>
        public sealed class LaneContinuity : IChunkEligibilityRule
        {
            public bool IsEligible(in ChunkCandidateContext ctx)
            {
                return (ctx.Candidate.entryLaneMask & ctx.PreviousExitLaneMask) != 0;
            }
        }

        /// <summary>An authored per-chunk cooldown, counted in generated chunks rather than seconds.</summary>
        public sealed class Cooldown : IChunkEligibilityRule
        {
            public bool IsEligible(in ChunkCandidateContext ctx)
            {
                if (ctx.LastUsedSequence < 0) return true;
                return ctx.SequenceIndex - ctx.LastUsedSequence > ctx.Candidate.cooldownChunks;
            }
        }
    }

    /// <summary>
    /// The built-in weight contributions (TECH_DOC §4.5 step 3). Multiplied together with the authored
    /// base weight; a contribution of 0 vetoes the candidate.
    /// </summary>
    public static class ChunkSelectionWeights
    {
        /// <summary>The designer's authored preference for this chunk.</summary>
        public sealed class BaseWeight : IChunkSelectionWeight
        {
            public float Weight(in ChunkCandidateContext ctx) => ctx.Candidate.baseWeight;
        }

        /// <summary>
        /// Recently used chunks are less likely to repeat: <c>repeatPenalty ^ usesInWindow</c>.
        /// A penalty of 1 disables the effect, 0 makes an immediate repeat impossible.
        /// </summary>
        public sealed class RepeatPenalty : IChunkSelectionWeight
        {
            public float Weight(in ChunkCandidateContext ctx)
            {
                int uses = ctx.UsesInRepeatWindow;
                if (uses <= 0) return 1f;

                float penalty = ctx.Config != null ? ctx.Config.repeatPenalty : 1f;
                if (penalty >= 1f) return 1f;
                if (penalty <= 0f) return 0f;

                return Mathf.Pow(penalty, uses);
            }
        }

        /// <summary>
        /// Prefers chunks whose band centre sits near the target difficulty, so the ramp feels authored
        /// rather than random. Never vetoes: the floor comes from <see cref="TrackGenerationConfig"/>.
        /// </summary>
        public sealed class DifficultyFit : IChunkSelectionWeight
        {
            public float Weight(in ChunkCandidateContext ctx)
            {
                Vector2 band = ctx.Candidate.difficultyBand;
                float centre = (band.x + band.y) * 0.5f;
                float fit = 1f - Mathf.Abs(ctx.TargetDifficulty - centre);
                float floor = ctx.Config != null ? ctx.Config.difficultyFitFloor : 0.15f;
                return Mathf.Max(floor, fit);
            }
        }
    }
}