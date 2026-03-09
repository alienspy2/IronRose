using System;
using System.Collections.Generic;
using System.Reflection;

namespace RoseEngine
{
    /// <summary>
    /// Invoke/InvokeRepeating timer scheduling.
    /// Extracted from SceneManager (Phase 15 — H-3).
    /// </summary>
    internal static class InvokeScheduler
    {
        private static readonly List<InvokeEntry> _invokeEntries = new();

        internal static void Schedule(MonoBehaviour target, string methodName, float delay, float repeatRate, bool repeating)
        {
            _invokeEntries.Add(new InvokeEntry
            {
                target = target,
                methodName = methodName,
                timer = delay,
                repeatRate = repeatRate,
                repeating = repeating,
            });
        }

        internal static void CancelAll(MonoBehaviour target)
        {
            _invokeEntries.RemoveAll(e => e.target == target);
        }

        internal static void Cancel(MonoBehaviour target, string methodName)
        {
            _invokeEntries.RemoveAll(e => e.target == target && e.methodName == methodName);
        }

        internal static bool IsInvoking(MonoBehaviour target)
        {
            foreach (var e in _invokeEntries)
                if (e.target == target) return true;
            return false;
        }

        internal static bool IsInvoking(MonoBehaviour target, string methodName)
        {
            foreach (var e in _invokeEntries)
                if (e.target == target && e.methodName == methodName) return true;
            return false;
        }

        internal static void Process(float deltaTime)
        {
            for (int i = _invokeEntries.Count - 1; i >= 0; i--)
            {
                var entry = _invokeEntries[i];
                if (entry.target._isDestroyed)
                {
                    _invokeEntries.RemoveAt(i);
                    continue;
                }

                entry.timer -= deltaTime;
                if (entry.timer <= 0f)
                {
                    try
                    {
                        var method = entry.target.GetType().GetMethod(entry.methodName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        method?.Invoke(entry.target, null);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Exception in Invoke '{entry.methodName}' of {entry.target.GetType().Name}: {ex.Message}");
                    }

                    if (entry.repeating)
                    {
                        entry.timer = entry.repeatRate;
                        _invokeEntries[i] = entry;
                    }
                    else
                    {
                        _invokeEntries.RemoveAt(i);
                    }
                }
                else
                {
                    _invokeEntries[i] = entry;
                }
            }
        }

        internal static void Clear()
        {
            _invokeEntries.Clear();
        }

        private struct InvokeEntry
        {
            public MonoBehaviour target;
            public string methodName;
            public float timer;
            public float repeatRate;
            public bool repeating;
        }
    }
}
