using System.Collections.Generic;
using System.Linq;

namespace IronRose.Engine.Editor
{
    public sealed class CompoundUndoAction : IUndoAction
    {
        public string Description { get; }
        private readonly IUndoAction[] _actions;

        public CompoundUndoAction(string description, IEnumerable<IUndoAction> actions)
        {
            Description = description;
            _actions = actions.ToArray();
        }

        public void Undo()
        {
            for (int i = _actions.Length - 1; i >= 0; i--)
                _actions[i].Undo();
        }

        public void Redo()
        {
            for (int i = 0; i < _actions.Length; i++)
                _actions[i].Redo();
        }
    }
}
