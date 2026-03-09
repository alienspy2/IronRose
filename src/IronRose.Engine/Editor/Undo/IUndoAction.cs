namespace IronRose.Engine.Editor
{
    public interface IUndoAction
    {
        string Description { get; }
        void Undo();
        void Redo();
    }
}
