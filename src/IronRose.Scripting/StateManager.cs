using System;
using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Scripting
{
    public class StateManager
    {
        private Dictionary<string, string> _savedStates = new();

        public void SaveStates(List<object> instances)
        {
            Debug.Log("[StateManager] Saving states...");
            _savedStates.Clear();

            int savedCount = 0;
            foreach (var instance in instances)
            {
                if (instance is IHotReloadable reloadable)
                {
                    string typeName = instance.GetType().FullName!;
                    string state = reloadable.SerializeState();
                    _savedStates[typeName] = state;
                    savedCount++;
                }
            }

            Debug.Log($"[StateManager] Saved {savedCount} states");
        }

        public void RestoreStates(List<object> instances)
        {
            Debug.Log("[StateManager] Restoring states...");

            int restoredCount = 0;
            foreach (var instance in instances)
            {
                if (instance is IHotReloadable reloadable)
                {
                    string typeName = instance.GetType().FullName!;
                    if (_savedStates.TryGetValue(typeName, out string? state))
                    {
                        reloadable.DeserializeState(state);
                        restoredCount++;
                    }
                }
            }

            Debug.Log($"[StateManager] Restored {restoredCount} states");
        }
    }
}
