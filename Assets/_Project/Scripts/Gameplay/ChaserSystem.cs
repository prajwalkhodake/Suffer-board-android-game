using Skyrift.Core.Events;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Visible pursuit pressure (TECH_DOC §4.8). Proximity is presentation and tension only: it drives
    /// animation, audio, camera and post-fx, and it never ends a run by itself. Death comes from the
    /// collision model (§4.7), so a player who never stumbles is never caught.
    /// </summary>
    public interface IChaserSystem
    {
        /// <summary>0 = far behind (safe), 1 = caught up.</summary>
        float Proximity { get; }

        void OnPlayerStumbled();

        /// <summary>Called when the player has fully recovered from a stumble.</summary>
        void OnPlayerRecovered();

        void ResetForRun();
        void Tick(float dt, float playerSpeed);
    }

    /// <summary>
    /// Visible pursuit pressure, implemented (§4.8).
    ///
    /// The rig follows the player's forward offset rather than a fixed Z, because both are re-based on an
    /// origin shift and the chaser must stay the same gap behind at any run distance. ChaserRoot itself is
    /// organisational and never translated — the same rule the track generator follows (§4.6).
    /// </summary>
    public sealed class ChaserSystem : MonoBehaviour, IChaserSystem
    {
        [Tooltip("Visual rig. Its Z tracks the player's forward offset minus the gap, so it stays inside the " +
                 "current origin-shift window. ChaserRoot is never translated.")]
        [SerializeField] private Transform rig;

        private BalanceConfig _balance;
        private IEventBus _bus;
        private IPlayerMotor _motor;
        private Transform _rig;

        private float _proximity;
        private float _lastPublished = -1f;
        private bool _caughtPublished;

        public float Proximity => _proximity;

        public void Construct(BalanceConfig balance, IEventBus bus, IPlayerMotor motor, Transform rigOverride = null)
        {
            _balance = balance;
            _bus = bus;
            _motor = motor;
            if (rigOverride != null) _rig = rigOverride;
            else if (_rig == null) _rig = rig != null ? rig : transform;
        }

        public void ResetForRun()
        {
            if (_balance == null) return;

            _proximity = Mathf.Clamp01(_balance.chaserProximityOnRunStart);
            _caughtPublished = false;
            _lastPublished = -1f;

            PublishIfChanged();
            UpdateRig();
        }

        public void OnPlayerStumbled()
        {
            if (_balance == null) return;

            // Snap toward the player, but never fall back because of a stumble.
            _proximity = Mathf.Max(_proximity, Mathf.Clamp01(_balance.chaserProximityOnStumble));
            _caughtPublished = false;

            PublishIfChanged();
            UpdateRig();
        }

        public void OnPlayerRecovered()
        {
            if (_balance == null) return;

            // A clean recovery lets the pursuer drop back to its cruising distance rather than lingering
            // at the stumble distance, which would make the next scrape feel unfairly lethal.
            _proximity = Mathf.Min(_proximity, Mathf.Clamp01(_balance.chaserProximityOnRunStart));
            _caughtPublished = false;

            PublishIfChanged();
            UpdateRig();
        }

        public void Tick(float dt, float playerSpeed)
        {
            if (_balance == null || dt <= 0f) return;

            // Falling off scales with how fast the player is running: outrunning the pursuer is the reward
            // for a clean, fast run. The authored influence spreads the rate symmetrically around
            // chaserDecayPerSecond, so that value stays the rate at mid-curve speed rather than a floor.
            float speedFactor = _balance.maxSpeed > 0f ? Mathf.Clamp01(playerSpeed / _balance.maxSpeed) : 0f;
            float influence = Mathf.Clamp01(_balance.chaserDecaySpeedInfluence);
            float decay = _balance.chaserDecayPerSecond * Mathf.Lerp(1f - influence, 1f + influence, speedFactor);

            _proximity = Mathf.Clamp01(_proximity - (decay * dt));

            if (_proximity >= 1f && !_caughtPublished)
            {
                _caughtPublished = true;
                _bus?.Publish(new ChaserCaughtPlayerEvent());
            }
            else if (_proximity < 1f)
            {
                _caughtPublished = false;
            }

            PublishIfChanged();
            UpdateRig();
        }

        private void PublishIfChanged()
        {
            // Quantised so a smoothly decaying value does not wake audio and camera every single frame.
            float quantised = Mathf.Round(_proximity * 100f) / 100f;
            if (Mathf.Approximately(quantised, _lastPublished)) return;

            _lastPublished = quantised;
            _bus?.Publish(new ChaserProximityChangedEvent(_proximity));
        }

        private void UpdateRig()
        {
            if (_rig == null || _balance == null) return;

            float distance = Mathf.Lerp(_balance.chaserFarDistance, _balance.chaserNearDistance, _proximity);
            float x = _motor != null ? _motor.LateralPosition * _balance.chaserLaneFollow : 0f;
            float z = (_motor != null ? _motor.ForwardOffset : 0f) - distance;

            // RunController ticks the motor, then the streamer (which may shift the origin), then the
            // chaser, so ForwardOffset here is already re-based. Reading it before the shift would leave
            // the rig a whole threshold behind for one frame (§4.6, §4.12).
            _rig.localPosition = new Vector3(x, 0f, z);
        }
    }
}