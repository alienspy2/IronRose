using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 에셋 드래그-앤-드롭으로 GameObject를 생성하는 Undo 액션.
    /// Undo: 오브젝트 삭제, Redo: 에셋 경로로 재생성.
    /// </summary>
    public sealed class SpawnGameObjectAction : IUndoAction
    {
        public string Description { get; }

        private readonly string _assetPath;
        private readonly Vector3 _spawnPosition;
        private int _gameObjectId;

        public SpawnGameObjectAction(string description, string assetPath,
            Vector3 spawnPosition, int gameObjectId)
        {
            Description = description;
            _assetPath = assetPath;
            _spawnPosition = spawnPosition;
            _gameObjectId = gameObjectId;
        }

        public void Undo()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go != null)
                RoseEngine.Object.DestroyImmediate(go);

            EditorSelection.Clear();
        }

        public void Redo()
        {
            var go = AssetSpawner.SpawnFromAsset(_assetPath, _spawnPosition);
            if (go == null) return;

            _gameObjectId = go.GetInstanceID();
            EditorSelection.SelectGameObject(go);
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
