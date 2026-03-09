using System.Collections.Generic;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// Tracks active drag/edit state for Inspector ImGui widgets.
    /// On first frame of a change, stores the old value keyed by widget ID.
    /// On edit end (IsItemDeactivatedAfterEdit), returns the old value for undo registration.
    /// </summary>
    internal sealed class InspectorUndoTracker
    {
        private readonly Dictionary<string, object?> _activeEdits = new();

        /// <summary>
        /// Call when a widget reports a value change (e.g., DragFloat returns true).
        /// Stores the old value only on the first call per edit session.
        /// </summary>
        public void BeginEdit(string widgetId, object? oldValue)
        {
            // Only capture on the first frame of this edit
            _activeEdits.TryAdd(widgetId, oldValue);
        }

        /// <summary>
        /// Call when IsItemDeactivatedAfterEdit() is true.
        /// Returns true and outputs the original value if an edit was tracked.
        /// </summary>
        public bool EndEdit(string widgetId, out object? oldValue)
        {
            if (_activeEdits.Remove(widgetId, out oldValue))
                return true;

            oldValue = null;
            return false;
        }

        /// <summary>
        /// Clear all tracked edits (e.g., on selection change).
        /// </summary>
        public void Clear()
        {
            _activeEdits.Clear();
        }
    }
}
