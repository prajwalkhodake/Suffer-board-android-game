using System;
using System.Threading;
using System.Threading.Tasks;
using Skyrift.Audio;
using Skyrift.Controls;
using Skyrift.Core;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Flow;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Core.Randomization;
using Skyrift.Core.Services;
using Skyrift.Core.Time;
using Skyrift.Data;
using Skyrift.Gameplay;
using Skyrift.Track;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// Slot 10 — clock, random, buses, logging, pools, floating origin, game flow (TECH_DOC §4.1 step 1).
    ///
    /// The bus and the logger are handed in by <see cref="BootstrapContext"/> rather than created here:
    /// <see cref="GameBootstrap"/> owns them so that a failure of this very step can still be reported.
    /// </summary>
    public sealed class CorePrimitivesStep : IBootstrapStep
    {
        public int Order => BootstrapOrder.CorePrimitives;
        public string Name => "CorePrimitives";

        public Task<Result> ExecuteAsync(BootstrapContext context, CancellationToken ct)
        {
            IServiceContainer services = context.Services;
            BootstrapConfig config = context.Config;

            if (config.playerRoot == null) return Task.FromResult(Missing("playerRoot"));
            if (config.chaserRoot == null) return Task.FromResult(Missing("chaserRoot"));

            var unityClock = new UnityClock();
            services.Register<IClock>(unityClock);

            // The run clock is registered as its own type, not as a second IClock: the controller needs
            // Scale/Paused, and anything that only needs time resolves IClock and gets unscaled real time.
            services.Register(new RunClock(unityClock));

            services.Register(context.Bus);
            services.Register(context.Log);

            // Presentation randomness (audio pitch variance). Deliberately NOT forked from the run seed:
            // a fixed-seed repro must produce the same track and the same obstacle draws no matter how
            // many times a cue happens to vary its pitch (§4.9, ADR-0005).
            services.Register<IRandomProvider>(new XorshiftRandom(unchecked((uint)DateTime.UtcNow.Ticks)));

            // Created empty. The rows come from the PoolCatalog, which does not exist until slot 30.
            var pools = new PoolRegistry(key => context.Log.Error(LogChannel.Pooling,
                $"Pool '{key}' overflowed during a run. Its prewarm count is below the worst-case concurrent " +
                "count; raise it in the PoolCatalog (TECH_DOC §5.14 rule 10)."));
            services.Register<IPoolRegistry>(pools);

            var origin = new WorldOriginShifter(context.Bus);

            // Roots are organisational containers and stay at the identity. Anything that stores a
            // position re-bases itself through IOriginShiftListener: the generator re-bases every live
            // chunk, and RunSystemsStep registers the motor for the player rig (§4.6). Shifting a parent
            // *and* accumulating local Z would leave the stored coordinate growing without bound, which
            // is exactly the precision loss the shift exists to prevent.
            //
            // VfxRoot is the exception: particles are short-lived and hold no accumulating coordinate, so
            // translating the parent is both correct and cheaper than a listener. It is optional in M1 —
            // there is no VFX yet. PoolRoot is deliberately not registered: released objects must not move.
            origin.RegisterRoot(config.vfxRoot);
            services.Register<IWorldOriginShifter>(origin);

            // The threshold is configured by TrackGenerator.Initialize from TrackGenerationConfig at run
            // start, so there is nothing to read from the catalog here.
            services.Register<IGameFlow>(new GameFlow(context.Bus, context.Log));

            return Task.FromResult(Result.Ok());
        }

        internal static Result Missing(string field) => Result.Fail(ErrorCodes.InvalidArgument,
            $"BootstrapConfig.{field} is not assigned. Run Tools > Skyrift > Build Greybox Scene, or wire it in Boot.unity.");
    }

    /// <summary>
    /// Slot 30 — load and validate the catalog, then materialize the pools (TECH_DOC §4.1 step 3).
    ///
    /// Pool registration belongs here rather than in slot 10 because a pool row is content: it names a
    /// prefab and a prewarm count from the <see cref="PoolCatalog"/>. Audio (slot 80) resolves its voice
    /// pool, so the rows must exist before then.
    /// </summary>
    public sealed class CatalogStep : IBootstrapStep
    {
        public int Order => BootstrapOrder.Catalogs;
        public string Name => "Catalogs";

        public async Task<Result> ExecuteAsync(BootstrapContext context, CancellationToken ct)
        {
            BootstrapConfig config = context.Config;

            if (config.poolRoot == null) return CorePrimitivesStep.Missing("poolRoot");
            if (config.obstacleBehaviours == null) return CorePrimitivesStep.Missing("obstacleBehaviours");
            if (config.audioCatalog == null) return CorePrimitivesStep.Missing("audioCatalog");

            // M1 resolves content from a manifest the scene already references. M3 swaps in an
            // Addressables-backed ICatalogSource here and nothing downstream changes (ADR-0012).
            ICatalogSource source = new LocalCatalogSource(config.catalog);
            Result<CatalogManifest> loaded = await source.LoadAsync(ct);
            if (!loaded.IsSuccess) return loaded.WithoutValue();

            var report = new ValidationReport();
            var database = new GameDatabase(loaded.Value, report);

            // These two catalogs live outside GameDatabase because Data may not reference Gameplay or
            // Audio (§3.3), so nothing else validates them. Running them into the same report means one
            // boot log lists every authoring error at once.
            config.obstacleBehaviours.Validate(report);
            config.audioCatalog.Validate(report);

            Result crossReferences = database.ValidateCrossReferences(report);

            for (int i = 0; i < report.Issues.Count; i++)
            {
                ValidationIssue issue = report.Issues[i];
                if (issue.IsError) context.Log.Error(LogChannel.Bootstrap, issue.ToString());
                else context.Log.Warn(LogChannel.Bootstrap, issue.ToString());
            }

            // A missing required config is fatal in every build. Dropping invalid entries cannot recover
            // a null BalanceConfig, and starting a run without one would silently use default game feel.
            if (database.Balance == null || database.TrackGeneration == null || database.Input == null ||
                database.Scoring == null || database.Pools == null || database.Continue == null || database.Audio == null)
            {
                return Result.Fail(ErrorCodes.CatalogInvalid, "A required config asset is missing from the CatalogManifest.");
            }

            if (database.Worlds.Count == 0)
                return Result.Fail(ErrorCodes.CatalogInvalid, "The manifest contains no valid world to run in.");

#if DEV || UNITY_EDITOR
            // §4.1 step 3: an invalid catalog aborts a DEV boot. The editor validator is the first gate;
            // this is the second, for an asset hand-edited after the last validation run.
            if (!crossReferences.IsSuccess) return crossReferences;
#else
            if (!crossReferences.IsSuccess)
            {
                // PROD degrades instead of dying: the repositories already dropped the invalid entries and
                // the report above says which. A shipped build must not brick on one bad content row.
                context.Log.Error(LogChannel.Bootstrap,
                    $"Catalog has {report.ErrorCount} error(s); invalid entries were dropped and boot continues (§4.1 step 3).");
            }
#endif

            context.Services.Register<IGameDatabase>(database);

            IPoolRegistry pools = context.Services.Resolve<IPoolRegistry>();
            for (int i = 0; i < database.Pools.entries.Count; i++)
                pools.Register(database.Pools.entries[i], config.poolRoot);

            // Prewarm happens per run in RunController.StartRun, not here: a run may be started several
            // times in one session and each start must find a full pool. Growth is only ever allowed there.
            context.Log.Info(LogChannel.Bootstrap,
                $"Catalog '{database.Manifest.contentVersion}' loaded: {database.Worlds.Count} world(s), " +
                $"{database.Chunks.Count} chunk(s), {database.Obstacles.Count} obstacle(s), {pools.Keys.Count} pool(s).");

            return Result.Ok();
        }
    }

    /// <summary>
    /// Slot 80 — the audio service and the two drivers that push into it (TECH_DOC §4.1 step 8, §4.18).
    ///
    /// The drivers live in Gameplay because they subscribe to Gameplay events; Audio subscribes to nothing
    /// above it. Both are subscribed here, before any run can start, so the first <c>RunStartedEvent</c>
    /// already has the music track resolved.
    /// </summary>
    public sealed class AudioStep : IBootstrapStep
    {
        public int Order => BootstrapOrder.Audio;
        public string Name => "Audio";

        public Task<Result> ExecuteAsync(BootstrapContext context, CancellationToken ct)
        {
            BootstrapConfig config = context.Config;
            if (config.audioRoot == null) return Task.FromResult(CorePrimitivesStep.Missing("audioRoot"));

            IServiceContainer services = context.Services;
            IGameDatabase database = services.Resolve<IGameDatabase>();

            var audio = new AudioService(database.Audio, config.audioCatalog,
                services.Resolve<IPoolRegistry>(), services.Resolve<IRandomProvider>(),
                context.Log, config.audioRoot, config.mixer);
            services.Register<IAudioService>(audio);

            var music = new MusicIntensityDriver(database.Audio, audio, context.Bus, database, database.Balance);
            services.Register<IMusicIntensityDriver>(music);
            music.Subscribe();

            var sfx = new RunSfxRouter(audio, context.Bus, database, database.Balance);
            services.Register(sfx);
            sfx.Subscribe();

            // Disposal order is registration order: the service stops first, then the drivers unsubscribe.
            // That is safe because AudioService.Dispose clears _ready before stopping anything and both
            // drivers' Dispose only detaches handlers — neither pushes into a stopped service.
            return Task.FromResult(Result.Ok());
        }
    }

    /// <summary>
    /// Slot 95 — the run object graph (TECH_DOC §4.12). Everything here is constructed once and injected;
    /// no system resolves a dependency per frame (§4.0).
    /// </summary>
    public sealed class RunSystemsStep : IBootstrapStep
    {
        public int Order => BootstrapOrder.RunSystems;
        public string Name => "RunSystems";

        public Task<Result> ExecuteAsync(BootstrapContext context, CancellationToken ct)
        {
            BootstrapConfig config = context.Config;
            IServiceContainer services = context.Services;

            if (config.trackRoot == null) return Task.FromResult(CorePrimitivesStep.Missing("trackRoot"));
            if (config.playerMotor == null) return Task.FromResult(CorePrimitivesStep.Missing("playerMotor"));
            if (config.playerSensor == null) return Task.FromResult(CorePrimitivesStep.Missing("playerSensor"));
            if (config.chaser == null) return Task.FromResult(CorePrimitivesStep.Missing("chaser"));
            if (config.inputActions == null) return Task.FromResult(CorePrimitivesStep.Missing("inputActions"));

            IGameDatabase database = services.Resolve<IGameDatabase>();
            IPoolRegistry pools = services.Resolve<IPoolRegistry>();
            IWorldOriginShifter origin = services.Resolve<IWorldOriginShifter>();
            IGameFlow flow = services.Resolve<IGameFlow>();
            RunClock runClock = services.Resolve<RunClock>();
            IClock clock = services.Resolve<IClock>();

            // ── Track ────────────────────────────────────────────────────────────────────────────────
            var generator = new TrackGenerator();
            services.Register<ITrackGenerator>(generator);

            var streamer = new TrackStreamer();
            services.Register(streamer);

            // ── Obstacles ────────────────────────────────────────────────────────────────────────────
            var contactIndex = new ObstacleContactIndex();
            services.Register(contactIndex);

            var populator = new ObstacleChunkPopulator();
            populator.Construct(database, pools, config.obstacleBehaviours, contactIndex,
                context.Log, generator, config.poolRoot);
            services.Register(populator);

            // ── Collision and score ──────────────────────────────────────────────────────────────────
            var resolver = new CollisionResolver(database.Balance);
            services.Register<ICollisionResolver>(resolver);

            var score = new ScoreCalculator(database.Scoring, context.Bus);
            services.Register<IScoreCalculator>(score);

            // ── Input ────────────────────────────────────────────────────────────────────────────────
            // Wall-clock, not run time: a gesture and its buffer window are physical durations, so a crash
            // freeze must not stretch them (§4.3).
            var buffer = new InputCommandBuffer(database.Input, clock);
            services.Register<IInputCommandBuffer>(buffer);

            IInputSource input = SelectInput(config, context, database, clock);
            if (input == null)
            {
                return Task.FromResult(Result.Fail(ErrorCodes.NotFound,
                    "No input source is assigned. Set BootstrapConfig.touchInput (and keyboardInput in the editor)."));
            }

            services.Register(input);

            // Registered so the container disposes it: the source is a scene object that outlives a boot,
            // and a handler left pointing at a dead buffer would push into it on the next run.
            services.Register(new InputIntentBridge(input, buffer));

            // ── Player, chaser, sensor ───────────────────────────────────────────────────────────────
            config.playerMotor.Construct(database.Balance, context.Bus, context.Log);
            services.Register<IPlayerMotor>(config.playerMotor);

            // The motor re-bases its own rig on a shift rather than living under a translated root. The
            // chaser needs no listener: it reads the motor's already-re-based ForwardOffset every tick.
            origin.RegisterListener(config.playerMotor);

            config.chaser.Construct(database.Balance, context.Bus, config.playerMotor);
            services.Register<IChaserSystem>(config.chaser);

            // No shield provider in M1. The M2 hoverboard implements IShieldProvider and is passed here;
            // the sensor and the resolver do not change (§4.10).
            config.playerSensor.Construct(config.playerMotor, resolver, contactIndex, config.chaser,
                database.Balance, context.Bus, context.Log);
            services.Register(config.playerSensor);

            // ── Continue policy ──────────────────────────────────────────────────────────────────────
            // Continues are M2 scope. NoContinueOfferPolicy is a real policy, not a stub: "this build does
            // not offer a continue" is a legitimate product state and it keeps crash → results honest.
            bool freeContinues = config.allowFreeContinues;
#if !DEV && !UNITY_EDITOR
            // A release build cannot hand out free continues because a scene flag was left on. The define is
            // the gate, not the scene — the same reason SelectInput ignores useKeyboardInput here (§4.12).
            freeContinues = false;
#endif
            IContinueOfferPolicy continuePolicy = freeContinues
                ? new DebugContinueOfferPolicy(database.Continue, true)
                : (IContinueOfferPolicy)new NoContinueOfferPolicy();
            services.Register(continuePolicy);

            // ── Run controller ───────────────────────────────────────────────────────────────────────
            var run = new RunController(runClock, context.Bus, context.Log);
            run.Construct(new RunSystems
            {
                Database = database,
                Pools = pools,
                Motor = config.playerMotor,
                Generator = generator,
                Streamer = streamer,
                Populator = populator,
                Resolver = resolver,
                Sensor = config.playerSensor,
                Chaser = config.chaser,
                Score = score,
                InputBuffer = buffer,
                Input = input,
                ContinuePolicy = continuePolicy,
                Origin = origin,
                TrackRoot = config.trackRoot,
                Music = services.Resolve<IMusicIntensityDriver>(),
                Flow = flow
            });

            if (!run.IsReady)
            {
                return Task.FromResult(Result.Fail(ErrorCodes.SystemError,
                    "RunController rejected the system bundle; the preceding error names the missing member."));
            }

            // Registered last so it is disposed last among the run systems: its teardown returns chunks to
            // pools that are still alive.
            services.Register<IRunController>(run);
            run.Subscribe();

            return Task.FromResult(Result.Ok());
        }

        /// <summary>
        /// Enables exactly one source. Both share the same <c>InputActionAsset</c> "Player" map, so an
        /// active second source would have its actions disabled by the first one's <c>SetEnabled(false)</c>
        /// — which is why both are authored inactive and only the chosen one is ever activated (§4.3).
        /// </summary>
        private static IInputSource SelectInput(BootstrapConfig config, BootstrapContext context,
            IGameDatabase database, IClock clock)
        {
            bool wantKeyboard = config.useKeyboardInput;
#if !DEV && !UNITY_EDITOR
            // A shipped build has one input path. The flag is editor convenience and cannot reach a player
            // build whatever the scene says; the settings enforcer forces it off as a second, independent gate.
            wantKeyboard = false;
#endif

            // Deactivate both first, so switching the flag between two boots in the same scene cannot leave
            // the previous source running.
            if (config.touchInput != null) config.touchInput.gameObject.SetActive(false);
            if (config.keyboardInput != null) config.keyboardInput.gameObject.SetActive(false);

            if (wantKeyboard && config.keyboardInput != null)
            {
                config.keyboardInput.gameObject.SetActive(true);
                config.keyboardInput.Construct(config.inputActions, context.Bus);
                context.Log.Info(LogChannel.Controls, "Input source: keyboard (editor/DEV only).");
                return config.keyboardInput;
            }

            if (config.touchInput == null) return null;

            config.touchInput.gameObject.SetActive(true);
            config.touchInput.Construct(clock, config.inputActions, database.Input, context.Bus);

            if (wantKeyboard)
                context.Log.Warn(LogChannel.Controls, "useKeyboardInput is set but no keyboard source is assigned; using touch.");

            return config.touchInput;
        }
    }

    /// <summary>
    /// Slot 100 — UI root and first screen (TECH_DOC §4.1 step 10).
    ///
    /// M1 has no UI module, so the "first screen" is the dev HUD and the flow is walked to Home and straight
    /// into a run. That is what makes the greybox playable with no menus. M3 replaces the auto-start with a
    /// Home screen whose Play button publishes <c>RunStartRequestedEvent</c>; this step keeps only the flow
    /// walk and the UI root.
    /// </summary>
    public sealed class UiRootStep : IBootstrapStep
    {
        public int Order => BootstrapOrder.UiRoot;
        public string Name => "UiRoot";

        public async Task<Result> ExecuteAsync(BootstrapContext context, CancellationToken ct)
        {
            IServiceContainer services = context.Services;
            BootstrapConfig config = context.Config;
            IGameFlow flow = services.Resolve<IGameFlow>();

            // Walk the real transition table instead of jumping to Home. Boot → Splash is what §4.1 step 10
            // mandates; continuing to Home exercises the same edges the shipped splash/loading screens will.
            if (!flow.TryTransition(GameFlowState.Splash, new TransitionContext("boot_complete")))
                return Result.Fail(ErrorCodes.NotAvailable, "Game flow refused Boot → Splash.");

            if (!flow.TryTransition(GameFlowState.Loading, new TransitionContext("catalog_ready")))
                return Result.Fail(ErrorCodes.NotAvailable, "Game flow refused Splash → Loading.");

            if (!flow.TryTransition(GameFlowState.Home, new TransitionContext("boot_finished")))
                return Result.Fail(ErrorCodes.NotAvailable, "Game flow refused Loading → Home.");

            bool showHud = config.showDebugHud;
#if !DEV && !UNITY_EDITOR
            // IMGUI allocates on every repaint, so §11.1's allocation budget is only credible if the overlay
            // cannot exist in a shipped player. DebugHud's body already compiles away; this stops the object
            // from being activated and Constructed at all (§4.12).
            showHud = false;
#endif

            if (showHud)
            {
                if (config.debugHud == null)
                {
                    context.Log.Warn(LogChannel.Bootstrap,
                        "showDebugHud is set but BootstrapConfig.debugHud is unassigned; the run will have no overlay.");
                }
                else
                {
                    config.debugHud.gameObject.SetActive(true);
                    config.debugHud.Construct(services, context.Log);
                }
            }

            if (!config.autoStartRun) return Result.Ok();

            IGameDatabase database = services.Resolve<IGameDatabase>();
            IRunController run = services.Resolve<IRunController>();

            uint seed = config.fixedSeed != 0u ? config.fixedSeed : unchecked((uint)DateTime.UtcNow.Ticks);

            // No character or board in M1: StableId.None is the honest value, and Meta will resolve both
            // from the profile before this context is built (§4.12).
            var start = new RunStartContext(seed, database.Worlds.All[0].Id, StableId.None, StableId.None);

            Result started = await run.StartRunAsync(start, ct);
            if (!started.IsSuccess)
                return Result.Fail(started.ErrorCode, $"Auto-start was refused: {started.Message}");

            return Result.Ok();
        }
    }
}