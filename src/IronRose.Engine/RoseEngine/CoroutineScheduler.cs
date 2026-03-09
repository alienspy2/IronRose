using System;
using System.Collections;
using System.Collections.Generic;

namespace RoseEngine
{
    /// <summary>
    /// Coroutine scheduling and yield instruction processing.
    /// Extracted from SceneManager (Phase 15 — H-3).
    /// </summary>
    internal static class CoroutineScheduler
    {
        private static readonly List<Coroutine> _coroutines = new();

        internal static void AddCoroutine(Coroutine coroutine)
        {
            if (AdvanceCoroutine(coroutine))
                _coroutines.Add(coroutine);
        }

        internal static void StopCoroutine(MonoBehaviour owner, string methodName)
        {
            foreach (var c in _coroutines)
            {
                if (c.owner == owner && c.routine.GetType().Name.Contains(methodName))
                    c.isDone = true;
            }
        }

        internal static void StopAllCoroutines(MonoBehaviour owner)
        {
            foreach (var c in _coroutines)
            {
                if (c.owner == owner)
                    c.isDone = true;
            }
        }

        internal static void Process(float deltaTime)
        {
            for (int i = _coroutines.Count - 1; i >= 0; i--)
            {
                var c = _coroutines[i];

                if (c.isDone || c.owner._isDestroyed || !c.owner.enabled)
                {
                    _coroutines.RemoveAt(i);
                    continue;
                }

                if (c.waitTimer > 0f)
                {
                    c.waitTimer -= deltaTime;
                    if (c.waitTimer > 0f) continue;
                }

                if (!AdvanceCoroutine(c))
                    _coroutines.RemoveAt(i);
            }
        }

        internal static void Clear()
        {
            _coroutines.Clear();
        }

        private static bool AdvanceCoroutine(Coroutine c)
        {
            bool hasMore;
            try { hasMore = c.routine.MoveNext(); }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in coroutine of {c.owner.GetType().Name}: {ex.Message}");
                c.isDone = true;
                return false;
            }

            if (!hasMore)
            {
                c.isDone = true;
                return false;
            }

            var current = c.routine.Current;
            switch (current)
            {
                case null:
                    c.waitTimer = 0f;
                    break;
                case WaitForSeconds wfs:
                    c.waitTimer = wfs.duration;
                    break;
                case WaitForEndOfFrame:
                    c.waitTimer = 0f;
                    break;
                case WaitForFixedUpdate:
                    c.waitTimer = 0f;
                    break;
                case Coroutine nested:
                    c.waitTimer = 0f;
                    c.routine = WaitForNestedCoroutine(nested, c.routine);
                    break;
                case CustomYieldInstruction custom:
                    c.routine = WaitForCustomYield(custom, c.routine);
                    break;
                case IEnumerator nestedRoutine:
                    var nestedCoroutine = new Coroutine(nestedRoutine, c.owner);
                    _coroutines.Add(nestedCoroutine);
                    c.routine = WaitForNestedCoroutine(nestedCoroutine, c.routine);
                    break;
            }

            return true;
        }

        private static IEnumerator WaitForNestedCoroutine(Coroutine nested, IEnumerator continuation)
        {
            while (!nested.isDone)
                yield return null;
            while (continuation.MoveNext())
                yield return continuation.Current;
        }

        private static IEnumerator WaitForCustomYield(CustomYieldInstruction custom, IEnumerator continuation)
        {
            while (custom.keepWaiting)
                yield return null;
            while (continuation.MoveNext())
                yield return continuation.Current;
        }
    }
}
