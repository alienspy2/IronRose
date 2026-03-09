using RoseEngine;

namespace IronRose.Engine.Editor
{
    public sealed class ReparentAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _goId;
        private readonly int? _oldParentId;
        private readonly int? _newParentId;
        private readonly int _oldSiblingIndex;
        private readonly int _newSiblingIndex;

        public ReparentAction(string description, GameObject go,
            int? oldParentId, int? newParentId,
            int oldSiblingIndex, int newSiblingIndex)
        {
            Description = description;
            _goId = go.GetInstanceID();
            _oldParentId = oldParentId;
            _newParentId = newParentId;
            _oldSiblingIndex = oldSiblingIndex;
            _newSiblingIndex = newSiblingIndex;
        }

        public void Undo()
        {
            var go = UndoUtility.FindGameObjectById(_goId);
            if (go == null) return;

            var oldParent = _oldParentId.HasValue
                ? UndoUtility.FindGameObjectById(_oldParentId.Value)?.transform
                : null;

            go.transform.SetParent(oldParent);
            go.transform.SetSiblingIndex(_oldSiblingIndex);
            SceneManager.GetActiveScene().isDirty = true;
        }

        public void Redo()
        {
            var go = UndoUtility.FindGameObjectById(_goId);
            if (go == null) return;

            var newParent = _newParentId.HasValue
                ? UndoUtility.FindGameObjectById(_newParentId.Value)?.transform
                : null;

            go.transform.SetParent(newParent);
            go.transform.SetSiblingIndex(_newSiblingIndex);
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
