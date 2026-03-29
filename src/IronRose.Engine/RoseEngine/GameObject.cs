using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public class GameObject : Object
    {
        /// <summary>Persistent GUID. 세션/씬 로드를 넘어 GO를 고유 식별하는 범용 ID.</summary>
        public string guid { get; internal set; } = Guid.NewGuid().ToString();

        public Transform transform { get; }
        public string tag { get; set; } = "Untagged";
        public int layer { get; set; } = 0;

        internal readonly List<Component> _components = new();

        /// <summary>에디터 내부 오브젝트 플래그. Hierarchy 등에서 필터링용.</summary>
        internal bool _isEditorInternal;

        internal IReadOnlyList<Component> InternalComponents => _components;

        private bool _activeSelf = true;

        public bool activeSelf => _activeSelf;

        public bool activeInHierarchy
        {
            get
            {
                if (!_activeSelf) return false;
                if (transform.parent == null) return true;
                return transform.parent.gameObject.activeInHierarchy;
            }
        }

        public GameObject(string name = "GameObject")
        {
            this.name = name;

            // Bootstrap Transform with self-reference
            var t = new Transform();
            t.gameObject = this;
            transform = t;
            _components.Add(t);

            // Register in scene
            SceneManager.RegisterGameObject(this);
        }

        public void SetActive(bool value)
        {
            if (_activeSelf == value) return;
            _activeSelf = value;

            // Notify MonoBehaviours
            foreach (var comp in _components)
            {
                if (comp is MonoBehaviour mb && mb.enabled && mb._hasAwoken)
                {
                    try
                    {
                        if (value) mb.OnEnable();
                        else mb.OnDisable();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Exception in {(value ? "OnEnable" : "OnDisable")}() of {mb.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        public bool CompareTag(string tag) => this.tag == tag;

        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T();
            component.gameObject = this;
            _components.Add(component);
            component.OnAddedToGameObject();

            // Auto-register MonoBehaviours
            if (component is MonoBehaviour mb)
                SceneManager.RegisterBehaviour(mb);

            return component;
        }

        public Component AddComponent(Type type)
        {
            if (!typeof(Component).IsAssignableFrom(type))
                throw new ArgumentException($"{type.Name} does not derive from Component");

            var component = (Component)Activator.CreateInstance(type)!;
            component.gameObject = this;
            _components.Add(component);
            component.OnAddedToGameObject();

            // Auto-register MonoBehaviours
            if (component is MonoBehaviour mb)
                SceneManager.RegisterBehaviour(mb);

            return component;
        }

        internal void RemoveComponent(Component component)
        {
            _components.Remove(component);
        }

        public T? GetComponent<T>() where T : Component
        {
            foreach (var c in _components)
            {
                if (c is T typed && !c._isDestroyed) return typed;
            }
            return null;
        }

        public Component? GetComponent(Type type)
        {
            foreach (var c in _components)
            {
                if (type.IsInstanceOfType(c) && !c._isDestroyed) return c;
            }
            return null;
        }

        public T[] GetComponents<T>() where T : Component
        {
            var results = new List<T>();
            foreach (var c in _components)
            {
                if (c is T typed && !c._isDestroyed) results.Add(typed);
            }
            return results.ToArray();
        }

        public T? GetComponentInChildren<T>(bool includeInactive = false) where T : Component
        {
            // 자기 자신 먼저
            var self = GetComponent<T>();
            if (self != null) return self;

            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i).gameObject;
                if (!includeInactive && !child.activeSelf) continue;
                var found = child.GetComponentInChildren<T>(includeInactive);
                if (found != null) return found;
            }
            return null;
        }

        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component
        {
            var results = new List<T>();
            GetComponentsInChildrenInternal<T>(results, includeInactive);
            return results.ToArray();
        }

        private void GetComponentsInChildrenInternal<T>(List<T> results, bool includeInactive) where T : Component
        {
            foreach (var c in _components)
            {
                if (c is T typed && !c._isDestroyed) results.Add(typed);
            }
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i).gameObject;
                if (!includeInactive && !child.activeSelf) continue;
                child.GetComponentsInChildrenInternal<T>(results, includeInactive);
            }
        }

        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            var go = new GameObject(type.ToString());
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.material = new Material();

            filter.mesh = type switch
            {
                PrimitiveType.Cube => PrimitiveGenerator.CreateCube(),
                PrimitiveType.Sphere => PrimitiveGenerator.CreateSphere(),
                PrimitiveType.Capsule => PrimitiveGenerator.CreateCapsule(),
                PrimitiveType.Cylinder => PrimitiveGenerator.CreateCylinder(),
                PrimitiveType.Plane => PrimitiveGenerator.CreatePlane(),
                PrimitiveType.Quad => PrimitiveGenerator.CreateQuad(),
                _ => PrimitiveGenerator.CreateCube(),
            };

            // Auto-attach matching collider
            switch (type)
            {
                case PrimitiveType.Cube:
                    go.AddComponent<BoxCollider>();
                    break;
                case PrimitiveType.Sphere:
                    go.AddComponent<SphereCollider>();
                    break;
                case PrimitiveType.Capsule:
                    go.AddComponent<CapsuleCollider>();
                    break;
                case PrimitiveType.Cylinder:
                    go.AddComponent<CylinderCollider>();
                    break;
                case PrimitiveType.Plane:
                    var planeCol = go.AddComponent<BoxCollider>();
                    planeCol.size = new Vector3(10f, 0.01f, 10f);
                    break;
                case PrimitiveType.Quad:
                    var quadCol = go.AddComponent<BoxCollider>();
                    quadCol.size = new Vector3(1f, 1f, 0.01f);
                    break;
            }

            return go;
        }

        // --- Static Find methods ---

        public static GameObject? Find(string name)
        {
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.name == name)
                    return go;
            }
            return null;
        }

        public static GameObject? FindWithTag(string tag)
        {
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.activeInHierarchy && go.tag == tag)
                    return go;
            }
            return null;
        }

        public static GameObject[] FindGameObjectsWithTag(string tag)
        {
            var results = new List<GameObject>();
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.activeInHierarchy && go.tag == tag)
                    results.Add(go);
            }
            return results.ToArray();
        }

        public override string ToString() => $"GameObject({name})";
    }
}
