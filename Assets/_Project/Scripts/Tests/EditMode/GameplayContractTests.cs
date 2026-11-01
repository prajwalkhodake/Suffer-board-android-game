using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Core;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Data;
using Skyrift.Gameplay;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Tests.Gameplay
{
    /// <summary>
    /// The collision model, the score composition and the continue policies (TECH_DOC §4.7, §4.11, §4.12).
    /// All three are pure enough to be tested without a scene: the resolver is a function of its context, the
    /// calculator is a function of its config and events, and a policy is a function of its config. That is
    /// deliberate — §4.7's whole argument for analytic collision over physics was testability.
    /// </summary>
    [TestFixture]
    public class GameplayContractTests
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

        /// <summary>A balance config on its code defaults, which is what the clearance maths below assumes.</summary>
        protected static BalanceConfig Balance(string id) => Create<BalanceConfig>(id);

        // ── Clearance classification ────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class ClearanceTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            /// <summary>
            /// Builds one context. Every parameter is explicit at the call site so a case reads like the row
            /// of the §4.7 outcome table it is testing.
            /// </summary>
            private static ContactContext Context(BalanceConfig balance, float feetAboveGround, float lateralLanes,
                PlayerVerticalState vertical, ClearanceProfile clearance, float objectHeight = 1.2f,
                float playerHeight = 1.8f, float relativeSpeed = 20f, ContactSurface surface = ContactSurface.Torso)
            {
                return new ContactContext(surface, 1, 1, 20f, relativeSpeed, feetAboveGround, playerHeight,
                    objectHeight, lateralLanes, vertical, false, clearance, null, balance);
            }

            private static ClearanceProfile Jumpable()
                => new ClearanceProfile { jumpable = true, clearanceHeight = 1.1f, laneOccupancyMask = 0b001 };

            private static ClearanceProfile Rollable()
                => new ClearanceProfile { rollable = true, underClearance = 1.0f, laneOccupancyMask = 0b001 };

            private static ClearanceProfile Mountable(float roofHeight)
                => new ClearanceProfile { mountable = true, clearanceHeight = roofHeight, laneOccupancyMask = 0b001 };

            private static ClearanceProfile Blocker()
                => new ClearanceProfile { laneOccupancyMask = 0b001 };

            [Test]
            public void LowBarrier_ClearedWhenFeetAreAboveTheClearanceHeight()
            {
                BalanceConfig balance = Balance("cfg_clear");
                ClearanceProfile jumpable = Jumpable();

                // clearanceForgiveness (0.18 m) is added to the feet height, so a jump that is still
                // fractionally short counts — the reason a player is not punished for frame alignment.
                ContactContext enough = Context(balance, 1.1f - balance.clearanceForgiveness + 0.01f, 0f,
                    PlayerVerticalState.Jumping, jumpable);
                Assert.That(ObstacleClearance.Classify(enough), Is.EqualTo(ContactOutcome.Cleared));

                ContactContext tooLow = Context(balance, 0.2f, 0f, PlayerVerticalState.Grounded, jumpable);
                Assert.That(ObstacleClearance.Classify(tooLow), Is.EqualTo(ContactOutcome.Crashed),
                    "a blocker in the same lane with the player on the ground is head-on");
            }

            [Test]
            public void RollUnderGate_ClearedOnlyWhileRollingAndLowEnough()
            {
                BalanceConfig balance = Balance("cfg_roll");
                ClearanceProfile rollable = Rollable();

                ContactContext rolling = Context(balance, 0f, 0f, PlayerVerticalState.Rolling, rollable,
                    playerHeight: balance.playerRollHeight);
                Assert.That(ObstacleClearance.Classify(rolling), Is.EqualTo(ContactOutcome.Cleared));

                // Standing height over the same under-clearance is a head-on hit, and jumping is not an
                // answer to a gate — the profile does not claim jumpable.
                ContactContext standing = Context(balance, 0f, 0f, PlayerVerticalState.Grounded, rollable,
                    playerHeight: balance.playerStandHeight);
                Assert.That(ObstacleClearance.Classify(standing), Is.EqualTo(ContactOutcome.Crashed));

                ContactContext airborne = Context(balance, 3f, 0f, PlayerVerticalState.Jumping, rollable,
                    playerHeight: balance.playerStandHeight);
                Assert.That(ObstacleClearance.Classify(airborne), Is.EqualTo(ContactOutcome.Crashed));
            }

            [Test]
            public void Mountable_ClearedWhenFeetAreAboveTheRoof()
            {
                BalanceConfig balance = Balance("cfg_mount");
                ClearanceProfile roof = Mountable(3.2f);

                ContactContext onRoof = Context(balance, 3.2f, 0f, PlayerVerticalState.Falling, roof, objectHeight: 3.2f);
                Assert.That(ObstacleClearance.Classify(onRoof), Is.EqualTo(ContactOutcome.Cleared));

                ContactContext belowRoof = Context(balance, 2.0f, 0f, PlayerVerticalState.Jumping, roof, objectHeight: 3.2f);
                Assert.That(ObstacleClearance.Classify(belowRoof), Is.EqualTo(ContactOutcome.Crashed),
                    "below the roof line the body is a wall, which is what makes an unreachable rooftop a design error");
            }

            [Test]
            public void SideScrape_IsLateralAndIndependentOfHeight()
            {
                BalanceConfig balance = Balance("cfg_side");
                ClearanceProfile blocker = Blocker();

                // At the graze threshold and beyond, the same contact that would be head-on becomes a stumble.
                ContactContext side = Context(balance, 0f, balance.grazeLateralThreshold, PlayerVerticalState.Grounded, blocker);
                Assert.That(ObstacleClearance.Classify(side), Is.EqualTo(ContactOutcome.Stumbled));

                ContactContext headOn = Context(balance, 0f, balance.grazeLateralThreshold - 0.01f,
                    PlayerVerticalState.Grounded, blocker);
                Assert.That(ObstacleClearance.Classify(headOn), Is.EqualTo(ContactOutcome.Crashed));

                // Threshold is in lane widths, so it survives a world that authors a different width.
                BalanceConfig wide = Balance("cfg_side_wide");
                wide.laneWidth = 3.2f;
                ContactContext sameLanesOnAWideTrack = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 20f,
                    0f, 1.8f, 1.2f, wide.grazeLateralThreshold, PlayerVerticalState.Grounded, false, blocker, null, wide);
                Assert.That(ObstacleClearance.Classify(sameLanesOnAWideTrack), Is.EqualTo(ContactOutcome.Stumbled));
            }

            [Test]
            public void FullBlocker_OffersNoVerticalAnswer()
            {
                BalanceConfig balance = Balance("cfg_blocker");
                ClearanceProfile blocker = Blocker();

                // Jumping does nothing when the profile claims no clearance: the body is tall, so the feet
                // being off the ground is irrelevant.
                ContactContext jumped = Context(balance, 5f, 0f, PlayerVerticalState.Jumping, blocker, objectHeight: 4f);
                Assert.That(ObstacleClearance.Classify(jumped), Is.EqualTo(ContactOutcome.Crashed));
            }

            [Test]
            public void Shield_TurnsAHurtfulContactIntoAGrazeOnly()
            {
                BalanceConfig balance = Balance("cfg_shield");
                ClearanceProfile blocker = Blocker();

                ContactContext shielded = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 20f, 0f, 1.8f, 1.2f,
                    0f, PlayerVerticalState.Grounded, true, blocker, null, balance);
                Assert.That(ObstacleClearance.Classify(shielded), Is.EqualTo(ContactOutcome.Crashed),
                    "the pure clearance reading knows nothing about shields — that is the resolver's job");

                CollisionResolver resolver = new CollisionResolver(balance);
                Assert.That(resolver.Resolve(in shielded), Is.EqualTo(ContactOutcome.Grazed));
                Assert.That(resolver.StumbleCount, Is.EqualTo(0), "a shield must not consume the stumble counter");
            }

            [Test]
            public void MovingBody_TravellingAwayCannotBeHitHeadOn()
            {
                BalanceConfig balance = Balance("cfg_moving");
                MovingObstacleBehaviour moving = Track(ScriptableObject.CreateInstance<MovingObstacleBehaviour>());
                ClearanceProfile trainBody = new ClearanceProfile { mountable = true, clearanceHeight = 3f, laneOccupancyMask = 0b001 };

                // RelativeSpeed ≤ 0 means the object is at least as fast away from the player as the player
                // is toward it, so a contact the sensor reports is unavoidable-but-harmless (a train pulling
                // away alongside the player).
                ContactContext receding = new ContactContext(ContactSurface.Side, 1, 1, 20f, 0f, 0f, 1.8f, 3f,
                    0.2f, PlayerVerticalState.Grounded, false, trainBody, moving, balance);
                Assert.That(moving.Resolve(in receding), Is.EqualTo(ContactOutcome.Ignored));

                ContactContext closing = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 5f, 0f, 1.8f, 3f,
                    0f, PlayerVerticalState.Grounded, false, trainBody, moving, balance);
                Assert.That(moving.Resolve(in closing), Is.EqualTo(ContactOutcome.Crashed));
            }

            [Test]
            public void Ramp_IsAlwaysALaunch_NeverAHit()
            {
                BalanceConfig balance = Balance("cfg_ramp");
                RampObstacleBehaviour ramp = Track(ScriptableObject.CreateInstance<RampObstacleBehaviour>());
                ClearanceProfile profile = new ClearanceProfile { mountable = true, clearanceHeight = 1.2f, laneOccupancyMask = 0b001 };

                ContactContext front = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 20f, 0f, 1.8f, 1.2f,
                    0f, PlayerVerticalState.Grounded, false, profile, ramp, balance);
                Assert.That(ramp.Resolve(in front), Is.EqualTo(ContactOutcome.Cleared));

                // Crest-first: approaching a ramp from above is a mount, which the standard reading must still
                // answer — otherwise a rooftop jump onto a ramp would be a crash.
                ContactContext crest = new ContactContext(ContactSurface.Top, 1, 1, 20f, 20f, 2f, 1.8f, 1.2f,
                    0f, PlayerVerticalState.Falling, false, profile, ramp, balance);
                Assert.That(ramp.Resolve(in crest), Is.EqualTo(ContactOutcome.Cleared));
            }

            [Test]
            public void StandardBehaviour_IsAPureFunctionOfTheContext()
            {
                BalanceConfig balance = Balance("cfg_pure");
                StandardObstacleBehaviour standard = Track(ScriptableObject.CreateInstance<StandardObstacleBehaviour>());
                ClearanceProfile profile = Jumpable();

                ContactContext ctx = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 20f, 0.5f, 1.8f, 1.2f,
                    0.3f, PlayerVerticalState.Jumping, false, profile, standard, balance);

                ContactOutcome first = standard.Resolve(in ctx);
                for (int i = 0; i < 8; i++)
                    Assert.That(standard.Resolve(in ctx), Is.EqualTo(first), "§4.7: same context ⇒ same outcome");
            }
        }

        // ── The two-hit stumble model ───────────────────────────────────────────────────────────────────

        [TestFixture]
        public class StumbleEscalationTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void FirstScrapeStumbles_SecondWithinTheWindowCrashes()
            {
                BalanceConfig balance = Balance("cfg_stumble");
                CollisionResolver resolver = new CollisionResolver(balance);
                ClearanceProfile blocker = new ClearanceProfile { laneOccupancyMask = 0b001 };

                ContactContext scrape = new ContactContext(ContactSurface.Side, 1, 1, 20f, 20f, 0f, 1.8f, 1.2f,
                    balance.grazeLateralThreshold, PlayerVerticalState.Grounded, false, blocker, null, balance);

                Assert.That(resolver.Resolve(in scrape), Is.EqualTo(ContactOutcome.Stumbled));
                Assert.That(resolver.StumbleCount, Is.EqualTo(1));

                Assert.That(resolver.Resolve(in scrape), Is.EqualTo(ContactOutcome.Crashed),
                    "maxStumblesBeforeCrash is " + balance.maxStumblesBeforeCrash);
                Assert.That(resolver.StumbleCount, Is.EqualTo(2));
            }

            [Test]
            public void HeadOnCrash_DoesNotConsumeTheStumbleBudget()
            {
                BalanceConfig balance = Balance("cfg_headon");
                CollisionResolver resolver = new CollisionResolver(balance);
                ClearanceProfile blocker = new ClearanceProfile { laneOccupancyMask = 0b001 };

                ContactContext headOn = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 20f, 0f, 1.8f, 1.2f,
                    0f, PlayerVerticalState.Grounded, false, blocker, null, balance);

                Assert.That(resolver.Resolve(in headOn), Is.EqualTo(ContactOutcome.Crashed));
                Assert.That(resolver.StumbleCount, Is.EqualTo(0));
            }

            [Test]
            public void AClear_LeavesTheCounterAlone_AndResetsClearIt()
            {
                BalanceConfig balance = Balance("cfg_reset");
                CollisionResolver resolver = new CollisionResolver(balance);
                ClearanceProfile jumpable = new ClearanceProfile { jumpable = true, clearanceHeight = 1f, laneOccupancyMask = 0b001 };
                ContactContext scraped = new ContactContext(ContactSurface.Side, 1, 1, 20f, 20f, 0f, 1.8f, 1.2f,
                    balance.grazeLateralThreshold, PlayerVerticalState.Grounded, false, jumpable, null, balance);
                ContactContext cleared = new ContactContext(ContactSurface.Torso, 1, 1, 20f, 20f,
                    3f, 1.8f, 1.2f, 0f, PlayerVerticalState.Jumping, false, jumpable, null, balance);

                Assert.That(resolver.Resolve(in scraped), Is.EqualTo(ContactOutcome.Stumbled));
                Assert.That(resolver.Resolve(in cleared), Is.EqualTo(ContactOutcome.Cleared));
                Assert.That(resolver.StumbleCount, Is.EqualTo(1), "a cleared obstacle forgives nothing, but costs nothing");

                resolver.ResetStumbles();
                Assert.That(resolver.StumbleCount, Is.EqualTo(0));

                // A successful continue must forgive the stumbles already taken, or the player is one scrape
                // from death the moment they respawn (§4.12).
                resolver.Resolve(in scraped);
                resolver.ResetForRun();
                Assert.That(resolver.StumbleCount, Is.EqualTo(0));
            }

            [Test]
            public void Policy_SwapsTheWholeStumbleModelWithoutTouchingTheResolver()
            {
                BalanceConfig balance = Balance("cfg_policy");
                ClearanceProfile blocker = new ClearanceProfile { laneOccupancyMask = 0b001 };
                ContactContext scrape = new ContactContext(ContactSurface.Side, 1, 1, 20f, 20f, 0f, 1.8f, 1.2f,
                    balance.grazeLateralThreshold, PlayerVerticalState.Grounded, false, blocker, null, balance);

                // One-hit mode: the §4.7 extension point, exercised the way a hard event or a tutorial would.
                CollisionResolver oneHit = new CollisionResolver(balance, new FixedThresholdPolicy(1));
                Assert.That(oneHit.Resolve(in scrape), Is.EqualTo(ContactOutcome.Crashed));

                CollisionResolver threeHit = new CollisionResolver(balance, new FixedThresholdPolicy(3));
                Assert.That(threeHit.Resolve(in scrape), Is.EqualTo(ContactOutcome.Stumbled));
                Assert.That(threeHit.Resolve(in scrape), Is.EqualTo(ContactOutcome.Stumbled));
                Assert.That(threeHit.Resolve(in scrape), Is.EqualTo(ContactOutcome.Crashed));
            }

            [Test]
            public void NullBalance_IsRejectedAtComposition()
            {
                Assert.Throws<System.ArgumentNullException>(() => new CollisionResolver(null));
            }

            /// <summary>Test double for the §4.7 policy extension point: same shape, different threshold.</summary>
            private sealed class FixedThresholdPolicy : IContactOutcomePolicy
            {
                private readonly int _max;
                public FixedThresholdPolicy(int max) => _max = max;

                public ContactOutcome Apply(ContactOutcome classified, int stumbleCount, BalanceConfig balance)
                    => classified != ContactOutcome.Stumbled
                        ? classified
                        : (stumbleCount >= _max ? ContactOutcome.Crashed : ContactOutcome.Stumbled);
            }
        }

        // ── Score composition ───────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class ScoreTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void Distance_IsWeightedByTheConfigAndAccumulatesOverTicks()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_distance");
                scoring.distanceWeight = 2f;
                scoring.kindWeights.Clear();
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);

                score.ResetForRun();
                Assert.That(score.CurrentScore, Is.EqualTo(0));

                // 10 m/s for 1 s = 10 m, at weight 2 = 20.
                for (int i = 0; i < 10; i++) score.Tick(0.1f, 10f);

                Assert.That(score.DistanceMetres, Is.EqualTo(10f).Within(1e-3f));
                Assert.That(score.CurrentScore, Is.EqualTo(20));
                Assert.That(score.Breakdown.DistanceScore, Is.EqualTo(20));
                Assert.That(score.Breakdown.Multiplier, Is.EqualTo(1f), "no power-up and no mission set is 1×");

                bus.Clear();
            }

            [Test]
            public void Collection_AddsItsWeight_AndBuildsAComboInsideTheWindow()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_combo");
                scoring.distanceWeight = 0f;
                scoring.comboWindow = 1.6f;
                scoring.comboBonusPerStep = 4f;
                scoring.kindWeights.Clear();
                scoring.kindWeights.Add(new KindScoreWeight { kind = CollectibleKind.Coin, weight = 10f });
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                score.OnCollected(1, CollectibleKind.Coin);
                Assert.That(score.Combo, Is.EqualTo(1), "the first collection after a gap starts the chain");
                Assert.That(score.Breakdown.Coins, Is.EqualTo(1));
                Assert.That(score.Breakdown.CollectionScore, Is.EqualTo(10));
                Assert.That(score.Breakdown.ComboBonus, Is.EqualTo(4));

                // Inside the window: the chain grows and the bonus is per step, not flat.
                score.Tick(0.5f, 0f);
                score.OnCollected(1, CollectibleKind.Coin);
                Assert.That(score.Combo, Is.EqualTo(2));
                Assert.That(score.Breakdown.ComboBonus, Is.EqualTo(12), "4 + 8");

                // Outside the window the chain has already been broken by the tick's own countdown.
                for (int i = 0; i < 5; i++) score.Tick(0.5f, 0f);
                Assert.That(score.Combo, Is.EqualTo(0), "comboWindow must expire the chain with no collection");

                score.OnCollected(1, CollectibleKind.Coin);
                Assert.That(score.Combo, Is.EqualTo(1), "and the next collection restarts at 1, not at the old value");

                bus.Clear();
            }

            [Test]
            public void Combo_IsCappedByComboMax()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_cap");
                scoring.comboWindow = 10f;
                scoring.comboMax = 3;
                scoring.distanceWeight = 0f;
                scoring.comboBonusPerStep = 1f;
                scoring.kindWeights.Clear();
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                for (int i = 0; i < 10; i++) score.OnCollected(1, CollectibleKind.Coin);

                Assert.That(score.Combo, Is.EqualTo(3), "comboMax is a data cap, and a long chain must not inflate the bonus");
                Assert.That(score.Breakdown.BestCombo, Is.EqualTo(3));

                bus.Clear();
            }

            [Test]
            public void BreakCombo_IsPublishedOnce_AndStumbleUsesIt()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_break");
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                int comboEvents = 0;
                System.Action<ComboChangedEvent> handler = evt => comboEvents++;
                bus.Subscribe(handler);

                score.OnCollected(3, CollectibleKind.Coin);
                Assert.That(score.Combo, Is.EqualTo(1));

                score.BreakCombo();
                Assert.That(score.Combo, Is.EqualTo(0));

                // A second break is a no-op, so a multi-hit crash cannot publish a stream of identical events.
                score.BreakCombo();
                Assert.That(comboEvents, Is.EqualTo(2), "1 → 0 and nothing after");

                bus.Unsubscribe(handler);
                bus.Clear();
            }

            [Test]
            public void Multipliers_ComposeOnceNotCompounded()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_mult");
                scoring.distanceWeight = 1f;
                scoring.kindWeights.Clear();
                scoring.missionSetMultiplierStep = 0.5f;
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                for (int i = 0; i < 10; i++) score.Tick(0.1f, 10f);
                Assert.That(score.CurrentScore, Is.EqualTo(10));

                // 2× power-up and 2 completed mission sets (1 + 2*0.5 = 2×) ⇒ 4× total, applied to the whole
                // composition exactly once (§4.11). If they compounded differently by registration order the
                // results screen would be unreproducible.
                score.OnPowerUpMultiplierChanged(2f);
                score.SetMissionSetsCompleted(2);

                Assert.That(score.Multiplier, Is.EqualTo(4f).Within(1e-4f));
                Assert.That(score.CurrentScore, Is.EqualTo(40));

                // A non-positive factor is a data error, not a way to zero a run.
                score.OnPowerUpMultiplierChanged(0f);
                Assert.That(score.Multiplier, Is.EqualTo(2f).Within(1e-4f), "falls back to 1× for the power-up term");

                score.SetMissionSetsCompleted(-4);
                Assert.That(score.Multiplier, Is.EqualTo(1f).Within(1e-4f));

                bus.Clear();
            }

            [Test]
            public void Contributors_AreAdditiveAndReportedAsTheirOwnLine()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_contrib");
                scoring.distanceWeight = 1f;
                scoring.kindWeights.Clear();
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                for (int i = 0; i < 10; i++) score.Tick(0.1f, 10f);

                // A contributor is a registered object, so the test keeps the reference it needs back —
                // ScoreCalculator deliberately offers no enumeration of what is registered (§4.11: the
                // composition is a sum, not a queryable ledger).
                FlatContributor first = new FlatContributor(5f);
                FlatContributor second = new FlatContributor(5f);
                score.AddContributor(first);
                score.AddContributor(second);

                // Tick ignores a non-positive dt (it is a simulated frame, not a recompute request), so the
                // recompute is forced with a zero-speed frame: distance stays exactly 10 m.
                score.Tick(0.001f, 0f);

                Assert.That(score.Breakdown.ContributorScore, Is.EqualTo(10));
                Assert.That(score.CurrentScore, Is.EqualTo(20), "distance 10 + contributions 10");

                score.RemoveContributor(first);
                score.Tick(0.001f, 0f);
                Assert.That(score.Breakdown.ContributorScore, Is.EqualTo(5));
                Assert.That(score.CurrentScore, Is.EqualTo(15), "removal is immediate, not deferred to the next contribution");

                // Registering the same instance twice must not double its line item.
                score.AddContributor(second);
                score.Tick(0.001f, 0f);
                Assert.That(score.CurrentScore, Is.EqualTo(15), "AddContributor de-duplicates by reference");

                bus.Clear();
            }

            [Test]
            public void Modifiers_ShapeTheCompositionAfterContributions()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_modifier");
                scoring.distanceWeight = 1f;
                scoring.kindWeights.Clear();
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                for (int i = 0; i < 10; i++) score.Tick(0.1f, 10f);
                Assert.That(score.CurrentScore, Is.EqualTo(10));

                // The M2 "near-miss floor" shape: a modifier sees the composed score and can raise it.
                FloorModifier floor = new FloorModifier(50f);
                HalverModifier halver = new HalverModifier();
                score.AddModifier(floor);
                score.Tick(0.001f, 0f);
                Assert.That(score.CurrentScore, Is.EqualTo(50));

                score.AddModifier(halver);
                score.Tick(0.001f, 0f);
                Assert.That(score.CurrentScore, Is.EqualTo(25), "modifiers apply in registration order, which is why they are registered once");

                // Dropping the floor leaves only the halver, so the composition is order-dependent and the
                // removal path is real rather than a field on the interface.
                score.RemoveModifier(floor);
                score.Tick(0.001f, 0f);
                Assert.That(score.CurrentScore, Is.EqualTo(5), "10 m halved, with no floor in the chain");

                bus.Clear();
            }

            [Test]
            public void ResetForRun_ClearsEveryAccumulatorAndDropsContributors()
            {
                ScoringConfig scoring = Create<ScoringConfig>("cfg_score_reset");
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(scoring, bus);
                score.ResetForRun();

                for (int i = 0; i < 20; i++) score.Tick(0.1f, 25f);
                score.OnCollected(4, CollectibleKind.Key);
                score.OnPowerUpMultiplierChanged(2f);
                score.AddContributor(new FlatContributor(9f));

                score.ResetForRun();

                Assert.That(score.CurrentScore, Is.EqualTo(0));
                Assert.That(score.Combo, Is.EqualTo(0));
                Assert.That(score.Multiplier, Is.EqualTo(1f));
                Assert.That(score.DistanceMetres, Is.EqualTo(0f));
                Assert.That(score.Breakdown.Keys, Is.EqualTo(0));
                Assert.That(score.Breakdown.BestCombo, Is.EqualTo(0), "the breakdown is rebuilt from live fields, so a stale best cannot leak into the next run");

                score.Tick(1f, 10f);
                Assert.That(score.CurrentScore, Is.EqualTo(10), "no contributor survived the reset");

                bus.Clear();
            }

            [Test]
            public void NullConfig_DegradesToNoScoring()
            {
                EventBus bus = new EventBus();
                ScoreCalculator score = new ScoreCalculator(null, bus);

                Assert.That(() =>
                {
                    score.Tick(0.1f, 10f);
                    score.OnCollected(1, CollectibleKind.Coin);
                }, Throws.Nothing);

                Assert.That(score.CurrentScore, Is.EqualTo(0));
                bus.Clear();
            }

            private sealed class FlatContributor : IScoreContributor
            {
                private readonly float _amount;
                public FlatContributor(float amount) => _amount = amount;
                public float Contribute(in ScoreContext ctx) => _amount;
            }

            private sealed class FloorModifier : IScoreModifier
            {
                private readonly float _floor;
                public FloorModifier(float floor) => _floor = floor;
                public float Modify(float baseScore, in ScoreContext ctx) => Mathf.Max(baseScore, _floor);
            }

            private sealed class HalverModifier : IScoreModifier
            {
                public float Modify(float baseScore, in ScoreContext ctx) => baseScore * 0.5f;
            }
        }

        // ── Locomotion helpers that are pure ────────────────────────────────────────────────────────────

        [TestFixture]
        public class LocomotionRuleTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void ClampedLaneChangeRule_CannotLeaveTheTrack()
            {
                ClampedLaneChangeRule rule = new ClampedLaneChangeRule();

                Assert.That(rule.ResolveTargetLane(0, -1, 3), Is.EqualTo(0), "the left edge absorbs the swipe");
                Assert.That(rule.ResolveTargetLane(2, 1, 3), Is.EqualTo(2), "and so does the right edge");
                Assert.That(rule.ResolveTargetLane(1, -1, 3), Is.EqualTo(0));
                Assert.That(rule.ResolveTargetLane(1, 1, 3), Is.EqualTo(2));
                Assert.That(rule.ResolveTargetLane(0, 99, 3), Is.EqualTo(2), "a dash power-up still cannot index out of the lane array");
                Assert.That(rule.ResolveTargetLane(1, 0, 3), Is.EqualTo(1));
            }

            [Test]
            public void SpeedProfileHandle_FallsBackToZeroWhenInvalid()
            {
                BalanceConfig balance = Balance("cfg_profile");
                SpeedProfileHandle fromBalance = SpeedProfileHandle.FromBalance(balance, 1f);
                Assert.That(fromBalance.IsValid, Is.True);
                Assert.That(fromBalance.SpeedAt(0f), Is.EqualTo(12f).Within(0.01f));
                Assert.That(fromBalance.SpeedAt(float.MaxValue), Is.EqualTo(28f).Within(0.01f));

                // A handle built from a curve with no keys is unusable, and the contract is 0 rather than NaN
                // — a NaN speed propagates into distance, score and every clearance check.
                SpeedProfileHandle broken = new SpeedProfileHandle(new AnimationCurve(), 100f, 0f, 1f);
                Assert.That(broken.IsValid, Is.False);
                Assert.That(broken.SpeedAt(50f), Is.EqualTo(0f));

                Assert.That(SpeedProfileHandle.FromBalance(null, 1f).IsValid, Is.False);
            }

            [Test]
            public void RunStartContext_NormalisesNonPositiveScales()
            {
                // Remote Config can hand out 0 or a negative scale; neither is allowed to reach the motor.
                RunStartContext ctx = new RunStartContext(1u, "world_test", "char_test", "board_test", 0f, -2f, 7, true);
                Assert.That(ctx.SpeedScale, Is.EqualTo(1f));
                Assert.That(ctx.DifficultyScale, Is.EqualTo(1f));
                Assert.That(ctx.StartLane, Is.EqualTo(7), "-1 means 'the default lane', so other values pass through");
                Assert.That(ctx.IsTutorial, Is.True);

                RunStartContext defaults = new RunStartContext(9u, "world_test", StableId.None, StableId.None);
                Assert.That(defaults.SpeedScale, Is.EqualTo(1f));
                Assert.That(defaults.StartLane, Is.EqualTo(-1));
                Assert.That(defaults.Seed, Is.EqualTo(9u));
            }
        }

        // ── Continue policies ───────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class ContinuePolicyTests
        {
            [TearDown]
            public void TearDown() => DestroyTracked();

            [Test]
            public void NoContinuePolicy_OffersNothingAndFailsWithNotAvailable()
            {
                NoContinueOfferPolicy policy = new NoContinueOfferPolicy();
                RunSummary crash = Crash(100f);

                Assert.That(policy.CanOffer(in crash, 0), Is.False);
                Assert.That(policy.Build(in crash, 0).IsAvailable, Is.False);

                ContinueResult result = policy.GrantAsync(ContinueSource.RewardedAd, ContinueOffer.NotAvailable,
                    default).Result;
                Assert.That(result.IsSuccess, Is.False);
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.NotAvailable));
            }

            [Test]
            public void DebugPolicy_GatesOnTheFlagConfigAndReasonButUsesAuthoredNumbers()
            {
                ContinueConfig config = Create<ContinueConfig>("cfg_continue_debug");
                config.enabled = true;
                config.maxContinuesPerRun = 2;
                config.keyCosts = new[] { 1, 2 };
                config.offerSeconds = 8f;

                DebugContinueOfferPolicy off = new DebugContinueOfferPolicy(config, false);
                RunSummary crash = Crash(100f);
                Assert.That(off.CanOffer(in crash, 0), Is.False, "allowFreeContinues off is the shipped state");

                DebugContinueOfferPolicy on = new DebugContinueOfferPolicy(config, true);
                Assert.That(on.CanOffer(in crash, 0), Is.True);

                // The offer the UI sees is the real one, cost included — only the payment is bypassed, so the
                // M2 continue screen can be built against this without a second data path.
                ContinueOffer offer = on.Build(in crash, 0);
                Assert.That(offer.IsAvailable, Is.True);
                Assert.That(offer.KeyCost, Is.EqualTo(1));
                Assert.That(offer.ContinueIndex, Is.EqualTo(0));
                Assert.That(offer.SecondsRemaining, Is.EqualTo(8f));

                Assert.That(on.CanOffer(in crash, 1), Is.True);
                Assert.That(on.CanOffer(in crash, 2), Is.False, "maxContinuesPerRun is still the cap");
                Assert.That(on.CanOffer(in crash, -1), Is.False);

                config.enabled = false;
                Assert.That(on.CanOffer(in crash, 0), Is.False, "the config switch outranks the debug flag");
            }

            [Test]
            public void DebugPolicy_RefusesAQuitAndAConfigThatDoesNotExist()
            {
                ContinueConfig config = Create<ContinueConfig>("cfg_continue_quit");
                config.enabled = true;
                config.maxContinuesPerRun = 2;
                config.keyCosts = new[] { 1 };
                DebugContinueOfferPolicy policy = new DebugContinueOfferPolicy(config, true);

                RunSummary quit = new RunSummary(default, 100f, 0.0, 1u, "world_test", StableId.None,
                    EndRunReason.PlayerQuit, CrashCause.None, 0, 0);
                Assert.That(policy.CanOffer(in quit, 0), Is.False,
                    "a quit is not a crash the player can buy their way out of");

                RunSummary crash = Crash(50f);
                Assert.That(policy.CanOffer(in crash, 0), Is.True);

                DebugContinueOfferPolicy noConfig = new DebugContinueOfferPolicy(null, true);
                Assert.That(noConfig.CanOffer(in crash, 0), Is.False);
            }

            [Test]
            public void DebugPolicy_OnlyAcceptsItsOwnSourceAndACancelIsACancel()
            {
                ContinueConfig config = Create<ContinueConfig>("cfg_continue_grant");
                config.enabled = true;
                config.maxContinuesPerRun = 1;
                config.keyCosts = new[] { 3 };
                DebugContinueOfferPolicy policy = new DebugContinueOfferPolicy(config, true);

                RunSummary crash = Crash(50f);
                ContinueOffer offer = policy.Build(in crash, 0);

                ContinueResult wrongSource = policy.GrantAsync(ContinueSource.Keys, offer, default).Result;
                Assert.That(wrongSource.IsSuccess, Is.False, "a debug grant must not quietly become a free key spend");
                Assert.That(wrongSource.ErrorCode, Is.EqualTo(ErrorCodes.NotAvailable));

                ContinueResult granted = policy.GrantAsync(ContinueSource.DebugFree, offer, default).Result;
                Assert.That(granted.IsSuccess, Is.True);
                Assert.That(granted.KeysSpent, Is.EqualTo(0), "nothing is charged, which is the whole point of the policy");

                System.Threading.CancellationTokenSource cancelled = new System.Threading.CancellationTokenSource();
                cancelled.Cancel();
                ContinueResult aborted = policy.GrantAsync(ContinueSource.DebugFree, offer, cancelled.Token).Result;
                Assert.That(aborted.IsSuccess, Is.False);
                Assert.That(aborted.ErrorCode, Is.EqualTo(ErrorCodes.Cancelled));
            }

            /// <summary>A crash summary with nothing else set — the policies only read Reason.</summary>
            private static RunSummary Crash(float distance)
                => new RunSummary(default, distance, 0.0, 1u, "world_test", StableId.None,
                    EndRunReason.Crash, CrashCause.HeadOnObstacle, 0, 0);
        }
    }
}
