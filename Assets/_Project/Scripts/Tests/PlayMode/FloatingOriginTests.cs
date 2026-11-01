using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Core.Events;
using Skyrift.Track;
using UnityEngine;

namespace Skyrift.Tests.PlayMode
{
    /// <summary>
    /// The floating origin (TECH_DOC §4.6, ADR-0007). A run that never ends would walk the player to a
    /// position where a float can no longer represent a centimetre, so the world is re-based under the
    /// player instead. Two properties have to hold and both were real defects during M1: every registered
    /// root must move exactly once per shift, and the accumulated offset must be reversible so a new run
    /// starts where the last one started.
    /// </summary>
    [TestFixture]
    public class FloatingOriginTests
    {
        private const float Threshold = 2048f;

        private readonly List<GameObject> _spawned = new List<GameObject>(8);

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        private Transform Root(string name, Vector3 position)
        {
            GameObject go = new GameObject(name);
            go.transform.position = position;
            _spawned.Add(go);
            return go.transform;
        }

        private static WorldOriginShifter Shifter(IEventBus bus)
        {
            WorldOriginShifter shifter = new WorldOriginShifter(bus);
            shifter.Configure(Threshold);
            return shifter;
        }

        // ── When a shift happens ────────────────────────────────────────────────────────────────────────

        [Test]
        public void BelowTheThreshold_NothingMoves()
        {
            EventBus bus = new EventBus();
            WorldOriginShifter shifter = Shifter(bus);
            Transform player = Root("player", new Vector3(0f, 0f, 512f));
            shifter.RegisterRoot(player);

            int events = 0;
            bus.Subscribe<OriginShiftedEvent>(evt => events++);

            shifter.ShiftIfNeeded(Threshold - 0.01f);

            Assert.That(shifter.OriginOffset, Is.EqualTo(0d));
            Assert.That(player.position.z, Is.EqualTo(512f));
            Assert.That(events, Is.EqualTo(0), "a near-miss must not re-base the world: every shift is a visible pop otherwise");

            bus.Clear();
        }

        [Test]
        public void AtTheThreshold_EveryRootMovesBackByExactlyOneThreshold()
        {
            EventBus bus = new EventBus();
            WorldOriginShifter shifter = Shifter(bus);

            Transform player = Root("player_origin", new Vector3(2.4f, 0f, 512f));
            Transform chaser = Root("chaser_origin", new Vector3(-1.2f, 0f, 480f));
            Transform track = Root("track_origin", Vector3.zero);
            shifter.RegisterRoot(player);
            shifter.RegisterRoot(chaser);
            shifter.RegisterRoot(track);

            Recorder listener = new Recorder();
            shifter.RegisterListener(listener);

            OriginShiftedEvent received = default;
            bus.Subscribe<OriginShiftedEvent>(evt => received = evt);

            shifter.ShiftIfNeeded(Threshold);

            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold).Within(1e-4));
            Assert.That(player.position.z, Is.EqualTo(512f - Threshold).Within(1e-3));
            Assert.That(chaser.position.z, Is.EqualTo(480f - Threshold).Within(1e-3));
            Assert.That(track.position.z, Is.EqualTo(-Threshold).Within(1e-3));

            Assert.That(player.position.x, Is.EqualTo(2.4f).Within(1e-5),
                "only the travel axis moves, so a lane position is untouched by a re-base");

            Assert.That(listener.Deltas, Is.EqualTo(new[] { (double)Threshold }), "the listener is told the delta");
            Assert.That(received.DeltaMetres, Is.EqualTo(Threshold).Within(1e-4));
            Assert.That(received.NewOffset, Is.EqualTo(Threshold).Within(1e-4),
                "and the event carries the running total, which is what an absolute-position store must subtract");

