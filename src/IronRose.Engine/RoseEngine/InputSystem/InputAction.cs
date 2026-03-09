using System;
using System.Collections.Generic;

namespace RoseEngine.InputSystem
{
    public class InputAction
    {
        public string name;
        public InputActionType type;
        public InputActionPhase phase { get; internal set; }
        public bool enabled { get; private set; }

        // Callbacks
        public event Action<CallbackContext>? started;
        public event Action<CallbackContext>? performed;
        public event Action<CallbackContext>? canceled;

        // Simple bindings (parsed)
        private readonly List<InputControlPath.ParsedPath> _bindings = new();

        // Composite bindings
        private readonly List<CompositeBinder> _composites = new();

        // Cached value from last Update
        private float _buttonValue;
        private Vector2 _vector2Value;

        public InputAction(string name, InputActionType type = InputActionType.Button, string? binding = null)
        {
            this.name = name;
            this.type = type;
            phase = InputActionPhase.Disabled;

            if (!string.IsNullOrEmpty(binding))
                AddBinding(binding);
        }

        public void Enable()
        {
            if (enabled) return;
            enabled = true;
            phase = InputActionPhase.Waiting;
            InputSystem.Register(this);
        }

        public void Disable()
        {
            if (!enabled) return;

            if (phase == InputActionPhase.Started || phase == InputActionPhase.Performed)
            {
                phase = InputActionPhase.Canceled;
                canceled?.Invoke(new CallbackContext(this));
            }

            enabled = false;
            phase = InputActionPhase.Disabled;
            _buttonValue = 0f;
            _vector2Value = Vector2.zero;
            InputSystem.Unregister(this);
        }

        public void AddBinding(string path)
        {
            var parsed = InputControlPath.Parse(path);
            _bindings.Add(parsed);
        }

        public CompositeBinder AddCompositeBinding(string composite)
        {
            var binder = new CompositeBinder(this, composite);
            _composites.Add(binder);
            return binder;
        }

        public T ReadValue<T>()
        {
            if (typeof(T) == typeof(float))
                return (T)(object)_buttonValue;
            if (typeof(T) == typeof(Vector2))
                return (T)(object)_vector2Value;

            return default!;
        }

        internal void Update()
        {
            if (!enabled) return;

            float prevButton = _buttonValue;
            Vector2 prevVector2 = _vector2Value;

            // Evaluate current value from all bindings
            EvaluateBindings();

            // Phase transition logic
            bool wasActive = prevButton > 0f || prevVector2 != Vector2.zero;
            bool isActive = _buttonValue > 0f || _vector2Value != Vector2.zero;

            switch (type)
            {
                case InputActionType.Button:
                    UpdateButtonPhase(wasActive, isActive);
                    break;

                case InputActionType.Value:
                    UpdateValuePhase(wasActive, isActive);
                    break;

                case InputActionType.PassThrough:
                    UpdatePassThroughPhase(wasActive, isActive);
                    break;
            }
        }

        private void EvaluateBindings()
        {
            _buttonValue = 0f;
            _vector2Value = Vector2.zero;

            // Simple bindings
            for (int i = 0; i < _bindings.Count; i++)
            {
                var parsed = _bindings[i];
                float v = InputControlPath.ReadButtonValue(in parsed);
                if (v > _buttonValue)
                    _buttonValue = v;

                var v2 = InputControlPath.ReadVector2Value(in parsed);
                _vector2Value += v2;
            }

            // Composite bindings
            for (int i = 0; i < _composites.Count; i++)
            {
                var composite = _composites[i];
                if (composite.CompositeType.Equals("2DVector", StringComparison.OrdinalIgnoreCase) ||
                    composite.CompositeType.Equals("Dpad", StringComparison.OrdinalIgnoreCase))
                {
                    _vector2Value += Evaluate2DVector(composite);
                }
                else if (composite.CompositeType.Equals("1DAxis", StringComparison.OrdinalIgnoreCase))
                {
                    _buttonValue += Evaluate1DAxis(composite);
                }
            }

            // Clamp vector components to [-1, 1]
            _vector2Value = new Vector2(
                Math.Clamp(_vector2Value.x, -1f, 1f),
                Math.Clamp(_vector2Value.y, -1f, 1f)
            );
        }

        private static Vector2 Evaluate2DVector(CompositeBinder composite)
        {
            float up = 0f, down = 0f, left = 0f, right = 0f;

            for (int i = 0; i < composite.parts.Count; i++)
            {
                var part = composite.parts[i];
                var parsed = InputControlPath.Parse(part.path);
                float val = InputControlPath.ReadButtonValue(in parsed);

                switch (part.compositePart?.ToLowerInvariant())
                {
                    case "up": up = val; break;
                    case "down": down = val; break;
                    case "left": left = val; break;
                    case "right": right = val; break;
                }
            }

            return new Vector2(right - left, up - down);
        }

        private static float Evaluate1DAxis(CompositeBinder composite)
        {
            float positive = 0f, negative = 0f;

            for (int i = 0; i < composite.parts.Count; i++)
            {
                var part = composite.parts[i];
                var parsed = InputControlPath.Parse(part.path);
                float val = InputControlPath.ReadButtonValue(in parsed);

                switch (part.compositePart?.ToLowerInvariant())
                {
                    case "positive": positive = val; break;
                    case "negative": negative = val; break;
                }
            }

            return positive - negative;
        }

        private void UpdateButtonPhase(bool wasActive, bool isActive)
        {
            if (!wasActive && isActive)
            {
                // Just pressed
                phase = InputActionPhase.Started;
                started?.Invoke(new CallbackContext(this));
                phase = InputActionPhase.Performed;
                performed?.Invoke(new CallbackContext(this));
            }
            else if (wasActive && !isActive)
            {
                // Just released
                phase = InputActionPhase.Canceled;
                canceled?.Invoke(new CallbackContext(this));
                phase = InputActionPhase.Waiting;
            }
            // If still held, stay in Performed (no repeated callbacks for Button type)
        }

        private void UpdateValuePhase(bool wasActive, bool isActive)
        {
            if (!wasActive && isActive)
            {
                phase = InputActionPhase.Started;
                started?.Invoke(new CallbackContext(this));
                phase = InputActionPhase.Performed;
                performed?.Invoke(new CallbackContext(this));
            }
            else if (wasActive && isActive)
            {
                // Value changed while active — fire performed again
                phase = InputActionPhase.Performed;
                performed?.Invoke(new CallbackContext(this));
            }
            else if (wasActive && !isActive)
            {
                phase = InputActionPhase.Canceled;
                canceled?.Invoke(new CallbackContext(this));
                phase = InputActionPhase.Waiting;
            }
        }

        private void UpdatePassThroughPhase(bool wasActive, bool isActive)
        {
            // PassThrough fires performed on every frame where there is input
            if (isActive)
            {
                phase = InputActionPhase.Performed;
                performed?.Invoke(new CallbackContext(this));
            }
            else if (wasActive)
            {
                phase = InputActionPhase.Waiting;
            }
        }
    }

    public struct CallbackContext
    {
        private readonly InputAction _action;

        internal CallbackContext(InputAction action)
        {
            _action = action;
        }

        public InputAction action => _action;
        public InputActionPhase phase => _action.phase;

        public T ReadValue<T>() => _action.ReadValue<T>();
    }
}
