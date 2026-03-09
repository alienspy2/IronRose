using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 컨텍스트 메뉴로 새 GameObject를 생성하는 Undo 액션.
    /// Undo: 오브젝트 삭제, Redo: 같은 타입으로 재생성.
    /// </summary>
    public sealed class CreateGameObjectAction : IUndoAction
    {
        public string Description { get; }

        private readonly CreateGameObjectType _type;
        private readonly int? _parentId;
        private int _createdId;

        public CreateGameObjectAction(string description, CreateGameObjectType type,
            int? parentId, int createdId)
        {
            Description = description;
            _type = type;
            _parentId = parentId;
            _createdId = createdId;
        }

        public void Undo()
        {
            var go = UndoUtility.FindGameObjectById(_createdId);
            if (go != null)
                RoseEngine.Object.DestroyImmediate(go);

            EditorSelection.Clear();
            SceneManager.GetActiveScene().isDirty = true;
        }

        public void Redo()
        {
            var go = GameObjectFactory.Create(_type, _parentId);
            if (go == null) return;

            _createdId = go.GetInstanceID();
            EditorSelection.SelectGameObject(go);
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
