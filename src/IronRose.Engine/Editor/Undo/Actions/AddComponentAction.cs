using System;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// GameObject에 컴포넌트를 추가하는 Undo 액션.
    /// Undo: 컴포넌트 제거, Redo: 다시 추가.
    /// </summary>
    public sealed class AddComponentAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly Type _componentType;

        public AddComponentAction(string description, int gameObjectId, Type componentType)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _componentType = componentType;
        }

        public void Undo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;

            var comp = go.GetComponent(_componentType);
            if (comp != null)
            {
                comp.OnComponentDestroy();
                go.RemoveComponent(comp);
            }
        }

        public void Redo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;

            go.AddComponent(_componentType);
        }
    }
}
