# Phase 47b: ScriptReloadManager -- dotnet build 방식 전환

## 목표
- `LiveCodeManager.cs`를 `ScriptReloadManager.cs`로 이름 변경
- Roslyn 인메모리 컴파일을 `dotnet build` + DLL 파일 읽기 방식으로 전환
- Play mode 중 FileSystemWatcher 완전 중단, 종료 시 변경 감지 후 일괄 빌드/리로드
- `FindLiveCodeDirectories()` 삭제, `ProjectContext.ScriptsPath` 단일 경로 사용

## 선행 조건
- Phase 47a 완료 (Scripts 통합 -- 상수/경로/템플릿/UI/CLI 변경)
- `ProjectContext.ScriptsPath` 속성이 존재

## 삭제할 파일

### `src/IronRose.Engine/LiveCodeManager.cs`
- 이 파일을 삭제하고, 동일 위치에 `ScriptReloadManager.cs`를 새로 생성한다.

## 생성할 파일

### `src/IronRose.Engine/ScriptReloadManager.cs`
- **역할**: Scripts 핫 리로드 관리자. Scripts 디렉토리 감시, dotnet build 실행, DLL 로드/리로드, MonoBehaviour 상태 보존
- **클래스**: `ScriptReloadManager` (internal)
- **네임스페이스**: `IronRose.Engine`
- **using 문**:
```csharp
using IronRose.Scripting;
using IronRose.Engine.Editor;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using RoseEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
```
- 참고: `IronRose.Rendering`의 `using`은 제거 (ScriptCompiler 참조가 없어지므로 `PostProcessStack` 타입 참조도 불필요)

- **주요 멤버**:

  - **필드** (제거된 것과 추가된 것 포함):
    ```csharp
    // 제거: _compiler (ScriptCompiler), _liveCodePaths (List<string>)
    // 추가:
    private string? _scriptsCsprojPath;  // Scripts.csproj 절대 경로
    private string? _scriptsDllPath;     // bin/Debug/net10.0/Scripts.dll 절대 경로

    // 유지 (이름 변경):
    private ScriptDomain? _scriptDomain;
    private readonly List<FileSystemWatcher> _scriptWatchers = new();  // _liveCodeWatchers에서 이름 변경
    private bool _reloadRequested;
    private DateTime _lastFileChangeTime = DateTime.MinValue;
    private const double DEBOUNCE_SECONDS = 0.5;
    private readonly Dictionary<string, string> _savedHotReloadStates = new();

    // 제거: _pendingReloadAfterPlayStop (play mode 로직 변경으로 불필요)
    ```

  - **프로퍼티**:
    ```csharp
    public Type[] ScriptDemoTypes { get; private set; } = Array.Empty<Type>();  // LiveCodeDemoTypes에서 이름 변경
    public Action? OnAfterReload { get; set; }
    public bool ReloadRequested => _reloadRequested;
    // 제거: HasPendingReload
    ```

  - **`Initialize()` 메서드** -- ScriptCompiler/AddReference 코드 전부 제거, dotnet build 경로 설정:
    ```csharp
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
    ```

  - **`BuildScripts()` 메서드** (CompileAllLiveCode 대체) -- dotnet build 프로세스 실행:
    ```csharp
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
    ```

  - **`OnEnterPlayMode()` / `OnExitPlayMode()` 메서드** (새로 추가):
    ```csharp
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
    ```

  - **`ProcessReload()` 메서드** (단순화 -- play mode 체크 제거):
    ```csharp
    public void ProcessReload()
    {
        if (!_reloadRequested) return;

        var elapsed = (DateTime.Now - _lastFileChangeTime).TotalSeconds;
        if (elapsed < DEBOUNCE_SECONDS) return;

        _reloadRequested = false;
        ExecuteReload();
    }
    ```

  - **`ExecuteReload()` 메서드** (동일 구조, 메서드명만 변경):
    ```csharp
    private void ExecuteReload()
    {
        RoseEngine.EditorDebug.Log("[Scripting] === ExecuteReload START ===", force: true);
        var reloadStart = DateTime.Now;

        BuildScripts();

        // GO를 유지하고 컴포넌트만 새 어셈블리 타입으로 교체
        MigrateEditorComponents();

        // 모든 외부 참조(씬 컴포넌트, 타입 캐시)가 해제된 후 ALC 수거 검증
        _scriptDomain?.VerifyPreviousContextUnloaded();

        var reloadElapsed = (DateTime.Now - reloadStart).TotalMilliseconds;
        RoseEngine.EditorDebug.Log($"[Scripting] === ExecuteReload END === (took {reloadElapsed:F1}ms)", force: true);
    }
    ```

  - **`RegisterScriptBehaviours()` 메서드** (RegisterLiveCodeBehaviours에서 이름 변경, 로그만 변경):
    - "LiveCode"를 "Scripts"로 변경한 것 외에는 로직 동일

  - **`OnScriptChanged()` 메서드** (OnLiveCodeChanged에서 이름 변경):
    - 로그 메시지만 "[Scripting] File change detected" 형식 유지

  - **`FlushPendingReload()` 메서드** -- 삭제 (play mode 로직 변경으로 불필요)
  - **`FindLiveCodeDirectories()` 메서드** -- 삭제 (Initialize에서 직접 ScriptsPath 사용)

  - **유지되는 메서드들** (로직 동일, "LiveCode"를 "Scripts"로 변경):
    - `UpdateScripts()`
    - `Dispose()`
    - `SaveHotReloadableState()`
    - `RestoreHotReloadableState()`
    - `MigrateEditorComponents()` -- `LiveCodeDemoTypes` 참조를 `ScriptDemoTypes`로 변경
    - `CopyFieldValues()` -- 동일

