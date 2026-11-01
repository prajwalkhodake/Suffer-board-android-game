using UnityEngine;

namespace Skyrift.Core.Diagnostics
{
    /// <summary>Log channels let QA filter noise without touching call sites.</summary>
    public enum LogChannel
    {
        Bootstrap,
        Gameplay,
        Track,
        Controls,
        Economy,
        Meta,
        Services,
        Audio,
        UI,
        Pooling
    }

    /// <summary>
    /// The only logging seam (TECH_DOC §4.0). Debug.Log is banned outside this file and editor tools.
    /// Never log per frame: string formatting allocates.
    /// </summary>
    public interface ILog
    {
        void Info(LogChannel channel, string message);
        void Warn(LogChannel channel, string message);
        void Error(LogChannel channel, string message);
        bool Enabled { get; set; }
    }

    /// <summary>Unity-backed logger. Compiled to no-ops for Info/Warn in PROD builds via the DEV define.</summary>
    public sealed class UnityLog : ILog
    {
        public bool Enabled { get; set; } = true;

        public void Info(LogChannel channel, string message)
        {
#if DEV || UNITY_EDITOR
            if (Enabled) Debug.Log($"[{channel}] {message}");
#endif
        }

        public void Warn(LogChannel channel, string message)
        {
#if DEV || UNITY_EDITOR
            if (Enabled) Debug.LogWarning($"[{channel}] {message}");
#else
            if (Enabled) Debug.LogWarning(message);
#endif
        }

        public void Error(LogChannel channel, string message)
        {
            // Errors always ship: they are the crash-free-session signal.
            Debug.LogError($"[{channel}] {message}");
        }
    }

    /// <summary>Silent logger for tests and headless runs.</summary>
    public sealed class NullLog : ILog
    {
        public bool Enabled { get; set; }
        public void Info(LogChannel channel, string message) { }
        public void Warn(LogChannel channel, string message) { }
        public void Error(LogChannel channel, string message) { }
    }
}