            bus.Clear();
        }

        [Test]
        public void TheNextShiftIsMeasuredFromTheLastOne_NotFromZero()
        {
            WorldOriginShifter shifter = Shifter(new EventBus());
            Transform root = Root("player_repeat", new Vector3(0f, 0f, 10f));
            shifter.RegisterRoot(root);

            shifter.ShiftIfNeeded(Threshold);
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold));

            // Still at the same distance: the player has not travelled another threshold, so nothing pops.
            shifter.ShiftIfNeeded(Threshold);
            shifter.ShiftIfNeeded(Threshold + 100f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold),
                "one shift per threshold travelled, however many frames ask for it");

            shifter.ShiftIfNeeded(Threshold * 2f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold * 2d).Within(1e-3));
            Assert.That(root.position.z, Is.EqualTo(10f - Threshold * 2f).Within(1e-2));
        }

        [Test]
        public void OneCallShiftsOnce_SoASuddenTeleportIsUnwoundOverSeveralFrames()
        {
            WorldOriginShifter shifter = Shifter(new EventBus());
            Transform root = Root("player_teleport", new Vector3(0f, 0f, 4f));
            shifter.RegisterRoot(root);

            // A debug teleport (or a resumed save) puts the player far past the threshold at once. One shift
            // per call keeps every frame's delta a constant, which is what makes the precision guarantee
            // exact; the consumer keeps asking until the distance is covered.
            shifter.ShiftIfNeeded(10000f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold));

            for (int i = 0; i < 4; i++) shifter.ShiftIfNeeded(10000f);

            // Four more requests, but only three still clear the threshold (10000 - 8192 < 2048).
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold * 4d).Within(1e-2));
            Assert.That(root.position.z, Is.EqualTo(4f - Threshold * 4f).Within(1e-1));
        }

        // ── Reversibility, which is what a new run needs ───────────────────────────────────────────────

        [Test]
        public void ResetForRun_PutsEveryRootBackWhereItStarted()
        {
            WorldOriginShifter shifter = Shifter(new EventBus());
            Transform player = Root("player_reset", new Vector3(0f, 1.8f, 300f));
            Transform chaser = Root("chaser_reset", new Vector3(0f, 0f, 260f));
            shifter.RegisterRoot(player);
            shifter.RegisterRoot(chaser);

            Vector3 playerStart = player.position;
            Vector3 chaserStart = chaser.position;

            shifter.ShiftIfNeeded(Threshold);
            shifter.ShiftIfNeeded(Threshold * 2f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold * 2d).Within(1e-3));

            shifter.ResetForRun();

            Assert.That(shifter.OriginOffset, Is.EqualTo(0d));
            Assert.That(Vector3.Distance(player.position, playerStart), Is.LessThan(1e-3f),
                "a second run starts in the same world space as the first");
            Assert.That(Vector3.Distance(chaser.position, chaserStart), Is.LessThan(1e-3f),
                "or the chaser would spawn 4 km behind a player that has not moved");

            // And the counter is reset too, so the next threshold is a full threshold away, not immediately due.
            shifter.ShiftIfNeeded(Threshold - 1f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(0d));
        }

        // ── Registration, which is where the M1 defects were ───────────────────────────────────────────

        [Test]
        public void RegisteringTheSameRootTwice_StillMovesItOnce()
        {
            WorldOriginShifter shifter = Shifter(new EventBus());
            Transform root = Root("player_double", new Vector3(0f, 0f, 100f));

            shifter.RegisterRoot(root);
            shifter.RegisterRoot(root);

            shifter.ShiftIfNeeded(Threshold);
            Assert.That(root.position.z, Is.EqualTo(100f - Threshold).Within(1e-3),
                "a duplicated registration would double the shift and drift the rig away from the track");
        }

        [Test]
        public void ARootThatLeftTheScene_IsSkippedRatherThanFatal()
        {
            WorldOriginShifter shifter = Shifter(new EventBus());
            Transform doomed = Root("doomed_root", new Vector3(0f, 0f, 20f));
            Transform survivor = Root("survivor_root", new Vector3(0f, 0f, 20f));
            shifter.RegisterRoot(doomed);
            shifter.RegisterRoot(survivor);

            Object.DestroyImmediate(doomed.gameObject);

            // The list still holds a destroyed reference. Unity's == overload reports it as null, and the
            // shift has to step over it: a mid-run exception here would kill the frame loop on device.
            Assert.That(() => shifter.ShiftIfNeeded(Threshold), Throws.Nothing);
            Assert.That(survivor.position.z, Is.EqualTo(20f - Threshold).Within(1e-3));
        }

        [Test]
        public void UnregisteringStopsARootBeingCarried()
        {
            WorldOriginShifter shifter = Shifter(new EventBus());
            Transform handed = Root("handed_off", new Vector3(0f, 0f, 50f));
            Transform kept = Root("kept", new Vector3(0f, 0f, 50f));

            shifter.RegisterRoot(handed);
            shifter.RegisterRoot(kept);
            shifter.UnregisterRoot(handed);

            shifter.ShiftIfNeeded(Threshold);

            Assert.That(handed.position.z, Is.EqualTo(50f), "a root owned by someone else must not be moved twice");
            Assert.That(kept.position.z, Is.EqualTo(50f - Threshold).Within(1e-3));
        }

        [Test]
        public void ANullRootOrListener_IsRefusedSilently_RatherThanThrowingDuringComposition()
        {
            // Registration happens from several bootstrap steps, and a MonoBehaviour that has already been
            // destroyed reads as null here. Refusing the entry is the only safe answer: throwing would
            // abort the boot sequence one step short of a playable scene.
            EventBus bus = new EventBus();
            WorldOriginShifter shifter = Shifter(bus);

            Assert.That(() =>
            {
                shifter.RegisterRoot(null);
                shifter.RegisterListener(null);
                shifter.UnregisterRoot(null);
                shifter.UnregisterListener(null);
            }, Throws.Nothing);

            Transform root = Root("player_null", new Vector3(0f, 0f, 10f));
            shifter.RegisterRoot(root);
            shifter.ShiftIfNeeded(Threshold);

            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold).Within(1e-4),
                "and the refusals left no phantom entries behind, so exactly one shift happened");
            Assert.That(root.position.z, Is.EqualTo(10f - Threshold).Within(1e-3));

            bus.Clear();
        }

        [Test]
        public void ADuplicateListener_IsNotifiedOnce_AndUnregisterReallyDetachesIt()
        {
            EventBus bus = new EventBus();
            WorldOriginShifter shifter = Shifter(bus);
            shifter.RegisterRoot(Root("player_listener", new Vector3(0f, 0f, 10f)));

            Recorder listener = new Recorder();
            shifter.RegisterListener(listener);
            shifter.RegisterListener(listener);

            shifter.ShiftIfNeeded(Threshold);
            Assert.That(listener.Deltas, Has.Count.EqualTo(1),
                "two systems registering the same listener is normal; being called twice would double-count the offset");

            shifter.UnregisterListener(listener);
            shifter.ShiftIfNeeded(Threshold * 2f);
            Assert.That(listener.Deltas, Has.Count.EqualTo(1),
                "a detached listener must go quiet, or the next run inherits this one's bookkeeping");
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold * 2d).Within(1e-3),
                "even though the world kept moving");

            bus.Clear();
        }

        // ── Configuration ───────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ANonPositiveThreshold_FallsBackToTheAuthoredDefault()
        {
            // originShiftThreshold is [Min(256)] authored data, but a Remote Config override can still hand
            // out 0. A zero threshold would shift every frame and strobe the world.
            WorldOriginShifter shifter = new WorldOriginShifter(new EventBus());
            shifter.Configure(0f);

            Transform root = Root("player_fallback", new Vector3(0f, 0f, 0f));
            shifter.RegisterRoot(root);

            shifter.ShiftIfNeeded(2047f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(0d), "the fallback is the documented 2048 m default");

            shifter.ShiftIfNeeded(2048f);
            Assert.That(shifter.OriginOffset, Is.EqualTo(2048d).Within(1e-3));
        }

        [Test]
        public void ASmallThreshold_StillShiftsByExactMultiples()
        {
            // The power-of-two requirement (§4.6) is what keeps repeated subtraction exact in binary
            // floating point. 256 is the authored floor and is a power of two, so the offset stays exact.
            WorldOriginShifter shifter = new WorldOriginShifter(null);
            shifter.Configure(256f);

            Transform root = Root("player_exact", new Vector3(0f, 0f, 100f));
            shifter.RegisterRoot(root);

            for (int i = 1; i <= 8; i++)
            {
                shifter.ShiftIfNeeded(i * 256f);
                Assert.That(shifter.OriginOffset, Is.EqualTo(i * 256d), "shift " + i);
            }

            Assert.That(root.position.z, Is.EqualTo(100f - 2048f),
                "eight exact shifts leave no residue, which is why the threshold must not be an arbitrary number");
        }

        [Test]
        public void ANullBus_StillMovesTheWorld()
        {
            // The bus is optional at construction (a headless run has no listeners), so the publish is
            // guarded — but the re-base itself is not presentation and must still happen.
            WorldOriginShifter shifter = new WorldOriginShifter(null);
            Transform root = Root("player_nobus", new Vector3(0f, 0f, 12f));
            shifter.RegisterRoot(root);

            Assert.That(() => shifter.ShiftIfNeeded(Threshold), Throws.Nothing);
            Assert.That(shifter.OriginOffset, Is.EqualTo(Threshold).Within(1e-4));
            Assert.That(root.position.z, Is.EqualTo(12f - Threshold).Within(1e-3));
        }

        /// <summary>Captures every delta the shifter reports, in order.</summary>
        private sealed class Recorder : IOriginShiftListener
        {
            public readonly List<double> Deltas = new List<double>(4);

            public void OnOriginShifted(double deltaMetres) => Deltas.Add(deltaMetres);
        }
    }
}