- **파일 상단 주석 헤더**: LiveCode 관련 설명을 Scripts로 전부 변경
  - `@file ScriptReloadManager.cs`
  - `@brief Scripts 핫 리로드 관리자...`
  - `@exports class ScriptReloadManager (internal)` 등

## 빌드 통과를 위해 함께 수정할 파일

`LiveCodeManager` 타입이 삭제되므로, 이를 참조하는 `EngineCore.cs`도 함께 수정해야 빌드가 통과된다.

### `src/IronRose.Engine/EngineCore.cs`
- **필드 변경**:
  - `private LiveCodeManager? _liveCodeManager;` --> `private ScriptReloadManager? _scriptReloadManager;`
  - `private static LiveCodeManager? _staticLiveCodeManager;` --> `private static ScriptReloadManager? _staticScriptReloadManager;`
- **프로퍼티 변경**:
  - `LiveCodeDemoTypes` --> `ScriptDemoTypes`
  - `_staticLiveCodeManager?.LiveCodeDemoTypes` --> `_staticScriptReloadManager?.ScriptDemoTypes`
- **OnAfterReload 프로퍼티**: `_liveCodeManager` --> `_scriptReloadManager` (3곳)
- **Update() 내 참조**:
  - `_liveCodeManager?.ProcessReload()` --> `_scriptReloadManager?.ProcessReload()`
  - `_liveCodeManager?.UpdateScripts()` --> `_scriptReloadManager?.UpdateScripts()`
- **Shutdown() 내 참조**: `_liveCodeManager?.Dispose()` --> `_scriptReloadManager?.Dispose()`
- **InitFrozenCode() 삭제**: Phase 47a에서 FrozenCodePath가 삭제되었고 이미 임시 수정으로 ScriptsPath를 사용 중이지만, 이 Phase에서 `InitFrozenCode()` 전체를 삭제한다 (Roslyn 컴파일 코드가 포함되어 있으므로 더 이상 의미 없음).
- **InitLiveCode() --> InitScripts() 변경**:
  ```csharp
  private void InitScripts()
  {
      _scriptReloadManager = new ScriptReloadManager();
      _scriptReloadManager.Initialize();
      _staticScriptReloadManager = _scriptReloadManager;
      if (_pendingOnAfterReload != null)
      {
          _scriptReloadManager.OnAfterReload = _pendingOnAfterReload;
          _pendingOnAfterReload = null;
      }
  }
  ```
- **Initialize() 호출부**: `InitFrozenCode(); InitLiveCode();` --> `InitScripts();`
- **EditorPlayMode.OnAfterStopPlayMode 콜백**: 기존 `FlushPendingReload()` 등록 제거 (InitScripts에서 등록하지 않음. Play mode 콜백은 Phase 47c에서 추가).

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`
- **assembly.info 핸들러**: `EngineCore.LiveCodeDemoTypes` --> `EngineCore.ScriptDemoTypes`

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `src/IronRose.Engine/LiveCodeManager.cs` 파일이 삭제됨
- [ ] `src/IronRose.Engine/ScriptReloadManager.cs` 파일이 생성됨
- [ ] `EngineCore.cs`에 `LiveCodeManager` 타입 참조가 없음
- [ ] `EngineCore.cs`에 `InitFrozenCode` 메서드가 없음
- [ ] 빌드 경고 최소화

## 참고
- 이 Phase에서 `EngineCore.cs`의 변경은 빌드 통과를 위한 필수 변경이다. Phase 47c에서 EditorPlayMode 콜백 연결 등 추가 정리를 수행한다.
- `IronRose.Rendering` using은 더 이상 ScriptReloadManager에서 필요하지 않으므로 제거한다 (`PostProcessStack` 타입 참조가 `ScriptCompiler.AddReference` 코드와 함께 제거됨).
- `EditorPlayMode.OnAfterStopPlayMode`는 아직 존재하지만, ScriptReloadManager에서는 `OnEnterPlayMode()`/`OnExitPlayMode()` 방식을 사용한다. 이 콜백 연결은 Phase 47c에서 수행한다.
