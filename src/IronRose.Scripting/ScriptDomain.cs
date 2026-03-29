using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using RoseEngine;

namespace IronRose.Scripting
{
    public class ScriptDomain
    {
        private AssemblyLoadContext? _currentALC;
        private Assembly? _currentAssembly;
        private readonly List<object> _scriptInstances = new();
        private Func<Type, bool>? _typeFilter;
        private Func<AssemblyLoadContext, AssemblyName, Assembly?>? _resolvingHandler;
        private WeakReference? _previousALCWeakRef;

        public bool IsLoaded => _currentALC != null;

        public void SetTypeFilter(Func<Type, bool> filter)
        {
            _typeFilter = filter;
        }

        public Type[] GetLoadedTypes()
        {
            return _currentAssembly?.GetTypes() ?? Array.Empty<Type>();
        }

        public void LoadScripts(byte[] assemblyBytes, byte[]? pdbBytes = null)
        {
            EditorDebug.Log($"[ScriptDomain] LoadScripts: assemblyBytes={assemblyBytes.Length}, pdbBytes={pdbBytes?.Length ?? 0}", force: true);

            // 새로운 ALC 생성
            var alcName = $"ScriptContext_{DateTime.Now.Ticks}";
            _currentALC = new AssemblyLoadContext(alcName, isCollectible: true);
            EditorDebug.Log($"[ScriptDomain] Created new ALC: {alcName}", force: true);

            // ALC Resolving: default ALC fallback (IronRose.Engine 등 참조 해결)
            _resolvingHandler = (alc, assemblyName) =>
            {
                // default ALC에서 이미 로드된 어셈블리 찾기
                foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
                {
                    if (loaded.GetName().Name == assemblyName.Name)
                    {
                        EditorDebug.Log($"[ScriptDomain] ALC Resolving: {assemblyName.Name} -> found in Default ALC", force: true);
                        return loaded;
                    }
                }
                EditorDebug.LogWarning($"[ScriptDomain] ALC Resolving: {assemblyName.Name} -> NOT FOUND in Default ALC");
                return null;
            };
            _currentALC.Resolving += _resolvingHandler;

            // 어셈블리 로드 (PDB가 있으면 함께 로드하여 스택트레이스에 소스 정보 포함)
            using var ms = new System.IO.MemoryStream(assemblyBytes);
            if (pdbBytes != null && pdbBytes.Length > 0)
            {
                using var pdbMs = new System.IO.MemoryStream(pdbBytes);
                _currentAssembly = _currentALC.LoadFromStream(ms, pdbMs);
                EditorDebug.Log("[ScriptDomain] Assembly loaded with PDB", force: true);
            }
            else
            {
                _currentAssembly = _currentALC.LoadFromStream(ms);
                EditorDebug.Log("[ScriptDomain] Assembly loaded without PDB", force: true);
            }

            EditorDebug.Log($"[ScriptDomain] Loaded assembly: {_currentAssembly.FullName}", force: true);

            try
            {
                var loadedTypes = _currentAssembly.GetTypes();
                EditorDebug.Log($"[ScriptDomain] Assembly contains {loadedTypes.Length} types", force: true);
            }
            catch (ReflectionTypeLoadException rtle)
            {
                EditorDebug.LogError($"[ScriptDomain] ReflectionTypeLoadException: {rtle.Types.Length} types, {rtle.LoaderExceptions.Length} loader exceptions");
                foreach (var lex in rtle.LoaderExceptions)
                {
                    if (lex != null)
                        EditorDebug.LogError($"[ScriptDomain]   loader exception: {lex.Message}");
                }
            }

            // 스크립트 클래스 인스턴스화
            InstantiateScripts();
        }

