using Skyrift.Core.Events;

namespace Skyrift.Core.Bootstrap
{
    // Placement: Core, not Bootstrap. UI shows the splash/loading screens and has to react to the boot
    // finishing, but the dependency DAG (§3.3) forbids UI → Bootstrap. §3.3's sanctioned escape hatch is a
    // Core-level contract, exactly as for GameFlowChangedEvent. Bootstrap still owns the only publisher.

    /// <summary>The object graph is built and the first screen may be shown (TECH_DOC §4.1).</summary>
    public readonly struct BootstrapCompletedEvent : IGameEvent
    {
    }

    /// <summary>
    /// Boot failed and was aborted. <see cref="Step"/> is the name of the step that refused, so a boot
    /// failure is diagnosable from a crash report without a repro.
    /// </summary>
    public readonly struct BootstrapFailedEvent : IGameEvent
    {
        public readonly string ErrorCode;
        public readonly string Step;
        public readonly string Message;

        public BootstrapFailedEvent(string errorCode, string step, string message)
        {
            ErrorCode = errorCode ?? string.Empty;
            Step = step ?? string.Empty;
            Message = message ?? string.Empty;
        }
    }
}