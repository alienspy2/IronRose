namespace IronRose.Engine.Editor
{
    /// <summary>
    /// GameObject의 컴포넌트 순서를 변경하는 Undo 액션.
    /// Undo/Redo 모두 동일한 swap 연산 (자기 역원).
    /// </summary>
    public sealed class MoveComponentAction : IUndoAction
    {
        public string Description { get; }

        private readonly int _gameObjectId;
        private readonly int _fromIndex;
        private readonly int _toIndex;

        public MoveComponentAction(string description, int gameObjectId, int fromIndex, int toIndex)
        {
            Description = description;
            _gameObjectId = gameObjectId;
            _fromIndex = fromIndex;
            _toIndex = toIndex;
        }

        public void Undo()
        {
            Swap();
        }

        public void Redo()
        {
            Swap();
        }

        private void Swap()
        {
            var go = UndoUtility.FindGameObjectById(_gameObjectId);
            if (go == null) return;

            var comps = go._components;
            if (_fromIndex >= 0 && _fromIndex < comps.Count &&
                _toIndex >= 0 && _toIndex < comps.Count)
            {
                (comps[_fromIndex], comps[_toIndex]) = (comps[_toIndex], comps[_fromIndex]);
            }
        }
    }
}
