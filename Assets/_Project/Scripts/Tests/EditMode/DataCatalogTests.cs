using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Core;
using Skyrift.Data;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Tests.Data
{
    /// <summary>
    /// The data layer's own contracts: the balance maths every system reads, and the authoring rules that
    /// decide whether a catalog is usable at all (TECH_DOC §5.14, §12.1). These tests are the reason a
    /// content error can be a boot report instead of a wrong-feeling build.
    /// </summary>
    [TestFixture]
    public class DataCatalogTests
    {
        private static readonly List<Object> Tracked = new List<Object>(32);

        /// <summary>
        /// <c>id</c> and <c>displayNameKey</c> are private on <see cref="DefinitionBase"/> by design (§5.1:
        /// identity is not writable by consumers), so a test gives a definition identity the same way the
        /// editor tooling does — through the serialiser.
        /// </summary>
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

        /// <summary>
        /// Instances created outside this helper (arrays of definitions, the fixture's catalogs) still have
        /// to be released, or they survive into the next test in the same domain reload.
        /// </summary>
        protected static T Track<T>(T instance) where T : Object
        {
            if (instance != null) Tracked.Add(instance);
            return instance;
        }

        /// <summary>
        /// Called from every nested fixture's TearDown: tracking is static so the helper can be shared, and
        /// a leaked ScriptableObject is an id collision that only reproduces in a full run.
        /// </summary>
        protected static void DestroyTracked()
        {
            for (int i = 0; i < Tracked.Count; i++)
            {
                if (Tracked[i] != null) Object.DestroyImmediate(Tracked[i]);
            }
            Tracked.Clear();
        }

        protected static string[] RulesOf(ValidationReport report)
        {
            string[] rules = new string[report.Issues.Count];
            for (int i = 0; i < report.Issues.Count; i++) rules[i] = report.Issues[i].Rule;
            return rules;
        }

        protected static int CountOf(ValidationReport report, string rule)
        {
            int count = 0;
            for (int i = 0; i < report.Issues.Count; i++)
                if (report.Issues[i].Rule == rule) count++;
            return count;
        }

        /// <summary>An obstacle that passes its own rules, so a test can break exactly one thing else.</summary>
        protected static ObstacleDefinition ValidObstacle(string id)
        {
            ObstacleDefinition obstacle = Create<ObstacleDefinition>(id);
            obstacle.poolKey = "pool_" + id;
            obstacle.behaviourId = "std";
            obstacle.tagSet = new[] { "low" };
            obstacle.footprintMetres = new Vector2(2f, 1f);
            obstacle.clearance = new ClearanceProfile { jumpable = true, laneOccupancyMask = 0b001 };
            return obstacle;
        }

        // ── BalanceConfig maths ─────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class BalanceMathsTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void Gravity_And_JumpVelocity_MatchTheClosedFormArc()
            {
                // §4.4: the motor integrates a closed-form jump, and these two constants are what make a jump
                // of `jumpHeight` metres last `jumpAirTime` seconds. Asserted against the config's own values
                // rather than literals, so tuning the jump keeps the test green and only a formula change
                // fails it.
                BalanceConfig balance = Create<BalanceConfig>("cfg_balance_arc");

                Assert.That(balance.Gravity,
                    Is.EqualTo(8f * balance.jumpHeight / (balance.jumpAirTime * balance.jumpAirTime)).Within(1e-3f));
                Assert.That(balance.JumpVelocity,
                    Is.EqualTo(4f * balance.jumpHeight / balance.jumpAirTime).Within(1e-3f));

                // The pair is only correct together: apex height and air time are the two constraints the
                // formulas were solved against, so re-deriving one without the other breaks the jump.
                float half = balance.jumpAirTime * 0.5f;
                float apex = balance.JumpVelocity * half - 0.5f * balance.Gravity * half * half;
                float atLanding = balance.JumpVelocity * balance.jumpAirTime
                                  - 0.5f * balance.Gravity * balance.jumpAirTime * balance.jumpAirTime;

                Assert.That(apex, Is.EqualTo(balance.jumpHeight).Within(1e-2f));
                Assert.That(atLanding, Is.EqualTo(0f).Within(1e-2f), "the arc must close exactly on the air time");
            }

            [Test]
            public void SpeedAtDistance_IsTheCurveScaledAndCapped()
            {
                // Pinned to the code defaults (12 → 28 m/s over 2000 m, capped at 30) because these are the
                // numbers the level-design rules in CatalogValidator were derived against. Changing them is
                // allowed — but it must be a deliberate change to this test as well.
                BalanceConfig balance = Create<BalanceConfig>("cfg_balance_speed");

                Assert.That(balance.speedRampMetres, Is.EqualTo(2000f));
                Assert.That(balance.SpeedAtDistance(0f, 1f), Is.EqualTo(12f).Within(0.01f));
                Assert.That(balance.SpeedAtDistance(balance.speedRampMetres * 0.5f, 1f), Is.EqualTo(20f).Within(0.01f));
                Assert.That(balance.SpeedAtDistance(balance.speedRampMetres, 1f), Is.EqualTo(28f).Within(0.01f));

                // Past the ramp the curve parameter clamps, so distance cannot accelerate forever.
                Assert.That(balance.SpeedAtDistance(balance.speedRampMetres * 10f, 1f), Is.EqualTo(28f).Within(0.01f));

                // The cap scales with the Remote Config scalar too, otherwise a +100% event would break
                // through the ceiling the level design was validated against.
                Assert.That(balance.SpeedAtDistance(balance.speedRampMetres, 2f),
                    Is.EqualTo(balance.maxSpeed * 2f).Within(0.01f));
            }

            [Test]
            public void LaneX_IsSymmetricAroundTheTrackCentre()
            {
                BalanceConfig balance = Create<BalanceConfig>("cfg_balance_lanes");
                float centre = (balance.laneCount - 1) * 0.5f;

                for (int lane = 0; lane < balance.laneCount; lane++)
                {
                    Assert.That(balance.LaneX(lane), Is.EqualTo((lane - centre) * balance.laneWidth).Within(1e-4f));
                }

                Assert.That(balance.LaneX(balance.laneCount / 2), Is.EqualTo(0f),
                    "the middle lane of an odd lane count is the track centre, which is where the rig is authored");
                Assert.That(balance.LaneX(balance.laneCount - 1), Is.EqualTo(-balance.LaneX(0)).Within(1e-4f));
            }

            [Test]
            public void Validate_RejectsTheValuesThatWouldBreakTheModel()
            {
                BalanceConfig valid = Create<BalanceConfig>("cfg_balance_clean");
                ValidationReport clean = new ValidationReport();
                valid.Validate(clean);
                Assert.That(clean.IsValid, Is.True, "the code defaults must validate: " + string.Join(", ", RulesOf(clean)));

                BalanceConfig broken = Create<BalanceConfig>("cfg_balance_broken");
                broken.jumpAirTime = 0f;
                broken.laneCount = 1;
                broken.maxStumblesBeforeCrash = 0;
                broken.playerRollHeight = broken.playerStandHeight;
                broken.chaserNearDistance = broken.chaserFarDistance;
                broken.speedCurve = new AnimationCurve();

                ValidationReport report = new ValidationReport();
                broken.Validate(report);

                string[] rules = RulesOf(report);
                Assert.That(rules, Does.Contain("jump_airtime"));
                Assert.That(rules, Does.Contain("lane_count"));
                Assert.That(rules, Does.Contain("stumbles"));
                Assert.That(rules, Does.Contain("player_height"));
                Assert.That(rules, Does.Contain("chaser_distance"));
                Assert.That(rules, Does.Contain("speed_curve"));

                // A zero air time is not merely a bad number: Gravity and JumpVelocity divide by it, so the
                // jump becomes NaN and every clearance check silently passes. Validation is what stops that.
                Assert.That(float.IsNaN(broken.Gravity) || float.IsInfinity(broken.Gravity), Is.True);
            }
        }

        // ── Config rules that are contracts, not preferences ────────────────────────────────────────────

        [TestFixture]
        public class ConfigRulesTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void ScoringConfig_WeightFor_ReadsTheTableAndDefaultsToZero()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_scoring_table");
                Assert.That(scoring.WeightFor(CollectibleKind.Coin), Is.EqualTo(10f));
                Assert.That(scoring.WeightFor(CollectibleKind.Key), Is.EqualTo(50f));
                Assert.That(scoring.kindWeights, Has.Count.EqualTo(6), "one row per CollectibleKind (§5.6)");

                // Power-ups and boxes score nothing by design — their value is the effect, not the number.
                Assert.That(scoring.WeightFor(CollectibleKind.PowerUpPickup), Is.EqualTo(0f));

                // A kind with no row earns nothing rather than throwing: new kinds arrive in M2 and the
                // scoring table is allowed to lag one authoring pass behind them.
                scoring.kindWeights.Clear();
                Assert.That(scoring.WeightFor(CollectibleKind.Coin), Is.EqualTo(0f));
            }

            [Test]
            public void ScoringConfig_RejectsDuplicateAndNegativeWeights()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_scoring_dupes");
                scoring.kindWeights.Add(new KindScoreWeight { kind = CollectibleKind.Coin, weight = 3f });
                scoring.kindWeights.Add(new KindScoreWeight { kind = CollectibleKind.Gem, weight = -1f });
                scoring.comboMax = 0;

                ValidationReport report = new ValidationReport();
                scoring.Validate(report);

                string[] rules = RulesOf(report);
                Assert.That(rules, Does.Contain("kind_unique"), "a kind with two rows scores by whichever comes first");
                Assert.That(rules, Does.Contain("kind_weight"));
                Assert.That(rules, Does.Contain("combo_max"));
            }

            [Test]
            public void ContinueConfig_KeyCostFor_ClampsInsteadOfThrowing()
            {
                ContinueConfig config = Create<ContinueConfig>("cfg_continue_costs");
                config.keyCosts = new[] { 1, 2 };

                Assert.That(config.KeyCostFor(0), Is.EqualTo(1));
                Assert.That(config.KeyCostFor(1), Is.EqualTo(2));
                Assert.That(config.KeyCostFor(9), Is.EqualTo(2), "beyond the table the last price repeats");
                Assert.That(config.KeyCostFor(-3), Is.EqualTo(1), "and a negative index cannot escape the table");

                config.keyCosts = null;
                Assert.That(config.KeyCostFor(0), Is.EqualTo(0), "no table means free, which the policy then refuses");
            }

            [Test]
            public void ContinueConfig_EnabledWithoutCosts_IsAnError_DisabledIsNot()
            {
                ContinueConfig enabled = Create<ContinueConfig>("cfg_continue_enabled");
                enabled.enabled = true;
                enabled.keyCosts = new int[0];
                ValidationReport enabledReport = new ValidationReport();
                enabled.Validate(enabledReport);
                Assert.That(RulesOf(enabledReport), Does.Contain("key_costs"));

                // A disabled continue is a legitimate product state (this build offers none), so it must not
                // fail validation on rows the game never reads.
                ContinueConfig disabled = Create<ContinueConfig>("cfg_continue_disabled");
                disabled.enabled = false;
                disabled.keyCosts = new int[0];
                ValidationReport disabledReport = new ValidationReport();
                disabled.Validate(disabledReport);
                Assert.That(RulesOf(disabledReport), Has.No.Member("key_costs"));
            }

            [Test]
            public void TrackGenerationConfig_RejectsAWindowThatCannotStream()
            {
                TrackGenerationConfig track = Create<TrackGenerationConfig>("cfg_track_clean");
                ValidationReport clean = new ValidationReport();
                track.Validate(clean);
                Assert.That(clean.IsValid, Is.True, string.Join(", ", RulesOf(clean)));

                // chunksAhead 0 means EnsureCoverage never spawns anything: the run starts on empty track.
                track.chunksAhead = 0;
                track.originShiftThreshold = 10f;
                track.difficultyRampMetres = 0f;
                ValidationReport report = new ValidationReport();
                track.Validate(report);

                string[] rules = RulesOf(report);
                Assert.That(rules, Does.Contain("chunks_ahead"));
                Assert.That(rules, Does.Contain("origin"), "shifting every few metres costs more than it saves (§4.6)");
                Assert.That(rules, Does.Contain("ramp"));
            }

            [Test]
            public void InputConfig_GestureDurationsMustNotOverlap()
            {
                InputConfig input = Create<InputConfig>("cfg_input_clean");
                ValidationReport clean = new ValidationReport();
                input.Validate(clean);
                Assert.That(clean.IsValid, Is.True, string.Join(", ", RulesOf(clean)));

                // A tap is "released before swipeMaxDuration and moved less than tapMaxMovePercent"; if a
                // swipe could finish inside the tap window the same gesture is two intents.
                input.swipeMaxDuration = input.tapMaxDuration;
                input.bufferDepth = 0;
                ValidationReport report = new ValidationReport();
                input.Validate(report);

                Assert.That(RulesOf(report), Does.Contain("durations"));
                Assert.That(RulesOf(report), Does.Contain("buffer"));
            }
        }

        // ── Repository rules ────────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class RepositoryTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void InvalidId_IsDroppedWithAnError()
            {
                ObstacleDefinition good = ValidObstacle("obs_good");
                ObstacleDefinition bad = ValidObstacle("BAD ID");

                ValidationReport report = new ValidationReport();
                Repository<ObstacleDefinition> repository = new Repository<ObstacleDefinition>(
                    new[] { good, bad }, report);

                Assert.That(repository.Count, Is.EqualTo(1), "an unaddressable definition cannot be looked up by id anyway");
                Assert.That(report.IsValid, Is.False);
                Assert.That(RulesOf(report), Does.Contain("id_format"));
            }

            [Test]
            public void DuplicateId_KeepsTheFirst_AndReportsTheSecond()
            {
                ObstacleDefinition first = ValidObstacle("obs_same");
                first.footprintMetres = new Vector2(1f, 1f);
                ObstacleDefinition second = ValidObstacle("obs_same");
                second.footprintMetres = new Vector2(2f, 2f);

                ValidationReport report = new ValidationReport();
                Repository<ObstacleDefinition> repository = new Repository<ObstacleDefinition>(
                    new[] { first, second }, report);

                Assert.That(repository.Count, Is.EqualTo(1));
                Assert.That(RulesOf(report), Does.Contain("id_unique"));
                Assert.That(repository.TryGet("obs_same", out ObstacleDefinition kept), Is.True);
                Assert.That(kept.footprintMetres.x, Is.EqualTo(1f),
                    "first one wins, so reordering a manifest cannot silently change a footprint");
            }

            [Test]
            public void NullEntry_IsAWarning_NotAnError()
            {
                // A hole in a serialized array is the commonest authoring accident (a deleted asset leaves a
                // null). It must be visible without blocking a build.
                ObstacleDefinition ok = ValidObstacle("obs_ok");
                ValidationReport report = new ValidationReport();
                Repository<ObstacleDefinition> repository = new Repository<ObstacleDefinition>(
                    new[] { null, ok }, report);

                Assert.That(repository.Count, Is.EqualTo(1));
                Assert.That(report.IsValid, Is.True);
                Assert.That(RulesOf(report), Does.Contain("null_entry"));
                Assert.That(CountOf(report, "null_entry"), Is.EqualTo(1));
            }

            [Test]
            public void DefinitionRules_AreRunOnEveryAcceptedEntry()
            {
                ObstacleDefinition obstacle = Create<ObstacleDefinition>("obs_broken");
                obstacle.poolKey = string.Empty;
                obstacle.behaviourId = string.Empty;
                obstacle.tagSet = new string[0];
                obstacle.footprintMetres = new Vector2(0f, 0f);
                obstacle.clearance = new ClearanceProfile { jumpable = true, laneOccupancyMask = 0 };

                ValidationReport report = new ValidationReport();
                new Repository<ObstacleDefinition>(new[] { obstacle }, report);

                string[] rules = RulesOf(report);
                Assert.That(rules, Does.Contain("pool_key"));
                Assert.That(rules, Does.Contain("behaviour"));
                Assert.That(rules, Does.Contain("tags"));
                Assert.That(rules, Does.Contain("lane_mask"));
                Assert.That(rules, Does.Contain("footprint"));
            }

            [Test]
            public void FullBlocker_IsReportedAsAWarningOnly()
            {
                ObstacleDefinition blocker = ValidObstacle("obs_blocker");
                blocker.clearance = new ClearanceProfile { laneOccupancyMask = 0b001 };

                ValidationReport report = new ValidationReport();
                new Repository<ObstacleDefinition>(new[] { blocker }, report);

                Assert.That(RulesOf(report), Does.Contain("full_blocker"));
                Assert.That(report.IsValid, Is.True, "avoiding it by changing lane is a legal design, not a defect");
            }

            [Test]
            public void MovingObstacleWithoutAPeriod_IsAnError()
            {
                // A zero period is a divide-by-zero in the behaviour's sine, which reads as a frozen train.
                ObstacleDefinition train = ValidObstacle("obs_train");
                train.isMoving = true;
                train.motion = new ObstacleMotion { axis = ObstacleMotion.MotionAxis.Lateral, amplitude = 3f, period = 0f };

                ValidationReport report = new ValidationReport();
                new Repository<ObstacleDefinition>(new[] { train }, report);

                Assert.That(RulesOf(report), Does.Contain("motion"));
            }

            [Test]
            public void NullDefinitionCollection_IsTolerated()
            {
                ValidationReport report = new ValidationReport();
                Repository<ObstacleDefinition> repository = new Repository<ObstacleDefinition>(null, report);

                Assert.That(repository.Count, Is.EqualTo(0));
                Assert.That(repository.All, Is.Empty);
                Assert.That(report.IsValid, Is.True);
            }

            [Test]
            public void Lookup_IsByOrdinalId_AndCaseSensitive()
            {
                ObstacleDefinition obstacle = ValidObstacle("obs_barrier");
                Repository<ObstacleDefinition> repository = new Repository<ObstacleDefinition>(
                    new[] { obstacle }, new ValidationReport());

                Assert.That(repository.TryGet("obs_barrier", out ObstacleDefinition found), Is.True);
                Assert.That(found, Is.SameAs(obstacle));
                Assert.That(repository.TryGet("OBS_BARRIER", out _), Is.False);
                Assert.That(repository.TryGet("obs_barrer", out _), Is.False);
            }
        }

        // ── Chunk definition rules ──────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class ChunkRuleTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void SlotsMustPointAtATagAndSitInsideTheChunk()
            {
                ChunkDefinition chunk = Create<ChunkDefinition>("chunk_bounds");
                chunk.poolKey = "pool_chunk_bounds";
                chunk.lengthMetres = 40f;
                chunk.worldTags = new[] { "world.test" };
                chunk.obstacleSlots = new[]
                {
                    null,
                    new ObstacleSlotData { lane = 0, zOffset = 10f, obstacleTag = string.Empty },
                    new ObstacleSlotData { lane = 1, zOffset = 95f, obstacleTag = "low" }
                };

                ValidationReport report = new ValidationReport();
                chunk.Validate(report);

                string[] rules = RulesOf(report);
                Assert.That(rules, Does.Contain("slot_null"));
                Assert.That(rules, Does.Contain("slot_tag"));
                Assert.That(rules, Does.Contain("slot_z"), "a slot past the far edge places the body in the next chunk");
            }

            [Test]
            public void MasksAndWeightsMustBeUsableByTheGenerator()
            {
                ChunkDefinition chunk = Create<ChunkDefinition>("chunk_masks");
                chunk.lengthMetres = 0f;
                chunk.entryLaneMask = 0;
                chunk.exitLaneMask = 0;
                chunk.baseWeight = 0f;
                chunk.difficultyBand = new Vector2(0.8f, 0.2f);

                ValidationReport report = new ValidationReport();
                chunk.Validate(report);

                string[] rules = RulesOf(report);
                Assert.That(rules, Does.Contain("length"));
                Assert.That(rules, Does.Contain("entry_mask"));
                Assert.That(rules, Does.Contain("exit_mask"), "an empty exit mask makes this chunk a dead end (§4.5)");
                Assert.That(rules, Does.Contain("weight"), "a zero weight can never be selected");
                Assert.That(rules, Does.Contain("band"));
                Assert.That(rules, Does.Contain("world_tags"));
                Assert.That(rules, Does.Contain("pool_key"));
            }
        }

        // ── Cross-reference rules (§5.14) ───────────────────────────────────────────────────────────────

        [TestFixture]
        public class CrossReferenceTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void CleanCatalog_PassesTheCrossReferencePass()
            {
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);

                Result cross = database.ValidateCrossReferences(report);

                Assert.That(cross.IsSuccess, Is.True, string.Join(" | ", RulesOf(report)));
                Assert.That(fixture.WorldCount, Is.EqualTo(1));
                Assert.That(database.Chunks.Count, Is.EqualTo(1));
                Assert.That(database.Obstacles.Count, Is.EqualTo(1));
            }

            [Test]
            public void ChunkPoolKey_NotInCatalog_IsAnError()
            {
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.Chunk.poolKey = "pool_that_does_not_exist";

                Result cross = database.ValidateCrossReferences(report);

                Assert.That(cross.IsSuccess, Is.False);
                Assert.That(cross.ErrorCode, Is.EqualTo(ErrorCodes.CatalogInvalid));
                Assert.That(RulesOf(report), Does.Contain("pool_ref"));
            }

            [Test]
            public void ObstaclePoolKey_NotInCatalog_IsAnError()
            {
                // This one cannot be caught by ObstacleDefinition.Validate: a single definition has no way to
                // see the PoolCatalog, which is exactly why the cross-reference pass exists.
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.Obstacle.poolKey = "pool_missing";

                Assert.That(database.ValidateCrossReferences(report).IsSuccess, Is.False);
                Assert.That(RulesOf(report), Does.Contain("pool_ref"));
            }

            [Test]
            public void SlotTag_WithNoMatchingObstacle_IsAnError()
            {
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.Chunk.obstacleSlots = new[]
                {
                    new ObstacleSlotData { lane = 0, zOffset = 10f, obstacleTag = "tag_nobody_carries" }
                };

                Assert.That(database.ValidateCrossReferences(report).IsSuccess, Is.False);
                Assert.That(RulesOf(report), Does.Contain("obstacle_ref"));
            }

            [Test]
            public void WorldWithNoEligibleChunks_IsAnError()
            {
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.World.chunkTagFilter = new[] { "world_nobody_belongs_to" };

                Assert.That(database.ValidateCrossReferences(report).IsSuccess, Is.False);
                Assert.That(RulesOf(report), Does.Contain("world_chunks"));
            }

            [Test]
            public void WorldWithChunksButNoSafeFallback_IsAnError()
            {
                // §4.5: the generator needs one always-eligible chunk to fall back on. Without it, a hard
                // difficulty target can leave every candidate ineligible and the track can only stall.
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.Chunk.safeFallback = false;

                Assert.That(database.ValidateCrossReferences(report).IsSuccess, Is.False);
                Assert.That(RulesOf(report), Does.Contain("world_fallback"));
                Assert.That(RulesOf(report), Has.No.Member("world_chunks"), "the chunk is still eligible — only the fallback is gone");
            }

            [Test]
            public void AudioVoicePoolKey_IsCheckedLikeAnyOtherReference()
            {
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.Manifest.audio.voicePoolKey = "pool_missing_voices";

                Assert.That(database.ValidateCrossReferences(report).IsSuccess, Is.False);
                Assert.That(RulesOf(report), Does.Contain("pool_ref"));
            }

            [Test]
            public void EmptyPoolKey_IsNotTreatedAsADanglingReference()
            {
                // The rule is about a key that names nothing. An absent key is a different rule (pool_key),
                // reported by the definition itself, and must not be double-reported here.
                Fixture fixture = new Fixture();
                GameDatabase database = fixture.Build(out ValidationReport report);
                fixture.Obstacle.poolKey = string.Empty;

                database.ValidateCrossReferences(report);
                Assert.That(CountOf(report, "pool_ref"), Is.EqualTo(0));
            }

            [Test]
            public void ManifestWithoutAPoolCatalogReportsEveryReferenceAsBroken()
            {
                // PoolCatalog is optional on the manifest as far as the compiler knows, so the reference pass
                // has to degrade to errors rather than a null reference on the first chunk row.
                Fixture fixture = new Fixture();
                fixture.Manifest.poolCatalog = null;
                GameDatabase database = fixture.Build(out ValidationReport report);

                Assert.That(database.ValidateCrossReferences(report).IsSuccess, Is.False);
                Assert.That(CountOf(report, "pool_ref"), Is.GreaterThanOrEqualTo(2), "chunk, obstacle and voice keys all resolve to nothing");
            }

            /// <summary>
            /// A two-line catalog that satisfies every cross-reference rule, so each test above can break
            /// exactly one of them and nothing else. The definitions are mutated through the same references
            /// the manifest array holds, which is why mutation is visible after <see cref="Build"/>.
            ///
            /// <see cref="Build"/> runs <see cref="GameDatabase"/>'s constructor against a throwaway report:
            /// a minimal fixture cannot satisfy every definition rule (a PoolCatalog row with no prefab is a
            /// "prefab" error, and a pooled prefab is a real asset these tests do not need). The pass under
            /// test is ValidateCrossReferences, so its report is the one asserted.
            /// </summary>
            private sealed class Fixture
            {
                public CatalogManifest Manifest;
                public WorldDefinition World;
                public ChunkDefinition Chunk;
                public ObstacleDefinition Obstacle;

                public int WorldCount => Manifest.worlds != null ? Manifest.worlds.Length : 0;

                public Fixture()
                {
                    Manifest = Track(ScriptableObject.CreateInstance<CatalogManifest>());
                    World = Track(ScriptableObject.CreateInstance<WorldDefinition>());
                    Chunk = Track(ScriptableObject.CreateInstance<ChunkDefinition>());

                    Name(World, "world_test");
                    Name(Chunk, "chunk_test");

                    Chunk.poolKey = "pool_chunk_test";
                    Chunk.lengthMetres = 60f;
                    Chunk.worldTags = new[] { "world.test" };
                    Chunk.entryLaneMask = 0b111;
                    Chunk.exitLaneMask = 0b111;
                    Chunk.safeFallback = true;
                    Chunk.obstacleSlots = new[]
                    {
                        new ObstacleSlotData { lane = 1, zOffset = 20f, obstacleTag = "low" }
                    };

                    World.chunkTagFilter = new[] { "world.test" };

                    Obstacle = Create<ObstacleDefinition>("obs_test");
                    Obstacle.poolKey = "pool_obs_test";
                    Obstacle.tagSet = new[] { "low" };

                    PoolCatalog pools = Track(ScriptableObject.CreateInstance<PoolCatalog>());
                    Name(pools, "cfg_pools_test");
                    pools.entries = new List<PoolEntry>
                    {
                        new PoolEntry { key = "pool_chunk_test", prewarm = 8 },
                        new PoolEntry { key = "pool_obs_test", prewarm = 8 },
                        new PoolEntry { key = "audio_voice", prewarm = 12 }
                    };
                    Manifest.poolCatalog = pools;

                    // The voice pool must exist for the clean case, so the audio config points at a real key.
                    AudioConfig audio = Create<AudioConfig>("cfg_audio_test");
                    audio.voicePoolKey = "audio_voice";
                    Manifest.audio = audio;
                }

                private static void Name(ScriptableObject so, string id)
                {
                    SerializedObject serialized = new SerializedObject(so);
                    serialized.FindProperty("id").stringValue = id;
                    serialized.FindProperty("displayNameKey").stringValue = "skyrift.test." + id;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                public GameDatabase Build(out ValidationReport report)
                {
                    // The constructor validates the manifest and every definition. Those rules have their own
                    // tests above, and a fixture this small cannot satisfy all of them (a PoolCatalog row with
                    // no prefab is a "prefab" error, and a real prefab is an asset these tests do not need),
                    // so the construction report is deliberately discarded.
                    GameDatabase database = new GameDatabase(Manifest, new ValidationReport());
                    report = new ValidationReport();
                    return database;
                }
            }
        }
    }
}
