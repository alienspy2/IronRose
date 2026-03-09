using System;
using System.Reflection;

namespace IronRose.Engine.Editor
{
    public sealed class SetPropertyAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly string _componentTypeName;
        private readonly string _memberName;
        private readonly object? _oldValue;
        private readonly object? _newValue;

        public SetPropertyAction(
            string description, int gameObjectId,
            string componentTypeName, string memberName,
            object? oldValue, object? newValue)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _componentTypeName = componentTypeName;
            _memberName = memberName;
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public void Undo() => Apply(_oldValue);
        public void Redo() => Apply(_newValue);

        private void Apply(object? value)
        {
            var comp = UndoUtility.FindComponent(_gameObjectId, _componentTypeName);
            if (comp == null) return;

            var type = comp.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var field = type.GetField(_memberName, flags);
            if (field != null)
            {
                field.SetValue(comp, value);
                return;
            }

            var prop = type.GetProperty(_memberName, flags);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(comp, value);
            }
        }
    }
}
