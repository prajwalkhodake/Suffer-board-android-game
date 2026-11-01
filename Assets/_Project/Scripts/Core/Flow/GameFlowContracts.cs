using System;
using Skyrift.Core.Events;

namespace Skyrift.Core.Flow
{
    /// <summary>
    /// The only run-related flow states (TECH_DOC §4.2, §6.1). Meta screens — shop, missions, character
    /// select, settings, events, leaderboards — are UI navigation inside <see cref="Home"/>, not states.
    ///
    /// Placement: this is a Core-level contract, not a Bootstrap type. Audio, Gameplay, UI and Bootstrap
    /// all need to observe flow, and the dependency DAG (§3.3) forbids Audio → Gameplay and anything →
    /// Bootstrap. §3.3's sanctioned escape hatch for exactly this case is "a Core-level contract";
    /// Bootstrap still owns the only implementation and the transition table.
    /// </summary>
    public enum GameFlowState
    {
        Boot = 0,
        Splash,
        Loading,
        Home,
        Running,
        Paused,
        Crashed,
        ContinueOffer,
        Results,
        Maintenance
    }

    /// <summary>
    /// Why a transition was requested. Carried for logging and telemetry only — guards read game state,
    /// never this struct, so a caller cannot talk its way past a guard by choosing a reason.
    /// </summary>
    public readonly struct TransitionContext
    {
        public readonly string Reason;

        public TransitionContext(string reason)
        {
            Reason = reason ?? string.Empty;
        }

        public static readonly TransitionContext None = new TransitionContext(string.Empty);
    }

    /// <summary>
    /// The single authority on "where is the player" (TECH_DOC §4.2). UI screens subscribe; they never
    /// decide flow. Illegal transitions are refused and logged, never silently applied.
    /// </summary>
    public interface IGameFlow
    {
        GameFlowState Current { get; }
        GameFlowState Previous { get; }
        bool CanTransition(GameFlowState next);
        bool TryTransition(GameFlowState next, in TransitionContext ctx = default);
        event Action<GameFlowState, GameFlowState> Transitioned;
        event Action<GameFlowState, GameFlowState> TransitionRefused;
    }

    /// <summary>Published after every accepted flow transition. The payload is a struct: no allocation.</summary>
    public readonly struct GameFlowChangedEvent : IGameEvent
    {
        public readonly GameFlowState From;
        public readonly GameFlowState To;

        public GameFlowChangedEvent(GameFlowState from, GameFlowState to)
        {
            From = from;
            To = to;
        }
    }
}