using Skyrift.Data;

namespace Skyrift.Gameplay
{
    /// <summary>Which part of the player touched the object (TECH_DOC §4.7).</summary>
    public enum ContactSurface
    {
        Head = 0,
        Torso,
        Feet,
        Side,
        Top
    }

    /// <summary>
    /// Result of one contact (TECH_DOC §4.7). <see cref="Grazed"/> means a crash-absorbing power-up took
    /// the hit; <see cref="Cleared"/> means the player legitimately beat the obstacle.
    /// </summary>
    public enum ContactOutcome
    {
        Ignored = 0,
        Grazed,
        Stumbled,
        Crashed,
        Cleared
    }

    /// <summary>Why a run ended in a crash. Reported to analytics and used to pick the crash animation.</summary>
    public enum CrashCause
    {
        None = 0,
        HeadOnObstacle,
        SecondStumble,
        ChaserCaught,
        FellOutOfWorld,
        SystemError
    }

    /// <summary>Immutable snapshot of the moment of impact, for the crash sequence and for telemetry.</summary>
    public readonly struct CrashContext
    {
        public readonly float DistanceMetres;
        public readonly float Speed;
        public readonly int Lane;
        public readonly int StumbleCount;
        public readonly StableId ObstacleId;

        public CrashContext(float distanceMetres, float speed, int lane, int stumbleCount, StableId obstacleId)
        {
            DistanceMetres = distanceMetres;
            Speed = speed;
            Lane = lane;
            StumbleCount = stumbleCount;
            ObstacleId = obstacleId;
        }
    }

    /// <summary>
    /// Everything needed to classify one contact (TECH_DOC §4.7). A readonly struct passed <c>in</c> so
    /// classification never allocates and never mutates the caller's state.
    ///
    /// The clearance data and the archetype behaviour travel with the context rather than being looked up
    /// inside the resolver: that keeps <see cref="ICollisionResolver"/> a pure function of its input.
    /// </summary>
    public readonly struct ContactContext
    {
        public readonly ContactSurface Surface;
        public readonly int PlayerLane;
        public readonly int ObjectLane;

        /// <summary>Player forward speed in m/s.</summary>
        public readonly float PlayerSpeed;

        /// <summary>Closing speed along the travel axis; negative means the object is outrunning the player.</summary>
        public readonly float RelativeSpeed;

        /// <summary>Height of the player's feet above the ground plane.</summary>
        public readonly float HeightAboveGround;

        /// <summary>Current height of the player's collision volume (reduced while rolling).</summary>
        public readonly float PlayerHeight;

        /// <summary>Height of the object's top surface above the ground plane.</summary>
        public readonly float ObjectHeight;

        /// <summary>
        /// Absolute lateral distance between player and object centre, expressed in lane widths.
        /// Below <c>BalanceConfig.grazeLateralThreshold</c> the hit is head-on, above it a side scrape.
        /// </summary>
        public readonly float LateralOffset;

        public readonly PlayerVerticalState VerticalState;
        public readonly bool HasShield;
        public readonly ClearanceProfile Clearance;
        public readonly IObstacleBehaviour Behaviour;

        /// <summary>
        /// Forgiveness and graze thresholds travel with the context so a behaviour is a pure function of
        /// its input and never has to resolve a config asset itself.
        /// </summary>
        public readonly BalanceConfig Balance;

        public ContactContext(ContactSurface surface, int playerLane, int objectLane, float playerSpeed,
            float relativeSpeed, float heightAboveGround, float playerHeight, float objectHeight,
            float lateralOffset, PlayerVerticalState verticalState, bool hasShield, ClearanceProfile clearance,
            IObstacleBehaviour behaviour, BalanceConfig balance)
        {
            Surface = surface;
            PlayerLane = playerLane;
            ObjectLane = objectLane;
            PlayerSpeed = playerSpeed;
            RelativeSpeed = relativeSpeed;
            HeightAboveGround = heightAboveGround;
            PlayerHeight = playerHeight;
            ObjectHeight = objectHeight;
            LateralOffset = lateralOffset;
            VerticalState = verticalState;
            HasShield = hasShield;
            Clearance = clearance;
            Behaviour = behaviour;
            Balance = balance;
        }
    }

    /// <summary>
    /// Per-archetype contact classification (TECH_DOC §4.7 extension point). Implementations are
    /// stateless ScriptableObjects selected by <c>ObstacleDefinition.behaviourId</c>, so a new obstacle
    /// that reuses an existing behaviour is pure data (playbook §7.3).
    ///
    /// Must be pure: same context ⇒ same outcome. Escalation (first stumble vs second) belongs to
    /// <see cref="ICollisionResolver"/>, never here.
    /// </summary>
    public interface IObstacleBehaviour
    {
        ContactOutcome Resolve(in ContactContext ctx);
    }

    /// <summary>
    /// Classifies a contact and applies the two-hit stumble model (TECH_DOC §4.7).
    /// The classification is pure; the only state is <see cref="StumbleCount"/>, which is exposed and
    /// resettable so tests can drive the escalation deterministically.
    /// </summary>
    public interface ICollisionResolver
    {
        ContactOutcome Resolve(in ContactContext ctx);
        int StumbleCount { get; }
        void ResetForRun();

        /// <summary>
        /// Clears the stumble counter without a full run reset — used by a successful continue, which
        /// forgives the stumbles already taken so the player is not one scrape from death on respawn.
        /// </summary>
        void ResetStumbles();
    }
}