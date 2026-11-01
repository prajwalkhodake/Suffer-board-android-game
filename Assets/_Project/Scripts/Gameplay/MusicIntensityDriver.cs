using System;
using Skyrift.Audio;
using Skyrift.Core.Events;
using Skyrift.Core.Flow;
using Skyrift.Core.Ids;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Turns run state into music intensity (TECH_DOC §4.18, extension point <c>IMusicIntensityDriver</c>).
    ///
    /// This lives in Gameplay, not Audio, on purpose: it reads <see cref="PlayerSpeedChangedEvent"/> and
    /// <see cref="ChaserProximityChangedEvent"/>, which are Gameplay payloads, and the assembly DAG forbids
    /// Audio → Gameplay (§3.3). Audio therefore exposes a push API and this driver does the subscribing.
    /// Swapping the intensity model means replacing this object, never editing the mixer.
    ///
    /// Ticked on <b>unscaled</b> time by the composition root so the mix keeps easing through a crash freeze.
    /// </summary>
    public sealed class MusicIntensityDriver : IMusicIntensityDriver, IDisposable
    {
        private readonly AudioConfig _config;
        private readonly IAudioService _audio;
        private readonly IEventBus _bus;
        private readonly IGameDatabase _database;

        private float _maxSpeed;
        private float _speedNormalised;
        private float _proximity;
        private float _current;
        private float _updateTimer;
        private bool _subscribed;
        private bool _disposed;

        public float CurrentIntensity => _current;

        public MusicIntensityDriver(AudioConfig config, IAudioService audio, IEventBus bus,
            IGameDatabase database, BalanceConfig balance)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
            _bus = bus;
            _database = database;
            _maxSpeed = balance != null ? balance.maxSpeed : 1f;
        }

        public void Subscribe()
        {
            if (_subscribed || _bus == null) return;
            _subscribed = true;

            _bus.Subscribe<PlayerSpeedChangedEvent>(OnSpeedChanged);
            _bus.Subscribe<ChaserProximityChangedEvent>(OnProximityChanged);
            _bus.Subscribe<RunStartedEvent>(OnRunStarted);
            _bus.Subscribe<RunEndedEvent>(OnRunEnded);
            _bus.Subscribe<GameFlowChangedEvent>(OnFlowChanged);
        }

        public void Unsubscribe()
        {
            if (!_subscribed || _bus == null) return;
            _subscribed = false;

            _bus.Unsubscribe<PlayerSpeedChangedEvent>(OnSpeedChanged);
            _bus.Unsubscribe<ChaserProximityChangedEvent>(OnProximityChanged);
            _bus.Unsubscribe<RunStartedEvent>(OnRunStarted);
            _bus.Unsubscribe<RunEndedEvent>(OnRunEnded);
            _bus.Unsubscribe<GameFlowChangedEvent>(OnFlowChanged);
        }

        public void SetMaxSpeed(float metresPerSecond)
        {
            _maxSpeed = metresPerSecond > 0f ? metresPerSecond : 1f;
        }

        public void ResetForRun()
        {
            _speedNormalised = 0f;
            _proximity = 0f;
            _current = 0f;
            _updateTimer = 0f;
            _audio.SetMusicIntensity(0f);
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (_disposed || unscaledDeltaTime <= 0f) return;

            // Weights are normalised by the config so two authored numbers that do not sum to 1 still
            // produce a 0..1 intensity instead of clipping or never reaching the chase layer.
            float target = Mathf.Clamp01(
                (_config.NormalisedSpeedWeight * _speedNormalised) +
                (_config.NormalisedChaserWeight * _proximity));

            float step = unscaledDeltaTime / Mathf.Max(0.0001f, _config.intensitySmoothingSeconds);
            _current = Mathf.MoveTowards(_current, target, step);

            _updateTimer -= unscaledDeltaTime;
            if (_updateTimer > 0f) return;

            _updateTimer = Mathf.Max(0.01f, _config.intensityUpdateInterval);
            _audio.SetMusicIntensity(_current);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
        }

        private void OnSpeedChanged(PlayerSpeedChangedEvent evt)
        {
            _speedNormalised = Mathf.Clamp01(evt.Speed / _maxSpeed);
        }

        private void OnProximityChanged(ChaserProximityChangedEvent evt)
        {
            _proximity = Mathf.Clamp01(evt.Proximity);
        }

        private void OnRunStarted(RunStartedEvent evt)
        {
            ResetForRun();
            _audio.PlayMusic(ResolveMusicId(evt.WorldId), false);
        }

        private void OnRunEnded(RunEndedEvent evt)
        {
            _speedNormalised = 0f;
            _proximity = 0f;
            _audio.StopMusic(true);
        }

        private void OnFlowChanged(GameFlowChangedEvent evt)
        {
            // Pause ducks through the audio service rather than stopping the track, so resuming does not
            // restart the music from the top.
            if (evt.To == GameFlowState.Paused) _audio.PauseAll(true);
            else if (evt.From == GameFlowState.Paused) _audio.PauseAll(false);

            if (evt.To == GameFlowState.Home || evt.To == GameFlowState.Maintenance) _audio.StopMusic(true);
        }

        private MusicId ResolveMusicId(StableId worldId)
        {
            if (_database != null && worldId.IsValid && _database.Worlds.TryGet(worldId, out WorldDefinition world))
            {
                if (!string.IsNullOrEmpty(world.musicId)) return world.musicId;
            }

            return _config.defaultMusicId;
        }
    }
}