namespace IronRose.Engine.Editor
{
    public sealed class SetActiveAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly bool _oldActive;
        private readonly bool _newActive;

        public SetActiveAction(string description, int gameObjectId, bool oldActive, bool newActive)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _oldActive = oldActive;
            _newActive = newActive;
        }

        public void Undo() => Apply(_oldActive);
        public void Redo() => Apply(_newActive);

        private void Apply(bool active)
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            go?.SetActive(active);
        }
    }
}
