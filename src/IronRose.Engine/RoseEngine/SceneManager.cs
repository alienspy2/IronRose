// ------------------------------------------------------------
// @file    SceneManager.cs
// @brief   씬 내 GameObject/MonoBehaviour 등록, 게임 루프(Update/FixedUpdate/LateUpdate),
//          Destroy 큐 처리, 코루틴/Invoke 위임, 씬 초기화(Clear) 등 핵심 관리자.
//          모든 레지스트리는 ComponentRegistry<T> 로 감싸져 lock/snapshot 기반으로 동작.
// @deps    RoseEngine/EditorDebug, RoseEngine/Debug, RoseEngine/Scene, RoseEngine/GameObject,
//          RoseEngine/MonoBehaviour, RoseEngine/Time, RoseEngine/CoroutineScheduler,
//          RoseEngine/InvokeScheduler, RoseEngine/ComponentRegistry, RoseEngine/ThreadGuard,
//          RoseEngine/MeshRenderer, RoseEngine/SpriteRenderer,
//          RoseEngine/TextRenderer, RoseEngine/UIText, RoseEngine/UIInputField,
//          RoseEngine/Light, RoseEngine/Camera, RoseEngine/Canvas, RoseEngine/CanvasRenderer,
//          RoseEngine/Collider, RoseEngine/Collider2D, RoseEngine/Rigidbody, RoseEngine/Rigidbody2D,
//          IronRose.Engine/PhysicsManager
// @exports
//   static class SceneManager
//     AllGameObjects: IReadOnlyList<GameObject>                — 전체 GO 목록 (Snapshot 반환)
//     GetActiveScene(): Scene                                  — 활성 씬 반환
//     SetActiveScene(Scene): void                              — 활성 씬 설정
//     RegisterGameObject(GameObject): void                     — GO 등록
//     RegisterBehaviour(MonoBehaviour): void                   — MB 등록 (Awake/OnEnable/Start 큐)
//     MoveGameObjectIndex(GameObject, int): void               — 루트 GO 순서 변경
//     Update(float): void                                      — 메인 업데이트 루프
//     FixedUpdate(float): void                                 — 물리 업데이트 루프
//     Clear(): void                                            — 씬 전체 초기화
// @note    AllGameObjects 는 매 호출마다 새 배열을 반환한다 (Snapshot). 호출자는
//          변수에 담아 재사용하는 것을 권장한다. _destroyQueue 는 struct 기반이므로
//          ComponentRegistry 대신 전용 _destroyQueueLock 으로 보호한다.
//          MonoBehaviour 콜백 에러는 Debug.LogError 로 출력.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public static class SceneManager
    {
        // --- Current scene ---
        private static Scene _activeScene = new Scene();

        /// <summary>현재 활성 씬.</summary>
        public static Scene GetActiveScene() => _activeScene;

        /// <summary>활성 씬 설정.</summary>
        public static void SetActiveScene(Scene scene) => _activeScene = scene;

        /// <summary>
        /// 씬 로드 델리게이트. 엔진 초기화 시 SceneSerializer.Load로 설정된다.
        /// Scripts에서 SceneSerializer에 직접 접근할 수 없으므로 이 델리게이트를 통해 호출.
        /// </summary>
        internal static Action<string>? _loadSceneDelegate;

        /// <summary>런타임에서 씬 파일 경로로 씬을 로드한다.</summary>
        public static void LoadScene(string scenePath)
        {
            if (_loadSceneDelegate == null)
            {
                Debug.LogError("[SceneManager] LoadScene delegate not set.");
                return;
            }
            _loadSceneDelegate(scenePath);
        }

        // --- Core registries ---
        private static readonly ComponentRegistry<MonoBehaviour> _behaviours = new();
        private static readonly ComponentRegistry<MonoBehaviour> _pendingStart = new();
        private static readonly ComponentRegistry<GameObject> _allGameObjects = new();

        // --- Deferred destroy ---
        // DestroyEntry 는 struct 이므로 ComponentRegistry<T> where T : class 제약에 맞지 않는다.
        // 따라서 전용 lock 으로 보호한다.
        private static readonly object _destroyQueueLock = new();
        private static readonly List<DestroyEntry> _destroyQueue = new();

        public static IReadOnlyList<GameObject> AllGameObjects => _allGameObjects.Snapshot();

        /// <summary>루트 오브젝트 표시 순서 변경용.</summary>
        public static void MoveGameObjectIndex(GameObject go, int newRootIndex)
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.MoveGameObjectIndex");
            _allGameObjects.WithLock(list =>
            {
                int old = list.IndexOf(go);
                if (old < 0) return;
                list.RemoveAt(old);

                // newRootIndex는 루트 전용 인덱스 → list 내 실제 위치로 변환
                if (newRootIndex < 0) newRootIndex = 0;
                int insertAt = list.Count; // 기본값: 리스트 끝
                int rootCount = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].transform.parent != null) continue;
                    if (rootCount == newRootIndex)
                    {
                        insertAt = i;
                        break;
                    }
                    rootCount++;
                }

                list.Insert(insertAt, go);
            });
        }

        // ================================================================
        // Registration
        // ================================================================

        public static void RegisterGameObject(GameObject go)
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.RegisterGameObject");
            _allGameObjects.Register(go);
        }

        /// <summary>지정 GO의 모든 MonoBehaviour를 등록 해제한다 (프리팹 템플릿 마킹 시 사용).</summary>
        public static void UnregisterBehaviours(GameObject go)
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.UnregisterBehaviours");
            _behaviours.WithLock(list =>
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].gameObject == go)
                    {
                        _pendingStart.Unregister(list[i]);
                        list.RemoveAt(i);
                    }
                }
            });
        }

        public static void RegisterBehaviour(MonoBehaviour behaviour)
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.RegisterBehaviour");
            if (_behaviours.Contains(behaviour)) return;
            // 프리팹 템플릿(_isEditorInternal) GO의 behaviour는 등록하지 않음
            if (behaviour.gameObject != null && behaviour.gameObject._isEditorInternal) return;

            _behaviours.Register(behaviour);

            try
            {
                behaviour.Awake();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in Awake() of {behaviour.GetType().Name}: {ex.Message}");
            }

            behaviour._hasAwoken = true;

            if (behaviour.enabled && behaviour.gameObject.activeSelf)
            {
                try { behaviour.OnEnable(); }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in OnEnable() of {behaviour.GetType().Name}: {ex.Message}");
                }
            }

            _pendingStart.Register(behaviour);
        }

        /// <summary>
        /// Scripts 마이그레이션 등에서 라이프사이클 콜백 없이 행동 등록 해제.
        /// </summary>
        internal static void UnregisterBehaviour(MonoBehaviour behaviour)
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.UnregisterBehaviour");
            CoroutineScheduler.StopAllCoroutines(behaviour);
            InvokeScheduler.CancelAll(behaviour);
            _behaviours.Unregister(behaviour);
            _pendingStart.Unregister(behaviour);
        }

        // ================================================================
        // Fixed Update Loop (physics)
        // ================================================================

        public static void FixedUpdate(float fixedDeltaTime)
        {
            var snap = _behaviours.Snapshot();
            for (int i = 0; i < snap.Length; i++)
            {
                var b = snap[i];
                if (!IsActive(b)) continue;
                try { b.FixedUpdate(); }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in FixedUpdate() of {b.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ================================================================
        // Main Update Loop
        // ================================================================

        public static void Update(float deltaTime)
        {
            Time.unscaledDeltaTime = deltaTime;
            float clampedDt = deltaTime > Time.maximumDeltaTime ? Time.maximumDeltaTime : deltaTime;
            Time.deltaTime = clampedDt * Time.timeScale;
            Time.time += Time.deltaTime;

            // 1. Process pending Start() calls (atomic drain)
            MonoBehaviour[]? pending = null;
            _pendingStart.WithLock(list =>
            {
                if (list.Count == 0) return;
                pending = list.ToArray();
                list.Clear();
            });
            if (pending != null)
            {
                foreach (var b in pending)
                {
                    if (!IsActive(b)) continue;
                    try { b.Start(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Exception in Start() of {b.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // 2. Process Invokes (delegated)
            InvokeScheduler.Process(Time.deltaTime);

            // 3. Update all behaviours
            var updSnap = _behaviours.Snapshot();
            for (int i = 0; i < updSnap.Length; i++)
            {
                var b = updSnap[i];
                if (!IsActive(b)) continue;
                try { b.Update(); }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in Update() of {b.GetType().Name}: {ex.Message}");
                }
            }

            // 4. Process coroutines (delegated)
            CoroutineScheduler.Process(Time.deltaTime);

            // 5. LateUpdate all behaviours
            var lateSnap = _behaviours.Snapshot();
            for (int i = 0; i < lateSnap.Length; i++)
            {
                var b = lateSnap[i];
                if (!IsActive(b)) continue;
                try { b.LateUpdate(); }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in LateUpdate() of {b.GetType().Name}: {ex.Message}");
                }
            }

            // 6. Process deferred destroy queue
            ProcessDestroyQueue(Time.deltaTime);

            Time.frameCount++;
        }

        private static bool IsActive(MonoBehaviour b)
        {
            return b.enabled && !b._isDestroyed && b.gameObject.activeInHierarchy;
        }

        // ================================================================
        // Coroutine delegation
        // ================================================================

        internal static void AddCoroutine(Coroutine coroutine)
            => CoroutineScheduler.AddCoroutine(coroutine);

        internal static void StopCoroutine(MonoBehaviour owner, string methodName)
            => CoroutineScheduler.StopCoroutine(owner, methodName);

        internal static void StopAllCoroutines(MonoBehaviour owner)
            => CoroutineScheduler.StopAllCoroutines(owner);

        // ================================================================
        // Invoke delegation
        // ================================================================

        internal static void ScheduleInvoke(MonoBehaviour target, string methodName, float delay, float repeatRate, bool repeating)
            => InvokeScheduler.Schedule(target, methodName, delay, repeatRate, repeating);

        internal static void CancelAllInvokes(MonoBehaviour target)
            => InvokeScheduler.CancelAll(target);

        internal static void CancelInvoke(MonoBehaviour target, string methodName)
            => InvokeScheduler.Cancel(target, methodName);

        internal static bool IsInvoking(MonoBehaviour target)
            => InvokeScheduler.IsInvoking(target);

        internal static bool IsInvoking(MonoBehaviour target, string methodName)
            => InvokeScheduler.IsInvoking(target, methodName);

        // ================================================================
        // Destroy
        // ================================================================

        internal static void ScheduleDestroy(Object obj, float delay)
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.ScheduleDestroy");
            lock (_destroyQueueLock)
            {
                _destroyQueue.Add(new DestroyEntry { target = obj, timer = delay });
            }
        }

        internal static void DestroyImmediate(Object obj)
        {
            ExecuteDestroy(obj);
        }

        private static void ProcessDestroyQueue(float deltaTime)
        {
            // ScheduleDestroy / ProcessDestroyQueue 는 메인에서만 호출되지만,
            // 방어적으로 lock 으로 감싼다. C# lock 은 동일 스레드 재진입을 허용하므로
            // ExecuteDestroy 내부에서 ScheduleDestroy 가 재호출되어도 데드락 없음.
            lock (_destroyQueueLock)
            {
                for (int i = _destroyQueue.Count - 1; i >= 0; i--)
                {
                    var entry = _destroyQueue[i];
                    entry.timer -= deltaTime;

                    if (entry.timer <= 0f)
                    {
                        ExecuteDestroy(entry.target);
                        _destroyQueue.RemoveAt(i);
                    }
                    else
                    {
                        _destroyQueue[i] = entry;
                    }
                }
            }
        }

        private static void DestroyComponent(Component comp)
        {
            if (comp is MonoBehaviour mb && !mb._isDestroyed)
            {
                try
                {
                    if (mb._hasAwoken && mb.enabled) mb.OnDisable();
                    mb.OnDestroy();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in OnDestroy() of {mb.GetType().Name}: {ex.Message}");
                }
                CoroutineScheduler.StopAllCoroutines(mb);
                InvokeScheduler.CancelAll(mb);
                _behaviours.Unregister(mb);
                _pendingStart.Unregister(mb);
            }

            comp.OnComponentDestroy();
            comp._isDestroyed = true;
        }

        private static void ExecuteDestroy(Object obj)
        {
            if (obj._isDestroyed) return;

            if (obj is GameObject go)
            {
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                    ExecuteDestroy(go.transform.GetChild(i).gameObject);

                foreach (var comp in go._components)
                    DestroyComponent(comp);

                go.transform.SetParent(null, false);
                _allGameObjects.Unregister(go);
                go._isDestroyed = true;
            }
            else if (obj is Component comp)
            {
                DestroyComponent(comp);
                comp.gameObject.RemoveComponent(comp);
            }
        }

        // ================================================================
        // Clear (hot-reload / scene change)
        // ================================================================

        public static void Clear()
        {
            ThreadGuard.DebugCheckMainThread("SceneManager.Clear");
            var snap = _behaviours.Snapshot();
            foreach (var b in snap)
            {
                try
                {
                    if (b._hasAwoken && b.enabled) b.OnDisable();
                    b.OnDestroy();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in OnDestroy() of {b.GetType().Name}: {ex.Message}");
                }
            }

            _behaviours.Clear();
            _pendingStart.Clear();
            _allGameObjects.Clear();
            CoroutineScheduler.Clear();
            InvokeScheduler.Clear();
            lock (_destroyQueueLock) { _destroyQueue.Clear(); }

            MeshRenderer.ClearAll();
            SpriteRenderer.ClearAll();
            TextRenderer.ClearAll();
            UIText.ClearAll();
            UIInputField.ClearAll();
            Light.ClearAll();
            Camera.ClearMain();
            Canvas.ClearAll();
            CanvasRenderer.ClearTextureCache();

            Collider.ClearAll();
            Collider2D.ClearAll();
            Rigidbody.ClearAll();
            Rigidbody2D.ClearAll();

            IronRose.Engine.PhysicsManager.Instance?.Reset();
        }

        // ================================================================
        // Internal types
        // ================================================================

        private struct DestroyEntry
        {
            public Object target;
            public float timer;
        }
    }
}
