using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Skyrift.Core;
using Skyrift.Core.Bootstrap;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Services;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// The composition root (TECH_DOC §4.1). Builds the object graph once, in slot order, and is the only
    /// place in the game that calls <c>IServiceContainer.Register</c>.
    ///
    /// The bus and the logger are created here rather than by a step, because step 1 can itself fail and a
    /// failure that cannot be logged or published is indistinguishable from a hang.
    ///
    /// M1 runs five steps. Slots 20/40/50/60/70/90 (save, Remote Config, consent, service adapters, Meta,
    /// preload) are <b>absent, not stubbed</b>: there is no save schema, no SDK and no Addressables content
    /// to load yet, and a no-op step that reports success would hide the fact that the capability does not
    /// exist. Each milestone adds a class at its existing slot and the pipeline below does not change.
    /// </summary>
    public sealed class GameBootstrap : IGameBootstrap, IDisposable
    {
        private readonly ServiceContainer _services = new ServiceContainer();
        private readonly EventBus _bus = new EventBus();
        private readonly ILog _log = new UnityLog();
        private readonly List<IBootstrapStep> _steps = new List<IBootstrapStep>(12);

        private bool _started;
        private bool _disposed;

        public IServiceContainer Services => _services;

        /// <summary>
        /// The bus, available before boot completes so a caller can observe <see cref="BootstrapFailedEvent"/>.
        /// Not on <see cref="IGameBootstrap"/>: consumers resolve it from the container once boot has finished.
        /// </summary>
        public IEventBus Bus => _bus;

        public ILog Log => _log;

        /// <summary>
        /// Adds a step before initialization (the §4.1 extension point). Steps are sorted by
        /// <see cref="IBootstrapStep.Order"/>, so a new capability is a class at an existing slot — never a
        /// renumbering of the sequence and never an edit to the loop below.
        /// </summary>
        public void AddStep(IBootstrapStep step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (_started || _disposed)
                throw new InvalidOperationException("Bootstrap steps may only be added before InitializeAsync.");

            _steps.Add(step);
        }

        public async Task<Result> InitializeAsync(BootstrapConfig config, CancellationToken ct)
        {
            if (_disposed) return Result.Fail(ErrorCodes.NotAvailable, "Bootstrap is disposed.");

            // One attempt per instance, successful or not. The container refuses a duplicate registration,
            // so a retry on a half-built graph would fail with a confusing "already registered" instead of
            // the original error. A caller that wants to retry constructs a new GameBootstrap.
            if (_started)
                return Result.Fail(ErrorCodes.AlreadyActive, "Bootstrap has already been attempted on this instance.");

            if (config == null) return Fail(ErrorCodes.InvalidArgument, "BootstrapConfig is null.", "config");

            _started = true;
            BuildSteps(config);

            // Ties break on name so two steps at the same slot always run in the same order. List.Sort is
            // unstable, and an unstable boot order is the kind of bug that only shows up on one device.
            _steps.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order)
                : string.CompareOrdinal(a.Name, b.Name));

            var context = new BootstrapContext(_services, config, _bus, _log);

            for (int i = 0; i < _steps.Count; i++)
            {
                IBootstrapStep step = _steps[i];

                if (ct.IsCancellationRequested)
                    return Fail(ErrorCodes.Cancelled, "Boot cancelled.", step.Name);

                Result result;
                try
                {
                    result = await step.ExecuteAsync(context, ct);
                }
                catch (OperationCanceledException)
                {
                    return Fail(ErrorCodes.Cancelled, "Boot cancelled.", step.Name);
                }
                catch (Exception e)
                {
                    // A step is allowed to throw (a null scene reference, a missing pool key). Converting it
                    // to a Result here is what keeps a wiring error a readable boot failure instead of an
                    // exception unwinding through Unity's synchronization context.
                    _log.Error(LogChannel.Bootstrap,
                        $"Boot step '{step.Name}' threw {e.GetType().Name}: {e.Message}");
                    result = Result.Fail(ErrorCodes.SystemError, e.Message);
                }

                if (!result.IsSuccess)
                    return Fail(result.ErrorCode, result.Message, step.Name);
            }

            _bus.Publish(new BootstrapCompletedEvent());
            _log.Info(LogChannel.Bootstrap, $"Boot complete: {_steps.Count} steps.");

            return Result.Ok();
        }

        /// <summary>
        /// Releases the graph. Registration order is dependency order, and the container disposes in that
        /// order, so every dependent is released before what it depends on: the audio drivers unsubscribe
        /// before the service they push into stops, and the run controller tears the track down while the
        /// pools it returns chunks to are still alive.
        /// </summary>
        public void Shutdown()
        {
            if (_disposed) return;
            _disposed = true;

            _services.Dispose();

            // Plain C# events on scene MonoBehaviours are not owned by the container. Clearing the bus drops
            // every domain subscriber so a second boot in the same scene (PlayMode tests) starts silent.
            _bus.Clear();
            _steps.Clear();
        }

        public void Dispose() => Shutdown();

        private void BuildSteps(BootstrapConfig config)
        {
            // The M1 set. A milestone adds its step here and nowhere else; the slots are already reserved
            // in BootstrapOrder so nothing has to be renumbered.
            _steps.Add(new CorePrimitivesStep());
            _steps.Add(new CatalogStep());
            _steps.Add(new AudioStep());
            _steps.Add(new RunSystemsStep());
            _steps.Add(new UiRootStep());

#if DEV || UNITY_EDITOR
            _log.Info(LogChannel.Bootstrap,
                $"Boot config: catalog={(config.catalog != null ? config.catalog.name : "<none>")}, " +
                $"keyboardInput={config.useKeyboardInput}, freeContinues={config.allowFreeContinues}, " +
                $"autoStart={config.autoStartRun}, fixedSeed={config.fixedSeed}.");
#endif
        }

        private Result Fail(string code, string message, string step)
        {
            string detail = string.IsNullOrEmpty(message) ? code : $"{code}: {message}";
            _log.Error(LogChannel.Bootstrap, $"Boot failed at step '{step}' — {detail}");
            _bus.Publish(new BootstrapFailedEvent(code, step, message));

            // The graph is left built but inert: nothing was started, and the caller decides whether to
            // retry or show a maintenance screen. Disposing here would destroy the evidence in the container.
            return Result.Fail(code, $"[{step}] {detail}");
        }
    }
}