using System;
using Skyrift.Audio;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Data;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Routes run events to sound cues (TECH_DOC §4.18). Like <see cref="MusicIntensityDriver"/> this lives
    /// in Gameplay because the payloads are Gameplay's and Audio may not reference them (§3.3). Audio stays
    /// a pure playback service: it knows cue ids, not what a stumble is.
    ///
    /// Cue ids come from <see cref="SfxIds"/> and per-obstacle impact sounds come from
    /// <c>ObstacleDefinition.impactSfxId</c>, so a new obstacle can bring its own sound as data.
    ///
    /// M1 plays these cues in 2D. Positional audio is a camera/VFX concern and lands with the polish pass;
    /// the service and <see cref="SfxParams"/> already support it, so enabling it is a routing change here.
    /// </summary>
    public sealed class RunSfxRouter : IDisposable
    {
        private readonly IAudioService _audio;
        private readonly IEventBus _bus;
        private readonly IGameDatabase _database;
        private readonly BalanceConfig _balance;

        private bool _subscribed;
        private bool _disposed;
        private bool _chaserWarned;

        public RunSfxRouter(IAudioService audio, IEventBus bus, IGameDatabase database, BalanceConfig balance)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
            _bus = bus;
            _database = database;
            _balance = balance;
        }

        public void Subscribe()
        {
            if (_subscribed || _bus == null) return;
            _subscribed = true;

            _bus.Subscribe<PlayerJumpedEvent>(OnJumped);
            _bus.Subscribe<PlayerLandedEvent>(OnLanded);
            _bus.Subscribe<PlayerRollStartedEvent>(OnRollStarted);
            _bus.Subscribe<PlayerRollEndedEvent>(OnRollEnded);
            _bus.Subscribe<PlayerLaneChangedEvent>(OnLaneChanged);
            _bus.Subscribe<PlayerStumbledEvent>(OnStumbled);
            _bus.Subscribe<PlayerCrashedEvent>(OnCrashed);
            _bus.Subscribe<ContactResolvedEvent>(OnContactResolved);
            _bus.Subscribe<ChaserProximityChangedEvent>(OnProximityChanged);
            _bus.Subscribe<ChaserCaughtPlayerEvent>(OnChaserCaught);
            _bus.Subscribe<RunStartedEvent>(OnRunStarted);
            _bus.Subscribe<RunEndedEvent>(OnRunEnded);
            _bus.Subscribe<ContinueOfferedEvent>(OnContinueOffered);
            _bus.Subscribe<RunContinuedEvent>(OnRunContinued);
        }

        public void Unsubscribe()
        {
            if (!_subscribed || _bus == null) return;
            _subscribed = false;

            _bus.Unsubscribe<PlayerJumpedEvent>(OnJumped);
            _bus.Unsubscribe<PlayerLandedEvent>(OnLanded);
            _bus.Unsubscribe<PlayerRollStartedEvent>(OnRollStarted);
            _bus.Unsubscribe<PlayerRollEndedEvent>(OnRollEnded);
            _bus.Unsubscribe<PlayerLaneChangedEvent>(OnLaneChanged);
            _bus.Unsubscribe<PlayerStumbledEvent>(OnStumbled);
            _bus.Unsubscribe<PlayerCrashedEvent>(OnCrashed);
            _bus.Unsubscribe<ContactResolvedEvent>(OnContactResolved);
            _bus.Unsubscribe<ChaserProximityChangedEvent>(OnProximityChanged);
            _bus.Unsubscribe<ChaserCaughtPlayerEvent>(OnChaserCaught);
            _bus.Unsubscribe<RunStartedEvent>(OnRunStarted);
            _bus.Unsubscribe<RunEndedEvent>(OnRunEnded);
            _bus.Unsubscribe<ContinueOfferedEvent>(OnContinueOffered);
            _bus.Unsubscribe<RunContinuedEvent>(OnRunContinued);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
        }

        private void OnJumped(PlayerJumpedEvent evt) => _audio.PlaySfx(SfxIds.Jump);

        private void OnLanded(PlayerLandedEvent evt) => _audio.PlaySfx(SfxIds.Land);

        private void OnRollStarted(PlayerRollStartedEvent evt) => _audio.PlaySfx(SfxIds.RollStart);

        private void OnRollEnded(PlayerRollEndedEvent evt) => _audio.PlaySfx(SfxIds.RollEnd);

        private void OnLaneChanged(PlayerLaneChangedEvent evt) => _audio.PlaySfx(SfxIds.LaneChange);

        private void OnStumbled(PlayerStumbledEvent evt) => _audio.PlaySfx(SfxIds.Stumble);

        private void OnCrashed(PlayerCrashedEvent evt)
        {
            _audio.PlaySfx(SfxIds.Crash);
            PlayImpact(evt.Context.ObstacleId);
        }

        private void OnContactResolved(ContactResolvedEvent evt)
        {
            // Stumble and crash already have their own events; handling them here too would double the cue.
            switch (evt.Outcome)
            {
                case ContactOutcome.Grazed:
                    _audio.PlaySfx(SfxIds.Graze);
                    break;

                case ContactOutcome.Cleared:
                    // Only a launch is worth a sound on a clean clear; jumping a barrier already played the
                    // jump and land cues, and a silent clear keeps the mix readable at high speed.
                    if (IsLaunch(evt.ObstacleId)) _audio.PlaySfx(SfxIds.RampLaunch);
                    break;
            }
        }

        private void OnProximityChanged(ChaserProximityChangedEvent evt)
        {
            if (_balance == null) return;

            // One shot per approach, re-armed when the pursuer falls back, so the warning cannot machine-gun
            // while proximity decays smoothly through the threshold.
            if (evt.Proximity >= _balance.chaserWarningProximity)
            {
                if (_chaserWarned) return;
                _chaserWarned = true;
                _audio.PlaySfx(SfxIds.ChaserWarning);
            }
            else
            {
                _chaserWarned = false;
            }
        }

        private void OnChaserCaught(ChaserCaughtPlayerEvent evt) => _audio.PlaySfx(SfxIds.ChaserCaught);

        private void OnRunStarted(RunStartedEvent evt)
        {
            _chaserWarned = false;
            _audio.PlaySfx(SfxIds.RunStart);
        }

        private void OnRunEnded(RunEndedEvent evt) => _audio.PlaySfx(SfxIds.RunEnd);

        private void OnContinueOffered(ContinueOfferedEvent evt) => _audio.PlaySfx(SfxIds.ContinueOffer);

        private void OnRunContinued(RunContinuedEvent evt) => _audio.PlaySfx(SfxIds.ContinueGranted);

        private void PlayImpact(StableId obstacleId)
        {
            if (!TryGetObstacle(obstacleId, out ObstacleDefinition def)) return;
            if (!string.IsNullOrEmpty(def.impactSfxId)) _audio.PlaySfx(def.impactSfxId);
        }

        private bool IsLaunch(StableId obstacleId)
        {
            return TryGetObstacle(obstacleId, out ObstacleDefinition def) && def.launchHeightMultiplier > 0f;
        }

        private bool TryGetObstacle(StableId obstacleId, out ObstacleDefinition definition)
        {
            definition = null;
            return _database != null && obstacleId.IsValid && _database.Obstacles.TryGet(obstacleId, out definition);
        }
    }
}