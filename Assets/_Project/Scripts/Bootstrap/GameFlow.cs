using System;
using System.Collections.Generic;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Flow;
using Skyrift.Core.StateMachine;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// Per-state behaviour seam (TECH_DOC §4.2 extension point). A handler owns what happens on entering and
    /// leaving one state — showing a screen, starting a preload, gating a transition — so adding a state is
    /// an enum value, a table row and a handler, never an edit to <see cref="GameFlow"/>.
    ///
    /// <see cref="CanEnter"/> is a guard on game state, not on the caller's reason: a caller cannot talk its
    /// way past a guard by choosing a flattering <c>TransitionContext</c>.
    /// </summary>
    public interface IGameFlowStateHandler
    {
        GameFlowState State { get; }
        bool CanEnter(GameFlowState from);
        void OnEnter(GameFlowState from);
        void OnExit(GameFlowState to);
    }

    /// <summary>
    /// The single authority on "where is the player" (TECH_DOC §4.2, §6.1). The transition table below is
    /// the one in §6.1, edge for edge; anything not listed is refused and logged, never silently applied.
    ///
    /// Meta screens — shop, missions, character select, settings, events, leaderboards — are UI navigation
    /// inside <see cref="GameFlowState.Home"/>, not states, so they never appear here.
    /// </summary>
    public sealed class GameFlow : IGameFlow, IDisposable
    {
        private readonly GameStateMachine<GameFlowState> _fsm;
        private readonly IEventBus _bus;
        private readonly ILog _log;

        private readonly Dictionary<GameFlowState, IGameFlowStateHandler> _handlers =
            new Dictionary<GameFlowState, IGameFlowStateHandler>(12);

        private bool _disposed;

        public GameFlowState Current => _fsm.Current;
        public GameFlowState Previous => _fsm.Previous;

        public event Action<GameFlowState, GameFlowState> Transitioned;
        public event Action<GameFlowState, GameFlowState> TransitionRefused;

        public GameFlow(IEventBus bus, ILog log)
        {
            _bus = bus;
            _log = log;
            _fsm = new GameStateMachine<GameFlowState>(GameFlowState.Boot);

            // TECH_DOC §6.1, verbatim. Declared once, at composition time.
            _fsm.Allow(GameFlowState.Boot, GameFlowState.Splash, GameFlowState.Maintenance);
            _fsm.Allow(GameFlowState.Splash, GameFlowState.Loading);
            _fsm.Allow(GameFlowState.Loading, GameFlowState.Home);
            _fsm.Allow(GameFlowState.Home, GameFlowState.Running);
            _fsm.Allow(GameFlowState.Running, GameFlowState.Paused, GameFlowState.Crashed, GameFlowState.Results);
            _fsm.Allow(GameFlowState.Paused, GameFlowState.Running, GameFlowState.Results, GameFlowState.Home);
            _fsm.Allow(GameFlowState.Crashed, GameFlowState.ContinueOffer, GameFlowState.Results);
            _fsm.Allow(GameFlowState.ContinueOffer, GameFlowState.Running, GameFlowState.Results);
            _fsm.Allow(GameFlowState.Results, GameFlowState.Home, GameFlowState.Running);
            _fsm.Allow(GameFlowState.Maintenance, GameFlowState.Splash);

            _fsm.Transitioned += OnTransitioned;
            _fsm.TransitionRefused += OnTransitionRefused;
        }

        /// <summary>Composition-time only. One handler per state; a second registration is a wiring error.</summary>
        public void RegisterHandler(IGameFlowStateHandler handler)
        {
            if (handler == null) return;

            if (_handlers.ContainsKey(handler.State))
            {
                _log?.Error(LogChannel.Bootstrap, $"A handler for flow state '{handler.State}' is already registered.");
                return;
            }

            _handlers.Add(handler.State, handler);
        }

        public bool CanTransition(GameFlowState next)
        {
            // Staying put is always legal, and it is what makes TryTransition idempotent.
            if (next == _fsm.Current) return true;
            if (!_fsm.CanTransition(_fsm.Current, next)) return false;

            return !_handlers.TryGetValue(next, out IGameFlowStateHandler handler) || handler.CanEnter(_fsm.Current);
        }

        public bool TryTransition(GameFlowState next, in TransitionContext ctx = default)
        {
            if (_disposed) return false;

            // Idempotent by contract. The run controller and the flow can both legitimately ask for Running
            // on resume; treating "already there" as a refusal would log an error for a normal call and, worse,
            // make the caller roll back a run that is in fact fine. No event is published for a no-op.
            if (next == _fsm.Current) return true;

            if (!_fsm.CanTransition(_fsm.Current, next))
            {
                OnTransitionRefused(_fsm.Current, next);
                return false;
            }

            if (_handlers.TryGetValue(next, out IGameFlowStateHandler guard) && !guard.CanEnter(_fsm.Current))
            {
                _log?.Warn(LogChannel.Bootstrap,
                    $"Flow transition {_fsm.Current} → {next} refused by the state guard ({ctx.Reason}).");
                TransitionRefused?.Invoke(_fsm.Current, next);
                return false;
            }

            return _fsm.TryTransition(next);
        }

        private void OnTransitioned(GameFlowState from, GameFlowState to)
        {
            // Exit and enter run before the event is published, so a subscriber never observes a state whose
            // side effects (screen shown, preload started) have not happened yet.
            if (_handlers.TryGetValue(from, out IGameFlowStateHandler exit)) exit.OnExit(to);
            if (_handlers.TryGetValue(to, out IGameFlowStateHandler enter)) enter.OnEnter(from);

            _bus?.Publish(new GameFlowChangedEvent(from, to));
            Transitioned?.Invoke(from, to);
        }

        private void OnTransitionRefused(GameFlowState from, GameFlowState to)
        {
            // An illegal edge is a bug, not a user action: it is always logged, in every build.
            _log?.Error(LogChannel.Bootstrap, $"Illegal flow transition {from} → {to} refused (TECH_DOC §6.1).");
            TransitionRefused?.Invoke(from, to);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _fsm.Transitioned -= OnTransitioned;
            _fsm.TransitionRefused -= OnTransitionRefused;
            _handlers.Clear();
        }
    }
}