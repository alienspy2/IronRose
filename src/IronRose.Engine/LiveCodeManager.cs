// ------------------------------------------------------------
// @file    LiveCodeManager.cs
// @brief   LiveCode 핫 리로드 관리자. LiveCode 디렉토리 감시, 컴파일, 스크립트 도메인
//          로드/리로드, MonoBehaviour 상태 보존을 담당한다.
// @deps    IronRose.Engine/ProjectContext, IronRose.Scripting/ScriptCompiler,
//          IronRose.Scripting/ScriptDomain, RoseEngine/EngineDirectories, RoseEngine/Debug,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/ImGuiScriptsPanel
// @exports
//   class LiveCodeManager (internal)
//     LiveCodeDemoTypes: Type[]              — LiveCode에서 발견된 MonoBehaviour 데모 타입 목록
//     OnAfterReload: Action?                 — 리로드 완료 후 콜백
//     ReloadRequested: bool                  — 리로드 요청 상태
//     Initialize(): void                     — LiveCode 디렉토리 탐색 및 초기 컴파일
//     ProcessReload(): void                  — 파일 변경 시 재컴파일 처리
//     UpdateScripts(): void                  — 스크립트 도메인 업데이트
//     Dispose(): void                        — 리소스 해제
// @note    FindLiveCodeDirectories()에서 ProjectContext.LiveCodePath와
//          ProjectContext.EngineRoot를 사용하여 경로 탐색.
//          src/*/LiveCode/ 하위 프로젝트도 추가 탐색하여 확장성 확보.
// ------------------------------------------------------------
using IronRose.Rendering;
using IronRose.Scripting;
using IronRose.Engine.Editor;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using RoseEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IronRose.Engine
{
    /// <summary>
    /// LiveCode hot-reload: file watching, compilation, and state preservation.
    /// Extracted from EngineCore (Phase 15 — H-2).
    /// </summary>
    internal class LiveCodeManager
    {
        private ScriptCompiler? _compiler;
        private ScriptDomain? _scriptDomain;
        private readonly List<string> _liveCodePaths = new();
        private readonly List<FileSystemWatcher> _liveCodeWatchers = new();
        private bool _reloadRequested;
        private bool _pendingReloadAfterPlayStop;
        private DateTime _lastReloadTime = DateTime.MinValue;
        private readonly Dictionary<string, string> _savedHotReloadStates = new();

        /// <summary>
        /// LiveCode에서 발견된 MonoBehaviour 데모 타입 목록 (DemoLauncher에서 참조).
        /// </summary>
        public Type[] LiveCodeDemoTypes { get; private set; } = Array.Empty<Type>();

        /// <summary>
        /// 핫 리로드 후 씬 복원 콜백.
        /// </summary>
        public Action? OnAfterReload { get; set; }

        public bool ReloadRequested => _reloadRequested;

        /// <summary>
        /// 플레이모드 종료 후 수행해야 할 보류 중인 리로드가 있는지 여부.
        /// </summary>
        public bool HasPendingReload => _pendingReloadAfterPlayStop;

        public void Initialize()
        {
            RoseEngine.EditorDebug.Log("[Engine] Initializing LiveCode hot-reload...");

            // 빌드 타임 LiveCode.dll이 Default ALC에 로드되었는지 확인
            var buildTimeLiveCode = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "LiveCode"
                    && !string.IsNullOrEmpty(a.Location));
            if (buildTimeLiveCode != null)
            {
                RoseEngine.EditorDebug.LogWarning(
                    "[Engine] Build-time LiveCode.dll detected in Default ALC! " +
                    "This may cause duplicate types. " +
                    "Ensure LiveCode.csproj is excluded from build output. " +
                    $"Location: {buildTimeLiveCode.Location}");
            }

            _compiler = new ScriptCompiler();
            _compiler.AddReference(typeof(IronRose.API.Screen));
            _compiler.AddReference(typeof(EngineCore).Assembly.Location);
            _compiler.AddReference(typeof(PostProcessStack).Assembly.Location);
            _compiler.AddReference(typeof(IHotReloadable).Assembly.Location);

            var entryAsm = System.Reflection.Assembly.GetEntryAssembly();
            if (entryAsm != null && !string.IsNullOrEmpty(entryAsm.Location))
                _compiler.AddReference(entryAsm.Location);
            _scriptDomain = new ScriptDomain();

            var monoBehaviourType = typeof(MonoBehaviour);
            _scriptDomain.SetTypeFilter(type => !monoBehaviourType.IsAssignableFrom(type));

            FindLiveCodeDirectories();

            foreach (var path in _liveCodePaths)
            {
                var watcher = new FileSystemWatcher(path, "*.cs");
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                watcher.Changed += OnLiveCodeChanged;
                watcher.Created += OnLiveCodeChanged;
                watcher.Deleted += OnLiveCodeChanged;
                watcher.Renamed += (s, e) => OnLiveCodeChanged(s, e);
                watcher.EnableRaisingEvents = true;
                _liveCodeWatchers.Add(watcher);
                RoseEngine.EditorDebug.Log($"[Engine] FileSystemWatcher active on {path}");
            }

            CompileAllLiveCode();
        }

        /// <summary>
        /// 메인 스레드에서 매 프레임 호출. 리로드 요청이 있으면 처리합니다.
        /// 플레이모드 중에는 리로드를 보류하고, 플레이모드 종료 시 수행합니다.
        /// </summary>
        public void ProcessReload()
        {
            if (!_reloadRequested) return;
            _reloadRequested = false;

            RoseEngine.EditorDebug.LogWarning($"[HotReload:DIAG] ProcessReload triggered. IsInPlaySession={EditorPlayMode.IsInPlaySession}, pendingReload={_pendingReloadAfterPlayStop}");

            // 플레이모드 중에는 리로드를 보류
            if (EditorPlayMode.IsInPlaySession)
            {
                _pendingReloadAfterPlayStop = true;
                RoseEngine.EditorDebug.Log("[Engine] LiveCode change detected during Play mode — reload deferred until Play stops");
                return;
            }

            ExecuteReload();
        }

        /// <summary>
        /// 플레이모드 종료 후 보류 중인 리로드를 수행합니다.
        /// EditorPlayMode.StopPlayMode()에서 호출됩니다.
        /// </summary>
        public void FlushPendingReload()
        {
            RoseEngine.EditorDebug.LogWarning($"[HotReload:DIAG] FlushPendingReload called. pending={_pendingReloadAfterPlayStop}");
            if (!_pendingReloadAfterPlayStop) return;
            _pendingReloadAfterPlayStop = false;

            RoseEngine.EditorDebug.Log("[Engine] Flushing deferred LiveCode reload after Play stop");
            ExecuteReload();
        }

        private void ExecuteReload()
        {
            RoseEngine.EditorDebug.LogWarning($"[HotReload:DIAG] ExecuteReload starting. PlayState={EditorPlayMode.State}");

            CompileAllLiveCode();

            // GO를 유지하고 컴포넌트만 새 어셈블리 타입으로 교체
            MigrateEditorComponents();

            // 모든 외부 참조(씬 컴포넌트, 타입 캐시)가 해제된 후 ALC 수거 검증
            _scriptDomain?.VerifyPreviousContextUnloaded();
        }

        public void UpdateScripts()
        {
            _scriptDomain?.Update();
        }

        public void Dispose()
        {
            foreach (var watcher in _liveCodeWatchers)
                watcher.Dispose();
            _liveCodeWatchers.Clear();
        }

        private void FindLiveCodeDirectories()
        {
            // 1) ProjectContext가 제공하는 루트 LiveCode/ 디렉토리
            string rootLiveCode = ProjectContext.LiveCodePath;
            if (Directory.Exists(rootLiveCode) && !_liveCodePaths.Contains(rootLiveCode))
            {
                _liveCodePaths.Add(rootLiveCode);
                RoseEngine.EditorDebug.Log($"[Engine] Found LiveCode directory: {rootLiveCode}");
            }

            // 2) src/*/LiveCode/ 하위 디렉토리도 추가 탐색 (확장성)
            string srcDir = Path.Combine(ProjectContext.EngineRoot, "src");
            if (Directory.Exists(srcDir))
            {
                foreach (var projectDir in Directory.GetDirectories(srcDir))
                {
                    string liveCodeDir = Path.Combine(
                        projectDir, RoseEngine.EngineDirectories.LiveCodePath);
                    if (!Directory.Exists(liveCodeDir)) continue;

                    string fullPath = Path.GetFullPath(liveCodeDir);
                    if (!_liveCodePaths.Contains(fullPath))
                    {
                        _liveCodePaths.Add(fullPath);
                        RoseEngine.EditorDebug.Log($"[Engine] Found LiveCode directory: {fullPath}");
                    }
                }
            }

            // 3) 아무것도 못 찾으면 생성
            if (_liveCodePaths.Count == 0)
            {
                string fallback = ProjectContext.LiveCodePath;
                Directory.CreateDirectory(fallback);
                _liveCodePaths.Add(fallback);
                RoseEngine.EditorDebug.Log($"[Engine] Created LiveCode directory: {fallback}");
            }
        }

        private void CompileAllLiveCode()
        {
            var csFiles = _liveCodePaths
                .Where(Directory.Exists)
                .SelectMany(p => Directory.GetFiles(p, "*.cs", SearchOption.AllDirectories))
                .Where(f =>
                {
                    // obj/, bin/ 등 빌드 산출물 디렉토리 제외
                    var sep = Path.DirectorySeparatorChar;
                    var altSep = Path.AltDirectorySeparatorChar;
                    return !f.Contains($"{sep}obj{sep}") && !f.Contains($"{altSep}obj{altSep}")
                        && !f.Contains($"{sep}bin{sep}") && !f.Contains($"{altSep}bin{altSep}");
                })
                .ToArray();

            if (csFiles.Length == 0)
                return;

            RoseEngine.EditorDebug.Log($"[Engine] Compiling {csFiles.Length} LiveCode files from {_liveCodePaths.Count} directories...");

            var result = _compiler!.CompileFromFiles(csFiles, "LiveCode");
            if (result.Success && result.AssemblyBytes != null)
            {
                if (_scriptDomain!.IsLoaded)
                {
                    // 이전 ALC Type 참조를 해제하여 GC가 이전 ALC를 수거할 수 있도록 한다
                    MonoBehaviour.ClearMethodCache();
                    _scriptDomain.Reload(result.AssemblyBytes, result.PdbBytes);
                }
                else
                    _scriptDomain.LoadScripts(result.AssemblyBytes, result.PdbBytes);

                RegisterLiveCodeBehaviours();
                RoseEngine.EditorDebug.Log("[Engine] LiveCode loaded!");
            }
            else
            {
                RoseEngine.EditorDebug.LogError("[Engine] LiveCode compilation failed");
            }
        }

        private void RegisterLiveCodeBehaviours()
        {
            var monoBehaviourType = typeof(MonoBehaviour);
            var types = _scriptDomain!.GetLoadedTypes();
            var demoTypes = new List<Type>();

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!monoBehaviourType.IsAssignableFrom(type)) continue;

                demoTypes.Add(type);
                RoseEngine.EditorDebug.Log($"[Engine] LiveCode demo detected: {type.Name}");
            }

            LiveCodeDemoTypes = demoTypes.ToArray();
            RoseEngine.EditorDebug.Log($"[Engine] LiveCode demos available: {LiveCodeDemoTypes.Length}");

            // 에디터 캐시 무효화: 새 타입이 Add Component 메뉴 및 씬 역직렬화에 반영
            ImGuiInspectorPanel.InvalidateComponentTypeCache();
            ImGuiHierarchyPanel.InvalidateComponentTypeCache();
            SceneSerializer.InvalidateComponentTypeCache();
        }

        private void SaveHotReloadableState()
        {
            _savedHotReloadStates.Clear();
            foreach (var go in SceneManager.AllGameObjects)
            {
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is IHotReloadable reloadable)
                    {
                        try
                        {
                            var state = reloadable.SerializeState();
                            _savedHotReloadStates[comp.GetType().Name] = state;
                            RoseEngine.EditorDebug.Log($"[Engine] State saved: {comp.GetType().Name}");
                        }
                        catch (Exception ex)
                        {
                            RoseEngine.EditorDebug.LogError($"[Engine] State save failed for {comp.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void RestoreHotReloadableState()
        {
            if (_savedHotReloadStates.Count == 0) return;

            foreach (var go in SceneManager.AllGameObjects)
            {
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is IHotReloadable reloadable)
                    {
                        string typeName = comp.GetType().Name;
                        if (_savedHotReloadStates.TryGetValue(typeName, out var state))
                        {
                            try
                            {
                                reloadable.DeserializeState(state);
                                RoseEngine.EditorDebug.Log($"[Engine] State restored: {typeName}");
                            }
                            catch (Exception ex)
                            {
                                RoseEngine.EditorDebug.LogError($"[Engine] State restore failed for {typeName}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            _savedHotReloadStates.Clear();
        }

        /// <summary>
        /// 에디터(비Playing) 모드에서 LiveCode 리로드 후,
        /// 기존 컴포넌트 인스턴스를 새 어셈블리 타입으로 교체하고 필드 값을 복사한다.
        /// </summary>
        private void MigrateEditorComponents()
        {
            if (_scriptDomain == null || LiveCodeDemoTypes.Length == 0) return;

            // 새 어셈블리의 타입을 이름으로 매핑
            var newTypeMap = new Dictionary<string, Type>();
            foreach (var t in _scriptDomain.GetLoadedTypes())
                newTypeMap[t.Name] = t;

            foreach (var go in SceneManager.AllGameObjects)
            {
                for (int i = 0; i < go._components.Count; i++)
                {
                    var old = go._components[i];
                    if (old is Transform) continue;

                    var oldType = old.GetType();
                    // 이미 최신 어셈블리 타입이면 스킵
                    if (!newTypeMap.TryGetValue(oldType.Name, out var newType)) continue;
                    if (oldType == newType) continue;

                    // 새 인스턴스 생성
                    Component? newComp;
                    try
                    {
                        newComp = (Component)Activator.CreateInstance(newType)!;
                    }
                    catch
                    {
                        continue;
                    }

                    // public 필드 값 복사 (이름 매칭)
                    CopyFieldValues(oldType, old, newType, newComp);

                    newComp.gameObject = go;
                    go._components[i] = newComp;

                    // MonoBehaviour 레지스트리 교체
                    if (old is MonoBehaviour oldMb)
                        SceneManager.UnregisterBehaviour(oldMb);
                    if (newComp is MonoBehaviour newMb)
                        SceneManager.RegisterBehaviour(newMb);

                    RoseEngine.EditorDebug.Log($"[Engine] Migrated component: {oldType.Name} on {go.name}");
                }
            }
        }

        private static void CopyFieldValues(Type oldType, object oldObj, Type newType, object newObj)
        {
            var newFields = newType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var nf in newFields)
            {
                var of = oldType.GetField(nf.Name, BindingFlags.Public | BindingFlags.Instance);
                if (of == null) continue;

                try
                {
                    var val = of.GetValue(oldObj);
                    // 타입 호환성 체크 (같은 이름이더라도 타입이 다를 수 있음)
                    if (val != null && nf.FieldType.IsAssignableFrom(val.GetType()))
                        nf.SetValue(newObj, val);
                    else if (val == null && !nf.FieldType.IsValueType)
                        nf.SetValue(newObj, null);
                }
                catch
                {
                    // 필드 복사 실패는 무시 (새 기본값 유지)
                }
            }
        }

        private void OnLiveCodeChanged(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.Now;
            var elapsed = (now - _lastReloadTime).TotalSeconds;
            if (elapsed < 1.0)
            {
                RoseEngine.EditorDebug.LogWarning($"[HotReload:DIAG] FileWatcher debounced: {e.Name} (elapsed={elapsed:F2}s < 1.0s)");
                return;
            }

            _lastReloadTime = now;
            _reloadRequested = true;
            RoseEngine.EditorDebug.LogWarning($"[HotReload:DIAG] FileWatcher fired: {e.Name}, changeType={e.ChangeType} -> _reloadRequested=true");
        }
    }
}
