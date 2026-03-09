using System.Collections.Generic;

namespace RoseEngine.InputSystem
{
    public static class InputSystem
    {
        private static readonly List<InputAction> _activeActions = new();

        internal static void Register(InputAction action)
        {
            if (!_activeActions.Contains(action))
                _activeActions.Add(action);
        }

        internal static void Unregister(InputAction action)
        {
            _activeActions.Remove(action);
        }

        public static void Update()
        {
            for (int i = 0; i < _activeActions.Count; i++)
                _activeActions[i].Update();
        }
    }
}
