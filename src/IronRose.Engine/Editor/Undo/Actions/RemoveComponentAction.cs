using RoseEngine;
using Tomlyn.Model;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// GameObject에서 컴포넌트를 제거하는 Undo 액션.
    /// Undo: 직렬화된 데이터로 컴포넌트 복원, Redo: 다시 제거.
    /// </summary>
    public sealed class RemoveComponentAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly TomlTable _serializedComponent;

        public RemoveComponentAction(string description, int gameObjectId, TomlTable serializedComponent)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _serializedComponent = serializedComponent;
        }

        public void Undo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;
            SceneSerializer.DeserializeComponent(go, _serializedComponent);
        }

        public void Redo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;

            var typeName = _serializedComponent.TryGetValue("type", out var tv) ? tv?.ToString() : null;
            if (typeName == null) return;

            foreach (var comp in go.InternalComponents)
            {
                if (comp is Transform) continue;
                if (comp.GetType().Name == typeName)
                {
                    comp.OnComponentDestroy();
                    go.RemoveComponent(comp);
                    break;
                }
            }
        }
    }
}
