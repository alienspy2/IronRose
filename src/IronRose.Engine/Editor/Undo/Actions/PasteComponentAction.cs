using RoseEngine;
using Tomlyn.Model;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 클립보드에서 컴포넌트를 붙여넣는 Undo 액션.
    /// Undo: 붙여넣은 컴포넌트 제거, Redo: 다시 붙여넣기.
    /// </summary>
    public sealed class PasteComponentAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly TomlTable _serializedComponent;

        public PasteComponentAction(string description, int gameObjectId, TomlTable serializedComponent)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _serializedComponent = serializedComponent;
        }

        public void Undo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;

            var typeName = _serializedComponent.TryGetValue("type", out var tv) ? tv?.ToString() : null;
            if (typeName == null) return;

            // 마지막으로 추가된 해당 타입의 컴포넌트를 제거
            Component? last = null;
            foreach (var comp in go.InternalComponents)
            {
                if (comp is Transform) continue;
                if (comp.GetType().Name == typeName)
                    last = comp;
            }
            if (last != null)
            {
                last.OnComponentDestroy();
                go.RemoveComponent(last);
            }
        }

        public void Redo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;
            SceneSerializer.DeserializeComponent(go, _serializedComponent);
        }
    }
}
