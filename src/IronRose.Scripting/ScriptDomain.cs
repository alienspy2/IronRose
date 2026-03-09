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

        public bool IsLoaded => _currentALC != null;

        public void SetTypeFilter(Func<Type, bool> filter)
        {
            _typeFilter = filter;
        }

        public Type[] GetLoadedTypes()
        {
            return _currentAssembly?.GetTypes() ?? Array.Empty<Type>();
        }

        public void LoadScripts(byte[] assemblyBytes)
        {
            Debug.Log("[ScriptDomain] Loading scripts...");

            // 새로운 ALC 생성
            _currentALC = new AssemblyLoadContext($"ScriptContext_{DateTime.Now.Ticks}", isCollectible: true);

            // ALC Resolving: default ALC fallback (IronRose.Engine 등 참조 해결)
            _currentALC.Resolving += (alc, assemblyName) =>
            {
                // default ALC에서 이미 로드된 어셈블리 찾기
                foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
                {
                    if (loaded.GetName().Name == assemblyName.Name)
                        return loaded;
                }
                return null;
            };

            // 어셈블리 로드
            using var ms = new System.IO.MemoryStream(assemblyBytes);
            _currentAssembly = _currentALC.LoadFromStream(ms);

            Debug.Log($"[ScriptDomain] Loaded assembly: {_currentAssembly.FullName}");

            // 스크립트 클래스 인스턴스화
            InstantiateScripts();
        }

        public void Reload(byte[] newAssemblyBytes)
        {
            Debug.Log("[ScriptDomain] Hot reloading scripts...");

            UnloadPreviousContext();
            LoadScripts(newAssemblyBytes);

            Debug.Log("[ScriptDomain] Hot reload completed!");
        }

        private void UnloadPreviousContext()
        {
            if (_currentALC == null)
            {
                Debug.Log("[ScriptDomain] No previous context to unload");
                return;
            }

            Debug.Log("[ScriptDomain] Unloading previous context...");

            _scriptInstances.Clear();
            _currentAssembly = null;

            var weakRef = new WeakReference(_currentALC, trackResurrection: true);
            _currentALC.Unload();
            _currentALC = null;

            // GC 강제 실행
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (weakRef.IsAlive)
            {
                Debug.LogWarning("[ScriptDomain] WARNING: ALC not fully unloaded!");
            }
            else
            {
                Debug.Log("[ScriptDomain] Previous context unloaded successfully");
            }
        }

        private void InstantiateScripts()
        {
            if (_currentAssembly == null)
            {
                Debug.LogError("[ScriptDomain] ERROR: No assembly loaded");
                return;
            }

            Debug.Log("[ScriptDomain] Instantiating script classes...");

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
                            Debug.Log($"[ScriptDomain] Instantiated: {type.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[ScriptDomain] ERROR instantiating {type.Name}: {ex.Message}");
                    }
                }
            }

            Debug.Log($"[ScriptDomain] Total instances: {_scriptInstances.Count}");
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
