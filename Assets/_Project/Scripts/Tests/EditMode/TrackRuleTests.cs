using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Data;
using Skyrift.Track;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Tests.Track
{
    /// <summary>
    /// Chunk selection is the generator's only gameplay decision, and it is entirely data (TECH_DOC §4.5).
    /// The rules and weights are stateless classes over a readonly context, so they can be pinned here
    /// without a scene, a pool or a prefab — which is exactly the reason the pipeline was built as rules
    /// rather than as branches inside <see cref="TrackGenerator"/>.
    ///
    /// The reachability fixture is the important one: it is the only automated proof that an authored chunk
    /// set can be walked forever. <see cref="Editor.CatalogValidator"/> checks the same corpus for spacing
    /// and pool bounds, but it never chains chunks into a sequence, so a set that passes validation can
    /// still dead-end at a difficulty the ramp will certainly reach.
    /// </summary>
    [TestFixture]
    public class TrackRuleTests
    {
        private static readonly List<Object> Tracked = new List<Object>(32);

        protected static T Create<T>(string id) where T : DefinitionBase
        {
            T definition = ScriptableObject.CreateInstance<T>();
            definition.name = id;

            SerializedObject serialized = new SerializedObject(definition);
            serialized.FindProperty("id").stringValue = id;
            serialized.FindProperty("displayNameKey").stringValue = "skyrift.test." + id;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            Tracked.Add(definition);
            return definition;
        }

        protected static T Track<T>(T instance) where T : Object
        {
            if (instance != null) Tracked.Add(instance);
            return instance;
        }

        protected static void DestroyTracked()
        {
            for (int i = 0; i < Tracked.Count; i++)
                if (Tracked[i] != null) Object.DestroyImmediate(Tracked[i]);
            Tracked.Clear();
        }

        /// <summary>
        /// A chunk authored the way the greybox authors them: masks wide open unless the test narrows them,
        /// a real pool key, and a length that is a multiple of nothing in particular.
        /// </summary>
        protected static ChunkDefinition Chunk(string id, Vector2 band, int entryMask, int exitMask,
            string tag = "test", int cooldown = 0, float weight = 1f, bool fallback = false)
        {
            ChunkDefinition chunk = Create<ChunkDefinition>(id);
            chunk.poolKey = "pool_chunk_test";
            chunk.prefabAddress = "chunks/" + id;
            chunk.worldTags = new[] { tag };
            chunk.lengthMetres = 40f;
            chunk.difficultyBand = band;
            chunk.entryLaneMask = entryMask;
            chunk.exitLaneMask = exitMask;
            chunk.cooldownChunks = cooldown;
            chunk.baseWeight = weight;
            chunk.safeFallback = fallback;
            return chunk;
        }

        protected static WorldDefinition World(string id, params string[] tags)
        {
            WorldDefinition world = Create<WorldDefinition>(id);
            world.chunkTagFilter = tags;
            return world;
        }

        /// <summary>One candidate evaluation, written out in full so each test reads as its own row.</summary>
        protected static ChunkCandidateContext Context(ChunkDefinition candidate, WorldDefinition world,
            TrackGenerationConfig config, BalanceConfig balance, float targetDifficulty, int previousExitMask,
            int sequenceIndex = 0, int lastUsedSequence = -1, int usesInRepeatWindow = 0, float playerSpeed = 20f)
        {
            return new ChunkCandidateContext(candidate, 0, world, config, balance, targetDifficulty,
                previousExitMask, sequenceIndex, lastUsedSequence, usesInRepeatWindow, playerSpeed);
        }

        // ── Hard filters ────────────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class EligibilityTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void WorldTag_RejectsAChunkFromAnotherWorld_AndAnUnassignedWorld()
            {
                ChunkDefinition rooftop = Chunk("chunk_rooftop", new Vector2(0f, 1f), 0b111, 0b111, "rooftop");
                WorldDefinition world = World("world_rooftop", "rooftop");
                ChunkDefinition other = Chunk("chunk_sewer", new Vector2(0f, 1f), 0b111, 0b111, "sewer");
                WorldDefinition empty = World("world_empty");

                ChunkEligibilityRules.WorldTag rule = new ChunkEligibilityRules.WorldTag();

                Assert.That(rule.IsEligible(in Context(rooftop, world, null, null, 0f, 0b111)), Is.True);
                Assert.That(rule.IsEligible(in Context(other, world, null, null, 0f, 0b111)), Is.False,
                    "a sewer layout must never appear on the rooftops");

                // No world at all is not "everything is allowed": the generator would otherwise happily mix
                // every corpus in the catalog into an unthemed track.
                Assert.That(rule.IsEligible(in Context(rooftop, null, null, null, 0f, 0b111)), Is.False);
                Assert.That(rule.IsEligible(in Context(rooftop, empty, null, null, 0f, 0b111)), Is.False,
                    "a world with an empty tag filter owns no chunk, which validation reports as world_chunks");
            }

            [Test]
            public void DifficultyBand_IsInclusiveAtBothEdges()
            {
                ChunkDefinition mid = Chunk("chunk_mid", new Vector2(0.25f, 0.75f), 0b111, 0b111);
                ChunkEligibilityRules.DifficultyBand rule = new ChunkEligibilityRules.DifficultyBand();

                Assert.That(rule.IsEligible(in Context(mid, null, null, null, 0.25f, 0b111)), Is.True, "band.x is inclusive");
                Assert.That(rule.IsEligible(in Context(mid, null, null, null, 0.75f, 0b111)), Is.True, "band.y is inclusive");
                Assert.That(rule.IsEligible(in Context(mid, null, null, null, 0.24f, 0b111)), Is.False);
                Assert.That(rule.IsEligible(in Context(mid, null, null, null, 0.76f, 0b111)), Is.False);

                // The ramp reaches 1 exactly, so a chunk that does not own the top of the range cannot be
                // the late-run content — and a chunk authored (0,1) is eligible everywhere.
                ChunkDefinition any = Chunk("chunk_any", new Vector2(0f, 1f), 0b111, 0b111);
                Assert.That(rule.IsEligible(in Context(any, null, null, null, 1f, 0b111)), Is.True);
                Assert.That(rule.IsEligible(in Context(any, null, null, null, 0f, 0b111)), Is.True);
            }

            [Test]
            public void LaneContinuity_NeedsOneSharedLaneWithThePreviousExit()
            {
                ChunkEligibilityRules.LaneContinuity rule = new ChunkEligibilityRules.LaneContinuity();

                ChunkDefinition shared = Chunk("chunk_shared", new Vector2(0f, 1f), 0b101, 0b111);
                Assert.That(rule.IsEligible(in Context(shared, null, null, null, 0f, 0b001)), Is.True,
                    "lane 0 is legal on both sides");
                Assert.That(rule.IsEligible(in Context(shared, null, null, null, 0f, 0b010)), Is.False,
                    "the previous chunk exits only through the middle lane, which this one blocks on entry");

                // A zero mask can never intersect, which is why ChunkDefinition.Validate rejects it outright.
                ChunkDefinition sealed_ = Chunk("chunk_sealed", new Vector2(0f, 1f), 0b000, 0b111);
                Assert.That(rule.IsEligible(in Context(sealed_, null, null, null, 0f, 0b111)), Is.False);
            }

            [Test]
            public void Cooldown_IsInGeneratedChunks_AndIsStrictlyGreaterThan()
            {
                ChunkEligibilityRules.Cooldown rule = new ChunkEligibilityRules.Cooldown();
                ChunkDefinition cooled = Chunk("chunk_cooled", new Vector2(0f, 1f), 0b111, 0b111, cooldown: 2);
                ChunkDefinition fresh = Chunk("chunk_fresh", new Vector2(0f, 1f), 0b111, 0b111, cooldown: 0);

                // Never used: the sequence index would be meaningless, so -1 short-circuits (§4.5).
                Assert.That(rule.IsEligible(in Context(cooled, null, null, null, 0f, 0b111,
                    sequenceIndex: 0, lastUsedSequence: -1)), Is.True);

                // The comparison is >, not >=, so cooldownChunks = 2 means "at least three chunks apart".
                Assert.That(rule.IsEligible(in Context(cooled, null, null, null, 0f, 0b111,
                    sequenceIndex: 2, lastUsedSequence: 0)), Is.False, "gap 2 is still inside a cooldown of 2");
                Assert.That(rule.IsEligible(in Context(cooled, null, null, null, 0f, 0b111,
                    sequenceIndex: 3, lastUsedSequence: 0)), Is.True);

                // cooldownChunks = 0 is the authored "may repeat back to back", which is what every greybox
                // chunk uses; the anti-repeat behaviour comes from the RepeatPenalty weight, not from here.
                Assert.That(rule.IsEligible(in Context(fresh, null, null, null, 0f, 0b111,
                    sequenceIndex: 1, lastUsedSequence: 0)), Is.True);
            }
        }

        // ── Soft weighting ──────────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class WeightTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void BaseWeight_IsTheAuthoredPreference_AndZeroVetoes()
            {
                ChunkSelectionWeights.BaseWeight rule = new ChunkSelectionWeights.BaseWeight();

                Assert.That(rule.Weight(in Context(Chunk("chunk_a", new Vector2(0f, 1f), 0b111, 0b111, weight: 3f),
                    null, null, null, 0f, 0b111)), Is.EqualTo(3f));

                // A designer parking a chunk out of rotation sets the weight rather than deleting the asset,
                // so 0 must reach the generator as a veto (§4.5 step 3: contributions are multiplied).
                Assert.That(rule.Weight(in Context(Chunk("chunk_b", new Vector2(0f, 1f), 0b111, 0b111, weight: 0.01f),
                    null, null, null, 0f, 0b111)), Is.GreaterThan(0f));
            }

            [Test]
            public void RepeatPenalty_IsAnExponentOfUses_AndBothEndsAreSpecial()
            {
                ChunkSelectionWeights.RepeatPenalty rule = new ChunkSelectionWeights.RepeatPenalty();
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_repeat");
                config.repeatPenalty = 0.5f;
                ChunkDefinition chunk = Chunk("chunk_repeat", new Vector2(0f, 1f), 0b111, 0b111);

                Assert.That(rule.Weight(in Context(chunk, null, config, null, 0f, 0b111, usesInRepeatWindow: 0)),
                    Is.EqualTo(1f), "an unused candidate is not penalised");
                Assert.That(rule.Weight(in Context(chunk, null, config, null, 0f, 0b111, usesInRepeatWindow: 1)),
                    Is.EqualTo(0.5f).Within(1e-5f));
                Assert.That(rule.Weight(in Context(chunk, null, config, null, 0f, 0b111, usesInRepeatWindow: 3)),
                    Is.EqualTo(0.125f).Within(1e-5f), "penalty ^ uses, so the third use is heavily disfavoured");

                config.repeatPenalty = 1f;
                Assert.That(rule.Weight(in Context(chunk, null, config, null, 0f, 0b111, usesInRepeatWindow: 4)),
                    Is.EqualTo(1f), "a penalty of 1 disables the effect, which is an authored choice");

                config.repeatPenalty = 0f;
                Assert.That(rule.Weight(in Context(chunk, null, config, null, 0f, 0b111, usesInRepeatWindow: 1)),
                    Is.EqualTo(0f), "0 makes an immediate repeat impossible — a veto, not a small weight");

                // A missing config must not silently become "never repeat": the neutral value is 1.
                Assert.That(rule.Weight(in Context(chunk, null, null, null, 0f, 0b111, usesInRepeatWindow: 2)),
                    Is.EqualTo(1f));
            }

            [Test]
            public void DifficultyFit_PrefersTheBandCentre_ButNeverVetoes()
            {
                ChunkSelectionWeights.DifficultyFit rule = new ChunkSelectionWeights.DifficultyFit();
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_fit");
                config.difficultyFitFloor = 0.15f;

                ChunkDefinition mid = Chunk("chunk_fit", new Vector2(0.4f, 0.6f), 0b111, 0b111);

                Assert.That(rule.Weight(in Context(mid, null, config, null, 0.5f, 0b111)),
                    Is.EqualTo(1f).Within(1e-5f), "target on the band centre is a perfect fit");
                Assert.That(rule.Weight(in Context(mid, null, config, null, 0.6f, 0b111)),
                    Is.EqualTo(0.9f).Within(1e-5f));

                // The far end would mathematically reach 0 (a veto), which is why the floor exists: a chunk
                // eligible by DifficultyBand must stay pickable, or a thin corpus would dead-end the ramp.
                Assert.That(rule.Weight(in Context(mid, null, config, null, 1f, 0b111)),
                    Is.EqualTo(0.15f).Within(1e-5f), "clamped to difficultyFitFloor");

                config.difficultyFitFloor = 0.5f;
                Assert.That(rule.Weight(in Context(mid, null, config, null, 1f, 0b111)),
                    Is.EqualTo(0.5f).Within(1e-5f), "the floor is data, so a sparse world can flatten the curve");

                Assert.That(rule.Weight(in Context(mid, null, null, null, 1f, 0b111)),
                    Is.GreaterThan(0f), "no config still leaves a positive weight");
            }
        }

        // ── Reachability: can the authored set be walked forever? ───────────────────────────────────────

        [TestFixture]
        public class ReachabilityTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            private static readonly IChunkEligibilityRule[] Rules =
            {
                new ChunkEligibilityRules.WorldTag(),
                new ChunkEligibilityRules.DifficultyBand(),
                new ChunkEligibilityRules.LaneContinuity(),
                new ChunkEligibilityRules.Cooldown()
            };

            private static readonly IChunkSelectionWeight[] Weights =
            {
                new ChunkSelectionWeights.BaseWeight(),
                new ChunkSelectionWeights.RepeatPenalty(),
                new ChunkSelectionWeights.DifficultyFit()
            };

            /// <summary>
            /// Counts the candidates the generator would see at one step. This is deliberately the same
            /// conjunction as <c>TrackGenerator.GenerateNext</c> — every rule must pass and the multiplied
            /// weight must be positive — because a walk through a subset of the rules would prove nothing
            /// about the real selection. If the generator gains a built-in rule, this list must gain it too;
            /// the coupling is stated rather than hidden.
            /// </summary>
            private static int CountCandidates(IReadOnlyList<ChunkDefinition> chunks, WorldDefinition world,
                TrackGenerationConfig config, BalanceConfig balance, float target, int previousExitMask,
                int sequence, int[] lastUsed)
            {
                int count = 0;
                for (int i = 0; i < chunks.Count; i++)
                {
                    ChunkCandidateContext ctx = new ChunkCandidateContext(chunks[i], i, world, config, balance,
                        target, previousExitMask, sequence, lastUsed[i], 0, 20f);

                    bool eligible = true;
                    for (int r = 0; r < Rules.Length; r++)
                    {
                        if (Rules[r].IsEligible(in ctx)) continue;
                        eligible = false;
                        break;
                    }
                    if (!eligible) continue;

                    float weight = 1f;
                    for (int w = 0; w < Weights.Length; w++)
                    {
                        weight *= Weights[w].Weight(in ctx);
                        if (weight <= 0f) break;
                    }
                    if (weight > 0f) count++;
                }
                return count;
            }

            /// <summary>Lowest-index candidate that passes every rule, or -1. Rules only, ignoring weight.</summary>
            private static int FirstEligible(IReadOnlyList<ChunkDefinition> chunks, WorldDefinition world,
                TrackGenerationConfig config, BalanceConfig balance, float target, int previousExitMask,
                int sequence, int[] lastUsed)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    ChunkCandidateContext ctx = new ChunkCandidateContext(chunks[i], i, world, config, balance,
                        target, previousExitMask, sequence, lastUsed[i], 0, 20f);

                    bool eligible = true;
                    for (int r = 0; r < Rules.Length; r++)
                    {
                        if (Rules[r].IsEligible(in ctx)) continue;
                        eligible = false;
                        break;
                    }
                    if (eligible) return i;
                }
                return -1;
            }

            /// <summary>
            /// Walks the corpus the way a run does: the ramp climbs, the previous chunk's exit mask gates the
            /// next entry, and each chunk carries a cooldown. Fails the moment no candidate exists, naming the
            /// distance and difficulty at which the track would have stopped.
            ///
            /// The walk takes the <em>first</em> eligible chunk instead of the generator's weighted roll, so a
            /// failure is exactly reproducible and the harness itself is never the source of a flake. That
            /// makes this a smoke test of one legal sequence, not an exhaustive proof: a corpus can pass it and
            /// still be unlikely to chain, which is what the repeat penalty and the safeFallback branch exist
            /// to absorb at runtime (<see cref="TrackGenerator"/> ignores bands and cooldowns when it falls back).
            /// </summary>
            private static void AssertWalkable(List<ChunkDefinition> chunks, WorldDefinition world,
                TrackGenerationConfig config, BalanceConfig balance, int steps)
            {
                int[] lastUsed = new int[chunks.Count];
                for (int i = 0; i < lastUsed.Length; i++) lastUsed[i] = -1;

                int exitMask = 0b111;
                for (int sequence = 0; sequence < steps; sequence++)
                {
                    float target = Mathf.Clamp01(config.difficultyRampMetres > 0f
                        ? (sequence * 40f) / config.difficultyRampMetres
                        : 1f);

                    int available = CountCandidates(chunks, world, config, balance, target, exitMask, sequence, lastUsed);
                    Assert.That(available, Is.GreaterThan(0),
                        "dead end at sequence " + sequence + " (distance " + (sequence * 40f) + " m, difficulty " +
                        target.ToString("0.###") + ", entry mask " + System.Convert.ToString(exitMask, 2) + ")");

                    int chosen = FirstEligible(chunks, world, config, balance, target, exitMask, sequence, lastUsed);
                    Assert.That(chosen, Is.GreaterThanOrEqualTo(0));

                    exitMask = chunks[chosen].exitLaneMask;
                    lastUsed[chosen] = sequence;
                }
            }

            [Test]
            public void OverlappingBands_WithAFallback_AndWideMasks_NeverDeadEnd()
            {
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_walk_ok");
                BalanceConfig balance = Create<BalanceConfig>("cfg_walk_balance");
                WorldDefinition world = World("world_walk", "test");

                List<ChunkDefinition> chunks = new List<ChunkDefinition>
                {
                    Chunk("chunk_open", new Vector2(0f, 0.35f), 0b111, 0b111),
                    Chunk("chunk_lowmid", new Vector2(0.2f, 0.6f), 0b111, 0b111),
                    Chunk("chunk_highmid", new Vector2(0.5f, 0.85f), 0b111, 0b111),
                    Chunk("chunk_hard", new Vector2(0.7f, 1f), 0b111, 0b111),
                    // The authored spine: one trivially traversable chunk covering the whole range is what
                    // §4.5 requires per world, and what makes the walk unconditional.
                    Chunk("chunk_safe", new Vector2(0f, 1f), 0b111, 0b111, fallback: true, cooldown: 1)
                };

                AssertWalkable(chunks, world, config, balance, 120);
            }

            [Test]
            public void A_HoleInTheDifficultyBands_IsCaught()
            {
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_walk_hole");
                // 40 m chunks over an 800 m ramp is a 0.05 step, so the walk lands exactly on the 0.45 in the
                // hole rather than jumping from 0.40 to 0.50 and missing it.
                config.difficultyRampMetres = 800f;
                BalanceConfig balance = Create<BalanceConfig>("cfg_walk_hole_balance");
                WorldDefinition world = World("world_hole", "test");

                // Two bands that do not overlap: nothing owns 0.41..0.49, and the ramp walks straight through it.
                List<ChunkDefinition> chunks = new List<ChunkDefinition>
                {
                    Chunk("chunk_easy_only", new Vector2(0f, 0.4f), 0b111, 0b111),
                    Chunk("chunk_hard_only", new Vector2(0.5f, 1f), 0b111, 0b111)
                };

                Assert.Throws<AssertionException>(() => AssertWalkable(chunks, world, config, balance, 120),
                    "the negative control: a band hole must stop the walk, or the test above proves nothing");
            }

            [Test]
            public void AMaskThatNothingReturnsFrom_IsCaught()
            {
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_walk_mask");
                config.difficultyRampMetres = 400f;
                BalanceConfig balance = Create<BalanceConfig>("cfg_walk_mask_balance");
                WorldDefinition world = World("world_mask", "test");

                // Bands cover the whole range and a fallback exists, so only the masks can strand the run:
                // the first chunk leaves through lane 0 alone, everything else can only be entered on the
                // right two lanes, and its own cooldown forbids re-entering it immediately.
                List<ChunkDefinition> chunks = new List<ChunkDefinition>
                {
                    Chunk("chunk_left_exit", new Vector2(0f, 1f), 0b001, 0b001, cooldown: 2),
                    Chunk("chunk_needs_right", new Vector2(0f, 1f), 0b110, 0b110),
                    Chunk("chunk_also_right", new Vector2(0f, 1f), 0b100, 0b100, fallback: true)
                };

                // The walk starts with every lane legal, so the pinhole chunk is itself reachable — and once
                // it has been used the run cannot continue. A corpus like this is a real authoring error: the
                // greybox avoids it by authoring every mask open (§4.5).
                Assert.Throws<AssertionException>(() => AssertWalkable(chunks, world, config, balance, 120),
                    "a one-lane exit followed by an all-blocked entry is the classic untraversable corpus");
            }

            [Test]
            public void CooldownsLongerThanTheCorpus_DeadEnd_AndTheValidatorWarnsAboutThat()
            {
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_walk_cool");
                config.difficultyRampMetres = 200f;
                BalanceConfig balance = Create<BalanceConfig>("cfg_walk_cool_balance");
                WorldDefinition world = World("world_cool", "test");

                List<ChunkDefinition> chunks = new List<ChunkDefinition>
                {
                    Chunk("chunk_a", new Vector2(0f, 1f), 0b111, 0b111, cooldown: 4),
                    Chunk("chunk_b", new Vector2(0f, 1f), 0b111, 0b111, cooldown: 4)
                };

                // Two chunks on a five-chunk cooldown: after four steps both are cooling and no rule can
                // invent content. The generator's answer is the safeFallback branch, which deliberately ignores
                // bands and cooldowns — and this corpus has no fallback chunk at all, which is precisely the
                // shape GameDatabase reports as "world_fallback" at boot (§4.5, §5.14).
                int[] lastUsed = { -1, -1 };
                Assert.That(CountCandidates(chunks, world, config, balance, 0.5f, 0b111, 4, lastUsed), Is.EqualTo(2));

                lastUsed[0] = 3;
                lastUsed[1] = 4;
                Assert.That(CountCandidates(chunks, world, config, balance, 0.5f, 0b111, 5, lastUsed), Is.EqualTo(0),
                    "sequence 5 is inside both cooldowns, so the fallback branch is the only way through");
            }

            [Test]
            public void ARuleSetAppliedToAnotherWorld_SeesNothing()
            {
                TrackGenerationConfig config = Create<TrackGenerationConfig>("cfg_walk_world");
                BalanceConfig balance = Create<BalanceConfig>("cfg_walk_world_balance");

                List<ChunkDefinition> chunks = new List<ChunkDefinition>
                {
                    Chunk("chunk_roof_a", new Vector2(0f, 1f), 0b111, 0b111, "rooftop"),
                    Chunk("chunk_roof_b", new Vector2(0f, 1f), 0b111, 0b111, "rooftop", fallback: true)
                };

                int[] lastUsed = { -1, -1 };
                Assert.That(CountCandidates(chunks, World("world_roof_x", "rooftop"), config, balance, 0.5f, 0b111, 0, lastUsed),
                    Is.EqualTo(2));
                Assert.That(CountCandidates(chunks, World("world_sewer_x", "sewer"), config, balance, 0.5f, 0b111, 0, lastUsed),
                    Is.EqualTo(0), "a world queries its own corpus, not the whole catalog");
            }
        }

        // ── The generator object without content ────────────────────────────────────────────────────────

        [TestFixture]
        public class GeneratorSafetyTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void AnIncompleteContext_LeavesTheGeneratorInertRatherThanThrowing()
            {
                // The composition root reports a failed boot from the step result, but the generator itself
                // must never throw across a module boundary (§4.1: a system failure is a logged error and an
                // inert object, so one bad wiring cannot take the whole frame down).
                TrackGenerator generator = new TrackGenerator();
                generator.Initialize(new TrackRunContext(
                    null, null, null, null, null, null, null, null, null, null));

                Assert.That(generator.LiveCount, Is.EqualTo(0));
                Assert.That(generator.PeekNext(1).IsValid, Is.False);

                Assert.That(() =>
                {
                    generator.EnsureCoverage(0f);
                    generator.Recycle(100f);
                    generator.SetPlayerSpeed(-5f);
                    generator.SetDifficultyScale(0f);
                    generator.Shutdown();
                    generator.Shutdown();
                }, Throws.Nothing);

                TrackDebugState state = generator.Debug;
                Assert.That(state.LiveChunks, Is.EqualTo(0));
                Assert.That(state.GeneratedCount, Is.EqualTo(0));
                Assert.That(state.RecycledCount, Is.EqualTo(0));
                Assert.That(state.FallbackCount, Is.EqualTo(0));
                Assert.That(state.LastChunkId.IsNone, Is.True, "the HUD reads this before the first chunk exists");
            }

            [Test]
            public void SpeedAndDifficultySetters_ClampTheValuesThatWouldBreakTheRamp()
            {
                TrackGenerator generator = new TrackGenerator();
                Assert.That(() =>
                {
                    generator.SetPlayerSpeed(float.NaN);
                    generator.SetPlayerSpeed(-100f);
                    generator.SetDifficultyScale(0f);
                    generator.SetDifficultyScale(-1f);
                    generator.Shutdown();
                }, Throws.Nothing, "these are called every frame by the run controller, so a bad scalar must not throw");
            }
        }

        // ── The handle the rest of the game reads ───────────────────────────────────────────────────────

        [TestFixture]
        public class ChunkHandleTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void DistanceWindows_AreHalfOpenSoOneChunkAlwaysOwnsABoundary()
            {
                ChunkDefinition definition = Chunk("chunk_handle", new Vector2(0f, 1f), 0b111, 0b111);
                ChunkHandle handle = new ChunkHandle(null, definition, 3, 100.0, 0b011, 0b110);

                Assert.That(handle.LengthMetres, Is.EqualTo(40f));
                Assert.That(handle.EndDistance, Is.EqualTo(140.0).Within(1e-6));
                Assert.That(handle.SequenceIndex, Is.EqualTo(3));
                Assert.That(handle.EntryLaneMask, Is.EqualTo(0b011));
                Assert.That(handle.ExitLaneMask, Is.EqualTo(0b110));
                Assert.That(handle.Id, Is.EqualTo(definition.Id), "the handle answers with the definition's identity (§5.1)");

                Assert.That(handle.Contains(100f), Is.True, "the start edge belongs to this chunk");
                Assert.That(handle.Contains(139.999f), Is.True);
                Assert.That(handle.Contains(140f), Is.False,
                    "and the end edge belongs to the next one, so the streaming window has no double owner");

                // A handle without a spawned root is not addressable: this is the state PeekNext returns when
                // the lookahead has not been generated yet, and callers must not touch it.
                Assert.That(handle.IsValid, Is.False);
                Assert.That(default(ChunkHandle).IsValid, Is.False);
                Assert.That(default(ChunkHandle).Id.IsNone, Is.True);
            }

            [Test]
            public void AHandleWithoutADefinition_DegradesToZeroInsteadOfThrowing()
            {
                // The debug HUD reads a default handle when the track is idle, so every accessor has to answer
                // rather than dereference a null definition.
                ChunkHandle empty = default;

                Assert.That(empty.LengthMetres, Is.EqualTo(0f));
                Assert.That(empty.EndDistance, Is.EqualTo(0.0));
                Assert.That(empty.Id.IsNone, Is.True);
                Assert.That(empty.Contains(0f), Is.False, "an empty chunk owns no distance, so it must not claim the start");
            }
        }
    }
}
