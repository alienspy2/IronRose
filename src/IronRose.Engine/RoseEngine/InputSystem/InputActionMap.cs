using System;
using System.Collections.Generic;

namespace RoseEngine.InputSystem
{
    public class InputActionMap
    {
        public string name { get; }
        private readonly List<InputAction> _actions = new();

        public InputActionMap(string name = "")
        {
            this.name = name;
        }

        public InputAction AddAction(string name, InputActionType type = InputActionType.Button, string? binding = null)
        {
            var action = new InputAction(name, type, binding);
            _actions.Add(action);
            return action;
        }

        public InputAction? FindAction(string name)
        {
            for (int i = 0; i < _actions.Count; i++)
            {
                if (string.Equals(_actions[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return _actions[i];
            }
            return null;
        }

        public void Enable()
        {
            for (int i = 0; i < _actions.Count; i++)
                _actions[i].Enable();
        }

        public void Disable()
        {
            for (int i = 0; i < _actions.Count; i++)
                _actions[i].Disable();
        }

        public IReadOnlyList<InputAction> actions => _actions;
    }
}
