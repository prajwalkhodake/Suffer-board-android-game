using Skyrift.Core.Events;
using Skyrift.Core.Ids;

namespace Skyrift.Gameplay
{
    // All gameplay event payloads are readonly structs (TECH_DOC §4.0, ADR-0004): publishing one never
    // boxes and never allocates. They are grouped in this contract-unit file because they are the public
    // signalling surface of the run, and a subscriber should be able to see all of it in one place.

    /// <summary>The player's target lane changed. Published once per accepted swipe, not per frame.</summary>
    public readonly struct PlayerLaneChangedEvent : IGameEvent
    {
        public readonly int FromLane;
        public readonly int ToLane;

        public PlayerLaneChangedEvent(int fromLane, int toLane)
        {
            FromLane = fromLane;
            ToLane = toLane;
        }
    }

    /// <summary>Take-off. <see cref="ApexHeight"/> is the implied peak, for animation and audio scaling.</summary>
    public readonly struct PlayerJumpedEvent : IGameEvent
    {
        public readonly float ApexHeight;

        public PlayerJumpedEvent(float apexHeight)
        {
            ApexHeight = apexHeight;
        }
    }

    /// <summary>Touchdown after a jump or a dive.</summary>
    public readonly struct PlayerLandedEvent : IGameEvent
    {
    }

    /// <summary>The roll started (from the ground or on landing after a dive).</summary>
    public readonly struct PlayerRollStartedEvent : IGameEvent
    {
    }

    /// <summary>The roll ended, either by duration or by being cancelled into a jump.</summary>
    public readonly struct PlayerRollEndedEvent : IGameEvent
    {
    }

    /// <summary>Speed changed by a meaningful amount. Drives music intensity, camera FOV and track difficulty.</summary>
    public readonly struct PlayerSpeedChangedEvent : IGameEvent
    {
        public readonly float Speed;

        public PlayerSpeedChangedEvent(float speed)
        {
            Speed = speed;
        }
    }

    /// <summary>A side scrape. <see cref="Count"/> is the stumble total for this run (1 = warning).</summary>
    public readonly struct PlayerStumbledEvent : IGameEvent
    {
        public readonly int Count;
        public readonly StableId ObstacleId;

        public PlayerStumbledEvent(int count, StableId obstacleId)
        {
            Count = count;
            ObstacleId = obstacleId;
        }
    }

    /// <summary>The run is over. The crash sequence plays on unscaled time after this is published.</summary>
    public readonly struct PlayerCrashedEvent : IGameEvent
    {
        public readonly CrashCause Cause;
        public readonly CrashContext Context;

        public PlayerCrashedEvent(CrashCause cause, CrashContext context)
        {
            Cause = cause;
            Context = context;
        }
    }

    /// <summary>The stumble slow-down finished and the player is back at full speed.</summary>
    public readonly struct PlayerRecoveredEvent : IGameEvent
    {
    }

    /// <summary>Every classified contact. Debug/telemetry only — never drive gameplay from this.</summary>
    public readonly struct ContactResolvedEvent : IGameEvent
    {
        public readonly ContactOutcome Outcome;
        public readonly StableId ObstacleId;

        public ContactResolvedEvent(ContactOutcome outcome, StableId obstacleId)
        {
            Outcome = outcome;
            ObstacleId = obstacleId;
        }
    }

    /// <summary>Chaser pressure, 0 = far behind, 1 = caught up. Presentation only; it never kills (§4.8).</summary>
    public readonly struct ChaserProximityChangedEvent : IGameEvent
    {
        public readonly float Proximity;

        public ChaserProximityChangedEvent(float proximity)
        {
            Proximity = proximity;
        }
    }

    /// <summary>The catch animation moment, played during the crash sequence.</summary>
    public readonly struct ChaserCaughtPlayerEvent : IGameEvent
    {
    }

    /// <summary>The integer score changed. Published only when the displayed value actually moves.</summary>
    public readonly struct ScoreChangedEvent : IGameEvent
    {
        public readonly long Score;
        public readonly long Delta;

        public ScoreChangedEvent(long score, long delta)
        {
            Score = score;
            Delta = delta;
        }
    }

    /// <summary>Combo step changed (collection chain within the data-defined window).</summary>
    public readonly struct ComboChangedEvent : IGameEvent
    {
        public readonly int Combo;

        public ComboChangedEvent(int combo)
        {
            Combo = combo;
        }
    }

    /// <summary>Product of all active score multipliers changed (power-ups, completed mission sets).</summary>
    public readonly struct ScoreMultiplierChangedEvent : IGameEvent
    {
        public readonly float Factor;

        public ScoreMultiplierChangedEvent(float factor)
        {
            Factor = factor;
        }
    }

    /// <summary>A run began. The seed is logged so any run can be reproduced from a bug report.</summary>
    public readonly struct RunStartedEvent : IGameEvent
    {
        public readonly uint Seed;
        public readonly StableId WorldId;
        public readonly bool IsTutorial;

        public RunStartedEvent(uint seed, StableId worldId, bool isTutorial)
        {
            Seed = seed;
            WorldId = worldId;
            IsTutorial = isTutorial;
        }
    }

    /// <summary>The run simulation is frozen; UI and audio keep running on unscaled time.</summary>
    public readonly struct RunPausedEvent : IGameEvent
    {
    }

    /// <summary>The run simulation resumed.</summary>
    public readonly struct RunResumedEvent : IGameEvent
    {
    }

    /// <summary>The crash sequence started (flow is already <c>Crashed</c>).</summary>
    public readonly struct RunCrashedEvent : IGameEvent
    {
        public readonly CrashCause Cause;

        public RunCrashedEvent(CrashCause cause)
        {
            Cause = cause;
        }
    }

    /// <summary>A continue is on offer, with its data-driven cost.</summary>
    public readonly struct ContinueOfferedEvent : IGameEvent
    {
        public readonly int KeyCost;
        public readonly int ContinueIndex;
        public readonly bool AdAvailable;

        public ContinueOfferedEvent(int keyCost, int continueIndex, bool adAvailable)
        {
            KeyCost = keyCost;
            ContinueIndex = continueIndex;
            AdAvailable = adAvailable;
        }
    }

    /// <summary>A continue was granted and the run resumed from the crash point.</summary>
    public readonly struct RunContinuedEvent : IGameEvent
    {
        public readonly ContinueSource Source;
        public readonly int KeysSpent;

        public RunContinuedEvent(ContinueSource source, int keysSpent)
        {
            Source = source;
            KeysSpent = keysSpent;
        }
    }

    /// <summary>The run is finished and committed. Carries the immutable summary the results screen reads.</summary>
    public readonly struct RunEndedEvent : IGameEvent
    {
        public readonly RunSummary Summary;

        public RunEndedEvent(RunSummary summary)
        {
            Summary = summary;
        }
    }

    /// <summary>UI asked for a run to start (TECH_DOC §6.2). The controller is the only consumer.</summary>
    public readonly struct RunStartRequestedEvent : IGameEvent
    {
        public readonly RunStartContext Context;

        public RunStartRequestedEvent(RunStartContext context)
        {
            Context = context;
        }
    }

    /// <summary>UI asked for a continue. The controller validates it against the current offer.</summary>
    public readonly struct ContinueRequestedEvent : IGameEvent
    {
        public readonly ContinueSource Source;

        public ContinueRequestedEvent(ContinueSource source)
        {
            Source = source;
        }
    }
}