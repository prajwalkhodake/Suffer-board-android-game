using System;
using Skyrift.Core.Events;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyrift.Controls
{
    /// <summary>
    /// Keyboard/gamepad intent source for the Editor and PlayMode tests (TECH_DOC §4.3). Enabled only
    /// under DEV so a shipped build has exactly one input path. It implements the same IInputSource,
    /// so the run controller cannot tell the difference.
    /// </summary>
    public sealed class KeyboardInputSource : MonoBehaviour, IInputSource
    {
        [SerializeField] private InputActionAsset actions;

        private InputActionMap _map;
        private InputAction _left, _right, _up, _down, _tap, _pause;
        private IEventBus _bus;
        private InputIntent _lastIntent = InputIntent.None;

        // Cached delegates: created once so OnDisable can actually unsubscribe (a fresh lambda would
        // never match the one added in OnEnable and would leak a handler per enable/disable cycle).
        private Action<InputAction.CallbackContext> _onLeft, _onRight, _onUp, _onDown, _onTap, _onPause;

        public event Action<InputIntent> IntentIssued;
        public bool IsEnabled { get; private set; } = true;

        public InputDebugState Debug => new InputDebugState(false, 0f, Vector2.zero, _lastIntent);

        /// <summary>Composition-root injection. Safe before or after OnEnable.</summary>
        public void Construct(InputActionAsset asset, IEventBus bus = null)
        {
            // Same reason as TouchInputSource: the boot is asynchronous, so this normally runs after
            // OnEnable and has to re-hook rather than rely on having been bound in time.
            UnhookAll();
            _map?.Disable();

            actions = asset;
            _bus = bus;
            Bind();

            if (isActiveAndEnabled) HookAll();
        }

        private void Awake()
        {
            if (_map == null) Bind();
        }

        private void Bind()
        {
            if (actions == null) return;
            _map = actions.FindActionMap(InputActionNames.Map, throwIfNotFound: false);
            if (_map == null) return;

            _left = _map.FindAction(InputActionNames.Left, false);
            _right = _map.FindAction(InputActionNames.Right, false);
            _up = _map.FindAction(InputActionNames.Up, false);
            _down = _map.FindAction(InputActionNames.Down, false);
            _tap = _map.FindAction(InputActionNames.Tap, false);
            _pause = _map.FindAction(InputActionNames.Pause, false);

            _onLeft ??= _ => Emit(InputIntent.SwipeLeft);
            _onRight ??= _ => Emit(InputIntent.SwipeRight);
            _onUp ??= _ => Emit(InputIntent.SwipeUp);
            _onDown ??= _ => Emit(InputIntent.SwipeDown);
            _onTap ??= _ => Emit(InputIntent.Tap);
            _onPause ??= _ => Emit(InputIntent.Pause);
        }

        private void OnEnable()
        {
            if (_map == null) Bind();
            HookAll();
        }

        private void OnDisable()
        {
            UnhookAll();
            _map?.Disable();
        }

        private void HookAll()
        {
            Hook(_left, _onLeft);
            Hook(_right, _onRight);
            Hook(_up, _onUp);
            Hook(_down, _onDown);
            Hook(_tap, _onTap);
            Hook(_pause, _onPause);

            if (IsEnabled) _map?.Enable();
        }

        private void UnhookAll()
        {
            Unhook(_left, _onLeft);
            Unhook(_right, _onRight);
            Unhook(_up, _onUp);
            Unhook(_down, _onDown);
            Unhook(_tap, _onTap);
            Unhook(_pause, _onPause);
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            if (_map == null) return;
            if (enabled) _map.Enable();
            else _map.Disable();
        }

        private static void Hook(InputAction action, Action<InputAction.CallbackContext> callback)
        {
            if (action == null || callback == null) return;
            // Remove first so a re-Construct cannot double-subscribe and emit two intents per key press.
            action.performed -= callback;
            action.performed += callback;
        }

        private static void Unhook(InputAction action, Action<InputAction.CallbackContext> callback)
        {
            if (action == null || callback == null) return;
            action.performed -= callback;
        }

        private void Emit(InputIntent intent)
        {
            if (!IsEnabled) return;
            _lastIntent = intent;
            _bus?.Publish(new InputIntentIssuedEvent(intent));
            IntentIssued?.Invoke(intent);
        }
    }
}
