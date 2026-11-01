using System;
using Skyrift.Core.Events;
using Skyrift.Core.Time;
using Skyrift.Data;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyrift.Controls
{
    /// <summary>
    /// Touch swipe/tap detector (TECH_DOC §4.3). Reads the generated InputActionAsset, never raw device
    /// state, so the same code path serves touch, pen and mouse-in-editor. Gesture timing accumulates
    /// unscaled delta from IClock so a paused run cannot distort a swipe.
    /// </summary>
    public sealed class TouchInputSource : MonoBehaviour, IInputSource
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private InputConfig config;

        private IClock _clock;
        private IEventBus _bus;
        private InputAction _point;
        private InputAction _press;
        private InputActionMap _map;

        private bool _tracking;
        private Vector2 _startPosition;
        private float _elapsed;
        private InputIntent _lastAxisIntent = InputIntent.SwipeUp;
        private InputIntent _lastIntent = InputIntent.None;

        public event Action<InputIntent> IntentIssued;
        public bool IsEnabled { get; private set; } = true;

        public InputDebugState Debug => new InputDebugState(_tracking, _elapsed, _startPosition, _lastIntent);

        /// <summary>Composition-root injection. Safe before or after OnEnable.</summary>
        public void Construct(IClock clock, InputActionAsset asset, InputConfig inputConfig, IEventBus bus = null)
        {
            // The composition root boots asynchronously, so this usually runs *after* OnEnable. Detaching
            // from whatever was bound before and re-hooking afterwards is what makes injection order
            // irrelevant; without it the actions bind but no callback ever fires and input is silently dead.
            UnhookPress();
            _map?.Disable();

            _clock = clock;
            _bus = bus;
            actions = asset;
            config = inputConfig;
            BindActions();

            if (isActiveAndEnabled) HookPress();
        }

        private void Awake()
        {
            if (_point == null) BindActions();
        }

        private void BindActions()
        {
            if (actions == null) return;
            _map = actions.FindActionMap(InputActionNames.Map, throwIfNotFound: false);
            if (_map == null) return;
            _point = _map.FindAction(InputActionNames.Point, throwIfNotFound: false);
            _press = _map.FindAction(InputActionNames.Press, throwIfNotFound: false);
        }

        private void OnEnable()
        {
            if (_press == null) BindActions();
            HookPress();
        }

        private void OnDisable()
        {
            UnhookPress();
            _map?.Disable();
            _tracking = false;
        }

        private void HookPress()
        {
            if (_press == null) return;

            // Idempotent: remove before adding so a re-Construct cannot double-subscribe and emit two
            // intents for one swipe.
            UnhookPress();
            _press.performed += OnPressStarted;
            _press.canceled += OnPressReleased;

            if (IsEnabled) _map?.Enable();
        }

        private void UnhookPress()
        {
            if (_press == null) return;
            _press.performed -= OnPressStarted;
            _press.canceled -= OnPressReleased;
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            _tracking = false;

            if (_map == null) return;
            if (enabled) _map.Enable();
            else _map.Disable();
        }

        private void Update()
        {
            // Accumulate real elapsed time for the gesture; IClock keeps this testable and pause-safe.
            if (_tracking && _clock != null) _elapsed += _clock.UnscaledDeltaTime;
        }

        private void OnPressStarted(InputAction.CallbackContext ctx)
        {
            if (!IsEnabled || _point == null) return;
            _tracking = true;
            _startPosition = _point.ReadValue<Vector2>();
            _elapsed = 0f;
        }

        private void OnPressReleased(InputAction.CallbackContext ctx)
        {
            if (!IsEnabled || !_tracking || _point == null || config == null) return;
            _tracking = false;

            Vector2 end = _point.ReadValue<Vector2>();
            Vector2 delta = end - _startPosition;
            float screenUnit = Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height));
            float minSwipe = config.swipeMinDistancePercent * screenUnit;
            float maxTapMove = config.tapMaxMovePercent * screenUnit;

            float absX = Mathf.Abs(delta.x);
            float absY = Mathf.Abs(delta.y);

            // Tap: short and barely moved.
            if (_elapsed <= config.tapMaxDuration && delta.magnitude <= maxTapMove)
            {
                Emit(InputIntent.Tap);
                return;
            }

            if (_elapsed > config.swipeMaxDuration) return;   // a drag, not a swipe
            if (delta.magnitude < minSwipe) return;

            InputIntent intent = ResolveAxis(delta, absX, absY);
            Emit(intent);
        }

        private InputIntent ResolveAxis(Vector2 delta, float absX, float absY)
        {
            bool horizontalDominant = absX > absY * config.axisDominanceRatio;
            bool verticalDominant = absY > absX * config.axisDominanceRatio;

            if (horizontalDominant) return delta.x > 0f ? InputIntent.SwipeRight : InputIntent.SwipeLeft;
            if (verticalDominant) return delta.y > 0f ? InputIntent.SwipeUp : InputIntent.SwipeDown;

            // Ambiguous diagonal: larger magnitude wins; an exact tie keeps the previous axis.
            if (absX > absY) return delta.x > 0f ? InputIntent.SwipeRight : InputIntent.SwipeLeft;
            if (absY > absX) return delta.y > 0f ? InputIntent.SwipeUp : InputIntent.SwipeDown;
            return _lastAxisIntent;
        }

        private void Emit(InputIntent intent)
        {
            if (intent == InputIntent.SwipeLeft || intent == InputIntent.SwipeRight ||
                intent == InputIntent.SwipeUp || intent == InputIntent.SwipeDown)
            {
                _lastAxisIntent = intent;
            }

            _lastIntent = intent;
            _bus?.Publish(new InputIntentIssuedEvent(intent));
            IntentIssued?.Invoke(intent);
        }
    }
}
