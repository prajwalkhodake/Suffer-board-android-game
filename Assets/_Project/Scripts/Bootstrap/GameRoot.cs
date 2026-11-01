using System;
using System.Threading;
using System.Threading.Tasks;
using Skyrift.Audio;
using Skyrift.Core;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Services;
using Skyrift.Core.Time;
using Skyrift.Gameplay;
using UnityEngine;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// The Unity-side adapter for the composition root (TECH_DOC §4.1, §3.5). It owns exactly two things:
    /// starting the boot, and the per-frame tick split.
    ///
    /// <b>Two tick owners, two clocks.</b> The run controller advances the <em>simulation</em> on
    /// <see cref="RunClock"/>, which is frozen for a crash and paused for a menu. Audio and the music
    /// intensity driver advance <em>presentation</em> on unscaled time, so the mix keeps easing through a
    /// crash freeze instead of stopping dead with the world (§4.12, §4.18). Nothing here reads
    /// <c>Time.timeScale</c>, and nothing here sets it.
    ///
    /// Every rig, root and catalog reference is authored in the scene by the editor setup tool. This
    /// component instantiates nothing: the "no runtime Instantiate in gameplay" rule (§11.2 rule 1) has to hold for
    /// the boot path too, or the first run pays for it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameRoot : MonoBehaviour
    {
        [SerializeField] private BootstrapConfig config = new BootstrapConfig();

        private GameBootstrap _bootstrap;
        private CancellationTokenSource _cancellation;

        private IRunController _run;
        private RunClock _runClock;
        private IClock _clock;
        private IAudioService _audio;
        private IMusicIntensityDriver _music;

        private bool _booted;

        /// <summary>Composition-time access for the editor tool and PlayMode tests. Never resolve per frame.</summary>
        public GameBootstrap Bootstrap => _bootstrap;

        public bool IsBooted => _booted;

        private void Start()
        {
            // Not async void: a discarded Task with a catch-all inside is the same behaviour without an
            // exception path onto Unity's synchronization context.
            _ = BootAsync();
        }

        private void Update()
        {
            if (!_booted) return;

            // Simulation first, so the events this step publishes (speed, proximity, contact) are already
            // in the drivers' state when the presentation tick eases toward them.
            _run.Tick(_runClock.DeltaTime);

            float unscaled = _clock.UnscaledDeltaTime;
            _audio.Tick(unscaled);
            _music.Tick(unscaled);
        }

        /// <summary>
        /// App background / foreground. On Android this is the reliable signal; <c>OnApplicationFocus</c>
        /// fires for transient overlays too and would pause a run that is still visible.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (!_booted) return;

            // Background mutes rather than ducks (§4.18): the player must not hear the game from a pocket.
            _audio.MuteAll(paused);

            // Both go through the run controller, which delegates to the game flow, so a focus-loss pause
            // takes exactly the same path as a pause button and publishes the same events.
            if (paused) _run.Pause();
            else _run.Resume();
        }

        private void OnDestroy()
        {
            _booted = false;

            if (_cancellation != null)
            {
                _cancellation.Cancel();
                _cancellation.Dispose();
                _cancellation = null;
            }

            if (_bootstrap != null)
            {
                _bootstrap.Shutdown();
                _bootstrap = null;
            }
        }

        private async Task BootAsync()
        {
            _cancellation = new CancellationTokenSource();
            _bootstrap = new GameBootstrap();

            try
            {
                Result result = await _bootstrap.InitializeAsync(config, _cancellation.Token);

                // §13.2 rule 9: an async path must never continue after the object is gone. OnDestroy
                // cancels the token and nulls the bootstrap, and touching `enabled` on a destroyed
                // MonoBehaviour throws, so this check comes before anything else.
                if (this == null) return;

                if (!result.IsSuccess)
                {
                    // GameBootstrap already logged the step and published BootstrapFailedEvent — that event
                    // is the UI's signal in M3. Here the only correct action is to stop ticking a graph that
                    // was never finished.
                    enabled = false;
                    return;
                }

                IServiceContainer services = _bootstrap.Services;
                _run = services.Resolve<IRunController>();
                _runClock = services.Resolve<RunClock>();
                _clock = services.Resolve<IClock>();
                _audio = services.Resolve<IAudioService>();
                _music = services.Resolve<IMusicIntensityDriver>();

                _booted = true;
            }
            catch (Exception e)
            {
                // InitializeAsync converts step failures to Results, so reaching here means the composition
                // root itself broke. _bootstrap is assigned before the try, so its logger is always available
                // and the channel filter still applies.
                if (this == null) return;

                _bootstrap.Log.Error(LogChannel.Bootstrap, $"Boot threw {e.GetType().Name}: {e.Message}");
                enabled = false;
            }
        }

        // No DontDestroyOnLoad: §3.5's Boot scene is never unloaded, and in M1 it is the only scene, so
        // moving this object to a persistent scene would buy nothing and would only obscure which scene
        // owns the rigs. M3 revisits this when Main.unity is split out.
    }
}