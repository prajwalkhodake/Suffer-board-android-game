using System;
using System.Threading;
using System.Threading.Tasks;
using Skyrift.Audio;
using Skyrift.Controls;
using Skyrift.Core;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Flow;
using Skyrift.Core.Ids;
using Skyrift.Core.Randomization;
using Skyrift.Core.Time;
using Skyrift.Data;
using Skyrift.Track;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Composition-time bundle of everything a run drives (TECH_DOC §4.12). Named fields rather than a long
    /// positional constructor, because several of these share an interface family and a transposed argument
    /// would compile. Built once by the composition root with an object initializer; never read per frame.
    ///
    /// <see cref="Flow"/> and <see cref="Music"/> are optional: an EditMode test drives the run without a
    /// game flow or an audio service, and the controller must still behave identically.
    /// </summary>
    public sealed class RunSystems
    {
        public IGameDatabase Database;
        public IPoolRegistry Pools;
        public IPlayerMotor Motor;
        public ITrackGenerator Generator;
        public TrackStreamer Streamer;
        public ObstacleChunkPopulator Populator;
        public ICollisionResolver Resolver;
        public PlayerContactSensor Sensor;
        public IChaserSystem Chaser;
        public IScoreCalculator Score;
        public IInputCommandBuffer InputBuffer;
        public IInputSource Input;
        public IContinueOfferPolicy ContinuePolicy;
        public IWorldOriginShifter Origin;
        public Transform TrackRoot;

        /// <summary>Optional. Absent in headless tests.</summary>
        public IMusicIntensityDriver Music;

        /// <summary>Optional. When present it is the single authority on pause/resume and on flow state.</summary>
        public IGameFlow Flow;
    }

    /// <summary>
    /// The only place that starts, pauses and ends a run, and the only place that drives the
    /// crash → continue → results sequence (TECH_DOC §4.12, §6.2, §6.3).
    ///
    /// It owns the <em>simulation</em> tick order and nothing else. Presentation ticks (audio, music
    /// intensity) belong to the composition root and run on unscaled time, so the mix keeps easing through
    /// a crash freeze instead of stopping dead with the world.
    ///
    /// It holds no balance numbers: every price, cap, timer and clearance length comes from
    /// <see cref="ContinueConfig"/> / <see cref="BalanceConfig"/> through the injected policy and systems.
    /// </summary>
    public sealed class RunController : IRunController, IDisposable
    {
        private readonly RunClock _clock;
        private readonly IEventBus _bus;
        private readonly ILog _log;

        private RunSystems _s;
        private IGameDatabase _database;

        private RunState _state = RunState.Idle;
        private RunStartContext _context;
        private WorldDefinition _world;
        private RunSummary _summary;

        private double _elapsed;
        private int _continuesUsed;
        private CrashCause _crashCause;

        private ContinueOffer _offer;
        private float _offerRemaining;

        // An intent consumed while the motor was locked is held here and retried, with the expiry still
        // measured from the original push (§4.3). Re-pushing into the buffer would re-stamp it every frame
        // and it would never expire.
        private InputIntent _pendingIntent;
        private double _pendingExpiryTicks;

        private bool _subscribed;
        private bool _disposed;
        private bool _ready;

        public RunController(RunClock clock, IEventBus bus, ILog log)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _bus = bus;
            _log = log;
        }

        public RunState State => _state;

        public bool IsReady => _ready && !_disposed;

        /// <summary>The committed summary of the last finished run; <c>default</c> before the first run ends.</summary>
        public RunSummary Summary => _summary;

        public float ContinueSecondsRemaining => _state == RunState.ContinueOffer ? Mathf.Max(0f, _offerRemaining) : 0f;

        /// <summary>Composition-root injection. Call once, before <see cref="Subscribe"/>.</summary>
        public void Construct(RunSystems systems)
        {
            _s = systems;
            _database = systems != null ? systems.Database : null;
            _ready = ValidateSystems(systems);
        }

        public void Subscribe()
        {
            if (_subscribed || _bus == null) return;
            _subscribed = true;

            _bus.Subscribe<PlayerCrashedEvent>(OnPlayerCrashed);
            _bus.Subscribe<PlayerStumbledEvent>(OnPlayerStumbled);
            _bus.Subscribe<PlayerRecoveredEvent>(OnPlayerRecovered);
            _bus.Subscribe<GameFlowChangedEvent>(OnFlowChanged);
            _bus.Subscribe<RunStartRequestedEvent>(OnRunStartRequested);
            _bus.Subscribe<ContinueRequestedEvent>(OnContinueRequested);
        }

        public void Unsubscribe()
        {
            if (!_subscribed || _bus == null) return;
            _subscribed = false;

            _bus.Unsubscribe<PlayerCrashedEvent>(OnPlayerCrashed);
            _bus.Unsubscribe<PlayerStumbledEvent>(OnPlayerStumbled);
            _bus.Unsubscribe<PlayerRecoveredEvent>(OnPlayerRecovered);
            _bus.Unsubscribe<GameFlowChangedEvent>(OnFlowChanged);
            _bus.Unsubscribe<RunStartRequestedEvent>(OnRunStartRequested);
            _bus.Unsubscribe<ContinueRequestedEvent>(OnContinueRequested);
        }

        // ── Run start ────────────────────────────────────────────────────────────────────────────────

        public Task<Result> StartRunAsync(RunStartContext ctx, CancellationToken ct)
        {
            // M1 resolves everything from the local catalog, so a start is synchronous. The signature is
            // already async because M3 inserts Addressables world/character loads at the resolve step;
            // callers must not assume it completes within the frame.
            return Task.FromResult(StartRun(ctx, ct));
        }

        private Result StartRun(RunStartContext ctx, CancellationToken ct)
        {
            if (_disposed) return Result.Fail(ErrorCodes.NotAvailable, "Run controller is disposed.");
            if (!_ready) return Result.Fail(ErrorCodes.NotAvailable, "RunController.Construct was not completed.");

            // Only one run may be active (§4.12). Starting is also rejected so a double Play press cannot
            // tear down a run that is already being built.
            if (_state != RunState.Idle && _state != RunState.Ended)
                return Result.Fail(ErrorCodes.AlreadyActive, "A run is already active.");

            if (ct.IsCancellationRequested) return Result.Fail(ErrorCodes.Cancelled);

            _state = RunState.Starting;

            try
            {
                WorldDefinition world = ResolveWorld(ctx.WorldId);
                if (world == null)
                    return AbortStart(ErrorCodes.NotFound, "No world definition is available for this run.");

                BalanceConfig balance = _database.Balance;
                if (balance == null || _database.TrackGeneration == null || _database.Input == null)
                    return AbortStart(ErrorCodes.CatalogInvalid, "Balance, track generation or input config is missing.");

                // Release the previous run's content and restore the world before anything is spawned, so
                // the opening frame is at origin zero with a full pool (§6.2 failure path also lands here).
                TeardownRun();

                _context = ctx;
                _world = world;
                _continuesUsed = 0;
                _elapsed = 0d;
                _crashCause = CrashCause.None;
                _offer = ContinueOffer.NotAvailable;
                _offerRemaining = 0f;
                _summary = default;

                // Prewarm is a loading-phase operation. Growth is allowed here and forbidden for the whole
                // run, so a mid-run Instantiate can never sneak in (§11.2 rule 1, ADR-0007).
                _s.Pools.SetGrowthAllowed(true);
                _s.Pools.PrewarmAll();
                _s.Pools.SetGrowthAllowed(false);

                if (ct.IsCancellationRequested)
                {
                    TeardownRun();
                    _state = RunState.Idle;
                    return Result.Fail(ErrorCodes.Cancelled);
                }

                // One RNG per run, forked per subsystem so track draws and obstacle draws never consume each
                // other's sequence: adding a consumer cannot change an existing run's layout (ADR-0005).
                var runRandom = new XorshiftRandom(ctx.Seed);

                _s.Resolver.ResetForRun();
                _s.Score.ResetForRun();
                _s.Motor.ResetForRun(ctx);
                _s.Motor.SetSpeedProfile(SpeedProfileHandle.FromBalance(balance, ctx.SpeedScale));
                _s.Chaser.ResetForRun();
                _s.Sensor.ResetForRun();

                if (_s.Music != null)
                {
                    _s.Music.ResetForRun();
                    _s.Music.SetMaxSpeed(balance.maxSpeed * ctx.SpeedScale);
                }

                var trackContext = new TrackRunContext(_database.TrackGeneration, world, _database,
                    runRandom.Fork("track"), _s.Pools, _bus, _log, _s.Origin, _s.TrackRoot, _s.Populator);

                _s.Generator.Initialize(in trackContext);
                // After Initialize, which resets the scalar to 1 and generates the opening runway.
                _s.Generator.SetDifficultyScale(ctx.DifficultyScale);
                _s.Streamer.Construct(_s.Generator, _s.Origin);
                _s.Streamer.SetPlayerSpeed(_s.Motor.CurrentSpeed);

                _clock.ResetForRun();
                _state = RunState.Running;

                if (_s.Flow != null && !_s.Flow.TryTransition(GameFlowState.Running, new TransitionContext("run_started")))
                {
                    // Flow refused (e.g. a maintenance flag landed mid-load). Never leave a live simulation
                    // running behind a screen that is not the run screen (§6.2 failure path).
                    TeardownRun();
                    _state = RunState.Idle;
                    return Result.Fail(ErrorCodes.NotAvailable, "Game flow refused the Running state.");
                }

                _s.Input.SetEnabled(true);
                _s.Sensor.SetEnabled(true);

                _bus?.Publish(new RunStartedEvent(ctx.Seed, world.Id, ctx.IsTutorial));

                _log?.Info(LogChannel.Gameplay,
                    $"Run started. seed={ctx.Seed} world={world.Id.Value} character={ctx.CharacterId.Value} tutorial={ctx.IsTutorial}");

                return Result.Ok();
            }
            catch (Exception e)
            {
                // A start failure is a value, never an exception through the UI (§4.0). The detail is logged
                // because this is the one place a catalog or scene wiring error becomes visible.
                _log?.Error(LogChannel.Gameplay, $"Run start failed: {e.GetType().Name}: {e.Message}");
                TeardownRun();
                _state = RunState.Idle;
                if (_s.Flow != null) _s.Flow.TryTransition(GameFlowState.Home, new TransitionContext("run_start_failed"));
                return Result.Fail(ErrorCodes.SystemError, e.Message);
            }
        }

        private Result AbortStart(string code, string message)
        {
            _log?.Error(LogChannel.Gameplay, $"Run start refused: {message}");
            _state = RunState.Idle;
            return Result.Fail(code, message);
        }

        /// <summary>
        /// Resolves the requested world, falling back to the first authored one. M1 has no world selection
        /// UI, so an unset id is the normal case rather than an error; an empty catalog is a real failure.
        /// </summary>
        private WorldDefinition ResolveWorld(StableId worldId)
        {
            if (_database == null || _database.Worlds.Count == 0) return null;
            if (worldId.IsValid && _database.Worlds.TryGet(worldId, out WorldDefinition world) && world != null)
                return world;

            return _database.Worlds.All[0];
        }

        // ── Tick ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One simulated step. The order is load-bearing (§4.12):
        /// input → motor → track coverage/recycle/shift → obstacle motion → chaser → score.
        ///
        /// The motor moves before the track streams so coverage is computed from the position the player
        /// actually reached this frame, and the score integrates the same speed × dt the motor used, so the
        /// two distances cannot drift apart.
        ///
        /// Contacts arrive from physics in FixedUpdate, which Unity runs before Update — a hit is therefore
        /// classified against last frame's transform and applied on this one. At the authored 30 m/s cap
        /// that is 0.5 m of travel, well inside the smallest obstacle footprint, and it is the reason the
        /// collision model is analytic rather than a physics sweep (§4.7).
        /// </summary>
        public void Tick(float dt)
        {
            if (_disposed || !_ready) return;

            if (_state == RunState.ContinueOffer)
            {
                // The run clock is frozen at Scale 0 here, so dt is 0. The countdown is real time: an offer
                // that only advanced on simulated time would never expire.
                TickContinueOffer(_clock.UnscaledDeltaTime);
                return;
            }

            if (_state != RunState.Running || dt <= 0f) return;

            ConsumeInput();

            // A Pause intent consumed above changes state and freezes the clock mid-frame; the rest of the
            // step must not run against a paused run, and that frame is not run time.
            if (_state != RunState.Running) return;

            _elapsed += dt;

            _s.Motor.Tick(dt);

            float speed = _s.Motor.CurrentSpeed;
            float distance = _s.Motor.DistanceRun;

            _s.Streamer.SetPlayerSpeed(speed);
            _s.Streamer.Tick(distance);
            _s.Populator.Tick(dt);
            _s.Chaser.Tick(dt, speed);
            _s.Score.Tick(dt, speed);
        }

        private void TickContinueOffer(float unscaledDt)
        {
            if (unscaledDt <= 0f) return;

            _offerRemaining -= unscaledDt;
            if (_offerRemaining > 0f) return;

            _offerRemaining = 0f;
            // Expiry is a decline, never a free continue (ContinueConfig.offerSeconds).
            EndRun(EndRunReason.ContinueDeclined);
        }

        // ── Input ────────────────────────────────────────────────────────────────────────────────────

        private void ConsumeInput()
        {
            // A fresh intent always supersedes one held across a lock: newest wins (§4.3).
            if (_s.InputBuffer.TryConsume(out InputIntent fresh, out double pushedTicks))
            {
                _pendingIntent = fresh;
                _pendingExpiryTicks = pushedTicks + BufferWindowTicks;
            }

            if (_pendingIntent == InputIntent.None) return;

            if (_clock.NowUtcTicks > _pendingExpiryTicks)
            {
                ClearPending();
                return;
            }

            if (Apply(_pendingIntent)) ClearPending();
        }

        private double BufferWindowTicks =>
            (_database != null && _database.Input != null ? _database.Input.bufferWindow : 0f) * TimeSpan.TicksPerSecond;

        /// <returns>
        /// True when the intent is finished with — applied, or a legal no-op that must not be retried.
        /// False only for a swipe-up refused by the airborne lock, which is held until touchdown.
        /// </returns>
        private bool Apply(InputIntent intent)
        {
            switch (intent)
            {
                case InputIntent.SwipeLeft:
                    _s.Motor.RequestLaneChange(-1);
                    // An edge swipe is a legal no-op, not a lock. Retrying it would fire the lane change
                    // late, after the player had already given up on it.
                    return true;

                case InputIntent.SwipeRight:
                    _s.Motor.RequestLaneChange(1);
                    return true;

                case InputIntent.SwipeUp:
                    // The only intent with a locomotion lock. The motor refuses a double jump, so the
                    // controller holds the swipe and lands it on touchdown — inside the original window.
                    if (_s.Motor.VerticalState == PlayerVerticalState.Jumping ||
                        _s.Motor.VerticalState == PlayerVerticalState.Falling)
                        return false;

                    _s.Motor.RequestJump();
                    return true;

                case InputIntent.SwipeDown:
                    // A dive is legal in the air and a re-roll is a legal no-op, so this is never held.
                    _s.Motor.RequestRoll();
                    return true;

                case InputIntent.Pause:
                    Pause();
                    return true;

                case InputIntent.Tap:
                case InputIntent.Hold:
                    // M1 has no tap action: hoverboard activation is a power-up (M2) and arrives as a
                    // modifier on the motor, not as a new intent. Consumed so it cannot occupy the buffer.
                    return true;

                default:
                    return true;
            }
        }

        private void ClearPending()
        {
            _pendingIntent = InputIntent.None;
            _pendingExpiryTicks = 0d;
        }

        // ── Pause / resume ───────────────────────────────────────────────────────────────────────────

        public void Pause()
        {
            if (_disposed || !_ready) return;
            if (_state != RunState.Running) return;

            // The flow is the authority when it exists: it publishes GameFlowChangedEvent and the handler
            // below does the work, so a pause requested by app-focus loss takes exactly the same path.
            if (_s.Flow == null) SetPaused(true);
            else _s.Flow.TryTransition(GameFlowState.Paused, new TransitionContext("pause_requested"));
        }

        public void Resume()
        {
            if (_disposed || !_ready) return;
            if (_state != RunState.Paused) return;

            if (_s.Flow == null) SetPaused(false);
            else _s.Flow.TryTransition(GameFlowState.Running, new TransitionContext("resume_requested"));
        }

        private void SetPaused(bool paused)
        {
            if (paused)
            {
                if (_state != RunState.Running) return;
                _state = RunState.Paused;

                // Paused, not Scale 0: the run clock keeps its scale so a crash freeze and a pause are
                // distinguishable, and neither touches Time.timeScale (§4.12).
                _clock.Paused = true;
                _s.Sensor.SetEnabled(false);
                _s.Input.SetEnabled(false);
                _s.InputBuffer.Clear();
                ClearPending();

                _bus?.Publish(new RunPausedEvent());
            }
            else
            {
                if (_state != RunState.Paused) return;
                _state = RunState.Running;

                _clock.Paused = false;
                _s.Sensor.SetEnabled(true);
                _s.Input.SetEnabled(true);

                _bus?.Publish(new RunResumedEvent());
            }
        }

        // ── Crash → continue → results ───────────────────────────────────────────────────────────────

        private void OnPlayerCrashed(PlayerCrashedEvent evt)
        {
            // A second contact reported during the freeze is ignored: the run is already over.
            if (_state != RunState.Running) return;

            _state = RunState.Crashed;
            _crashCause = evt.Cause;

            FreezeSimulation();

            _bus?.Publish(new RunCrashedEvent(evt.Cause));

            if (_s.Flow != null) _s.Flow.TryTransition(GameFlowState.Crashed, new TransitionContext("player_crashed"));

            ContinueOffer offer = BuildContinueOffer();
            if (!offer.IsAvailable)
            {
                EndRun(EndRunReason.Crash);
                return;
            }

            _offer = offer;
            _offerRemaining = offer.SecondsRemaining;
            _state = RunState.ContinueOffer;

            if (_s.Flow != null)
                _s.Flow.TryTransition(GameFlowState.ContinueOffer, new TransitionContext("continue_offered"));

            _bus?.Publish(new ContinueOfferedEvent(offer.KeyCost, offer.ContinueIndex, offer.AdAvailable));
        }

        public ContinueOffer BuildContinueOffer()
        {
            if (_state != RunState.Crashed && _state != RunState.ContinueOffer) return ContinueOffer.NotAvailable;
            if (_s == null || _s.ContinuePolicy == null) return ContinueOffer.NotAvailable;

            // The controller never computes a price: sources, escalation and ad availability are the
            // policy's, which reads ContinueConfig and Remote Config (§4.12).
            RunSummary snapshot = Snapshot(EndRunReason.Crash);
            return _s.ContinuePolicy.Build(in snapshot, _continuesUsed);
        }

        public async Task<ContinueResult> TryContinueAsync(ContinueSource source, CancellationToken ct)
        {
            if (_disposed || !_ready) return ContinueResult.Fail(ErrorCodes.NotAvailable);
            if (_state != RunState.ContinueOffer) return ContinueResult.Fail(ErrorCodes.NotAvailable);
            if (_s.ContinuePolicy == null || !_offer.IsAvailable) return ContinueResult.Fail(ErrorCodes.NotAvailable);
            if (ct.IsCancellationRequested) return ContinueResult.Fail(ErrorCodes.Cancelled);

            // Copied before the await so an expiry during the ad cannot change what the policy was asked to
            // grant, and so no field is passed by reference across a suspension.
            ContinueOffer offer = _offer;

            ContinueResult result;
            try
            {
                result = await _s.ContinuePolicy.GrantAsync(source, in offer, ct);
            }
            catch (OperationCanceledException)
            {
                return ContinueResult.Fail(ErrorCodes.Cancelled);
            }
            catch (Exception e)
            {
                _log?.Error(LogChannel.Gameplay, $"Continue grant threw: {e.GetType().Name}: {e.Message}");
                result = ContinueResult.Fail(ErrorCodes.SystemError);
            }

            // The countdown may have expired while the purchase or the ad was in flight. The run is already
            // committed in that case; reporting success would resurrect a finished run.
            if (_state != RunState.ContinueOffer) return ContinueResult.Fail(ErrorCodes.NotAvailable);

            if (!result.IsSuccess)
            {
                // A recoverable refusal (not enough keys, ad not ready) leaves the offer up so the player can
                // pick another source. Only a cancellation ends it here; an explicit "no thanks" from the UI
                // calls EndRun(ContinueDeclined) directly, and doing nothing lets the countdown expire.
                if (result.ErrorCode == ErrorCodes.Cancelled) EndRun(EndRunReason.ContinueDeclined);
                return result;
            }

            _continuesUsed++;
            ApplyContinue(result);
            return result;
        }

        private void ApplyContinue(ContinueResult result)
        {
            ContinueConfig config = _database.Continue;

            _clock.ResetForRun();

            if (config != null && config.resetStumblesOnContinue) _s.Resolver.ResetStumbles();

            float invuln = config != null ? config.respawnInvulnerabilitySeconds : 0f;
            if (invuln > 0f) _s.Motor.SetInvulnerable(invuln);

            // Clear the runway ahead so the player does not respawn inside the obstacle that killed them.
            // The length is data, and the track geometry stays: this is a respawn, not a new run.
            if (config != null && config.respawnClearanceMetres > 0f)
            {
                double from = _s.Motor.DistanceRun;
                _s.Populator.ClearRange(from, from + config.respawnClearanceMetres);
            }

            _s.Chaser.OnPlayerRecovered();
            _s.Sensor.ResetForRun();

            _offer = ContinueOffer.NotAvailable;
            _offerRemaining = 0f;
            ClearPending();
            _s.InputBuffer.Clear();

            _state = RunState.Running;
            if (_s.Flow != null) _s.Flow.TryTransition(GameFlowState.Running, new TransitionContext("continue_granted"));

            _s.Input.SetEnabled(true);

            _bus?.Publish(new RunContinuedEvent(result.Source, result.KeysSpent));
        }

        public void EndRun(EndRunReason reason)
        {
            if (_disposed || !_ready) return;

            // Ending is also the guard against re-entrancy: RunEndedEvent subscribers may ask for a new run.
            if (_state == RunState.Idle || _state == RunState.Ended || _state == RunState.Ending) return;

            _state = RunState.Ending;
            FreezeSimulation();

            _summary = Snapshot(reason);

            // M1 has no Economy, Meta or Services. Publishing the immutable summary once is the whole commit
            // step: in M2+ the wallet grant, mission progress, analytics and save all react to this same
            // value, and the controller never grows a dependency on any of them (§3.2, §4.12).
            _bus?.Publish(new RunEndedEvent(_summary));

            _state = RunState.Ended;

            if (_s.Flow != null) _s.Flow.TryTransition(GameFlowState.Results, new TransitionContext("run_ended"));

            // The track is deliberately left standing, frozen, so the crash tableau reads behind the results
            // screen. TeardownRun releases it at the start of the next run and on Dispose, so nothing leaks.
        }

        private RunSummary Snapshot(EndRunReason reason)
        {
            bool crashed = reason == EndRunReason.Crash || reason == EndRunReason.ContinueDeclined;

            return new RunSummary(
                _s.Score.Breakdown,
                // The motor is authoritative: it is the thing that actually moved the rig. The score
                // calculator integrates the same speed × dt, so the two agree to float precision.
                _s.Motor.DistanceRun,
                _elapsed,
                _context.Seed,
                _world != null ? _world.Id : StableId.None,
                _context.CharacterId,
                reason,
                crashed ? _crashCause : CrashCause.None,
                _continuesUsed,
                _s.Resolver.StumbleCount);
        }

        private void FreezeSimulation()
        {
            // Scale 0 on the run clock, never Time.timeScale: UI, the crash animation and audio keep running
            // on unscaled time (§4.12).
            _clock.Scale = 0f;
            _s.Sensor.SetEnabled(false);
            _s.Input.SetEnabled(false);
            _s.InputBuffer.Clear();
            ClearPending();
        }

        private void TeardownRun()
        {
            _s.Sensor.SetEnabled(false);
            _s.Input.SetEnabled(false);
            _s.InputBuffer.Clear();
            ClearPending();

            // Shutdown releases every live chunk, and each release clears its pooled children first, so
            // this one call returns the whole track and its obstacles to the pools.
            _s.Generator.Shutdown();
            _s.Origin.ResetForRun();

            _clock.ResetForRun();
        }

        // ── Event routing ────────────────────────────────────────────────────────────────────────────

        private void OnPlayerStumbled(PlayerStumbledEvent evt)
        {
            // The contact sensor already snapped the chaser forward; doing it here too would be a second
            // authority for the same event. The chain break is the controller's job (§4.11).
            if (_state == RunState.Running) _s.Score.BreakCombo();
        }

        private void OnPlayerRecovered(PlayerRecoveredEvent evt)
        {
            // The motor does not know the chaser exists. Closing this loop here is what lets a clean recovery
            // drop the pursuer back to cruising distance instead of leaving it at the stumble distance.
            if (_state == RunState.Running) _s.Chaser.OnPlayerRecovered();
        }

        private void OnFlowChanged(GameFlowChangedEvent evt)
        {
            if (_disposed || !_ready) return;

            if (evt.To == GameFlowState.Paused)
            {
                SetPaused(true);
                return;
            }

            if (evt.From == GameFlowState.Paused && evt.To == GameFlowState.Running)
            {
                SetPaused(false);
                return;
            }

            // Leaving the run entirely must not leave a live simulation behind it. SetPaused and EndRun both
            // guard on state, so the transitions this controller itself published are no-ops here.
            if (evt.To == GameFlowState.Home || evt.To == GameFlowState.Maintenance)
                EndRun(EndRunReason.PlayerQuit);
        }

        private async void OnRunStartRequested(RunStartRequestedEvent evt)
        {
            // The only async-void in the run loop, and it is safe: StartRunAsync catches everything and
            // returns a Result, so no exception can escape onto the Unity synchronization context.
            Result result = await StartRunAsync(evt.Context, CancellationToken.None);
            if (!result.IsSuccess)
                _log?.Warn(LogChannel.Gameplay, $"Run start request refused: {result.ErrorCode}.");
        }

        private async void OnContinueRequested(ContinueRequestedEvent evt)
        {
            ContinueResult result = await TryContinueAsync(evt.Source, CancellationToken.None);
            if (!result.IsSuccess)
                _log?.Warn(LogChannel.Gameplay, $"Continue request refused: {result.ErrorCode}.");
        }

        // ── Composition validation ───────────────────────────────────────────────────────────────────

        private bool ValidateSystems(RunSystems s)
        {
            if (s == null)
            {
                _log?.Error(LogChannel.Gameplay, "RunController.Construct received a null RunSystems bundle.");
                return false;
            }

            // Reported as one boot-time error rather than a null-reference on the first frame of the first
            // run, which is the difference between a wiring bug QA can read and a crash report.
            string missing = null;
            AppendIfNull(s.Database, "Database", ref missing);
            AppendIfNull(s.Pools, "Pools", ref missing);
            AppendIfNull(s.Motor, "Motor", ref missing);
            AppendIfNull(s.Generator, "Generator", ref missing);
            AppendIfNull(s.Streamer, "Streamer", ref missing);
            AppendIfNull(s.Populator, "Populator", ref missing);
            AppendIfNull(s.Resolver, "Resolver", ref missing);
            AppendIfNull(s.Sensor, "Sensor", ref missing);
            AppendIfNull(s.Chaser, "Chaser", ref missing);
            AppendIfNull(s.Score, "Score", ref missing);
            AppendIfNull(s.InputBuffer, "InputBuffer", ref missing);
            AppendIfNull(s.Input, "Input", ref missing);
            AppendIfNull(s.ContinuePolicy, "ContinuePolicy", ref missing);
            AppendIfNull(s.Origin, "Origin", ref missing);

            // Checked separately: Transform needs UnityEngine.Object's overloaded ==, which also reports a
            // destroyed or unassigned scene object as null. A generic or object-typed comparison would only
            // see the managed reference and would miss exactly the wiring error this is here to catch.
            if (s.TrackRoot == null) Append("TrackRoot", ref missing);

            if (missing == null) return true;

            _log?.Error(LogChannel.Gameplay, $"RunController is missing required systems:{missing}");
            return false;
        }

        private static void AppendIfNull(object value, string name, ref string missing)
        {
            // A plain reference check: every one of these is a C# service, not a UnityEngine.Object.
            if (value != null) return;
            Append(name, ref missing);
        }

        private static void Append(string name, ref string missing)
        {
            missing = missing == null ? " " + name : missing + ", " + name;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Unsubscribe();
            if (_ready) TeardownRun();

            _s = null;
            _database = null;
        }
    }
}