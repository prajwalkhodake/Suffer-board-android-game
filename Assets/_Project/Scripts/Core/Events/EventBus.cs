using System;
using System.Collections.Generic;

namespace Skyrift.Core.Events
{
    /// <summary>
    /// Marker for a domain/UI event payload. Payloads must be <c>readonly struct</c>s so that
    /// publishing never boxes and never allocates (TECH_DOC §4.0, ADR-0004).
    /// </summary>
    public interface IGameEvent { }

    /// <summary>
    /// The only cross-module signalling mechanism. Gameplay publishes, Meta/Services/Audio/UI subscribe.
    /// Subscribe in OnInit/OnEnable, unsubscribe in OnDispose/OnDisable — never per frame.
    /// </summary>
    public interface IEventBus
    {
        void Subscribe<T>(Action<T> handler) where T : struct, IGameEvent;
        void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent;
        void Publish<T>(in T evt) where T : struct, IGameEvent;

        /// <summary>Number of live handlers for a payload type. Debug/profiling only.</summary>
        int HandlerCount<T>() where T : struct, IGameEvent;

        /// <summary>Drops every handler. Used by tests and by run-scope teardown.</summary>
        void Clear();
    }

    /// <summary>
    /// Per-payload-type channel. Static state is allowed here because this is Core (TECH_DOC §13.2 rule 4)
    /// and because a generic static avoids a dictionary lookup on every publish.
    /// </summary>
    internal static class EventChannel<T> where T : struct, IGameEvent
    {
        public static Action<T> Handlers;
        public static int Depth;
        public static List<Action<T>> PendingAdd;
        public static List<Action<T>> PendingRemove;
    }

    /// <summary>
    /// Allocation-free event bus. Publishing snapshots the immutable delegate so a handler may publish
    /// or unsubscribe during dispatch; subscription changes made during dispatch are applied afterwards.
    /// </summary>
    public sealed class EventBus : IEventBus
    {
        private readonly List<Action> _clearActions = new List<Action>(32);
        private readonly HashSet<Type> _clearTypes = new HashSet<Type>();

        public void Subscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            if (handler == null) return;

            if (EventChannel<T>.Depth > 0)
            {
                (EventChannel<T>.PendingAdd ??= new List<Action<T>>(4)).Add(handler);
                return;
            }

            EventChannel<T>.Handlers = (Action<T>)Delegate.Combine(EventChannel<T>.Handlers, handler);
            TrackClear<T>();
        }

        public void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            if (handler == null) return;

            if (EventChannel<T>.Depth > 0)
            {
                (EventChannel<T>.PendingRemove ??= new List<Action<T>>(4)).Add(handler);
                return;
            }

            EventChannel<T>.Handlers = (Action<T>)Delegate.Remove(EventChannel<T>.Handlers, handler);
        }

        public void Publish<T>(in T evt) where T : struct, IGameEvent
        {
            Action<T> handlers = EventChannel<T>.Handlers;
            if (handlers == null) return;

            EventChannel<T>.Depth++;
            try
            {
                handlers(evt);
            }
            finally
            {
                EventChannel<T>.Depth--;
                if (EventChannel<T>.Depth == 0) FlushPending<T>();
            }
        }

        public int HandlerCount<T>() where T : struct, IGameEvent
        {
            Action<T> handlers = EventChannel<T>.Handlers;
            return handlers?.GetInvocationList().Length ?? 0;
        }

        public void Clear()
        {
            for (int i = 0; i < _clearActions.Count; i++) _clearActions[i]();
            _clearActions.Clear();
        }

        private void TrackClear<T>() where T : struct, IGameEvent
        {
            // One clearer per payload type. HashSet.Add is allocation-free once warm and only runs on subscribe.
            if (_clearTypes.Add(typeof(T))) _clearActions.Add(ClearChannel<T>);
        }

        private static void ClearChannel<T>() where T : struct, IGameEvent
        {
            EventChannel<T>.Handlers = null;
            EventChannel<T>.PendingAdd = null;
            EventChannel<T>.PendingRemove = null;
            EventChannel<T>.Depth = 0;
        }

        private static void FlushPending<T>() where T : struct, IGameEvent
        {
            List<Action<T>> remove = EventChannel<T>.PendingRemove;
            if (remove != null)
            {
                for (int i = 0; i < remove.Count; i++)
                    EventChannel<T>.Handlers = (Action<T>)Delegate.Remove(EventChannel<T>.Handlers, remove[i]);
                remove.Clear();
            }

            List<Action<T>> add = EventChannel<T>.PendingAdd;
            if (add != null)
            {
                for (int i = 0; i < add.Count; i++)
                    EventChannel<T>.Handlers = (Action<T>)Delegate.Combine(EventChannel<T>.Handlers, add[i]);
                add.Clear();
            }
        }
    }
}
