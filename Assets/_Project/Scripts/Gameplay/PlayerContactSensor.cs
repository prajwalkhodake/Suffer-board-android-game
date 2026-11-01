using System.Collections.Generic;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Crash-absorbing charge seam (TECH_DOC §4.10). M1 has no shield source, so the sensor simply runs
    /// without one; the M2 hoverboard/shield power-up implements this and is injected — the sensor and the
    /// resolver never change.
    /// </summary>
    public interface IShieldProvider
    {
        bool HasShield { get; }

        /// <summary>Spends one charge. Returns false when there was nothing left to spend.</summary>
        bool TryConsume();
    }

    /// <summary>
    /// Turns trigger overlaps into classified contacts (TECH_DOC §4.7). This is the only place that reads
    /// physics during a run, and it reads nothing but the collider's identity: positions come from the
    /// motor and the obstacle, and the outcome comes from the pure resolver.
    ///
    /// <c>OnTriggerStay</c> is handled as well as <c>OnTriggerEnter</c> so a player who is still inside a
    /// long train when the stumble invulnerability expires is correctly hit again, instead of ghosting
    /// through it. Contacts already cleared are remembered until exit so a rooftop run does not re-trigger.
    ///
    /// <b>Support surfaces.</b> Clearing a mountable roof from above also declares it to the motor as the
    /// ground the rig lands on (§4.4). The declaration lives here because this is the only system that
    /// reads physics; the motor never learns what an obstacle is. It is withdrawn on trigger exit, so
    /// running off the far edge of a roof is a fall rather than a walk on air.
    /// </summary>
    public sealed class PlayerContactSensor : MonoBehaviour
    {
        // Collider instance ids already resolved as a clear/graze for the current overlap.
        private readonly HashSet<int> _clearedContacts = new HashSet<int>();

        // Mountable roofs the rig is currently over, keyed by collider instance id. A roof stays in here
        // until the rig leaves its volume, so jumping on the spot does not drop the player through it —
        // the authored trigger volume carries headroom above the roof for exactly this reason (§4.7).
        private readonly Dictionary<int, float> _mounts = new Dictionary<int, float>(4);
        private int _supportId;
        private float _supportHeight;

        private IPlayerMotor _motor;
        private ICollisionResolver _resolver;
        private ObstacleContactIndex _index;
        private IChaserSystem _chaser;
        private IShieldProvider _shield;
        private IEventBus _bus;
        private ILog _log;
        private BalanceConfig _balance;
        private bool _contactsEnabled;

        public void Construct(IPlayerMotor motor, ICollisionResolver resolver, ObstacleContactIndex index,
            IChaserSystem chaser, BalanceConfig balance, IEventBus bus, ILog log, IShieldProvider shield = null)
        {
            _motor = motor;
            _resolver = resolver;
            _index = index;
            _chaser = chaser;
            _balance = balance;
            _bus = bus;
            _log = log;
            _shield = shield;
        }

        /// <summary>Disabled on crash, pause and ads so a frozen run cannot register a new hit.</summary>
        public void SetEnabled(bool enabled)
        {
            _contactsEnabled = enabled;
            if (!enabled)
            {
                _clearedContacts.Clear();
                // A frozen run must not keep the player standing on a roof a continue is about to clear.
                // Withdrawing support lets the motor settle them onto the ground plane on respawn.
                ClearMounts();
            }
        }

        public void ResetForRun()
        {
            _clearedContacts.Clear();
            ClearMounts();
            _contactsEnabled = true;
        }

        private void OnTriggerEnter(Collider other) => Handle(other, true);

        private void OnTriggerStay(Collider other) => Handle(other, false);

        private void OnTriggerExit(Collider other)
        {
            if (other == null) return;

            int id = other.GetInstanceID();
            _clearedContacts.Remove(id);
            RemoveMount(id);
        }

        private void Handle(Collider other, bool isEnter)
        {
            if (!_contactsEnabled || other == null) return;
            if (_motor == null || _resolver == null || _index == null || _balance == null) return;

            int id = other.GetInstanceID();
            if (_clearedContacts.Contains(id)) return;

            // Invulnerability is checked before resolution so the stumble counter cannot be advanced by a
            // contact the player is currently immune to.
            if (_motor.IsInvulnerable) return;

            if (!_index.TryGet(id, out ObstacleInstance obstacle)) return;

            ObstacleDefinition definition = obstacle.Definition;
            if (definition == null) return;

            ContactContext ctx = BuildContext(obstacle, definition);
            ContactOutcome outcome = _resolver.Resolve(in ctx);

            if (outcome == ContactOutcome.Ignored) return;

            _bus?.Publish(new ContactResolvedEvent(outcome, definition.Id));

            switch (outcome)
            {
                case ContactOutcome.Cleared:
                    _clearedContacts.Add(id);
                    // The launch impulse is data; the resolver stays pure and the motor owns motion.
                    if (isEnter && definition.launchHeightMultiplier > 0f)
                    {
                        _motor.RequestLaunch(_balance.JumpVelocity * definition.launchHeightMultiplier);
                    }
                    else if (ctx.Surface == ContactSurface.Top && definition.clearance.mountable
                             && definition.launchHeightMultiplier <= 0f)
                    {
                        // A launcher is not a floor: adopting a ramp's crest as support would cancel the
                        // impulse it just applied. Only a pure platform declares a surface (§4.7).
                        AddMount(id, obstacle.ObjectHeight);
                    }
                    break;

                case ContactOutcome.Grazed:
                    _clearedContacts.Add(id);
                    if (_shield != null && !_shield.TryConsume())
                        _log?.Warn(LogChannel.Gameplay, "Contact was grazed but the shield had no charge to spend.");
                    break;

                case ContactOutcome.Stumbled:
                    _motor.NotifyStumble();
                    _chaser?.OnPlayerStumbled();
                    _bus?.Publish(new PlayerStumbledEvent(_resolver.StumbleCount, definition.Id));
                    break;

                case ContactOutcome.Crashed:
                    CrashCause cause = _resolver.StumbleCount >= _balance.maxStumblesBeforeCrash
                        ? CrashCause.SecondStumble
                        : CrashCause.HeadOnObstacle;

                    var crashContext = new CrashContext(_motor.DistanceRun, _motor.CurrentSpeed, _motor.Lane,
                        _resolver.StumbleCount, definition.Id);

                    _contactsEnabled = false;
                    _bus?.Publish(new PlayerCrashedEvent(cause, crashContext));
                    break;
            }
        }

        private ContactContext BuildContext(ObstacleInstance obstacle, ObstacleDefinition definition)
        {
            float laneWidth = Mathf.Max(0.01f, _balance.laneWidth);

            // The rig's parent is only ever translated along Z, so its local X is also its world X.
            float lateral = Mathf.Abs(_motor.LateralPosition - obstacle.CachedTransform.position.x) / laneWidth;

            float feet = _motor.HeightAboveGround;
            float objectHeight = obstacle.ObjectHeight;
            float playerTop = feet + _motor.CurrentHeight;

            ContactSurface surface;
            if (feet + _balance.clearanceForgiveness >= objectHeight) surface = ContactSurface.Top;
            else if (IsMountDescent(definition, feet, objectHeight)) surface = ContactSurface.Top;
            else if (lateral >= _balance.grazeLateralThreshold) surface = ContactSurface.Side;
            else if (playerTop <= definition.clearance.underClearance) surface = ContactSurface.Feet;
            else surface = ContactSurface.Torso;

            bool hasShield = _shield != null && _shield.HasShield;

            return new ContactContext(
                surface,
                _motor.Lane,
                obstacle.Lane,
                _motor.CurrentSpeed,
                _motor.CurrentSpeed - obstacle.ForwardSpeed,
                feet,
                _motor.CurrentHeight,
                objectHeight,
                lateral,
                _motor.VerticalState,
                hasShield,
                definition.clearance,
                obstacle.Behaviour,
                _balance);
        }

        /// <summary>
        /// A descending player about to touch down on a mountable roof. Trigger callbacks run on the physics
        /// step, not on the render tick the motor integrates in, so a fast descent can step straight over the
        /// narrow <c>clearanceForgiveness</c> band and be read as a torso hit — a fatal, frame-rate dependent
        /// coin flip. <c>mountForgiveness</c> widens the band to cover that step (§4.7).
        ///
        /// Requiring <see cref="PlayerVerticalState.Falling"/> is what keeps this from forgiving a player who
        /// ran into the roof's side: a grounded rig is never descending, so it still resolves as Torso.
        /// </summary>
        private bool IsMountDescent(ObstacleDefinition definition, float feet, float objectHeight)
        {
            return definition.clearance.mountable
                   && _motor.VerticalState == PlayerVerticalState.Falling
                   && feet >= objectHeight - _balance.mountForgiveness;
        }

        private void AddMount(int colliderId, float height)
        {
            _mounts[colliderId] = height;
            if (height < _supportHeight) return;

            _supportId = colliderId;
            _supportHeight = height;
            _motor.SetSupportHeight(height);
        }

        private void RemoveMount(int colliderId)
        {
            if (!_mounts.Remove(colliderId) || colliderId != _supportId) return;

            // The highest remaining roof becomes the support. A Dictionary's enumerator is a struct, so this
            // allocates nothing, and it runs on a contact exit rather than per frame (§11.2).
            _supportId = 0;
            _supportHeight = 0f;
            foreach (KeyValuePair<int, float> mount in _mounts)
            {
                if (mount.Value <= _supportHeight) continue;
                _supportId = mount.Key;
                _supportHeight = mount.Value;
            }

            _motor.SetSupportHeight(_supportHeight);
        }

        private void ClearMounts()
        {
            _mounts.Clear();
            _supportId = 0;
            _supportHeight = 0f;
            if (_motor != null) _motor.SetSupportHeight(0f);
        }
    }
}