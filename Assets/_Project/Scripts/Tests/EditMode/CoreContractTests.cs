using System;
using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Core.Randomization;
using Skyrift.Core.Services;
using Skyrift.Core.StateMachine;
using Skyrift.Core.Time;

namespace Skyrift.Tests.Core
{
    /// <summary>
    /// Contracts for the primitives every other module is built on (TECH_DOC §4.0, §12.1). These are the
    /// tests that pay for themselves fastest: a wrong assumption here is a defect in every system above,
    /// and each of them is testable with no scene, no assets and no frame loop.
    /// </summary>
    [TestFixture]
    public class CoreContractTests
    {
        // ── StableId ────────────────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class StableIdTests
        {
            [Test]
            [TestCase("a")]
            [TestCase("ab")]
            public void TooShort_IsInvalid(string value)
            {
                Assert.That(StableId.IsValidString(value), Is.False,
                    $"{value.Length} character(s) is below MinLength ({StableId.MinLength}).");
            }

            [Test]
            [TestCase("Obstacle_Barrier", TestCaseName = "uppercase")]
            [TestCase("obstacle-barrier", TestCaseName = "hyphen")]
            [TestCase("obstacle barrier", TestCaseName = "space")]
            [TestCase("obstacle.barrier", TestCaseName = "dot")]
            public void NonSnakeCase_IsInvalid(string value)
            {
                // Saves and analytics reference these ids, so the grammar has no lenient characters in it.
                Assert.That(StableId.IsValidString(value), Is.False, $"'{value}' must not be accepted.");
            }

            [Test]
            [TestCase("obs")]
            [TestCase("chunk_ramp_roof")]
            [TestCase("a0_")]
            [TestCase("x0123456789")]
            public void SnakeCaseWithDigits_IsValid(string value)
            {
                Assert.That(StableId.IsValidString(value), Is.True, "'" + value + "' is snake_case within the length bounds.");
                Assert.That(StableId.TryCreate(value, out StableId id), Is.True);
                Assert.That(id.Value, Is.EqualTo(value));
            }

            [Test]
            public void Exactly_MinLength_IsValid()
            {
                string id = new string('a', StableId.MinLength);
                Assert.That(StableId.IsValidString(id), Is.True);
                Assert.That(StableId.IsValidString(new string('a', StableId.MinLength - 1)), Is.False);
            }

            [Test]
            public void Exactly_MaxLength_IsValid_OneOver_IsNot()
            {
                Assert.That(StableId.IsValidString(new string('a', StableId.MaxLength)), Is.True);
                Assert.That(StableId.IsValidString(new string('a', StableId.MaxLength + 1)), Is.False);
            }

            [Test]
            [TestCase(null)]
            [TestCase("")]
            public void EmptyOrNull_IsInvalid_AndBecomesNone(string value)
            {
                Assert.That(StableId.IsValidString(value), Is.False);
                Assert.That(StableId.TryCreate(value, out StableId id), Is.False);
                Assert.That(id.IsNone, Is.True, "A failed TryCreate must yield None, never a half-built id.");
            }

            [Test]
            public void Create_OnInvalid_Throws()
            {
                Assert.Throws<ArgumentException>(() => StableId.Create("Bad Id"));
            }

            [Test]
            public void ImplicitConversions_RoundTrip_AndCompareOrdinal()
            {
                StableId fromString = "coin_magnet";
                Assert.That((string)fromString, Is.EqualTo("coin_magnet"));
                Assert.That(fromString, Is.EqualTo(StableId.Create("coin_magnet")));
                Assert.That(fromString == "coin_magnet", Is.True);
                Assert.That(fromString != "COIN_MAGNET", Is.True, "ids are case-sensitive even though valid ones are lowercase");

                HashSet<StableId> set = new HashSet<StableId> { fromString };
                Assert.That(set.Contains("coin_magnet"), Is.True, "GetHashCode must follow Value, not the boxing object.");
            }

            [Test]
            public void None_IsNotValid_ButIsNone()
            {
                Assert.That(StableId.None.IsValid, Is.False);
                Assert.That(StableId.None.IsNone, Is.True);
                Assert.That(StableId.None.Value, Is.Empty);
            }
        }

