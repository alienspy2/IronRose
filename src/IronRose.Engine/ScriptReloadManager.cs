// ------------------------------------------------------------
// @file    ScriptReloadManager.cs
// @brief   Scripts 핫 리로드 관리자. Scripts 디렉토리 감시, dotnet build 실행,
//          DLL 로드/리로드, MonoBehaviour 상태 보존을 담당한다.
// @deps    IronRose.Engine/ProjectContext, IronRose.Scripting/ScriptDomain,
//          RoseEngine/EngineDirectories, RoseEngine/Debug,
//          IronRose.Engine.Editor/EditorPlayMode,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/ImGuiScriptsPanel,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/ImGuiInspectorPanel,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/ImGuiHierarchyPanel,
//          IronRose.Engine.Editor/SceneSerializer
// @exports
//   class ScriptReloadManager (internal)
//     ScriptDemoTypes: Type[]                  — Scripts에서 발견된 MonoBehaviour 데모 타입 목록
//     OnAfterReload: Action?                   — 리로드 완료 후 콜백
//     ReloadRequested: bool                    — 리로드 요청 상태
//     Initialize(): void                       — Scripts 디렉토리 설정 및 초기 빌드
//     ProcessReload(): void                    — 파일 변경 시 재빌드 처리 (디바운스)
//     UpdateScripts(): void                    — 스크립트 도메인 업데이트
//     OnEnterPlayMode(): void                  — Play mode 진입 시 FileSystemWatcher 중단
//     OnExitPlayMode(): void                   — Play mode 종료 시 FileSystemWatcher 재개 및 변경 감지 빌드
//     Dispose(): void                          — 리소스 해제
// @note    dotnet build로 Scripts.csproj를 빌드하여 DLL을 생성한 뒤 byte[]로 로드.
//          Play mode 중에는 FileSystemWatcher를 중단하고, 종료 시 변경 감지 후 일괄 빌드.
//          ProjectContext.ScriptsPath 단일 경로만 사용.
// ------------------------------------------------------------
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
    /// Scripts hot-reload: file watching, dotnet build, DLL loading, and state preservation.
    /// </summary>
    internal class ScriptReloadManager
    {
        private string? _scriptsCsprojPath;  // Scripts.csproj 절대 경로
        private string? _scriptsDllPath;     // bin/Debug/net10.0/Scripts.dll 절대 경로

        private ScriptDomain? _scriptDomain;
        private readonly List<FileSystemWatcher> _scriptWatchers = new();
        private bool _reloadRequested;
        private DateTime _lastFileChangeTime = DateTime.MinValue;
        private const double DEBOUNCE_SECONDS = 0.5;
        private readonly Dictionary<string, string> _savedHotReloadStates = new();

        /// <summary>
        /// Scripts에서 발견된 MonoBehaviour 데모 타입 목록 (DemoLauncher에서 참조).
        /// </summary>
        public Type[] ScriptDemoTypes { get; private set; } = Array.Empty<Type>();

        /// <summary>
        /// 핫 리로드 후 씬 복원 콜백.
        /// </summary>
        public Action? OnAfterReload { get; set; }

        public bool ReloadRequested => _reloadRequested;

        public void Initialize()
        {
            RoseEngine.EditorDebug.Log("[Engine] Initializing Scripts hot-reload...");

            // 빌드 타임 Scripts.dll이 Default ALC에 로드되었는지 확인
            var buildTimeScripts = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Scripts"
                    && !string.IsNullOrEmpty(a.Location));
            if (buildTimeScripts != null)
            {
                RoseEngine.EditorDebug.LogWarning(
                    "[Engine] Build-time Scripts.dll detected in Default ALC! " +
                    "This may cause duplicate types. " +
                    "Ensure Scripts.csproj is excluded from build output. " +
                    $"Location: {buildTimeScripts.Location}");
            }

            _scriptDomain = new ScriptDomain();

            var monoBehaviourType = typeof(MonoBehaviour);
            _scriptDomain.SetTypeFilter(type => !monoBehaviourType.IsAssignableFrom(type));

            var scriptsDir = ProjectContext.ScriptsPath;
            if (!Directory.Exists(scriptsDir))
            {
                Directory.CreateDirectory(scriptsDir);
                RoseEngine.EditorDebug.Log($"[Engine] Created Scripts directory: {scriptsDir}");
            }

            _scriptsCsprojPath = Path.Combine(scriptsDir, "Scripts.csproj");
            _scriptsDllPath = Path.Combine(scriptsDir, "bin", "Debug", "net10.0", "Scripts.dll");

            RoseEngine.EditorDebug.Log($"[Scripting] Scripts csproj: {_scriptsCsprojPath}");
            RoseEngine.EditorDebug.Log($"[Scripting] Scripts DLL path: {_scriptsDllPath}");

            // FileSystemWatcher 설정
            var watcher = new FileSystemWatcher(scriptsDir, "*.cs");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
            watcher.Changed += OnScriptChanged;
            watcher.Created += OnScriptChanged;
            watcher.Deleted += OnScriptChanged;
            watcher.Renamed += (s, e) => OnScriptChanged(s, e);
            watcher.EnableRaisingEvents = true;
            _scriptWatchers.Add(watcher);
            RoseEngine.EditorDebug.Log($"[Engine] FileSystemWatcher active on {scriptsDir}");

            BuildScripts();
        }

        private void BuildScripts()
        {
            if (_scriptsCsprojPath == null || !File.Exists(_scriptsCsprojPath))
            {
                RoseEngine.EditorDebug.LogWarning("[Scripting] BuildScripts: Scripts.csproj not found");
                return;
            }

            RoseEngine.EditorDebug.Log("[Scripting] BuildScripts: running dotnet build...", force: true);
            var buildStart = DateTime.Now;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{_scriptsCsprojPath}\" --no-restore -c Debug -v q",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_scriptsCsprojPath)!,
            };

            string stdout, stderr;
            int exitCode;

            try
            {
                using var process = System.Diagnostics.Process.Start(psi)!;
                stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                RoseEngine.EditorDebug.LogError(
                    $"[Scripting] BuildScripts: failed to start dotnet build: {ex.Message}");
                return;
            }

            var buildElapsed = (DateTime.Now - buildStart).TotalMilliseconds;

            if (exitCode != 0)
            {
                RoseEngine.EditorDebug.LogError(
                    $"[Scripting] BuildScripts: dotnet build FAILED (exit={exitCode}) in {buildElapsed:F1}ms");
                var errorOutput = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    foreach (var line in errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                            RoseEngine.EditorDebug.LogError($"[Scripting]   {trimmed}");
                    }
                }
                return;
            }

            // 빌드 성공 - DLL 로드
            if (!File.Exists(_scriptsDllPath))
            {
                RoseEngine.EditorDebug.LogError(
                    $"[Scripting] BuildScripts: build succeeded but DLL not found: {_scriptsDllPath}");
                return;
            }

            byte[] assemblyBytes = File.ReadAllBytes(_scriptsDllPath!);
            var pdbPath = Path.ChangeExtension(_scriptsDllPath, ".pdb");
            byte[]? pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

            RoseEngine.EditorDebug.Log(
                $"[Scripting] BuildScripts: SUCCESS in {buildElapsed:F1}ms " +
                $"-- assembly={assemblyBytes.Length}bytes, pdb={pdbBytes?.Length ?? 0}bytes", force: true);

            bool wasLoaded = _scriptDomain!.IsLoaded;
            if (wasLoaded)
            {
                MonoBehaviour.ClearMethodCache();
                _scriptDomain.Reload(assemblyBytes, pdbBytes);
            }
            else
            {
                _scriptDomain.LoadScripts(assemblyBytes, pdbBytes);
            }

            RegisterScriptBehaviours();
            RoseEngine.EditorDebug.Log("[Engine] Scripts loaded!", force: true);
        }

        /// <summary>
        /// Play mode 진입 시 호출. FileSystemWatcher를 중단한다.
        /// </summary>
        public void OnEnterPlayMode()
        {
            foreach (var watcher in _scriptWatchers)
                watcher.EnableRaisingEvents = false;
            RoseEngine.EditorDebug.Log("[Scripting] FileSystemWatcher paused (play mode)", force: true);
        }

        /// <summary>
        /// Play mode 종료 시 호출. FileSystemWatcher를 재활성화하고,
        /// 파일 변경이 있었으면 일괄 빌드/리로드를 수행한다.
        /// </summary>
        public void OnExitPlayMode()
        {
            foreach (var watcher in _scriptWatchers)
                watcher.EnableRaisingEvents = true;
            RoseEngine.EditorDebug.Log("[Scripting] FileSystemWatcher resumed", force: true);

            // play mode 중 파일이 변경되었는지 확인
            if (HasSourceChangedSinceBuild())
            {
                RoseEngine.EditorDebug.Log("[Scripting] Source changes detected during play mode -- rebuilding", force: true);
                ExecuteReload();
            }
        }

        private bool HasSourceChangedSinceBuild()
        {
            if (_scriptsDllPath == null || !File.Exists(_scriptsDllPath))
                return true; // DLL이 없으면 빌드 필요

            var dllWriteTime = File.GetLastWriteTime(_scriptsDllPath);
            var scriptsDir = ProjectContext.ScriptsPath;
            if (!Directory.Exists(scriptsDir))
                return false;

            var csFiles = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories);
            return csFiles.Any(f =>
            {
                var dir = Path.GetDirectoryName(f) ?? "";
                // obj/, bin/ 제외
                if (dir.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || dir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    return false;
                return File.GetLastWriteTime(f) > dllWriteTime;
            });
        }

        /// <summary>
        /// 메인 스레드에서 매 프레임 호출. 리로드 요청이 있으면 처리합니다.
        /// Trailing edge debounce: 마지막 파일 변경 후 DEBOUNCE_SECONDS가 경과해야 리로드를 실행합니다.
        /// </summary>
        public void ProcessReload()
        {
            if (!_reloadRequested) return;

            // Play mode 중에는 리로드하지 않음 — 실행 중인 컴포넌트 교체로 상태 손실 위험
            if (Editor.EditorPlayMode.IsInPlaySession) return;

            var elapsed = (DateTime.Now - _lastFileChangeTime).TotalSeconds;
            if (elapsed < DEBOUNCE_SECONDS) return;

            _reloadRequested = false;
            ExecuteReload();
        }

        private void ExecuteReload()
        {
            RoseEngine.EditorDebug.Log("[Scripting] === ExecuteReload START ===", force: true);
            var reloadStart = DateTime.Now;

            BuildScripts();

            // GO를 유지하고 컴포넌트만 새 어셈블리 타입으로 교체
            MigrateEditorComponents();

            // 캐시된 프리팹 템플릿도 이전 ALC 타입을 참조하므로 무효화
            // (다음 InstantiatePrefab 시 새 ALC 타입으로 재역직렬화됨)
            var db = Resources.GetAssetDatabase();
            db?.InvalidateScriptPrefabCache();

            // 모든 외부 참조(씬 컴포넌트, 타입 캐시)가 해제된 후 ALC 수거 검증
            _scriptDomain?.VerifyPreviousContextUnloaded();

            var reloadElapsed = (DateTime.Now - reloadStart).TotalMilliseconds;
            RoseEngine.EditorDebug.Log($"[Scripting] === ExecuteReload END === (took {reloadElapsed:F1}ms)", force: true);
        }

        public void UpdateScripts()
        {
            _scriptDomain?.Update();
        }

        public void Dispose()
        {
            foreach (var watcher in _scriptWatchers)
                watcher.Dispose();
            _scriptWatchers.Clear();
        }

        private void RegisterScriptBehaviours()
        {
            var monoBehaviourType = typeof(MonoBehaviour);
            var types = _scriptDomain!.GetLoadedTypes();
            RoseEngine.EditorDebug.Log($"[Scripting] RegisterScriptBehaviours: {types.Length} types loaded from assembly", force: true);

            var demoTypes = new List<Type>();

            foreach (var type in types)
            {
                bool isAbstract = type.IsAbstract || type.IsInterface;
                bool isMono = monoBehaviourType.IsAssignableFrom(type);
                RoseEngine.EditorDebug.Log($"[Scripting]   type: {type.FullName} (isAbstract={isAbstract}, isMonoBehaviour={isMono}, baseType={type.BaseType?.Name ?? "null"})", force: true);

                if (isAbstract) continue;
                if (!isMono) continue;

                demoTypes.Add(type);
                RoseEngine.EditorDebug.Log($"[Scripting]   -> registered as Scripts MonoBehaviour: {type.Name}", force: true);
            }

            ScriptDemoTypes = demoTypes.ToArray();
            RoseEngine.EditorDebug.Log($"[Scripting] RegisterScriptBehaviours: total {ScriptDemoTypes.Length} MonoBehaviour types registered", force: true);

            // 에디터 캐시 무효화: 새 타입이 Add Component 메뉴 및 씬 역직렬화에 반영
            ImGuiInspectorPanel.InvalidateComponentTypeCache();
            ImGuiHierarchyPanel.InvalidateComponentTypeCache();
            SceneSerializer.InvalidateComponentTypeCache();
            RoseEngine.EditorDebug.Log("[Scripting] RegisterScriptBehaviours: editor type caches invalidated", force: true);
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
        /// 에디터(비Playing) 모드에서 Scripts 리로드 후,
        /// 기존 컴포넌트 인스턴스를 새 어셈블리 타입으로 교체하고 필드 값을 복사한다.
        /// </summary>
        private void MigrateEditorComponents()
        {
            if (_scriptDomain == null || ScriptDemoTypes.Length == 0)
            {
                RoseEngine.EditorDebug.Log($"[Scripting] MigrateEditorComponents: skipped (scriptDomain={((_scriptDomain == null) ? "null" : "ok")}, demoTypes={ScriptDemoTypes.Length})", force: true);
                return;
            }

            RoseEngine.EditorDebug.Log("[Scripting] MigrateEditorComponents: building new type map...", force: true);

            // 새 어셈블리의 타입을 이름으로 매핑
            var newTypeMap = new Dictionary<string, Type>();
            foreach (var t in _scriptDomain.GetLoadedTypes())
            {
                newTypeMap[t.Name] = t;
                RoseEngine.EditorDebug.Log($"[Scripting]   newTypeMap: {t.Name} -> {t.AssemblyQualifiedName}", force: true);
            }

            int totalGOs = 0;
            int totalComponents = 0;
            int migratedCount = 0;
            int skippedSameType = 0;
            int skippedNoMatch = 0;
            int failedCreation = 0;

            foreach (var go in SceneManager.AllGameObjects)
            {
                totalGOs++;
                for (int i = 0; i < go._components.Count; i++)
                {
                    var old = go._components[i];
                    if (old is Transform) continue;

                    totalComponents++;
                    var oldType = old.GetType();
                    // 이미 최신 어셈블리 타입이면 스킵
                    if (!newTypeMap.TryGetValue(oldType.Name, out var newType))
                    {
                        skippedNoMatch++;
                        continue;
                    }
                    if (oldType == newType)
                    {
                        skippedSameType++;
                        RoseEngine.EditorDebug.Log($"[Scripting]   skip (same type): {oldType.Name} on GO '{go.name}' — assembly={oldType.Assembly.GetName().Name}", force: true);
                        continue;
                    }

                    RoseEngine.EditorDebug.Log($"[Scripting]   migrating: {oldType.Name} on GO '{go.name}' — old asm={oldType.Assembly.GetName().Name}, new asm={newType.Assembly.GetName().Name}", force: true);

                    // 새 인스턴스 생성
                    Component? newComp;
                    try
                    {
                        newComp = (Component)Activator.CreateInstance(newType)!;
                    }
                    catch (Exception ex)
                    {
                        failedCreation++;
                        RoseEngine.EditorDebug.LogError($"[Scripting]   FAILED to create instance of {newType.Name}: {ex.Message}");
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

                    migratedCount++;
                    RoseEngine.EditorDebug.Log($"[Scripting]   migrated OK: {oldType.Name} on GO '{go.name}'", force: true);
                }
            }

            RoseEngine.EditorDebug.Log($"[Scripting] MigrateEditorComponents: done — scanned {totalGOs} GOs, {totalComponents} components, migrated={migratedCount}, skippedSameType={skippedSameType}, skippedNoMatch={skippedNoMatch}, failedCreation={failedCreation}", force: true);
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
                    if (val == null)
                    {
                        if (!nf.FieldType.IsValueType)
                            nf.SetValue(newObj, null);
                        continue;
                    }

                    // 엔진 타입(non-Scripts) 필드는 ALC 경계와 무관하므로 직접 할당.
                    // Scripts 타입 필드는 IsAssignableFrom 체크 후 할당.
                    var valType = val.GetType();
                    if (nf.FieldType.IsAssignableFrom(valType))
                    {
                        nf.SetValue(newObj, val);
                    }
                    else
                    {
                        // IsAssignableFrom 실패 시 — 같은 이름의 엔진 타입이면 직접 할당 시도
                        // (ALC 경계에서 Type identity가 달라지는 엣지 케이스 방어)
                        try
                        {
                            nf.SetValue(newObj, val);
                            RoseEngine.EditorDebug.Log($"[Scripting] CopyFieldValues: fallback SetValue succeeded for '{nf.Name}' (fieldType={nf.FieldType.Name}, valType={valType.Name})", force: true);
                        }
                        catch (ArgumentException)
                        {
                            RoseEngine.EditorDebug.Log($"[Scripting] CopyFieldValues: skipped '{nf.Name}' — type mismatch (fieldType={nf.FieldType.FullName} [{nf.FieldType.Assembly.GetName().Name}], valType={valType.FullName} [{valType.Assembly.GetName().Name}])", force: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    RoseEngine.EditorDebug.LogWarning($"[Scripting] CopyFieldValues: failed to copy '{nf.Name}' on {newType.Name}: {ex.Message}");
                }
            }
        }

        private void OnScriptChanged(object sender, FileSystemEventArgs e)
        {
            // obj/, bin/ 디렉토리 내 파일 변경은 무시 (dotnet build 부산물)
            var name = e.Name ?? "";
            if (name.StartsWith($"obj{Path.DirectorySeparatorChar}") ||
                name.StartsWith($"bin{Path.DirectorySeparatorChar}"))
                return;

            // Trailing edge debounce: 파일 변경이 감지될 때마다 타이머를 리셋.
            // ProcessReload()에서 마지막 변경 후 DEBOUNCE_SECONDS가 경과한 뒤에 리로드를 실행한다.
            _lastFileChangeTime = DateTime.Now;
            _reloadRequested = true;
            RoseEngine.EditorDebug.Log($"[Scripting] File change detected: {e.Name} ({e.ChangeType}) — debounce timer reset, _reloadRequested=true, time={_lastFileChangeTime:HH:mm:ss.fff}", force: true);
        }
    }
}
