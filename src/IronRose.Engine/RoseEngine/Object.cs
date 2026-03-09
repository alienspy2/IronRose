using System;
using System.Collections.Generic;
using System.Reflection;

namespace RoseEngine
{
    public class Object
    {
        internal bool _isDestroyed;

        public virtual string name { get; set; } = "";

        public int GetInstanceID() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public override string ToString() => name;

        // --- Fake Null pattern (Unity-compatible) ---

        private static bool IsNullOrDestroyed(Object? obj)
            => ReferenceEquals(obj, null) || obj._isDestroyed;

        public static implicit operator bool(Object? obj) => !IsNullOrDestroyed(obj);

        public static bool operator ==(Object? a, Object? b)
        {
            bool aNull = IsNullOrDestroyed(a);
            bool bNull = IsNullOrDestroyed(b);
            if (aNull && bNull) return true;
            if (aNull || bNull) return false;
            return ReferenceEquals(a, b);
        }

        public static bool operator !=(Object? a, Object? b) => !(a == b);

        public override bool Equals(object? obj)
        {
            if (obj is Object unityObj)
                return this == unityObj;
            return obj is null && _isDestroyed;
        }

        public override int GetHashCode() => GetInstanceID();

        public static void Destroy(Object obj, float t = 0f)
        {
            if (obj == null || obj._isDestroyed) return;
            SceneManager.ScheduleDestroy(obj, t);
        }

        public static void DestroyImmediate(Object obj)
        {
            if (obj == null || obj._isDestroyed) return;
            SceneManager.DestroyImmediate(obj);
        }

        public static T Instantiate<T>(T original) where T : Object
        {
            if (original is GameObject go)
                return (T)(Object)CloneGameObject(go);

            throw new ArgumentException($"Instantiate only supports GameObject, got {original?.GetType().Name}");
        }

        public static T Instantiate<T>(T original, Vector3 position, Quaternion rotation) where T : Object
        {
            var clone = Instantiate(original);
            if (clone is GameObject cloneGO)
            {
                cloneGO.transform.position = position;
                cloneGO.transform.rotation = rotation;
            }
            return clone;
        }

        public static T Instantiate<T>(T original, Transform parent) where T : Object
        {
            var clone = Instantiate(original);
            if (clone is GameObject cloneGO)
            {
                cloneGO.transform.SetParent(parent);
            }
            return clone;
        }

        public static T? FindObjectOfType<T>() where T : Object
        {
            // Search MonoBehaviours first
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (go._isDestroyed) continue;
                if (typeof(Component).IsAssignableFrom(typeof(T)))
                {
                    var comp = go.GetComponent(typeof(T));
                    if (comp is T result) return result;
                }
                else if (go is T goResult)
                {
                    return goResult;
                }
            }
            return null;
        }

        public static T[] FindObjectsOfType<T>() where T : Object
        {
            var results = new List<T>();
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (go._isDestroyed) continue;
                if (typeof(Component).IsAssignableFrom(typeof(T)))
                {
                    foreach (var comp in go.InternalComponents)
                    {
                        if (comp is T typed && !comp._isDestroyed)
                            results.Add(typed);
                    }
                }
                else if (go is T goResult)
                {
                    results.Add(goResult);
                }
            }
            return results.ToArray();
        }

        private static GameObject CloneGameObject(GameObject original)
        {
            var clone = new GameObject(original.name);

            // Copy transform values
            clone.transform.localPosition = original.transform.localPosition;
            clone.transform.localRotation = original.transform.localRotation;
            clone.transform.localScale = original.transform.localScale;

            // Clone components (except Transform which is auto-created)
            foreach (var comp in original.InternalComponents)
            {
                if (comp is Transform) continue;
                var clonedComp = clone.AddComponent(comp.GetType());
                CopyFields(comp, clonedComp);
            }

            // Recursively clone children
            for (int i = 0; i < original.transform.childCount; i++)
            {
                var childClone = CloneGameObject(original.transform.GetChild(i).gameObject);
                childClone.transform.SetParent(clone.transform, false);
            }

            return clone;
        }

        private static void CopyFields(object source, object target)
        {
            var type = source.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var field in type.GetFields(flags))
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                // Skip internal engine fields
                if (field.Name.StartsWith("_is") || field.Name == "gameObject") continue;
                // Copy public fields and [SerializeField] fields
                if (field.IsPublic || field.GetCustomAttribute<SerializeFieldAttribute>() != null)
                {
                    try { field.SetValue(target, field.GetValue(source)); }
                    catch { /* skip uncopyable fields */ }
                }
            }

            // Copy public instance properties with both getter and setter
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (prop.Name == "gameObject" || prop.Name == "transform") continue;
                try { prop.SetValue(target, prop.GetValue(source)); }
                catch { /* skip uncopyable properties */ }
            }
        }
    }
}
