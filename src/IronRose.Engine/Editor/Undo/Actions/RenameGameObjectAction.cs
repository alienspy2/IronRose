namespace IronRose.Engine.Editor
{
    public sealed class RenameGameObjectAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly string _oldName;
        private readonly string _newName;

        public RenameGameObjectAction(string description, int gameObjectId, string oldName, string newName)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _oldName = oldName;
            _newName = newName;
        }

        public void Undo() => Apply(_oldName);
        public void Redo() => Apply(_newName);

        private void Apply(string name)
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go != null) go.name = name;
        }
    }
}
