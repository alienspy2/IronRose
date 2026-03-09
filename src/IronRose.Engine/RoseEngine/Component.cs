using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public class Component : Object
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform => gameObject.transform;

        public override string name
        {
            get => gameObject?.name ?? "";
            set { if (gameObject != null) gameObject.name = value; }
        }

        public string tag
        {
            get => gameObject?.tag ?? "Untagged";
            set { if (gameObject != null) gameObject.tag = value; }
        }

        public bool CompareTag(string tag) => gameObject != null && gameObject.CompareTag(tag);

        public T? GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public Component? GetComponent(Type type) => gameObject.GetComponent(type);
        public T[] GetComponents<T>() where T : Component => gameObject.GetComponents<T>();

        public T? GetComponentInChildren<T>() where T : Component
        {
            var result = GetComponent<T>();
            if (result != null) return result;
            return FindInChildren<T>(transform);
        }

        public T? GetComponentInParent<T>() where T : Component
        {
            var result = GetComponent<T>();
            if (result != null) return result;
            var p = transform.parent;
            while (p != null)
            {
                result = p.gameObject.GetComponent<T>();
                if (result != null) return result;
                p = p.parent;
            }
            return null;
        }

        public T[] GetComponentsInChildren<T>() where T : Component
        {
            var results = new List<T>();
            CollectInChildren(transform, results);
            return results.ToArray();
        }

        public T[] GetComponentsInParent<T>() where T : Component
        {
            var results = new List<T>();
            var current = transform;
            while (current != null)
            {
                var comp = current.gameObject.GetComponent<T>();
                if (comp != null) results.Add(comp);
                current = current.parent;
            }
            return results.ToArray();
        }

        private static T? FindInChildren<T>(Transform parent) where T : Component
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var result = child.gameObject.GetComponent<T>();
                if (result != null) return result;
                result = FindInChildren<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static void CollectInChildren<T>(Transform parent, List<T> results) where T : Component
        {
            var comp = parent.gameObject.GetComponent<T>();
            if (comp != null) results.Add(comp);
            for (int i = 0; i < parent.childCount; i++)
                CollectInChildren(parent.GetChild(i), results);
        }

        internal virtual void OnAddedToGameObject() { }

        /// <summary>컴포넌트 파괴 시 자신의 전역 레지스트리에서 제거.</summary>
        internal virtual void OnComponentDestroy() { }

        /// <summary>에디터 Scene View에서 매 프레임 호출. 항상 보이는 기즈모를 그림.</summary>
        public virtual void OnDrawGizmos() { }

        /// <summary>에디터 Scene View에서 이 컴포넌트의 GameObject가 선택되었을 때만 호출.</summary>
        public virtual void OnDrawGizmosSelected() { }
    }
}
