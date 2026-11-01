using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Skyrift.Bootstrap;
using Skyrift.Controls;
using Skyrift.Core;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Core.Services;
using Skyrift.Data;
using Skyrift.Editor;
using Skyrift.Gameplay;
using Skyrift.Track;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Skyrift.Tests.PlayMode
{
    /// <summary>
    /// The M1 acceptance test (TECH_DOC §12.1): the shipped Boot scene boots, runs, crashes, continues and
    /// finishes, driven through the real composition root.
    ///
    /// The bring-up lives in <see cref="GreyboxSceneFixture"/> — why the scene is loaded rather than hand-wired,
    /// why additively, and the per-test teardown that keeps these cases order-independent. This fixture only
    /// asserts behaviour, so each case below starts with the graph already resolved in <c>_run</c>, <c>_motor</c>,
    /// <c>_buffer</c>, <c>_generator</c>, <c>_pools</c>, <c>_bus</c> and <c>_database</c>.
    /// </summary>
    [TestFixture]
    public class GreyboxRunLoopTests : GreyboxSceneFixture
    {
        // ── Boot ────────────────────────────────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator TheGreyboxBootsIntoARunningSimulation()
        {
            // The scene is authored with autoStartRun, so "boot finished" and "the player is moving" are the
            // same event. This is the assertion that the greybox is playable, not merely constructible.
            Assert.That(_run.IsReady, Is.True, "RunController.Construct rejected its RunSystems bundle.");
            Assert.That(_run.State, Is.EqualTo(RunState.Running),
                "autoStartRun is set in the greybox, so UiRootStep should have started a run");
            Assert.That(_database.IsValid, Is.True, "the catalog was accepted by the validator at boot (§5.14).");

            yield return null;
            yield return null;

            Assert.That(_motor.DistanceRun, Is.GreaterThan(0f), "the forward motion is automatic — no input needed.");
            Assert.That(_motor.CurrentSpeed, Is.GreaterThan(0f), "the speed profile is applied on the first step.");
            Assert.That(_motor.Lane, Is.EqualTo(_database.Balance.laneCount / 2),
                "the run starts in the centre lane, which is what makes a lane change observable in both directions.");

            // Coverage proves the generator drew chunks and the streamer spawned them from the pool.
            Assert.That(_generator.LiveCount, Is.GreaterThan(0), "no chunk is live ahead of the player.");
            Assert.That(_generator.PeekNext(1).IsValid, Is.True, "the next chunk has not been generated.");
            Assert.That(_pools.Keys, Is.Not.Empty, "the PoolCatalog contributed no pool.");
            Assert.That(_pools.OverflowCount, Is.EqualTo(0),
                "a pool that overflowed on the opening spawn is below the authored prewarm count (§11.2, §5.14 rule 10).");
        }

        [UnityTest]
        public IEnumerator TheBootSceneIsInTheBuildList_SoThisRunCanActuallyShip()
        {
            // Loading additively works from the editor whether or not the scene is in Build Settings, so the
            // tests above cannot catch the failure that actually ships: a greybox that runs in the editor and
            // launches into a blank player. Asserted here because it is the only place it is cheap to catch.
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            bool found = false;
            bool enabled = false;

            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path != SkyriftPaths.BootScene) continue;
                found = true;
                enabled = scenes[i].enabled;
                break;
            }

            Assert.That(found, Is.True, $"{SkyriftPaths.BootScene} is missing from EditorBuildSettings.scenes.");
            Assert.That(enabled, Is.True, "the boot scene is in the build list but switched off.");

            // Nothing to wait for, but a UnityTest must return an enumerator.
            yield break;
        }

        // ── One run at a time ───────────────────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator ASecondStartIsRefused_AndTheLiveRunIsUntouched()
        {
            float before = _motor.DistanceRun;
            Assert.That(_run.State, Is.EqualTo(RunState.Running));

            var again = new RunStartContext(0xC0FFEEu, _worldId, StableId.None, StableId.None);
            Task<Result> pending = _run.StartRunAsync(again, CancellationToken.None);
            while (!pending.IsCompleted) yield return null;

            Assert.That(pending.Result.IsSuccess, Is.False,
                "a start that lands on a live run would tear down the track the player is standing on (§4.12).");
            Assert.That(pending.Result.ErrorCode, Is.EqualTo(ErrorCodes.AlreadyActive),
                "and the caller gets a code, not an exception — the Play button has to be able to show it.");

            Assert.That(_run.State, Is.EqualTo(RunState.Running), "the refusal left the simulation alone.");
            _run.Tick(Step);
            Assert.That(_motor.DistanceRun, Is.GreaterThan(before), "which is still advancing.");
            Assert.That(_run.Summary.Breakdown.Total, Is.EqualTo(0L),
                "Summary is the finished run's record and stays empty while one is live.");
        }

        // ── Input reaches the rig ───────────────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator ABufferedSwipeMovesTheRigOneLane()
        {
            int lane = _motor.Lane;
            float startX = _motor.LateralPosition;

            // The intent goes into the shipped buffer — the same object the Input System bridge writes to —
            // and is consumed by an explicit Tick rather than a rendered frame. The authored window is 0.2 s,
            // and a single editor frame on a cold shader cache can exceed it, so driving the step directly is
            // what keeps this a test of the wiring rather than a test of how fast the machine is (§4.3).
            _buffer.Push(InputIntent.SwipeLeft);
            Assert.That(_buffer.PendingCount, Is.EqualTo(1));

            _run.Tick(Step);

            Assert.That(_buffer.PendingCount, Is.EqualTo(0), "the intent was consumed, not dropped.");
            Assert.That(_motor.Lane, Is.EqualTo(lane - 1), "the target lane changed on the step that read the swipe.");

            // Lane is the target; the rig eases to it. Twenty steps is a third of a second of simulation.
            for (int i = 0; i < 20; i++) _run.Tick(Step);
            yield return null;

            Assert.That(Mathf.Abs(_motor.LateralPosition - startX), Is.GreaterThan(0.05f),
                "the interpolation carried the rig toward the new lane instead of teleporting it.");

            _buffer.Push(InputIntent.SwipeRight);
            _run.Tick(Step);
            Assert.That(_motor.Lane, Is.EqualTo(lane), "the opposite swipe comes back to the centre lane.");
        }

        [UnityTest]
        public IEnumerator ASwipeAtTheOuterLaneIsALegalNoOp_NotRetriedLater()
        {
            // Drive the rig to the outer lane first, so the next swipe left has nowhere to go.
            _buffer.Push(InputIntent.SwipeLeft);
            _run.Tick(Step);
            _buffer.Push(InputIntent.SwipeLeft);
            _run.Tick(Step);

            int edge = _motor.Lane;
            Assert.That(edge, Is.EqualTo(0),
                "two lane changes from the centre reach the left edge; if this is wrong the lane count or the "
                + "start lane changed, and the case below is no longer testing an edge.");

            _buffer.Push(InputIntent.SwipeLeft);
            _run.Tick(Step);

            Assert.That(_motor.Lane, Is.EqualTo(edge), "an edge swipe is refused by the motor.");
            Assert.That(_buffer.PendingCount, Is.EqualTo(0),
                "and consumed: holding it would fire a lane change seconds later, after the player had given up (§4.3).");

            for (int i = 0; i < 20; i++) _run.Tick(Step);
            Assert.That(_motor.Lane, Is.EqualTo(edge), "and nothing appeared late.");

            yield return null;
        }

        // ── Pause ───────────────────────────────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator APauseStopsTheWorld_WithoutTouchingTimeScale()
        {
            Assert.That(_run.State, Is.EqualTo(RunState.Running));
            float frozen = _motor.DistanceRun;

            _run.Pause();
            Assert.That(_run.State, Is.EqualTo(RunState.Paused),
                "the flow owns the pause, so this came back through GameFlowChangedEvent (§4.2).");

            // Real frames, so GameRoot.Update is ticking against a paused RunClock.
            yield return null;
            yield return null;
            yield return null;

            Assert.That(_motor.DistanceRun, Is.EqualTo(frozen),
                "a paused run advances by exactly nothing — DeltaTime is 0, not merely small.");
            Assert.That(Time.timeScale, Is.EqualTo(1f).Within(1e-4f),
                "and Time.timeScale was never touched: UI, the crash animation and audio run on unscaled time (§4.12).");
            Assert.That(_run.ContinueSecondsRemaining, Is.EqualTo(0f), "no offer is up while paused.");

            // A crash reported during a pause must not resurrect a frozen run.
            _bus.Publish(new PlayerCrashedEvent(CrashCause.HeadOnObstacle, default));
            Assert.That(_run.State, Is.EqualTo(RunState.Paused), "the freeze guard in OnPlayerCrashed held.");

            _run.Resume();
            Assert.That(_run.State, Is.EqualTo(RunState.Running));

            for (int i = 0; i < 10; i++) _run.Tick(Step);
            Assert.That(_motor.DistanceRun, Is.GreaterThan(frozen), "the world started again.");
        }

        // ── Crash → continue → decline → results ────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator TheCrashContinueDeclineSequence_RunsOnTheShippedWiring()
        {
            ContinueConfig config = _database.Continue;
            Assert.That(config, Is.Not.Null, "the catalog has no ContinueConfig.");

            Assert.That(_run.State, Is.EqualTo(RunState.Running));

            // One deterministic step first, so the run has travelled a non-zero distance whatever the render
            // rate did while the scene was loading. The summary is asserted below, and it reads the motor.
            _run.Tick(Step);

            // The contact sensor is the only thing that normally publishes this; publishing it here makes the
            // crash deterministic without depending on where the nearest obstacle happens to be.
            _bus.Publish(new PlayerCrashedEvent(CrashCause.HeadOnObstacle, default));

            // The greybox scene sets allowFreeContinues, and PlayMode tests run in the editor, so UiRootStep
            // installed DebugContinueOfferPolicy. If this assertion ever fails, either the scene was rebuilt
            // with the flag off or ContinueConfig.enabled was cleared — both are real changes, not flakiness.
            Assert.That(_run.State, Is.EqualTo(RunState.ContinueOffer),
                "a crash should have offered a continue (DEV/editor + ContinueConfig.enabled). If it ended "
                + "instead, the shipped NoContinueOfferPolicy is in use and the sequence below cannot be exercised.");

            Assert.That(_run.ContinueSecondsRemaining, Is.GreaterThan(0f), "the offer counts down in real time.");
            Assert.That(_run.ContinueSecondsRemaining, Is.EqualTo(config.offerSeconds).Within(1f),
                "and the countdown is the authored one, not a constant in the controller.");

            ContinueOffer offer = _run.BuildContinueOffer();
            Assert.That(offer.IsAvailable, Is.True);
            Assert.That(offer.KeyCost, Is.EqualTo(config.KeyCostFor(0)),
                "the first continue is priced from data (§4.12).");
            Assert.That(offer.ContinueIndex, Is.EqualTo(0));

            Task<ContinueResult> grant = _run.TryContinueAsync(ContinueSource.DebugFree, CancellationToken.None);
            while (!grant.IsCompleted) yield return null;

            Assert.That(grant.Result.IsSuccess, Is.True, "the debug policy grants the continue it just offered.");
            Assert.That(_run.State, Is.EqualTo(RunState.Running), "and the run resumes in place (§4.12).");
            Assert.That(_run.ContinueSecondsRemaining, Is.EqualTo(0f), "the offer came down with the grant.");
            Assert.That(_motor.IsInvulnerable, Is.True,
                "respawnInvulnerabilitySeconds is applied, or the player dies again on the obstacle that killed them.");

            // Second crash: the offer comes back, escalated, because one of maxContinuesPerRun is spent.
            _bus.Publish(new PlayerCrashedEvent(CrashCause.HeadOnObstacle, default));
            Assert.That(_run.State, Is.EqualTo(RunState.ContinueOffer), "the cap allows a second continue.");

            ContinueOffer second = _run.BuildContinueOffer();
            Assert.That(second.ContinueIndex, Is.EqualTo(1), "the policy is told how many were already used.");
            Assert.That(second.KeyCost, Is.EqualTo(config.KeyCostFor(1)), "and the price escalated from data.");

            _run.EndRun(EndRunReason.ContinueDeclined);

            RunSummary summary = _run.Summary;
            Assert.That(_run.State, Is.EqualTo(RunState.Ended));
            Assert.That(summary.Reason, Is.EqualTo(EndRunReason.ContinueDeclined));
            Assert.That(summary.CrashCause, Is.EqualTo(CrashCause.HeadOnObstacle),
                "a declined continue is still a crash the results screen can explain.");
            Assert.That(summary.ContinuesUsed, Is.EqualTo(1), "the grant was counted, the decline was not.");
            Assert.That(summary.DistanceMetres, Is.GreaterThan(0f));
            Assert.That(summary.Seed, Is.Not.EqualTo(0u), "the auto-start derived a seed from the clock.");
            Assert.That(summary.Stumbles, Is.EqualTo(0), "the crash was reported, not earned: no contact happened.");

            // A report arriving after the run is committed must not overwrite the finished summary.
            _bus.Publish(new PlayerCrashedEvent(CrashCause.ChaserCaught, default));
            Assert.That(_run.Summary.CrashCause, Is.EqualTo(CrashCause.HeadOnObstacle),
                "the cause is still the one that ended the run, not the one that arrived after it (§4.12).");
        }

        // ── Play again ──────────────────────────────────────────────────────────────────────────────────

        [UnityTest]
        public IEnumerator AFinishedRunCanStartAgain_AndTheSummaryCarriesItsOwnContext()
        {
            _run.Tick(Step);
            _run.EndRun(EndRunReason.PlayerQuit);

            Assert.That(_run.State, Is.EqualTo(RunState.Ended));
            Assert.That(_run.Summary.Reason, Is.EqualTo(EndRunReason.PlayerQuit));
            Assert.That(_run.Summary.CrashCause, Is.EqualTo(CrashCause.None),
                "a quit is not a crash: the results screen must not offer a continue for it.");

            float firstRunDistance = _motor.DistanceRun;

            const uint seed = 0x5EEDBEEFu;
            var second = new RunStartContext(seed, _worldId, StableId.None, StableId.None);
            Task<Result> start = _run.StartRunAsync(second, CancellationToken.None);
            while (!start.IsCompleted) yield return null;

            Assert.That(start.Result.IsSuccess, Is.True,
                $"Results → Running is a legal flow edge (§6.1): {start.Result.ErrorCode} {start.Result.Message}");
            Assert.That(_run.State, Is.EqualTo(RunState.Running));
            Assert.That(_motor.DistanceRun, Is.LessThan(firstRunDistance),
                "the motor was reset, so the second run is measured from zero and not from where the first stopped.");

            for (int i = 0; i < 30; i++) _run.Tick(Step);

            _run.EndRun(EndRunReason.PlayerQuit);

            RunSummary summary = _run.Summary;
            Assert.That(summary.Seed, Is.EqualTo(seed), "the summary reports the context it was built from (§4.12).");
            Assert.That(summary.WorldId, Is.EqualTo(_worldId));
            Assert.That(summary.DistanceMetres, Is.GreaterThan(0f));
            Assert.That(summary.Score, Is.GreaterThan(0L),
                "distance alone scores at the authored weight, so a finished run is never a blank results screen.");

            yield return null;
        }
    }
}
