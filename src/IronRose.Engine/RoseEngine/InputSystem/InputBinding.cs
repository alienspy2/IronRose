using System.Collections.Generic;

namespace RoseEngine.InputSystem
{
    public struct InputBinding
    {
        public string path;
        public string? compositePart; // null for simple bindings, "Up"/"Down"/"Left"/"Right" for composite parts

        public InputBinding(string path, string? compositePart = null)
        {
            this.path = path;
            this.compositePart = compositePart;
        }
    }

    public class CompositeBinder
    {
        private readonly InputAction _action;
        private readonly string _compositeType;
        internal readonly List<InputBinding> parts = new();

        internal CompositeBinder(InputAction action, string compositeType)
        {
            _action = action;
            _compositeType = compositeType;
        }

        public CompositeBinder With(string partName, string path)
        {
            parts.Add(new InputBinding(path, partName));
            return this;
        }

        internal string CompositeType => _compositeType;
    }
}
