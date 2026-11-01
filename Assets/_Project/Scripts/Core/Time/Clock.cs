using System;
using UnityEngine;

namespace Skyrift.Core.Time
{
    /// <summary>
    /// Single source of time for gameplay (TECH_DOC §4.0). Tests drive a ManualClock so no test ever
    /// depends on wall-clock time. Never read Time.deltaTime directly outside an IClock implementation.
    /// </summary>
    public interface IClock
    {
        float DeltaTime { get; }
        float UnscaledDeltaTime { get; }
        double NowUtcTicks { get; }
    }

    /// <summary>Real time, straight from the Unity player loop.</summary>
    public sealed class UnityClock : IClock
    {
        public float DeltaTime => UnityEngine.Time.deltaTime;
        public float UnscaledDeltaTime => UnityEngine.Time.unscaledDeltaTime;
        public double NowUtcTicks => DateTime.UtcNow.Ticks;
    }

    /// <summary>Deterministic clock for EditMode/PlayMode tests.</summary>
    public sealed class ManualClock : IClock
    {
        private long _ticks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        public float DeltaTime { get; private set; }
        public float UnscaledDeltaTime => DeltaTime;
        public double NowUtcTicks => _ticks;

        public void Advance(float seconds)
        {
            DeltaTime = seconds;
            _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
        }
    }

    /// <summary>
    /// Run-scoped clock. Pausing or freezing a run scales this clock, never Time.timeScale, so UI,
    /// crash animations and audio keep running on unscaled time (TECH_DOC §4.12).
    /// </summary>
    public sealed class RunClock : IClock
    {
        private readonly IClock _source;

        public RunClock(IClock source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>0 freezes the run (crash sequence), 1 is normal speed.</summary>
        public float Scale { get; set; } = 1f;

        public bool Paused { get; set; }

        public float DeltaTime => Paused ? 0f : _source.DeltaTime * Scale;
        public float UnscaledDeltaTime => _source.UnscaledDeltaTime;
        public double NowUtcTicks => _source.NowUtcTicks;

        public void ResetForRun()
        {
            Scale = 1f;
            Paused = false;
        }
    }
}
