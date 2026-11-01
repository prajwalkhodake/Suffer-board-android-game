using System;
using System.Collections.Generic;

namespace Skyrift.Core.StateMachine
{
    /// <summary>
    /// Generic, table-driven state machine (TECH_DOC §4.0). Illegal transitions are refused and reported,
    /// never silently applied. The concrete game-flow table lives in Skyrift.Bootstrap (§6.1).
    /// </summary>
    public interface IGameStateMachine<TState> where TState : struct, Enum
    {
        TState Current { get; }
        TState Previous { get; }
        bool CanTransition(TState from, TState to);
        bool TryTransition(TState next);
        event Action<TState, TState> Transitioned;
        event Action<TState, TState> TransitionRefused;
    }

    /// <summary>
    /// Dictionary-of-sets transition table. All lookups happen on transition (a user-scale event),
    /// never per frame, so allocation on the first transition of a pair is acceptable.
    /// </summary>
    public sealed class GameStateMachine<TState> : IGameStateMachine<TState> where TState : struct, Enum
    {
        private readonly Dictionary<TState, HashSet<TState>> _allowed = new Dictionary<TState, HashSet<TState>>();

        public TState Current { get; private set; }
        public TState Previous { get; private set; }

        public event Action<TState, TState> Transitioned;
        public event Action<TState, TState> TransitionRefused;

        public GameStateMachine(TState initial)
        {
            Current = initial;
            Previous = initial;
        }

        /// <summary>Declares a legal edge. Called once during composition.</summary>
        public GameStateMachine<TState> Allow(TState from, params TState[] to)
        {
            if (!_allowed.TryGetValue(from, out HashSet<TState> set))
            {
                set = new HashSet<TState>();
                _allowed[from] = set;
            }

            for (int i = 0; i < to.Length; i++) set.Add(to[i]);
            return this;
        }

        public bool CanTransition(TState from, TState to)
        {
            if (EqualityComparer<TState>.Default.Equals(from, to)) return false;
            return _allowed.TryGetValue(from, out HashSet<TState> set) && set.Contains(to);
        }

        public bool TryTransition(TState next)
        {
            TState from = Current;
            if (!CanTransition(from, next))
            {
                TransitionRefused?.Invoke(from, next);
                return false;
            }

            Previous = from;
            Current = next;
            Transitioned?.Invoke(from, next);
            return true;
        }
    }
}
