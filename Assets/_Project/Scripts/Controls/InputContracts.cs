using System;
using Skyrift.Core.Events;
using Skyrift.Core.Time;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Controls
{
    /// <summary>
    /// Device-agnostic player intent (TECH_DOC §4.3). Input knows nothing about lanes or jumping;
    /// the run controller maps intents to motor requests.
    ///
    /// <see cref="None"/> is the buffer's "nothing pending" sentinel and must stay 0 so a default-valued
    /// intent is never mistaken for a swipe. <see cref="Hold"/> is reserved for press-and-hold gestures
    /// (M2 power-up activation); no M1 source emits it.
    /// </summary>
    public enum InputIntent
    {
        None = 0,
        SwipeLeft,
        SwipeRight,
        SwipeUp,
        SwipeDown,
        Tap,
        Hold,
        Pause
    }

    /// <summary>Allocation-free snapshot of the detector, for the debug HUD and gesture tests.</summary>
    public readonly struct InputDebugState
    {
        public readonly bool IsTracking;
        public readonly float GestureElapsed;
        public readonly Vector2 StartPosition;
        public readonly InputIntent LastIntent;

        public InputDebugState(bool isTracking, float gestureElapsed, Vector2 startPosition, InputIntent lastIntent)
        {
            IsTracking = isTracking;
            GestureElapsed = gestureElapsed;
            StartPosition = startPosition;
            LastIntent = lastIntent;
        }
    }

    /// <summary>
    /// A source of intents. Implementations: touch swipe/tap (device), keyboard (editor + PlayMode tests).
    /// Consumers subscribe once and must not poll.
    /// </summary>
    public interface IInputSource
    {
        event Action<InputIntent> IntentIssued;
        void SetEnabled(bool enabled);
        bool IsEnabled { get; }
        InputDebugState Debug { get; }
    }

    /// <summary>Published for every emitted intent (TECH_DOC §4.3). Telemetry and the tutorial consume it.</summary>
    public readonly struct InputIntentIssuedEvent : IGameEvent
    {
        public readonly InputIntent Intent;

        public InputIntentIssuedEvent(InputIntent intent)
        {
            Intent = intent;
        }
    }

    /// <summary>
    /// Short-lived intent buffer so a swipe issued a few frames before it becomes legal still lands
    /// (TECH_DOC §4.3). Newest intent supersedes the pending one; stale intents expire.
    /// </summary>
    public interface IInputCommandBuffer
    {
        void Push(InputIntent intent);
        bool TryConsume(out InputIntent intent);

        /// <summary>
        /// Consumes the oldest live intent and reports when it was pushed (§4.3). A consumer that has to
        /// retry an intent across a locomotion lock needs this to honour the <em>original</em> window:
        /// re-stamping on each retry would let a held swipe survive indefinitely.
        /// </summary>
        bool TryConsume(out InputIntent intent, out double pushedUtcTicks);

        void Clear();
        int PendingCount { get; }
    }

    /// <summary>
    /// Fixed-capacity ring buffer. No allocation on Push/TryConsume; time comes from IClock so tests
    /// can expire entries deterministically.
    /// </summary>
    public sealed class InputCommandBuffer : IInputCommandBuffer
    {
        private readonly InputIntent[] _intents;
        private readonly double[] _timestamps;
        private readonly IClock _clock;
        private readonly float _windowSeconds;
        private int _head;
        private int _count;

        public int PendingCount => _count;

        public InputCommandBuffer(InputConfig config, IClock clock)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            int depth = Math.Max(1, config.bufferDepth);
            _intents = new InputIntent[depth];
            _timestamps = new double[depth];
            _windowSeconds = config.bufferWindow;
        }

        public void Push(InputIntent intent)
        {
            if (intent == InputIntent.None) return;

            if (_count == _intents.Length)
            {
                // Full: the newest intent supersedes the oldest (depth is 1 in shipped data).
                _head = (_head + 1) % _intents.Length;
                _count--;
            }

            int index = (_head + _count) % _intents.Length;
            _intents[index] = intent;
            _timestamps[index] = _clock.NowUtcTicks;
            _count++;
        }

        public bool TryConsume(out InputIntent intent) => TryConsume(out intent, out _);

        public bool TryConsume(out InputIntent intent, out double pushedUtcTicks)
        {
            while (_count > 0)
            {
                int index = _head;
                double pushed = _timestamps[index];
                double ageTicks = _clock.NowUtcTicks - pushed;
                bool expired = ageTicks > _windowSeconds * TimeSpan.TicksPerSecond;

                _head = (_head + 1) % _intents.Length;
                _count--;

                if (expired) continue;

                intent = _intents[index];
                pushedUtcTicks = pushed;
                return true;
            }

            intent = InputIntent.None;
            pushedUtcTicks = 0d;
            return false;
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Action names in the generated InputActionAsset. Single source of truth for bindings.</summary>
    public static class InputActionNames
    {
        public const string Map = "Player";
        public const string Point = "Point";
        public const string Press = "Press";
        public const string Left = "Left";
        public const string Right = "Right";
        public const string Up = "Up";
        public const string Down = "Down";
        public const string Tap = "Tap";
        public const string Pause = "Pause";
    }
}
