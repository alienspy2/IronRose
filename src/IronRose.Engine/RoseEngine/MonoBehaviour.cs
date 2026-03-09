using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace RoseEngine
{
    public class MonoBehaviour : Component
    {
        // 리플렉션 캐시: (Type, methodName) → MethodInfo  (Phase 15 — L-2)
        private static readonly Dictionary<(Type, string), MethodInfo?> _methodCache = new();

        private const BindingFlags InstanceMethodFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal bool _hasAwoken;

        private bool _enabled = true;
        public bool enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (_hasAwoken && gameObject != null && gameObject.activeSelf)
                {
                    try
                    {
                        if (_enabled) OnEnable();
                        else OnDisable();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Exception in {(_enabled ? "OnEnable" : "OnDisable")}() of {GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        // --- Lifecycle methods ---
        public virtual void Awake() { }
        public virtual void OnEnable() { }
        public virtual void Start() { }
        public virtual void FixedUpdate() { }
        public virtual void Update() { }
        public virtual void LateUpdate() { }
        public virtual void OnDisable() { }
        public virtual void OnDestroy() { }

        // --- 3D Physics callbacks ---
        public virtual void OnCollisionEnter(Collision collision) { }
        public virtual void OnCollisionStay(Collision collision) { }
        public virtual void OnCollisionExit(Collision collision) { }
        public virtual void OnTriggerEnter(Collider other) { }
        public virtual void OnTriggerStay(Collider other) { }
        public virtual void OnTriggerExit(Collider other) { }

        // --- CharacterController callback ---
        public virtual void OnControllerColliderHit(ControllerColliderHit hit) { }

        // --- 2D Physics callbacks ---
        public virtual void OnCollisionEnter2D(Collision2D collision) { }
        public virtual void OnCollisionStay2D(Collision2D collision) { }
        public virtual void OnCollisionExit2D(Collision2D collision) { }
        public virtual void OnTriggerEnter2D(Collider2D other) { }
        public virtual void OnTriggerStay2D(Collider2D other) { }
        public virtual void OnTriggerExit2D(Collider2D other) { }

        // --- Coroutines ---
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            var coroutine = new Coroutine(routine, this);
            SceneManager.AddCoroutine(coroutine);
            return coroutine;
        }

        public Coroutine StartCoroutine(string methodName)
        {
            var method = GetCachedMethod(GetType(), methodName);
            if (method == null)
                throw new ArgumentException($"Coroutine '{methodName}' not found on {GetType().Name}");

            var routine = (IEnumerator)method.Invoke(this, null)!;
            return StartCoroutine(routine);
        }

        public void StopCoroutine(Coroutine coroutine)
        {
            if (coroutine != null)
                coroutine.isDone = true;
        }

        public void StopCoroutine(string methodName)
        {
            SceneManager.StopCoroutine(this, methodName);
        }

        public void StopAllCoroutines()
        {
            SceneManager.StopAllCoroutines(this);
        }

        // --- Invoke ---
        public void Invoke(string methodName, float time)
        {
            SceneManager.ScheduleInvoke(this, methodName, time, 0f, false);
        }

        public void InvokeRepeating(string methodName, float time, float repeatRate)
        {
            SceneManager.ScheduleInvoke(this, methodName, time, repeatRate, true);
        }

        public void CancelInvoke()
        {
            SceneManager.CancelAllInvokes(this);
        }

        public void CancelInvoke(string methodName)
        {
            SceneManager.CancelInvoke(this, methodName);
        }

        public bool IsInvoking() => SceneManager.IsInvoking(this);

        public bool IsInvoking(string methodName) => SceneManager.IsInvoking(this, methodName);

        // --- Reflection cache helper ---
        internal static MethodInfo? GetCachedMethod(Type type, string methodName)
        {
            var key = (type, methodName);
            if (!_methodCache.TryGetValue(key, out var method))
            {
                method = type.GetMethod(methodName, InstanceMethodFlags);
                _methodCache[key] = method;
            }
            return method;
        }
    }
}