        public void Reload(byte[] newAssemblyBytes, byte[]? pdbBytes = null)
        {
            EditorDebug.Log($"[ScriptDomain] Reload: starting hot reload (newAssembly={newAssemblyBytes.Length}bytes, pdb={pdbBytes?.Length ?? 0}bytes)", force: true);

            UnloadPreviousContext();
            LoadScripts(newAssemblyBytes, pdbBytes);

            EditorDebug.Log("[ScriptDomain] Reload: hot reload completed!", force: true);
        }

        private void UnloadPreviousContext()
        {
            if (_currentALC == null)
            {
                EditorDebug.Log("[ScriptDomain] UnloadPreviousContext: no previous context to unload", force: true);
                return;
            }

            EditorDebug.Log($"[ScriptDomain] UnloadPreviousContext: unloading ALC (instances={_scriptInstances.Count}, assembly={_currentAssembly?.FullName ?? "null"})", force: true);

            _scriptInstances.Clear();
            _currentAssembly = null;

            // Resolving 이벤트 구독 해제
            if (_resolvingHandler != null)
            {
                _currentALC.Resolving -= _resolvingHandler;
                _resolvingHandler = null;
            }

            _previousALCWeakRef = new WeakReference(_currentALC, trackResurrection: true);
            _currentALC.Unload();
            _currentALC = null;
            EditorDebug.Log("[ScriptDomain] UnloadPreviousContext: ALC unloaded, weak reference set", force: true);
        }

        /// <summary>
        /// 이전 ALC가 GC에 의해 수거되었는지 검증한다.
        /// 모든 외부 참조(씬 컴포넌트, 타입 캐시 등)가 해제된 후 호출해야 정확하다.
        /// </summary>
        public void VerifyPreviousContextUnloaded()
        {
            if (_previousALCWeakRef == null || !_previousALCWeakRef.IsAlive)
            {
                EditorDebug.Log($"[ScriptDomain] VerifyPreviousContextUnloaded: no previous ALC to check (weakRef={((_previousALCWeakRef == null) ? "null" : "dead")})", force: true);
                return;
            }

            EditorDebug.Log("[ScriptDomain] VerifyPreviousContextUnloaded: attempting GC collection...", force: true);
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (_previousALCWeakRef.IsAlive)
            {
                EditorDebug.LogWarning("[ScriptDomain] VerifyPreviousContextUnloaded: WARNING — ALC not fully unloaded after 5 GC cycles! Possible type leak.");
            }
            else
            {
                EditorDebug.Log("[ScriptDomain] VerifyPreviousContextUnloaded: previous ALC unloaded successfully", force: true);
                _previousALCWeakRef = null;
            }
        }

        private void InstantiateScripts()
        {
            if (_currentAssembly == null)
            {
                EditorDebug.LogError("[ScriptDomain] ERROR: No assembly loaded");
                return;
            }

            EditorDebug.Log("[ScriptDomain] Instantiating script classes...");

            foreach (var type in _currentAssembly.GetTypes())
            {
                // TypeFilter가 설정된 경우 필터링 (MonoBehaviour 제외)
                if (_typeFilter != null && !_typeFilter(type))
                    continue;

                // Update() 메서드가 있는 클래스만 인스턴스화
                if (type.GetMethod("Update") != null)
                {
                    try
                    {
                        var instance = Activator.CreateInstance(type);
                        if (instance != null)
                        {
                            _scriptInstances.Add(instance);
                            EditorDebug.Log($"[ScriptDomain] Instantiated: {type.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        EditorDebug.LogError($"[ScriptDomain] ERROR instantiating {type.Name}: {ex.Message}");
                    }
                }
            }

            EditorDebug.Log($"[ScriptDomain] Total instances: {_scriptInstances.Count}");
        }

        public void Update()
        {
            foreach (var instance in _scriptInstances)
            {
                try
                {
                    var updateMethod = instance.GetType().GetMethod("Update");
                    updateMethod?.Invoke(instance, null);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ScriptDomain] ERROR in Update: {ex.Message}");
                }
            }
        }
    }
}
