using System;
using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Controls;
using Skyrift.Core.Time;
using Skyrift.Data;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Tests.Controls
{
    /// <summary>
    /// The intent buffer is the reason a swipe issued two frames before a lane change becomes legal still
    /// lands (TECH_DOC §4.3), and the reason a swipe issued half a second ago does not. Both halves matter:
    /// a buffer that never expires makes the player's inputs lag behind the screen, and one that expires too
    /// eagerly feels like dropped input on a mid-range device.
    ///
    /// Time comes from <see cref="ManualClock"/>, so every boundary here is exact rather than a tolerance
    /// against <c>Time.time</c>. That is the whole reason the buffer takes an <see cref="IClock"/>.
    /// </summary>
    [TestFixture]
    public class InputBufferTests
    {
        private static readonly List<Object> _created = new List<Object>(16);

        private static InputConfig Config(string id, float window = 0.2f, int depth = 1)
        {
            InputConfig config = ScriptableObject.CreateInstance<InputConfig>();
            config.name = id;

            SerializedObject serialized = new SerializedObject(config);
            serialized.FindProperty("id").stringValue = id;
            serialized.FindProperty("displayNameKey").stringValue = "skyrift.test." + id;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            config.bufferWindow = window;
            config.bufferDepth = depth;
            _created.Add(config);
            return config;
        }

        private static System.Collections.Generic.List<UnityEngine.Object> _created
            = new System.Collections.Generic.List<UnityEngine.Object>(16);

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            _created.Clear();
        }

        // ── Lifetime of one intent ──────────────────────────────────────────────────────────────────────

        [Test]
        public void AFreshBuffer_HoldsNothing()
        {
            InputConfig config = Config("cfg_buffer_empty");
            ManualClock clock = new ManualClock();
            InputCommandBuffer buffer = new InputCommandBuffer(config, clock);

            Assert.That(buffer.PendingCount, Is.EqualTo(0));

            buffer.TryConsume(out InputIntent intent, out double pushedUtcTicks);
            Assert.That(intent, Is.EqualTo(InputIntent.None), "a failed consume still answers with a defined value");
            Assert.That(pushedUtcTicks, Is.EqualTo(0d));
        }

        [Test]
        public void PushingNone_IsIgnored()
        {
            InputConfig config = Config("cfg_buffer_none");
            InputCommandBuffer buffer = new InputCommandBuffer(config, new ManualClock());

            buffer.Push(InputIntent.None);
            Assert.That(buffer.PendingCount, Is.EqualTo(0),
                "a source that cannot resolve a gesture emits None, which must not consume a buffer slot");

            buffer.Push(InputIntent.SwipeLeft);
            buffer.Push(InputIntent.None);
            Assert.That(buffer.PendingCount, Is.EqualTo(1), "and it must not supersede a real intent already queued");
        }

        [Test]
        public void AnIntentSurvivesExactlyTheAuthoredWindow_ThenDies()
        {
            InputConfig config = Config("cfg_buffer_window", window: 0.2f);
            ManualClock clock = new ManualClock();
            InputCommandBuffer buffer = new InputCommandBuffer(config, clock);

            buffer.Push(InputIntent.SwipeUp);

            clock.Advance(0.1f);
            Assert.That(buffer.TryConsume(out InputIntent first), Is.True, "inside the window the intent lands");
            Assert.That(first, Is.EqualTo(InputIntent.SwipeUp));

            // A second push, pushed to the exact edge. The comparison is strictly greater than, so an intent
            // that is precisely bufferWindow old is still live: at 60 FPS the last live frame is the one that
            // lands on the boundary, and making the edge exclusive would silently shave a frame off every
            // buffered input.
            buffer.Push(InputIntent.SwipeDown);
            clock.Advance(0.1f);
            clock.Advance(0.1f);
            Assert.That(buffer.TryConsume(out InputIntent boundary, out _), Is.True,
                "exactly bufferWindow is still legal");
            Assert.That(boundary, Is.EqualTo(InputIntent.SwipeDown));

            // One millisecond more and it is gone. The player has moved on; applying it would be lag.
            buffer.Push(InputIntent.SwipeLeft);
            clock.Advance(0.1f);
            clock.Advance(0.1f);
            clock.Advance(0.001f);
            Assert.That(buffer.TryConsume(out _), Is.False, "past the window the intent is dropped");
            Assert.That(buffer.PendingCount, Is.EqualTo(0), "an expired intent is evicted, not rewound into the queue");
        }

        [Test]
        public void AZeroWindow_MeansSameFrameOnly()
        {
            InputConfig config = Config("cfg_buffer_zero", window: 0f);
            ManualClock clock = new ManualClock();
            InputCommandBuffer buffer = new InputCommandBuffer(config, clock);

            buffer.Push(InputIntent.Tap);
            Assert.That(buffer.TryConsume(out _), Is.True, "a consumer running in the same tick still gets it");

            buffer.Push(InputIntent.Tap);
            clock.Advance(0.001f);
            Assert.That(buffer.TryConsume(out _), Is.False,
                "bufferWindow 0 is a legal authored value meaning 'no buffering at all'");
        }

        // ── Depth and supersession ──────────────────────────────────────────────────────────────────────

        [Test]
        public void DepthOne_SupersedesSoTheLatestSwipeWins()
        {
            InputConfig config = Config("cfg_buffer_depth1", depth: 1);
            ManualClock clock = new ManualClock();
            InputCommandBuffer buffer = new InputCommandBuffer(config, clock);

            buffer.Push(InputIntent.SwipeLeft);
            clock.Advance(0.05f);
            buffer.Push(InputIntent.SwipeRight);

            Assert.That(buffer.PendingCount, Is.EqualTo(1),
                "the shipped depth is 1: a player who changes their mind is heard, not queue-drained");

            Assert.That(buffer.TryConsume(out InputIntent intent), Is.True);
            Assert.That(intent, Is.EqualTo(InputIntent.SwipeRight));
            Assert.That(buffer.TryConsume(out _), Is.False, "and the superseded intent is gone, not queued behind");
        }

        [Test]
        public void GreaterDepth_PreservesInsertionOrder()
        {
            InputConfig config = Config("cfg_buffer_depth3", depth: 3);
            InputCommandBuffer buffer = new InputCommandBuffer(config, new ManualClock());

            buffer.Push(InputIntent.SwipeLeft);
            buffer.Push(InputIntent.SwipeUp);
            buffer.Push(InputIntent.SwipeRight);
            Assert.That(buffer.PendingCount, Is.EqualTo(3));

            // Oldest first: a jump-then-lane-change gesture must execute in the order it was performed.
            Assert.That(buffer.TryConsume(out InputIntent a), Is.True);
            Assert.That(a, Is.EqualTo(InputIntent.SwipeLeft));
            Assert.That(buffer.TryConsume(out InputIntent b), Is.True);
            Assert.That(b, Is.EqualTo(InputIntent.SwipeUp));
            Assert.That(buffer.TryConsume(out InputIntent c), Is.True);
            Assert.That(c, Is.EqualTo(InputIntent.SwipeRight));
            Assert.That(buffer.TryConsume(out _), Is.False);
        }

        [Test]
        public void AFullBuffer_DropsTheOldest_NotTheNewest()
        {
            InputConfig config = Config("cfg_buffer_full", depth: 2);
            ManualClock clock = new ManualClock();
            InputCommandBuffer buffer = new InputCommandBuffer(config, clock);

            buffer.Push(InputIntent.SwipeLeft);
            clock.Advance(0.02f);
            buffer.Push(InputIntent.SwipeUp);
            clock.Advance(0.02f);
            buffer.Push(InputIntent.SwipeRight);

            Assert.That(buffer.PendingCount, Is.EqualTo(2), "the ring overflows by dropping, never by growing");

            Assert.That(buffer.TryConsume(out InputIntent first), Is.True);
            Assert.That(first, Is.EqualTo(InputIntent.SwipeUp), "the oldest (SwipeLeft) was the one sacrificed");
            Assert.That(buffer.TryConsume(out InputIntent second), Is.True);
            Assert.That(second, Is.EqualTo(InputIntent.SwipeRight));
        }

        [Test]
        public void ADepthBelowOne_IsClampedNotRejected()
        {
            // bufferDepth is authored data and Remote Config can hand out 0. The buffer must not allocate a
            // zero-length ring (every Push would then divide by zero on the modulo), and it must not throw
            // across a module boundary either.
            InputConfig config = Config("cfg_buffer_clamp", depth: 0);
            InputCommandBuffer buffer = new InputCommandBuffer(config, new ManualClock());

            buffer.Push(InputIntent.SwipeLeft);
            buffer.Push(InputIntent.SwipeRight);
            Assert.That(buffer.PendingCount, Is.EqualTo(1));
            Assert.That(buffer.TryConsume(out InputIntent intent), Is.True);
            Assert.That(intent, Is.EqualTo(InputIntent.SwipeRight));
        }

        // ── The retry contract ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void Consume_ReportsTheOriginalPushTimeSoARetryCannotLiveForever()
        {
            InputConfig config = Config("cfg_buffer_age", window: 0.2f, depth: 2);
            ManualClock clock = new ManualClock();
            InputCommandBuffer buffer = new InputCommandBuffer(config, clock);

            double pushedAt = clock.NowUtcTicks;
            buffer.Push(InputIntent.SwipeLeft);

            clock.Advance(0.15f);
            buffer.Push(InputIntent.SwipeRight);   // the newer intent also stamps "now"

            clock.Advance(0.15f);

            // SwipeLeft is 0.30 s old and must be dropped; SwipeRight is 0.15 s and must survive. If the
            // buffer re-stamped on write-back or on retry, both would look brand new and a held swipe could
            // fire a lane change half a second after the player released it (§4.3).
            Assert.That(buffer.TryConsume(out InputIntent survived, out double stamped), Is.True);
            Assert.That(survived, Is.EqualTo(InputIntent.SwipeRight), "the expired intent was skipped");
            Assert.That(stamped, Is.EqualTo(pushedAt + 1500000d).Within(2d),
                "the stamp is the moment of the push, in ticks, and it never moves");

            double ageTicks = clock.NowUtcTicks - stamped;
            Assert.That(ageTicks, Is.EqualTo(1500000d).Within(2d),
                "a consumer can therefore measure the true age and refuse a stale intent");
        }

        [Test]
        public void Clear_DropsPendingIntentsAndLeavesTheBufferUsable()
        {
            InputConfig config = Config("cfg_buffer_clear", depth: 2);
            InputCommandBuffer buffer = new InputCommandBuffer(config, new ManualClock());

            buffer.Push(InputIntent.SwipeLeft);
            buffer.Push(InputIntent.SwipeUp);
            buffer.Clear();

            Assert.That(buffer.PendingCount, Is.EqualTo(0));
            Assert.That(buffer.TryConsume(out _), Is.False, "a clear on a partially consumed ring stays consistent");

            // The run controller clears on every transition, so the buffer must be reusable afterwards.
            buffer.Push(InputIntent.SwipeDown);
            Assert.That(buffer.TryConsume(out InputIntent intent), Is.True);
            Assert.That(intent, Is.EqualTo(InputIntent.SwipeDown));
        }

        // ── Interaction with a paused run ───────────────────────────────────────────────────────────────

        [Test]
        public void AFrozenRun_StillAgesTheBuffer_WhichIsDeliberate()
        {
            InputConfig config = Config("cfg_buffer_pause", window: 0.2f);
            ManualClock real = new ManualClock();
            RunClock run = new RunClock(real) { Paused = true };
            InputCommandBuffer buffer = new InputCommandBuffer(config, run);

            buffer.Push(InputIntent.SwipeLeft);

            real.Advance(0.5f);

            Assert.That(run.DeltaTime, Is.EqualTo(0f), "the simulation is frozen");
            Assert.That(run.UnscaledDeltaTime, Is.GreaterThan(0f), "and the clock still sees real time");
            Assert.That(buffer.TryConsume(out _), Is.False,
                "so a swipe issued before the pause is dropped, not teleported into the resumed run");
        }

        // ── Construction guards and the composition seam ────────────────────────────────────────────────

        [Test]
        public void TheConstructor_RefusesAClockOrConfigThatCannotExist()
        {
            InputConfig config = Config("cfg_buffer_guard");
            ManualClock clock = new ManualClock();

            Assert.Throws<ArgumentNullException>(() => new InputCommandBuffer(null, clock));
            Assert.Throws<ArgumentNullException>(() => new InputCommandBuffer(config, null));
        }

        [Test]
        public void TheBridge_MovesIntentsFromTheSourceToTheBufferAndFilterNothing()
        {
            InputConfig config = Config("cfg_buffer_bridge", depth: 4);
            InputCommandBuffer buffer = new InputCommandBuffer(config, new ManualClock());
            FakeInputSource source = new FakeInputSource();

            using (InputIntentBridge bridge = new InputIntentBridge(source, buffer))
            {
                source.Issue(InputIntent.SwipeLeft);
                source.Issue(InputIntent.None);          // the bridge does not filter (§4.3)
                source.Issue(InputIntent.Pause);

                Assert.That(buffer.PendingCount, Is.EqualTo(2), "None is dropped by the buffer, not by the bridge");
                Assert.That(buffer.TryConsume(out InputIntent first), Is.True);
                Assert.That(first, Is.EqualTo(InputIntent.SwipeLeft));
                Assert.That(buffer.TryConsume(out InputIntent second), Is.True);
                Assert.That(second, Is.EqualTo(InputIntent.Pause),
                    "an intent the run controller cannot act on yet is still buffered exactly as issued");
            }

            Assert.That(buffer.PendingCount, Is.EqualTo(0),
                "disposing clears what is queued: a pending swipe must not leak into the next run");

            source.Issue(InputIntent.SwipeRight);
            Assert.That(buffer.PendingCount, Is.EqualTo(0), "and the cached delegate really detaches");
        }

        [Test]
        public void TheBridge_RejectsTheWiringErrorsThatWouldSilentlyDropEverySwipe()
        {
            InputConfig config = Config("cfg_buffer_bridge_guard");
            InputCommandBuffer buffer = new InputCommandBuffer(config, new ManualClock());

            Assert.Throws<ArgumentNullException>(() => new InputIntentBridge(null, buffer));
            Assert.Throws<ArgumentNullException>(() => new InputIntentBridge(new FakeInputSource(), null));
        }

        /// <summary>
        /// A source that only exists to raise the event. The real touch and keyboard sources are Monobehaviours
        /// that need a device or an InputActionAsset, and neither belongs in an EditMode assertion about
        /// wiring: what is being pinned here is the bridge, not the detector.
        /// </summary>
        private sealed class FakeInputSource : IInputSource
        {
            public event Action<InputIntent> IntentIssued;

            public bool IsEnabled { get; private set; } = true;
            public InputDebugState Debug => new InputDebugState(false, 0f, Vector2.zero, InputIntent.None);

            public void SetEnabled(bool enabled) => IsEnabled = enabled;

            /// <summary>Simulates one resolved gesture.</summary>
            public void Issue(InputIntent intent) => IntentIssued?.Invoke(intent);
        }
    }
}
