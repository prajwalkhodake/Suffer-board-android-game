using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Skyrift.Core;
using Skyrift.Core.Ids;
using Skyrift.Data;
using Skyrift.Gameplay;
using Skyrift.Track;
using Unity.Profiling;
using UnityEngine;

namespace Skyrift.Tests.PlayMode
{
    /// <summary>
    /// The two M1 exit criteria no other fixture reaches (TECH_DOC §12.1, §11.2, ROADMAP §M1).
    ///
    /// <b>Determinism through the shipped wiring.</b> The replay guarantee is already proven twice underneath:
    /// <c>CoreContractTests.SameSeed_ProducesSameSequence</c> shows the forked random stream replays exactly, and
    /// <c>TrackRuleTests</c> shows selection is a pure function of the eligibility rules and weights. Neither can
    /// see the composition root, so neither proves that <see cref="RunStartContext"/>'s seed actually reaches the
    /// generator — a controller that handed the track a fresh RNG would leave both green while every
    /// "replay this run" claim made in M5 became fiction. This fixture asserts the chunk ids that really spawned,
    /// in order, at the distances they really started at.
    ///
    /// <b>Why a capture never yields a frame.</b> The difficulty target is a function of how far the player has
    /// travelled (§4.5), and <c>GameRoot.Update</c> advances the run once per rendered frame (§4.12). A capture
    /// that spanned frames would therefore compare two runs that had travelled slightly different distances when
    /// each chunk was drawn, and a weighted pick sitting on a difficulty band edge could legitimately diverge —
    /// which is a flaky test, not a bug. <c>StartRunAsync</c> is <c>Task.FromResult</c> in M1 because everything
    /// resolves from the local catalog, so a seeded restart completes inside the call and a whole capture fits in
    /// one frame. That is asserted below rather than assumed: when M3 inserts an Addressables load at the resolve
    /// step this is the assertion that fails, and the fix is to budget the capture by distance instead of
    /// relaxing the comparison.
    ///
    /// <b>The soak measures the simulation, not a device.</b> Editor PlayMode is not a mid-range ARM64 phone, so
    /// nothing here discharges §11.2's 60 FPS clause — it discharges the zero-allocation clause, which is the
    /// part that can be proven without hardware. The numbers are logged so they can be recorded in
    /// <c>CHANGELOG.md</c> next to the device profile they came from.
    /// </summary>
    [TestFixture]
    public class TrackDeterminismSoakTests : GreyboxSceneFixture
    {
        private const uint SeedA = 0x5EEDBEEFu;
        private const uint SeedB = 0x13579BDFu;

        /// <summary>
        /// Fixed steps per capture: five simulated seconds, which crosses several chunk boundaries at the
        /// opening speed and still sits inside the flat part of the difficulty ramp, where the only thing that
        /// can make two runs differ is the seed.
        /// </summary>
        private const int CaptureSteps = 300;

        // ── Soak budget ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>60 simulated seconds at <see cref="GreyboxSceneFixture.Step"/>.</summary>
        private const int SoakSteps = 3600;

        /// <summary>Steps per rendered frame, so the soak finishes in a sane number of frames and each frame is still measurable.</summary>
        private const int StepsPerFrame = 12;

        /// <summary>Frames spent doing nothing but the harness's own tick, to price what the runner itself allocates.</summary>
        private const int CalibrationFrames = 30;

        /// <summary>
        /// Room for twelve extra simulation steps above the calibrated floor: about 42 bytes per step. That is
        /// enough to excuse nothing except a stray boxed value, which is precisely what §11.2 forbids.
        /// </summary>
        private const long StepAllowanceBytes = 512;

        /// <summary>
        /// Room for the single worst frame to exceed the worst calibration frame. A pool miss instantiates a
        /// prefab and a chunk spawns a handful of obstacles, so a real regression shows up as one expensive
        /// frame rather than as a raised average — which is why an average alone is not enough (§4.6).
        /// </summary>
        private const long BurstAllowanceBytes = 8192;

        // ── Determinism ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Restarts the run under <paramref name="seed"/> and records every chunk the generator brings up.
        /// No <c>yield</c> appears in this method and none is needed: the start completes synchronously in M1,
        /// which is what keeps both captures in the same frame and therefore comparable.
        /// </summary>
        private void CaptureSequence(uint seed, List<string> into)
        {
            into.Clear();

            // Ending is a no-op when nothing is live, so the auto-started greybox run and the capture before
            // this one are both handled by the same line.
            _run.EndRun(EndRunReason.PlayerQuit);

            var ctx = new RunStartContext(seed, _worldId, StableId.None, StableId.None);
            Task<Result> start = _run.StartRunAsync(ctx, CancellationToken.None);

            Assert.That(start.IsCompleted, Is.True,
                "StartRunAsync is Task.FromResult in M1. If it now needs frames, the captures below are no "
                + "longer comparable frame-for-frame: budget them by distance instead of relaxing this.");
            Assert.That(start.Result.IsSuccess, Is.True,
                $"the seeded restart was refused: {start.Result.ErrorCode} — {start.Result.Message}");

            // Set after the start, which resets the motor, so nothing has been consumed yet.
            Assert.That(_run.State, Is.EqualTo(RunState.Running));

            Action<ChunkActivatedEvent> record = evt => into.Add(Describe(evt));
            _bus.Subscribe(record);

            for (int i = 0; i < CaptureSteps; i++) _run.Tick(Step);

            _bus.Unsubscribe(record);

            Assert.That(into.Count, Is.GreaterThanOrEqualTo(4),
                $"only {into.Count} chunk(s) activated in {CaptureSteps} steps. A two-element capture cannot "
                + "distinguish a seeded generator from a broken one, so this is the test being vacuous, not passing.");
        }