        // ── XorshiftRandom ──────────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class RandomProviderTests
        {
            [Test]
            public void SameSeed_ProducesSameSequence()
            {
                // The ADR-0005 determinism contract, in its smallest form: a fixed seed must replay exactly.
                uint[] a = Take(new XorshiftRandom(12345u), 64);
                uint[] b = Take(new XorshiftRandom(12345u), 64);
                Assert.That(b, Is.EqualTo(a));
            }

            [Test]
            public void DifferentSeeds_ProduceDifferentSequences()
            {
                uint[] a = Take(new XorshiftRandom(1u), 16);
                uint[] b = Take(new XorshiftRandom(2u), 16);
                Assert.That(b, Is.Not.EqualTo(a));
            }

            [Test]
            public void ZeroSeed_DoesNotLockUp()
            {
                // xorshift's only fixed point is the all-zero state; the constructor forces a non-zero first
                // word, so a seed of 0 has to behave like any other seed rather than returning 0 forever.
                XorshiftRandom rng = new XorshiftRandom(0u);
                uint[] values = Take(rng, 32);

                bool anyNonZero = false;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] == 0u) continue;
                    anyNonZero = true;
                    break;
                }

                Assert.That(anyNonZero, Is.True, "a zero-locked generator emits nothing but zeros");
                Assert.That(values, Is.EquivalentTo(Take(new XorshiftRandom(0u), 32)));
            }

            [Test]
            public void Range_IsInclusiveExclusive_AndNeverEscapes()
            {
                XorshiftRandom rng = new XorshiftRandom(777u);
                for (int i = 0; i < 4096; i++)
                {
                    int value = rng.Range(3, 7);
                    Assert.That(value, Is.InRange(3, 6), "maxExclusive must never be returned.");
                }
            }

            [Test]
            public void Range_OnEmptySpan_ReturnsMinInsteadOfThrowing()
            {
                XorshiftRandom rng = new XorshiftRandom(1u);
                Assert.That(rng.Range(5, 5), Is.EqualTo(5));
                Assert.That(rng.Range(5, 4), Is.EqualTo(5), "An inverted span is a caller bug, not a crash.");
            }

            [Test]
            public void Range_EveryValueInSpan_IsReached()
            {
                // A generator that silently collapsed to one value would pass the determinism test above, so
                // coverage is checked separately — this is what makes weighted chunk selection meaningful.
                XorshiftRandom rng = new XorshiftRandom(4242u);
                HashSet<int> seen = new HashSet<int>();
                for (int i = 0; i < 512; i++) seen.Add(rng.Range(0, 5));
                Assert.That(seen, Is.EquivalentTo(new[] { 0, 1, 2, 3, 4 }));
            }

            [Test]
            public void Value01_StaysInHalfOpenUnitInterval()
            {
                XorshiftRandom rng = new XorshiftRandom(9001u);
                for (int i = 0; i < 4096; i++)
                {
                    float v = rng.Value01();
                    Assert.That(v, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
                }
            }

            [Test]
            public void Fork_IsDeterministicPerStreamName_AndAdvancesTheParent()
            {
                // Two properties the track and audio systems both rely on: a named stream replays, and the
                // fork is not the parent's next values (so a presentation cue cannot shift the track layout).
                XorshiftRandom parentA = new XorshiftRandom(555u);
                XorshiftRandom parentB = new XorshiftRandom(555u);

                IRandomProvider chunkA = parentA.Fork("track.chunk");
                IRandomProvider chunkB = parentB.Fork("track.chunk");
                Assert.That(TakeRaw(chunkB, 32), Is.EqualTo(TakeRaw(chunkA, 32)));

                IRandomProvider otherA = parentA.Fork("audio.pitch");
                IRandomProvider otherB = parentB.Fork("audio.pitch");
                Assert.That(TakeRaw(otherB, 32), Is.EqualTo(TakeRaw(otherA, 32)));
                Assert.That(TakeRaw(otherA, 32), Is.Not.EqualTo(TakeRaw(chunkA, 32)));

                // Documenting the cost: Fork consumes from the parent, so fork order is part of the seed's
                // meaning. A refactor that moves a Fork call changes every later draw on the parent stream —
                // which is also why the two parents above must still agree afterwards.
                Assert.That(parentA.NextUInt(), Is.EqualTo(parentB.NextUInt()));
            }

            [Test]
            public void Hash_IsStableFnvAndEmptyNameIsTheOffset()
            {
                Assert.That(XorshiftRandom.Hash(string.Empty), Is.EqualTo(2166136261u));
                Assert.That(XorshiftRandom.Hash("track.chunk"), Is.EqualTo(XorshiftRandom.Hash("track.chunk")));
                Assert.That(XorshiftRandom.Hash("track.chunk"), Is.Not.EqualTo(XorshiftRandom.Hash("track.chunK")));
            }

            private static uint[] Take(XorshiftRandom rng, int count)
            {
                uint[] values = new uint[count];
                for (int i = 0; i < count; i++) values[i] = rng.NextUInt();
                return values;
            }

            private static uint[] TakeRaw(IRandomProvider rng, int count)
            {
                uint[] values = new uint[count];
                for (int i = 0; i < count; i++) values[i] = rng.NextUInt();
                return values;
            }
        }

        // ── Clocks ──────────────────────────────────────────────────────────────────────────────────────

        [TestFixture]
        public class ClockTests
        {
            [Test]
            public void ManualClock_AdvancesDeltaTimeAndTicksTogether()
            {
                ManualClock clock = new ManualClock();
                double before = clock.NowUtcTicks;

                clock.Advance(0.25f);

                Assert.That(clock.DeltaTime, Is.EqualTo(0.25f));
                Assert.That(clock.UnscaledDeltaTime, Is.EqualTo(0.25f), "A manual clock has no scaled/unscaled split.");
                Assert.That(clock.NowUtcTicks - before, Is.EqualTo(TimeSpan.TicksPerSecond * 0.25).Within(1),
                    "Buffer expiry is measured in ticks, so Advance has to move the clock's time, not just its delta.");
            }

            [Test]
            public void RunClock_ScaleAndPauseOnlyAffectDeltaTime()
            {
                ManualClock source = new ManualClock();
                source.Advance(0.5f);

                RunClock run = new RunClock(source);
                Assert.That(run.DeltaTime, Is.EqualTo(0.5f));

                run.Scale = 0.25f;
                Assert.That(run.DeltaTime, Is.EqualTo(0.125f));

                // §4.12: a crash freeze and a pause are different things. Paused means nothing advances, and
                // UnscaledDeltaTime must survive both so presentation keeps running.
                run.Paused = true;
                Assert.That(run.DeltaTime, Is.EqualTo(0f));
                Assert.That(run.UnscaledDeltaTime, Is.EqualTo(0.5f));
                Assert.That(run.NowUtcTicks, Is.EqualTo(source.NowUtcTicks),
                    "Wall clock passes through — a buffered swipe must still expire while the run is frozen.");

                run.ResetForRun();
                Assert.That(run.Scale, Is.EqualTo(1f));
                Assert.That(run.Paused, Is.False);
                Assert.That(run.DeltaTime, Is.EqualTo(0.5f));
            }

            [Test]
            public void RunClock_NullSource_Throws()
            {
                Assert.Throws<ArgumentNullException>(() => new RunClock(null));
            }
        }

        // ── EventBus ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Payloads are nested so each fixture owns its own static channel; the TearDown clears it.</summary>
        private readonly struct PingEvent : IGameEvent
        {
            public readonly int Value;
            public PingEvent(int value) { Value = value; }
        }

        private readonly struct PongEvent : IGameEvent
        {
            public readonly int Value;
            public PongEvent(int value) { Value = value; }
        }

        [TestFixture]
        public class EventBusTests
        {
            private EventBus _bus;

            [SetUp]
            public void SetUp() => _bus = new EventBus();

            [TearDown]
            public void TearDown()
            {
                // Channels are static per payload type, so a handler left subscribed here would leak into the
                // next test in the same domain reload — and into every other fixture that publishes PingEvent.
                _bus.Clear();
            }

            [Test]
            public void Publish_ReachesEverySubscriber()
            {
                int a = 0, b = 0;
                Action<PingEvent> handlerA = evt => a += evt.Value;
                Action<PingEvent> handlerB = evt => b += evt.Value;
                _bus.Subscribe(handlerA);
                _bus.Subscribe(handlerB);

                _bus.Publish(new PingEvent(3));

                Assert.That(a, Is.EqualTo(3));
                Assert.That(b, Is.EqualTo(3));
                Assert.That(_bus.HandlerCount<PingEvent>(), Is.EqualTo(2));
            }

            [Test]
            public void Unsubscribe_StopsDelivery_ForThatHandlerOnly()
            {
                int a = 0, b = 0;
                Action<PingEvent> handlerA = evt => a++;
                Action<PingEvent> handlerB = evt => b++;
                _bus.Subscribe(handlerA);
                _bus.Subscribe(handlerB);
                _bus.Unsubscribe(handlerA);

                _bus.Publish(new PingEvent(1));

                Assert.That(a, Is.EqualTo(0));
                Assert.That(b, Is.EqualTo(1));
            }

            [Test]
            public void NullHandler_IsIgnored()
            {
                _bus.Subscribe<PingEvent>(null);
                Assert.That(_bus.HandlerCount<PingEvent>(), Is.EqualTo(0));
                Assert.That(() => _bus.Publish(new PingEvent(1)), Throws.Nothing);
                Assert.That(() => _bus.Unsubscribe<PingEvent>(null), Throws.Nothing);
            }

            [Test]
            public void Publish_WithNoSubscriber_IsANoOp()
            {
                Assert.That(() => _bus.Publish(new PongEvent(1)), Throws.Nothing);
                Assert.That(_bus.HandlerCount<PongEvent>(), Is.EqualTo(0));
            }

            [Test]
            public void SubscribeDuringDispatch_IsDeferredToTheNextPublish()
            {
                // The reason this exists: a system that subscribes from inside a handler would otherwise be
                // invoked by the same dispatch it was born in, and the ordering would depend on the delegate
                // snapshot. §4.0 makes late subscription deterministic instead.
                int added = 0;
                Action<PingEvent> late = evt => added++;
                Action<PingEvent> reenter = evt => _bus.Subscribe(late);

                _bus.Subscribe(reenter);
                _bus.Publish(new PingEvent(1));

                Assert.That(added, Is.EqualTo(0), "A handler created mid-dispatch must not see that dispatch.");

                _bus.Publish(new PingEvent(2));
                Assert.That(added, Is.EqualTo(1));
            }

            [Test]
            public void UnsubscribeDuringDispatch_StillDeliversToThatHandlerThisTime()
            {
                // Snapshot semantics: the running dispatch walks the list it started with. A handler that
                // unsubscribes itself (a one-shot listener, which is the common shape) still finishes its work.
                int hits = 0;
                Action<PingEvent> oneShot = default;
                oneShot = evt =>
                {
                    hits++;
                    _bus.Unsubscribe(oneShot);
                };

                _bus.Subscribe(oneShot);
                _bus.Publish(new PingEvent(1));
                _bus.Publish(new PingEvent(2));

                Assert.That(hits, Is.EqualTo(1));
                Assert.That(_bus.HandlerCount<PingEvent>(), Is.EqualTo(0));
            }

            [Test]
            public void Clear_DropsEveryTypeThisBusSubscribed()
            {
                _bus.Subscribe<PingEvent>(evt => { });
                _bus.Subscribe<PongEvent>(evt => { });

                _bus.Clear();

                Assert.That(_bus.HandlerCount<PingEvent>(), Is.EqualTo(0));
                Assert.That(_bus.HandlerCount<PongEvent>(), Is.EqualTo(0));
            }

            [Test]
            public void HandlerCount_CountsEachDelegateExactlyOnce()
            {
                Action<PingEvent> handler = evt => { };
                _bus.Subscribe(handler);
                _bus.Subscribe(handler);
                Assert.That(_bus.HandlerCount<PingEvent>(), Is.EqualTo(2),
                    "The bus does not dedupe: a system that subscribes twice will be notified twice, which is its own bug to catch.");
            }
        }

        // ── GameStateMachine ────────────────────────────────────────────────────────────────────────────

        private enum TestPhase { Boot, Splash, Loading, Home, Running, Paused, Results }

        [TestFixture]
        public class StateMachineTests
        {
            [Test]
            public void UnlistedEdge_IsRefusedAndStateIsUnchanged()
            {
                GameStateMachine<TestPhase> fsm = Build();

                // Home → Running is legal; Running → Home is not declared, so the table is directional.
                Assert.That(fsm.TryTransition(TestPhase.Running), Is.True);
                Assert.That(fsm.CanTransition(TestPhase.Running, TestPhase.Home), Is.False);
                Assert.That(fsm.TryTransition(TestPhase.Home), Is.False);
                Assert.That(fsm.Current, Is.EqualTo(TestPhase.Running));
            }

            [Test]
            public void Transition_UpdatesCurrentAndPrevious_AndRaisesTheEvent()
            {
                GameStateMachine<TestPhase> fsm = Build();
                List<string> raised = new List<string>();
                fsm.Transitioned += (from, to) => raised.Add(from + ">" + to);

                fsm.TryTransition(TestPhase.Running);
                fsm.TryTransition(TestPhase.Paused);

                Assert.That(fsm.Current, Is.EqualTo(TestPhase.Paused));
                Assert.That(fsm.Previous, Is.EqualTo(TestPhase.Running));
                Assert.That(raised, Is.EqualTo(new[] { "Home>Running", "Running>Paused" }));
            }

            [Test]
            public void Refusal_RaisesTransitionRefused_WithBothStates()
            {
                GameStateMachine<TestPhase> fsm = Build();
                TestPhase seenFrom = default, seenTo = default;
                int refusals = 0;
                fsm.TransitionRefused += (from, to) => { refusals++; seenFrom = from; seenTo = to; };

                fsm.TryTransition(TestPhase.Results);

                Assert.That(refusals, Is.EqualTo(1));
                Assert.That(seenFrom, Is.EqualTo(TestPhase.Home));
                Assert.That(seenTo, Is.EqualTo(TestPhase.Results));
            }

            [Test]
            public void TransitionToTheSameState_IsAlwaysRefused()
            {
                // Declared as a rule rather than left to the table: a self-edge would otherwise make a
                // double-tap on Pause publish a second Paused event and duck the music twice (§4.18).
                GameStateMachine<TestPhase> fsm = Build();
                fsm.Allow(TestPhase.Home, TestPhase.Home);

                Assert.That(fsm.CanTransition(TestPhase.Home, TestPhase.Home), Is.False);
                Assert.That(fsm.TryTransition(TestPhase.Home), Is.False);
            }

            [Test]
            public void Allow_IsAdditive_AndReturnsTheSameInstanceForChaining()
            {
                GameStateMachine<TestPhase> fsm = new GameStateMachine<TestPhase>(TestPhase.Boot);
                GameStateMachine<TestPhase> returned = fsm
                    .Allow(TestPhase.Boot, TestPhase.Splash)
                    .Allow(TestPhase.Splash, TestPhase.Loading)
                    .Allow(TestPhase.Splash, TestPhase.Home);

                Assert.That(returned, Is.SameAs(fsm));
                Assert.That(fsm.CanTransition(TestPhase.Splash, TestPhase.Loading), Is.True);
                Assert.That(fsm.CanTransition(TestPhase.Splash, TestPhase.Home), Is.True);
            }

            [Test]
            public void InitialState_SetsPreviousToItselfSoTheFirstEdgeHasAValue()
            {
                GameStateMachine<TestPhase> fsm = new GameStateMachine<TestPhase>(TestPhase.Boot);
                Assert.That(fsm.Current, Is.EqualTo(TestPhase.Boot));
                Assert.That(fsm.Previous, Is.EqualTo(TestPhase.Boot));
            }

            private static GameStateMachine<TestPhase> Build()
            {
                // A reduced copy of the §6.1 flow: enough edges to make both a legal and an illegal path real.
                return new GameStateMachine<TestPhase>(TestPhase.Boot)
                    .Allow(TestPhase.Boot, TestPhase.Splash)
                    .Allow(TestPhase.Splash, TestPhase.Loading)
                    .Allow(TestPhase.Loading, TestPhase.Home)
                    .Allow(TestPhase.Home, TestPhase.Running)
                    .Allow(TestPhase.Running, TestPhase.Paused, TestPhase.Results)
                    .Allow(TestPhase.Paused, TestPhase.Running, TestPhase.Results)
                    .Allow(TestPhase.Results, TestPhase.Home);
            }
        }

        // ── ServiceContainer ────────────────────────────────────────────────────────────────────────────

        private interface IGreeter { string Greet(); }

        private sealed class Greeter : IGreeter
        {
            public string Greet() => "hello";
        }

        private sealed class CountedGreeter : IGreeter, IDisposable
        {
            public int DisposeCount;
            public string Greet() => "counted";
            public void Dispose() => DisposeCount++;
        }

        [TestFixture]
        public class ServiceContainerTests
        {
            [Test]
            public void RegisterThenResolve_ReturnsTheSameInstance()
            {
                ServiceContainer container = new ServiceContainer();
                Greeter greeter = new Greeter();
                container.Register<IGreeter>(greeter);

                Assert.That(container.Resolve<IGreeter>(), Is.SameAs(greeter));
                Assert.That(container.TryResolve(out IGreeter found), Is.True);
                Assert.That(found, Is.SameAs(greeter));
            }

            [Test]
            public void DuplicateRegistration_Throws_NamingTheService()
            {
                // §4.1: a half-built graph retried on the same instance must fail here, not later with a
                // null reference, so the refusal is deliberately loud.
                ServiceContainer container = new ServiceContainer();
                container.Register<IGreeter>(new Greeter());

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => container.Register<IGreeter>(new Greeter()));
                Assert.That(ex.Message, Does.Contain("IGreeter"));
            }

            [Test]
            public void Resolve_OfUnknownService_Throws_AndTryResolveReturnsFalse()
            {
                ServiceContainer container = new ServiceContainer();
                Assert.That(container.TryResolve(out IGreeter _), Is.False);
                Assert.Throws<InvalidOperationException>(() => container.Resolve<IGreeter>());
            }

            [Test]
            public void RegisterNull_Throws()
            {
                ServiceContainer container = new ServiceContainer();
                Assert.Throws<ArgumentNullException>(() => container.Register<IGreeter>(null));
                Assert.Throws<ArgumentNullException>(() => container.RegisterLazy<IGreeter>(null));
            }

            [Test]
            public void RegisterLazy_BuildsOnceOnFirstResolve_AndCaches()
            {
                ServiceContainer container = new ServiceContainer();
                int builds = 0;
                container.RegisterLazy<IGreeter>(() => { builds++; return new Greeter(); });

                Assert.That(builds, Is.EqualTo(0), "A lazy service must not be built by registration.");
                IGreeter first = container.Resolve<IGreeter>();
                IGreeter second = container.Resolve<IGreeter>();

                Assert.That(builds, Is.EqualTo(1));
                Assert.That(second, Is.SameAs(first));
            }

            [Test]
            public void ChildScope_ResolvesParent_AndOverridesLocally()
            {
                ServiceContainer root = new ServiceContainer();
                Greeter rootGreeter = new Greeter();
                root.Register<IGreeter>(rootGreeter);

                IServiceContainer scope = root.CreateScope();
                Assert.That(scope.Resolve<IGreeter>(), Is.SameAs(rootGreeter), "Fall-through to the parent.");

                Greeter local = new Greeter();
                scope.Register<IGreeter>(local);
                Assert.That(scope.Resolve<IGreeter>(), Is.SameAs(local));
                Assert.That(root.Resolve<IGreeter>(), Is.SameAs(rootGreeter), "A scope must not write through.");
            }

            [Test]
            public void Dispose_ReleasesOwnedDisposables_ButNotTheParents()
            {
                ServiceContainer root = new ServiceContainer();
                CountedGreeter rootService = new CountedGreeter();
                root.Register<IGreeter>(rootService);

                // Constructed directly rather than through CreateScope, because IServiceContainer does not
                // expose Dispose — and CreateScope returns exactly this object graph.
                ServiceContainer scope = new ServiceContainer(root);
                CountedGreeter scopeService = new CountedGreeter();
                scope.Register<IGreeter>(scopeService);

                scope.Dispose();

                Assert.That(scopeService.DisposeCount, Is.EqualTo(1));
                Assert.That(rootService.DisposeCount, Is.EqualTo(0),
                    "The owner of a service disposes it. A run scope that killed a global service would break the next run.");

                Assert.Throws<ObjectDisposedException>(() => scope.Resolve<IGreeter>());
                Assert.That(root.TryResolve(out IGreeter stillAlive), Is.True);
                Assert.That(stillAlive, Is.SameAs(rootService));

                root.Dispose();
                Assert.That(rootService.DisposeCount, Is.EqualTo(1));
            }

            [Test]
            public void Dispose_IsIdempotent()
            {
                ServiceContainer container = new ServiceContainer();
                CountedGreeter service = new CountedGreeter();
                container.Register<IGreeter>(service);

                container.Dispose();
                container.Dispose();

                Assert.That(service.DisposeCount, Is.EqualTo(1));
            }
        }
    }
}
