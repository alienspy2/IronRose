using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    public static class UndoSystem
    {
        private static readonly List<IUndoAction> _undoStack = new();
        private static readonly List<IUndoAction> _redoStack = new();
        private const int MaxHistory = 100;

        public static string? UndoDescription =>
            _undoStack.Count > 0 ? _undoStack[^1].Description : null;

        public static string? RedoDescription =>
            _redoStack.Count > 0 ? _redoStack[^1].Description : null;

        public static void Record(IUndoAction action)
        {
            _undoStack.Add(action);
            _redoStack.Clear();

            if (_undoStack.Count > MaxHistory)
                _undoStack.RemoveAt(0);

            MarkSceneDirty();
        }

        public static string? PerformUndo()
        {
            if (_undoStack.Count == 0) return null;

            var action = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            action.Undo();
            _redoStack.Add(action);
            MarkSceneDirty();
            return action.Description;
        }

        public static string? PerformRedo()
        {
            if (_redoStack.Count == 0) return null;

            var action = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            action.Redo();
            _undoStack.Add(action);
            MarkSceneDirty();
            return action.Description;
        }

        public static void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            UndoUtility.SetIdRemap(null);
        }

        /// <summary>현재 스택을 반환하고 내부를 비운다 (Prefab Edit Mode 진입 시 사용).</summary>
        public static (List<IUndoAction> undo, List<IUndoAction> redo) SaveAndClear()
        {
            var undo = new List<IUndoAction>(_undoStack);
            var redo = new List<IUndoAction>(_redoStack);
            _undoStack.Clear();
            _redoStack.Clear();
            return (undo, redo);
        }

        /// <summary>저장된 스택으로 복원한다 (Prefab Edit Mode 종료 시 사용).</summary>
        public static void Restore(List<IUndoAction> undo, List<IUndoAction> redo)
        {
            _undoStack.Clear();
            _undoStack.AddRange(undo);
            _redoStack.Clear();
            _redoStack.AddRange(redo);
        }

        private static void MarkSceneDirty()
        {
            SceneManager.GetActiveScene().isDirty = true;
        }
    }
}
