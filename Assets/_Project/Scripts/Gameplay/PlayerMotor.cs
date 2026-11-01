using System.Collections.Generic;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Data;
using Skyrift.Track;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>Vertical locomotion state (TECH_DOC §4.4).</summary>
    public enum PlayerVerticalState
    {
        Grounded = 0,
        Jumping,
        Falling,
        Rolling
    }

    /// <summary>
    /// A speed curve over run distance with a cap and a Remote Config scalar (TECH_DOC §4.4).
    /// A handle rather than a direct config reference so a run modifier or an event can swap the whole
    /// profile without the motor knowing where it came from.
    /// </summary>
    public readonly struct SpeedProfileHandle
    {
        public readonly AnimationCurve Curve;
        public readonly float RampMetres;
        public readonly float MaxSpeed;
        public readonly float Scale;

        public SpeedProfileHandle(AnimationCurve curve, float rampMetres, float maxSpeed, float scale)
        {
            Curve = curve;
            RampMetres = rampMetres;
            MaxSpeed = maxSpeed;
            Scale = scale > 0f ? scale : 1f;
        }

        public bool IsValid => Curve != null && Curve.length > 0 && MaxSpeed > 0f;

        public float SpeedAt(float distanceMetres)
        {
            if (!IsValid) return 0f;
            float t = RampMetres > 0f ? Mathf.Clamp01(distanceMetres / RampMetres) : 1f;
            return Mathf.Min(Curve.Evaluate(t) * Scale, MaxSpeed * Scale);
        }

        public static SpeedProfileHandle FromBalance(BalanceConfig balance, float scale)
        {
            return balance == null
                ? default
                : new SpeedProfileHandle(balance.speedCurve, balance.speedRampMetres, balance.maxSpeed, scale);
        }
    }

    /// <summary>
    /// Additive jump contribution (TECH_DOC §4.4 extension point). Modifiers are contributions, not
    /// booleans, so two sources of extra height stack predictably and neither needs to know about the other.
    /// </summary>
    public interface IJumpModifier
    {
        /// <summary>Metres added to the authored jump height.</summary>
        float HeightBonus { get; }

        /// <summary>Multiplier on the authored air time (1 = unchanged).</summary>
        float AirTimeScale { get; }
    }

    /// <summary>Multiplicative speed contribution applied after the profile curve (TECH_DOC §4.4).</summary>
    public interface ISpeedModifier
    {
        float Modify(float baseSpeed, float distanceMetres);
    }

    /// <summary>
    /// Resolves a lane request (TECH_DOC §4.4 extension point). The default clamps to the track edges;
    /// a power-up that allows a 1→3 dash replaces this object, not the motor.
    /// </summary>
    public interface ILaneChangeRule
    {
        int ResolveTargetLane(int currentLane, int delta, int laneCount);
    }

    /// <summary>Shipped lane rule: one lane per swipe, clamped at the edges.</summary>
    public sealed class ClampedLaneChangeRule : ILaneChangeRule
    {
        public int ResolveTargetLane(int currentLane, int delta, int laneCount)
        {
            return Mathf.Clamp(currentLane + delta, 0, Mathf.Max(0, laneCount - 1));
        }
    }

    /// <summary>
    /// Player locomotion contract (TECH_DOC §4.4). <c>Tick</c> is part of the contract because the run
    /// controller owns the simulation order; the motor never reads a clock or runs its own Update.
    /// </summary>
    public interface IPlayerMotor
    {
        /// <summary>Target lane, 0..laneCount-1. During an interpolation this is already the new lane.</summary>
        int Lane { get; }

        /// <summary>Metres, monotonically increasing through the run. Unaffected by origin shifts.</summary>
        float DistanceRun { get; }

        float CurrentSpeed { get; }
        PlayerVerticalState VerticalState { get; }
        bool IsInvulnerable { get; }

        /// <summary>True while the stumble slow-down is still running. The chaser drops back once it clears.</summary>
        bool IsRecovering { get; }

        /// <summary>Feet height above the ground plane — the value clearance checks compare against.</summary>
        float HeightAboveGround { get; }

        /// <summary>Height of the collision volume; reduced while rolling.</summary>
        float CurrentHeight { get; }

        /// <summary>
        /// Height of the surface the rig is supported by — 0 is the ground plane, a train roof or a
        /// rooftop is its authored top. <see cref="HeightAboveGround"/> is measured from the world ground
        /// plane, not from this surface, so clearance maths stays absolute (§4.4, §4.7).
        /// </summary>
        float GroundHeight { get; }

        /// <summary>Interpolated world X of the rig, including mid-lane-change positions.</summary>
        float LateralPosition { get; }

        /// <summary>
        /// Rig Z inside the current origin-shift window — <see cref="DistanceRun"/> minus the accumulated
        /// shift. The chaser matches this so the pair stays together without either storing an absolute
        /// world position (§4.6, §4.8). Never use it as a distance; it wraps at the shift threshold.
        /// </summary>
        float ForwardOffset { get; }

        void RequestLaneChange(int delta);
        void RequestJump(float heightMultiplier = 1f);
        void RequestRoll();

        /// <summary>Upward impulse that may act mid-air (ramp / rooftop launch, §4.7).</summary>
        void RequestLaunch(float upwardVelocity);

        void SetSpeedProfile(SpeedProfileHandle profile);
        void ResetForRun(RunStartContext ctx);
        void Tick(float dt);

        void NotifyStumble();
        void SetInvulnerable(float seconds);

        /// <summary>
        /// Declares the top of the mountable surface the rig is standing over, or 0 for none. The contact
        /// sensor owns this because it is the only system that reads physics; the motor owns what the
        /// declaration <i>means</i> — land on it, step up onto it, or fall off its far edge (§4.7).
        /// </summary>
        void SetSupportHeight(float metres);

        void AddJumpModifier(IJumpModifier modifier);
        void RemoveJumpModifier(IJumpModifier modifier);
        void AddSpeedModifier(ISpeedModifier modifier);
        void RemoveSpeedModifier(ISpeedModifier modifier);
        void SetLaneChangeRule(ILaneChangeRule rule);
    }

    /// <summary>
    /// Owns the player rig, lane index, vertical state and speed (TECH_DOC §4.4). It does not decide
    /// collisions or scoring, and it never subscribes to input — the run controller maps intents to requests.
    ///
    /// Vertical motion is closed-form (gravity derived from the authored height and air time), not a
    /// Rigidbody simulation: identical on every device, exactly reproducible from a seed, and free.
    /// The kinematic Rigidbody on the rig exists only so trigger contacts are reported (§4.7).
    ///
    /// <b>Support surfaces.</b> Vertical motion is integrated against <see cref="GroundHeight"/> rather than
    /// against zero, so a ramp launch can land on a train roof and the player can run along it and jump off
    /// it. The sensor declares the surface; the motor never learns what an obstacle is. Losing the surface
    /// is a fall from rest, not a snap to the ground plane, so walking off a roof reads as a drop.
    ///
    /// The rig re-bases itself on an origin shift rather than being moved by a shifted parent, which is the
    /// same rule the track generator follows for live chunks (§4.6). PlayerRoot is therefore organisational
    /// and stays at the identity: shifting the parent <i>and</i> accumulating local Z would leave the stored
    /// local coordinate growing without bound, which is the precision loss the shift exists to prevent.
    /// </summary>
    public sealed class PlayerMotor : MonoBehaviour, IPlayerMotor, IOriginShiftListener
    {
        [Tooltip("Transform that moves. Defaults to this component's transform when left empty.")]
        [SerializeField] private Transform rig;

        private readonly List<IJumpModifier> _jumpModifiers = new List<IJumpModifier>(4);
        private readonly List<ISpeedModifier> _speedModifiers = new List<ISpeedModifier>(4);

        private BalanceConfig _balance;
        private IEventBus _bus;
        private ILog _log;
        private Transform _rig;
        private ILaneChangeRule _laneRule = new ClampedLaneChangeRule();

        private SpeedProfileHandle _profile;
        private bool _hasProfile;
        private bool _runActive;

        private int _lane;
        private float _laneFromX;
        private float _laneToX;
        private float _laneT = 1f;

        private PlayerVerticalState _vertical;
        private float _y;
        private float _vy;
        private float _groundHeight;
        private float _gravity;
        private float _rollRemaining;
        private bool _rollOnLanding;

        private float _distance;
        private float _speed;
        private float _lastPublishedSpeed = -1f;
        private float _stumbleRemaining;
        private float _invulnRemaining;

        public int Lane => _lane;
        public float DistanceRun => _distance;
        public float CurrentSpeed => _speed;
        public PlayerVerticalState VerticalState => _vertical;
        public bool IsInvulnerable => _invulnRemaining > 0f;
        public bool IsRecovering => _stumbleRemaining > 0f;
        public float HeightAboveGround => _y;
        public float GroundHeight => _groundHeight;
        public float CurrentHeight => _balance == null
            ? 0f
            : (_vertical == PlayerVerticalState.Rolling ? _balance.playerRollHeight : _balance.playerStandHeight);
        public float LateralPosition => CurrentLateralX();
        public float ForwardOffset => _rig != null ? _rig.localPosition.z : 0f;
        public float CurrentGravity => _gravity;

        private void Awake()
        {
            _rig = rig != null ? rig : transform;
        }

        /// <summary>Composition-root injection. <paramref name="rig"/> overrides the serialized field.</summary>
        public void Construct(BalanceConfig balance, IEventBus bus, ILog log, Transform rigOverride = null)
        {
            _balance = balance;
            _bus = bus;
            _log = log;
            if (rigOverride != null) _rig = rigOverride;
            else if (_rig == null) _rig = rig != null ? rig : transform;
        }

        public void SetLaneChangeRule(ILaneChangeRule rule)
        {
            _laneRule = rule ?? new ClampedLaneChangeRule();
        }

        public void AddJumpModifier(IJumpModifier modifier)
        {
            if (modifier != null && !_jumpModifiers.Contains(modifier)) _jumpModifiers.Add(modifier);
        }

        public void RemoveJumpModifier(IJumpModifier modifier)
        {
            if (modifier != null) _jumpModifiers.Remove(modifier);
        }

        public void AddSpeedModifier(ISpeedModifier modifier)
        {
            if (modifier != null && !_speedModifiers.Contains(modifier)) _speedModifiers.Add(modifier);
        }

        public void RemoveSpeedModifier(ISpeedModifier modifier)
        {
            if (modifier != null) _speedModifiers.Remove(modifier);
        }

        public void SetSpeedProfile(SpeedProfileHandle profile)
        {
            _profile = profile;
            _hasProfile = profile.IsValid;
            if (!_hasProfile) _log?.Warn(LogChannel.Gameplay, "Speed profile is invalid; falling back to BalanceConfig.");
        }

        public void ResetForRun(RunStartContext ctx)
        {
            if (_balance == null)
            {
                _log?.Error(LogChannel.Gameplay, "PlayerMotor.ResetForRun called before Construct.");
                return;
            }

            if (_rig == null) _rig = rig != null ? rig : transform;

            _runActive = true;
            _distance = 0f;
            _speed = 0f;
            _lastPublishedSpeed = -1f;

            int centre = _balance.laneCount / 2;
            _lane = ctx.StartLane >= 0 ? Mathf.Clamp(ctx.StartLane, 0, _balance.laneCount - 1) : centre;
            _laneFromX = _laneToX = _balance.LaneX(_lane);
            _laneT = 1f;

            _vertical = PlayerVerticalState.Grounded;
            _y = 0f;
            _vy = 0f;
            _groundHeight = 0f;
            _gravity = _balance.Gravity;
            _rollRemaining = 0f;
            _rollOnLanding = false;

            _stumbleRemaining = 0f;
            _invulnRemaining = 0f;

            _jumpModifiers.Clear();
            _speedModifiers.Clear();

            _profile = SpeedProfileHandle.FromBalance(_balance, ctx.SpeedScale);
            _hasProfile = _profile.IsValid;

            _rig.localPosition = new Vector3(_laneToX, 0f, 0f);
            UpdateSpeed(0f);
        }

        public void Tick(float dt)
        {
            if (!_runActive || dt <= 0f || _balance == null) return;

            UpdateSpeed(dt);
            UpdateLane(dt);
            UpdateVertical(dt);

            _distance += _speed * dt;

            if (_invulnRemaining > 0f) _invulnRemaining = Mathf.Max(0f, _invulnRemaining - dt);

            if (_stumbleRemaining > 0f)
            {
                _stumbleRemaining -= dt;
                if (_stumbleRemaining <= 0f)
                {
                    _stumbleRemaining = 0f;
                    // The chaser drops back to cruising distance on a clean recovery; the run controller
                    // routes this rather than the motor knowing the chaser exists.
                    _bus?.Publish(new PlayerRecoveredEvent());
                }
            }

            // One transform write per frame. Local Z is re-based by OnOriginShifted, so it stays inside a
            // single shift window no matter how long the run gets (§4.6). DistanceRun is the scalar that
            // keeps growing; this coordinate never does.
            Vector3 p = _rig.localPosition;
            p.x = CurrentLateralX();
            p.y = _y;
            p.z += _speed * dt;
            _rig.localPosition = p;
        }

        /// <summary>
        /// Re-bases the rig after the world is translated back. Registered by the composition root, not by
        /// the motor itself: the motor does not know the shifter exists beyond this callback, and the
        /// generator uses the identical seam (§4.6).
        /// </summary>
        public void OnOriginShifted(double deltaMetres)
        {
            if (_rig == null) return;

            Vector3 p = _rig.localPosition;
            p.z -= (float)deltaMetres;
            _rig.localPosition = p;
        }

        public void RequestLaneChange(int delta)
        {
            if (!_runActive || delta == 0 || _balance == null) return;

            int target = _laneRule.ResolveTargetLane(_lane, delta, _balance.laneCount);
            if (target == _lane) return;

            int previous = _lane;

            // Retarget from wherever the interpolation currently is, so a second swipe mid-change never
            // snaps. Queue depth stays 1: the newest request replaces the pending one.
            _laneFromX = CurrentLateralX();
            _laneToX = _balance.LaneX(target);
            _laneT = 0f;
            _lane = target;

            _bus?.Publish(new PlayerLaneChangedEvent(previous, target));
        }

        public void RequestJump(float heightMultiplier = 1f)
        {
            if (!_runActive || _balance == null) return;

            // No double jump: an airborne swipe-up is buffered by the controller and lands on touchdown.
            // A ramp launch goes through RequestLaunch instead, which is allowed to act mid-air.
            if (_vertical == PlayerVerticalState.Jumping || _vertical == PlayerVerticalState.Falling) return;

            if (_vertical == PlayerVerticalState.Rolling) EndRoll();

            float height = (_balance.jumpHeight + JumpHeightBonus()) * Mathf.Max(0.01f, heightMultiplier);
            float airTime = Mathf.Max(0.05f, _balance.jumpAirTime * JumpAirTimeScale());

            _gravity = 8f * height / (airTime * airTime);
            _vy = 4f * height / airTime;
            _vertical = PlayerVerticalState.Jumping;

            _bus?.Publish(new PlayerJumpedEvent(height));
        }

        /// <summary>
        /// Applies an upward impulse regardless of the current vertical state — the ramp/rooftop launch
        /// from §4.7. The resolver stays pure; the impulse is applied here, by the owner of motion.
        /// </summary>
        public void RequestLaunch(float upwardVelocity)
        {
            if (!_runActive || _balance == null || upwardVelocity <= 0f) return;

            if (_vertical == PlayerVerticalState.Rolling) EndRoll();

            float height = _balance.jumpHeight + JumpHeightBonus();
            float airTime = Mathf.Max(0.05f, _balance.jumpAirTime * JumpAirTimeScale());
            _gravity = 8f * height / (airTime * airTime);

            _vy = Mathf.Max(_vy, upwardVelocity);
            _vertical = _vy > 0f ? PlayerVerticalState.Jumping : PlayerVerticalState.Falling;

            float apex = _gravity > 0f ? (_vy * _vy) / (2f * _gravity) : 0f;
            _bus?.Publish(new PlayerJumpedEvent(apex));
        }

        public void RequestRoll()
        {
            if (!_runActive || _balance == null) return;

            // Already rolling: the authored duration is a minimum, so a re-swipe cannot shorten or stack it.
            if (_vertical == PlayerVerticalState.Rolling) return;

            if (_vertical == PlayerVerticalState.Jumping || _vertical == PlayerVerticalState.Falling)
            {
                // Swipe down in the air dives and rolls on touchdown, which is what players expect from
                // the genre. The descent is at least as fast as the take-off so the dive never feels floaty.
                _vy = -Mathf.Max(Mathf.Abs(_vy), _balance.JumpVelocity);
                _vertical = PlayerVerticalState.Falling;
                _rollOnLanding = true;
                return;
            }

            _vertical = PlayerVerticalState.Rolling;
            _rollRemaining = _balance.rollDuration;
            _bus?.Publish(new PlayerRollStartedEvent());
        }

        /// <summary>Applies the stumble slow-down and the recovery invulnerability window (TECH_DOC §4.7).</summary>
        public void NotifyStumble()
        {
            if (_balance == null) return;
            _stumbleRemaining = _balance.stumbleRecoveryTime;
            SetInvulnerable(_balance.stumbleRecoveryTime);
        }

        public void SetInvulnerable(float seconds)
        {
            if (seconds > _invulnRemaining) _invulnRemaining = seconds;
        }

        /// <summary>
        /// Adopts the declared support surface. The sensor only declares a roof the rig has already been
        /// classified as clearing from above, so this needs no guard against a surface appearing over the
        /// player's head: the mount window in <c>PlayerContactSensor</c> is what bounds that case (§4.7).
        /// </summary>
        public void SetSupportHeight(float metres)
        {
            _groundHeight = Mathf.Max(0f, metres);
        }

        private void UpdateSpeed(float dt)
        {
            float value = _hasProfile ? _profile.SpeedAt(_distance) : _balance.SpeedAtDistance(_distance, 1f);

            for (int i = 0; i < _speedModifiers.Count; i++) value = _speedModifiers[i].Modify(value, _distance);

            float slow = 1f;
            if (_stumbleRemaining > 0f && _balance.stumbleRecoveryTime > 0f)
            {
                float recovered = 1f - Mathf.Clamp01(_stumbleRemaining / _balance.stumbleRecoveryTime);
                slow = Mathf.Lerp(_balance.stumbleSlowFactor, 1f, recovered);
            }

            float cap = _hasProfile ? _profile.MaxSpeed * _profile.Scale : _balance.maxSpeed;
            _speed = Mathf.Clamp(value * slow, 0f, cap);

            // Publish on a meaningful change only: a per-frame event would wake every subscriber for nothing.
            if (Mathf.Abs(_speed - _lastPublishedSpeed) >= 0.05f)
            {
                _lastPublishedSpeed = _speed;
                _bus?.Publish(new PlayerSpeedChangedEvent(_speed));
            }
        }

        private void UpdateLane(float dt)
        {
            if (_laneT >= 1f) return;
            float duration = Mathf.Max(0.0001f, _balance.laneChangeDuration);
            _laneT = Mathf.Clamp01(_laneT + dt / duration);
        }

        private float CurrentLateralX()
        {
            if (_laneT >= 1f) return _laneToX;

            float t = _balance != null && _balance.laneChangeCurve != null && _balance.laneChangeCurve.length > 0
                ? _balance.laneChangeCurve.Evaluate(_laneT)
                : _laneT;

            // Unclamped: an authored overshoot curve is a legitimate lane-change feel.
            return Mathf.LerpUnclamped(_laneFromX, _laneToX, t);
        }

        private void UpdateVertical(float dt)
        {
            switch (_vertical)
            {
                case PlayerVerticalState.Jumping:
                case PlayerVerticalState.Falling:
                    _vy -= _gravity * dt;
                    _y += _vy * dt;
                    // The support surface is the floor, not zero: this is what lets a ramp launch land on a
                    // train roof instead of falling through it to the ground plane (§4.4, §4.7).
                    if (_y <= _groundHeight) Land();
                    else _vertical = _vy > 0f ? PlayerVerticalState.Jumping : PlayerVerticalState.Falling;
                    break;

                case PlayerVerticalState.Grounded:
                case PlayerVerticalState.Rolling:
                    if (_vertical == PlayerVerticalState.Rolling)
                    {
                        _rollRemaining -= dt;
                        if (_rollRemaining <= 0f) EndRoll();
                    }

                    // The surface changed under a rig that is not airborne. A low crest is a step up; the
                    // far edge of a roof passing underneath is a fall from rest, so the drop has weight
                    // instead of teleporting the player to the ground.
                    if (_y < _groundHeight)
                    {
                        _y = _groundHeight;
                    }
                    else if (_y > _groundHeight)
                    {
                        // A roll interrupted by the edge resumes on touchdown, which is what a swipe-down
                        // off a roof should do.
                        if (_vertical == PlayerVerticalState.Rolling) _rollOnLanding = true;
                        _vertical = PlayerVerticalState.Falling;
                        _vy = 0f;
                    }
                    break;
            }
        }

        private void Land()
        {
            _y = _groundHeight;
            _vy = 0f;
            _bus?.Publish(new PlayerLandedEvent());

            bool roll = _rollOnLanding;
            _rollOnLanding = false;

            if (roll)
            {
                _vertical = PlayerVerticalState.Rolling;
                _rollRemaining = _balance.rollDuration;
                _bus?.Publish(new PlayerRollStartedEvent());
            }
            else
            {
                _vertical = PlayerVerticalState.Grounded;
            }
        }

        private void EndRoll()
        {
            _rollRemaining = 0f;
            _vertical = PlayerVerticalState.Grounded;
            _bus?.Publish(new PlayerRollEndedEvent());
        }

        private float JumpHeightBonus()
        {
            float bonus = 0f;
            for (int i = 0; i < _jumpModifiers.Count; i++) bonus += _jumpModifiers[i].HeightBonus;
            return bonus;
        }

        private float JumpAirTimeScale()
        {
            float scale = 1f;
            for (int i = 0; i < _jumpModifiers.Count; i++) scale *= _jumpModifiers[i].AirTimeScale;
            return Mathf.Max(0.1f, scale);
        }
    }
}