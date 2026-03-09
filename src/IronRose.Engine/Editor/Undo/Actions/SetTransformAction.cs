using RoseEngine;

namespace IronRose.Engine.Editor
{
    public sealed class SetTransformAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly Vector3 _oldPosition;
        private readonly Quaternion _oldRotation;
        private readonly Vector3 _oldScale;
        private readonly Vector3 _newPosition;
        private readonly Quaternion _newRotation;
        private readonly Vector3 _newScale;

        public SetTransformAction(
            string description, int gameObjectId,
            Vector3 oldPos, Quaternion oldRot, Vector3 oldScale,
            Vector3 newPos, Quaternion newRot, Vector3 newScale)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _oldPosition = oldPos;
            _oldRotation = oldRot;
            _oldScale = oldScale;
            _newPosition = newPos;
            _newRotation = newRot;
            _newScale = newScale;
        }

        public void Undo() => Apply(_oldPosition, _oldRotation, _oldScale);
        public void Redo() => Apply(_newPosition, _newRotation, _newScale);

        private void Apply(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
        }
    }
}