        /// <summary>
        /// Identity, draw order and span in one comparable string. Invariant culture because the distance is a
        /// double and a locale that prints a comma for a decimal point must not be able to make two identical
        /// runs look different.
        /// </summary>
        private static string Describe(ChunkActivatedEvent evt)
        {
            return evt.ChunkId.Value + "|"
                   + evt.SequenceIndex.ToString(CultureInfo.InvariantCulture) + "|"
                   + evt.StartDistance.ToString("R", CultureInfo.InvariantCulture);
        }

        [UnityTest]
        public IEnumerator TheSameSeedRebuildsTheSameChunks_OnTheShippedWiring()
        {
            var first = new List<string>(16);
            var second = new List<string>(16);

            CaptureSequence(SeedA, first);
            CaptureSequence(SeedA, second);

            Assert.That(second, Is.EqualTo(first),
                "the same seed produced a different track on the second run. The seed reached the generator — "
                + "the summary below proves that — so something else is feeding selection: check that "
                + "TrackRunContext.Random is the run's forked stream and that no rule or weight caches state "
                + "across Initialize (§4.5, ADR-0005).");

            // The summary is the run's own record of the context it was built from, so this is the half of the
            // assertion that says "the seed in the test is the seed the run reports". Without it, a controller
            // that ignored RunStartContext.Seed entirely would still pass above if the generator happened to be
            // reset deterministically.
            RunSummary summary = _run.Summary;
            Assert.That(summary.Seed, Is.EqualTo(SeedA),
                "the finished run does not report the seed it was started with, so a replay request built from "
                + "the summary would not reproduce it (§4.12).");
            Assert.That(summary.WorldId, Is.EqualTo(_worldId));

            _run.EndRun(EndRunReason.PlayerQuit);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ADifferentSeedDiverges_TheSeedIsNotDecoration()
        {
            var a = new List<string>(16);
            var b = new List<string>(16);

            CaptureSequence(SeedA, a);
            CaptureSequence(SeedB, b);

            Assert.That(b, Is.Not.EqualTo(a),
                $"two different seeds drew the same {a.Count} chunks. The probability of that from a working "
                + "generator is negligible, so the likely reading is that the seed never reaches the track's "
                + "random stream, or that the catalog offers exactly one eligible chunk at difficulty 0 and "
                + "weighting has nothing to choose between (§4.5).");

            _run.EndRun(EndRunReason.PlayerQuit);
            yield return null;
        }

        // ── Soak ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A minute of generated track: does the run survive, does the recycling bound hold, do the pools stay
        /// inside their prewarm, and does the loop allocate. The rig is made invulnerable for the duration, which
        /// is what makes a fixed-length soak possible at all — <see cref="PlayerContactSensor"/> is the only
        /// thing in the build that reports a crash (§4.7), and it returns before resolving a contact while the
        /// motor is invulnerable. The cost is that this case proves nothing about support surfaces or stumbles,
        /// which is the run-loop fixture's job.
        /// </summary>
        [UnityTest]
        public IEnumerator ASixtySecondSoak_Survives_Recycles_AndAllocatesNothingPerStep()
        {
            _run.EndRun(EndRunReason.PlayerQuit);
            Task<Result> start = _run.StartRunAsync(
                new RunStartContext(SeedA, _worldId, StableId.None, StableId.None), CancellationToken.None);
            while (!start.IsCompleted) yield return null;

            Assert.That(start.Result.IsSuccess, Is.True,
                $"the soak could not start a seeded run: {start.Result.ErrorCode} — {start.Result.Message}");

            // 600 seconds of cover against ~70 seconds of simulated plus real time: the motor counts the
            // remaining invulnerability down every step, so this is a ceiling, not a period to be tested.
            _motor.SetInvulnerable(600f);

            ProfilerRecorder recorder =
                ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            if (!recorder.valid)
            {
                recorder.Dispose();
                Assert.Ignore("GC Allocated In Frame is unavailable; the profiling module must be enabled.");
            }

            int recycled = 0;
            Action<ChunkRecycledEvent> countRecycle = _ => recycled++;
            _bus.Subscribe(countRecycle);

            // ── Calibration: the harness's own per-frame cost, with the run ticking exactly one real step.
            yield return null;
            long calibrationTotal = 0;
            long calibrationWorst = 0;
            for (int frame = 0; frame < CalibrationFrames; frame++)
            {
                yield return null;
                long bytes = recorder.LastValue;
                calibrationTotal += bytes;
                if (bytes > calibrationWorst) calibrationWorst = bytes;
            }
            long calibrationAverage = calibrationTotal / CalibrationFrames;

            // ── The soak itself. No logging, no string work and no assertion inside these loops: the thing
            // being measured is allocation, and a test that allocates while measuring allocation is measuring
            // itself. The recorder is disposed by hand rather than in a using block because an iterator may not
            // yield inside a using body.
            int soakFrames = SoakSteps / StepsPerFrame;
            long soakTotal = 0;
            long soakWorst = 0;
            int step = 0;

            for (int frame = 0; frame < soakFrames; frame++)
            {
                for (int i = 0; i < StepsPerFrame; i++)
                {
                    _run.Tick(Step);
                    step++;
                }

                yield return null;

                long bytes = recorder.LastValue;
                soakTotal += bytes;
                if (bytes > soakWorst) soakWorst = bytes;
            }

            long soakAverage = soakTotal / soakFrames;
            recorder.Dispose();
            _bus.Unsubscribe(countRecycle);

            TrackGenerationConfig cfg = _database.TrackGeneration;
            Assert.That(cfg, Is.Not.Null, "the catalog has no TrackGenerationConfig, so the live-window bound below is uncomputable.");
            int live = _generator.LiveCount;
            int generated = _generator.Debug.GeneratedCount;
            int recycledByCounter = _generator.Debug.RecycledCount;
            int fallbacks = _generator.Debug.FallbackCount;
            float distance = _motor.DistanceRun;

            Debug.Log($"[soak] {step} steps, {distance:F0} m, {generated} chunks generated, "
                      + $"{recycledByCounter} recycled, {live} live, {fallbacks} fallbacks; "
                      + $"GC/frame calibrated {calibrationAverage} B (worst {calibrationWorst}), "
                      + $"soak {soakAverage} B (worst {soakWorst}).");

            // ── It survived.
            Assert.That(_run.State, Is.EqualTo(RunState.Running),
                "a minute of authored track ended the run. Nothing should have been able to crash an "
                + "invulnerable rig, so read the console: this is a system publishing a crash outside the "
                + "contact sensor, which §4.7 says cannot happen.");
            Assert.That(distance, Is.GreaterThan(400f),
                $"the run travelled only {distance:F0} m in 60 simulated seconds; the speed profile is not "
                + "ramping as authored (§4.9).");

            // ── The recycling bound actually holds, which is what keeps the live set a constant.
            Assert.That(recycledByCounter, Is.GreaterThan(0),
                "no chunk was ever returned over a whole minute, so the live list would grow without bound "
                + "and §4.6's floating origin has nothing to re-base (§4.6, ADR-0014).");
            Assert.That(recycled, Is.EqualTo(recycledByCounter),
                $"{recycled} ChunkRecycledEvent(s) for {recycledByCounter} recycle(s): the counter and the "
                + "event are supposed to be one code path, and a consumer that recycles silently is a leak "
                + "no profiler will attribute (§4.5).");
            Assert.That(live, Is.LessThanOrEqualTo(cfg.chunksBehind + cfg.chunksAhead + 2),
                $"{live} chunks live against an authored window of behind {cfg.chunksBehind} + ahead "
                + $"{cfg.chunksAhead}. The generator holds a fixed-size window on purpose; this is the check "
                + "that it stays that way (§4.5).");
            Assert.That(generated, Is.GreaterThan(cfg.chunksAhead),
                "the generator never got ahead of the player, so the streamer would have had nothing to spawn.");

            // ── The corpus satisfied the ramp without emergency geometry.
            Assert.That(fallbacks, Is.EqualTo(0),
                $"{fallbacks} chunk(s) came from the fallback path. A weighted selection that has to reach for "
                + "the emergency chunk means the authored greybox corpus cannot satisfy the difficulty the ramp "
                + "reaches by now — that is a content defect this soak exists to find (§4.5).");
            Assert.That(_pools.OverflowCount, Is.EqualTo(0),
                "a pool ran dry and grew at runtime. The prewarm counts in the PoolCatalog are the authoring "
                + "answer to a minute of this track, so an overflow is the test telling us they are too small "
                + "(§4.6, §11.2).");

            // ── Allocation, against the harness's own floor.
            Assert.That(soakAverage, Is.LessThanOrEqualTo(calibrationAverage + StepAllowanceBytes),
                $"twelve extra simulation steps cost {soakAverage - calibrationAverage} B per frame over the "
                + $"calibrated floor of {calibrationAverage} B. The run loop is meant to allocate nothing (§11.2); "
                + "use the Frame Cutting profiler on a frame rather than guessing which system boxed something.");
            Assert.That(soakWorst, Is.LessThanOrEqualTo(calibrationWorst + BurstAllowanceBytes),
                $"the worst soak frame spent {soakWorst - calibrationWorst} B above the worst calibration "
                + "frame. A single expensive frame in a minute is how a pool miss or a one-off Instantiate "
                + "shows up, and it is a hitch the player can see even when the average is clean (§4.6).");

            _run.EndRun(EndRunReason.PlayerQuit);
            yield return null;
        }
    }
}
