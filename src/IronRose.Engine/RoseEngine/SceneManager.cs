// ------------------------------------------------------------
// @file    SceneManager.cs
// @brief   씬 내 GameObject/MonoBehaviour 등록, 게임 루프(Update/FixedUpdate/LateUpdate),
//          Destroy 큐 처리, 코루틴/Invoke 위임, 씬 초기화(Clear) 등 핵심 관리자.
// @deps    RoseEngine/EditorDebug, RoseEngine/Debug, RoseEngine/Scene, RoseEngine/GameObject,
//          RoseEngine/MonoBehaviour, RoseEngine/Time, RoseEngine/CoroutineScheduler,
//          RoseEngine/InvokeScheduler, RoseEngine/MeshRenderer, RoseEngine/SpriteRenderer,
//          RoseEngine/TextRenderer, RoseEngine/UIText, RoseEngine/UIInputField,
//          RoseEngine/Light, RoseEngine/Camera, RoseEngine/Canvas, RoseEngine/CanvasRenderer,
//          RoseEngine/Collider, RoseEngine/Collider2D, RoseEngine/Rigidbody, RoseEngine/Rigidbody2D,
//          IronRose.Engine/PhysicsManager
// @exports
//   static class SceneManager
//     AllGameObjects: IReadOnlyList<GameObject>                — 전체 GO 목록
//     GetActiveScene(): Scene                                  — 활성 씬 반환
//     SetActiveScene(Scene): void                              — 활성 씬 설정
//     RegisterGameObject(GameObject): void                     — GO 등록
//     RegisterBehaviour(MonoBehaviour): void                   — MB 등록 (Awake/OnEnable/Start 큐)
//     MoveGameObjectIndex(GameObject, int): void               — 루트 GO 순서 변경
//     Update(float): void                                      — 메인 업데이트 루프
//     FixedUpdate(float): void                                 — 물리 업데이트 루프
//     Clear(): void                                            — 씬 전체 초기화
// @note    [Diag] 태그 로그는 EditorDebug로, MonoBehaviour 콜백 에러는 Debug로 출력.
//          Update 루프에서 300프레임마다 [Diag] 진단 로그 출력.
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

        // --- Core registries ---
        private static readonly List<MonoBehaviour> _behaviours = new();
        private static readonly List<MonoBehaviour> _pendingStart = new();
        private static readonly List<GameObject> _allGameObjects = new();

        // --- Deferred destroy ---
        private static readonly List<DestroyEntry> _destroyQueue = new();

        public static IReadOnlyList<GameObject> AllGameObjects => _allGameObjects;

        /// <summary>루트 오브젝트 표시 순서 변경용.</summary>
        public static void MoveGameObjectIndex(GameObject go, int newRootIndex)
        {
            int old = _allGameObjects.IndexOf(go);
            if (old < 0) return;
            _allGameObjects.RemoveAt(old);

            // newRootIndex는 루트 전용 인덱스 → _allGameObjects 내 실제 위치로 변환
            if (newRootIndex < 0) newRootIndex = 0;
            int insertAt = _allGameObjects.Count; // 기본값: 리스트 끝
            int rootCount = 0;
            for (int i = 0; i < _allGameObjects.Count; i++)
            {
                if (_allGameObjects[i].transform.parent != null) continue;
                if (rootCount == newRootIndex)
                {
                    insertAt = i;
                    break;
                }
                rootCount++;
            }

            _allGameObjects.Insert(insertAt, go);
        }

        // ================================================================
        // Registration
        // ================================================================

        public static void RegisterGameObject(GameObject go)
        {
            _allGameObjects.Add(go);
        }

        public static void RegisterBehaviour(MonoBehaviour behaviour)
        {
            if (_behaviours.Contains(behaviour)) return;

            EditorDebug.Log($"[Diag] RegisterBehaviour: {behaviour.GetType().Name} (enabled={behaviour.enabled}, go={behaviour.gameObject?.name}, active={behaviour.gameObject?.activeInHierarchy})");
            _behaviours.Add(behaviour);

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

            _pendingStart.Add(behaviour);
        }

        /// <summary>
        /// LiveCode 마이그레이션 등에서 라이프사이클 콜백 없이 행동 등록 해제.
        /// </summary>
        internal static void UnregisterBehaviour(MonoBehaviour behaviour)
        {
            CoroutineScheduler.StopAllCoroutines(behaviour);
            InvokeScheduler.CancelAll(behaviour);
            _behaviours.Remove(behaviour);
            _pendingStart.Remove(behaviour);
        }

        // ================================================================
        // Fixed Update Loop (physics)
        // ================================================================

        public static void FixedUpdate(float fixedDeltaTime)
        {
            for (int i = 0; i < _behaviours.Count; i++)
            {
                var b = _behaviours[i];
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

            // 1. Process pending Start() calls
            if (_pendingStart.Count > 0)
            {
                var pending = new List<MonoBehaviour>(_pendingStart);
                _pendingStart.Clear();

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
            if (Time.frameCount % 300 == 0)
                EditorDebug.Log($"[Diag] Update loop: {_behaviours.Count} behaviours registered");
            for (int i = 0; i < _behaviours.Count; i++)
            {
                var b = _behaviours[i];
                if (!IsActive(b))
                {
                    if (Time.frameCount % 300 == 0)
                        EditorDebug.Log($"[Diag] SKIPPED {b.GetType().Name}: enabled={b.enabled}, destroyed={b._isDestroyed}, activeInHierarchy={b.gameObject.activeInHierarchy}");
                    continue;
                }
                try { b.Update(); }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception in Update() of {b.GetType().Name}: {ex.Message}");
                }
            }

            // 4. Process coroutines (delegated)
            CoroutineScheduler.Process(Time.deltaTime);

            // 5. LateUpdate all behaviours
            for (int i = 0; i < _behaviours.Count; i++)
            {
                var b = _behaviours[i];
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
            _destroyQueue.Add(new DestroyEntry { target = obj, timer = delay });
        }

        internal static void DestroyImmediate(Object obj)
        {
            ExecuteDestroy(obj);
        }

        private static void ProcessDestroyQueue(float deltaTime)
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
                _behaviours.Remove(mb);
                _pendingStart.Remove(mb);
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
                _allGameObjects.Remove(go);
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
            foreach (var b in _behaviours)
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
            _destroyQueue.Clear();

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
