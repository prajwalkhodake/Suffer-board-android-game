using System;

namespace Skyrift.Controls
{
    /// <summary>
    /// Wires an <see cref="IInputSource"/> to an <see cref="IInputCommandBuffer"/> (TECH_DOC §4.3).
    ///
    /// This is a separate object rather than a lambda in the composition root because the subscription has
    /// to be removable: the source is a scene MonoBehaviour that outlives a run-scope teardown, and a
    /// handler still pointing at a disposed buffer would keep it alive and push into it on the next run.
    /// The cached delegate is what makes <see cref="Dispose"/> actually detach — a fresh lambda would never
    /// match the one that was added.
    ///
    /// It does no filtering. Whether an intent is legal yet is the run controller's decision (§4.12); the
    /// bridge only moves it, so the buffer sees exactly what the device produced.
    /// </summary>
    public sealed class InputIntentBridge : IDisposable
    {
        private readonly IInputSource _source;
        private readonly IInputCommandBuffer _buffer;
        private readonly Action<InputIntent> _onIntent;
        private bool _disposed;

        public InputIntentBridge(IInputSource source, IInputCommandBuffer buffer)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

            _onIntent = OnIntent;
            _source.IntentIssued += _onIntent;
        }

        private void OnIntent(InputIntent intent)
        {
            if (_disposed) return;
            _buffer.Push(intent);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _source.IntentIssued -= _onIntent;

            // Anything still queued belongs to the run that just ended. §4.3: the buffer is cleared on a
            // transition, never replayed afterwards.
            _buffer.Clear();
        }
    }
}