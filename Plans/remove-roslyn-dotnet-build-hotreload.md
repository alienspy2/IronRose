# Roslyn 제거 및 Scripts 통합 — dotnet build DLL 교체 방식 전환

## 배경

IronRose 엔진의 스크립트 시스템은 Roslyn(`Microsoft.CodeAnalysis.CSharp`)을 두 곳에서 사용한다:

1. **LiveCode 핫리로드** (`LiveCodeManager.cs`) — .cs 변경 감지 -> Roslyn 인메모리 컴파일 -> ALC 로드
2. **FrozenCode 초기화** (`EngineCore.cs:InitFrozenCode()`) — 엔진 시작 시 FrozenCode .cs 파일을 Roslyn으로 컴파일 -> `Assembly.Load()`

### 문제점
- 수동 `AddReference` 관리가 취약하고, IDE(csproj)와 런타임(Roslyn) 참조 불일치가 반복 발생
- LiveCode/FrozenCode 이중 구조가 불필요한 복잡성을 유발 (`/digest` 워크플로우, 중복 csproj, 타입 검색 순서 등)
- Roslyn 패키지(`Microsoft.CodeAnalysis.CSharp`)가 불필요한 의존성 증가 유발

## 목표

1. **LiveCode + FrozenCode → Scripts** 단일 프로젝트로 통합
2. **LiveCodeManager → ScriptReloadManager** 이름 변경
3. **Roslyn 제거** — `dotnet build Scripts.csproj` + DLL `File.ReadAllBytes()` -> ALC 로드
4. Play mode 중 FileSystemWatcher 중단, 종료 시 일괄 빌드/리로드

---

## 현재 상태

### 프로젝트 구조 (현재)

```
IronRose/
  src/
    IronRose.Scripting/
      ScriptCompiler.cs       -- Roslyn 컴파일러 (제거 대상)
      ScriptDomain.cs         -- ALC 로드/언로드 (유지)
      StateManager.cs         -- 핫리로드 상태 관리 (유지)
      IHotReloadable.cs       -- 인터페이스 (유지)
      IronRose.Scripting.csproj -- Microsoft.CodeAnalysis.CSharp 참조 (제거)
    IronRose.Engine/
      LiveCodeManager.cs      -- 핫리로드 매니저 (이름 변경 + 수정)
      EngineCore.cs           -- InitFrozenCode() + InitLiveCode() (통합)
      ProjectContext.cs       -- LiveCodePath, FrozenCodePath (통합)
      RoseEngine/EngineConstants.cs -- LiveCodePath, FrozenCodePath 상수 (통합)
      Editor/ImGui/Panels/ImGuiScriptsPanel.cs -- LiveCode/FrozenCode 트리 (통합)
      Editor/EditorPlayMode.cs -- OnAfterStopPlayMode 콜백
      Cli/CliCommandDispatcher.cs -- FrozenCode/LiveCode 어셈블리 검색 (통합)
      RoseEngine/SceneManager.cs -- LiveCode 참조
    IronRose.Standalone/
      IronRose.Standalone.csproj -- FrozenCode ProjectReference (수정)
  templates/default/
    LiveCode/LiveCode.csproj  -- (Scripts/Scripts.csproj로 변경)
    FrozenCode/FrozenCode.csproj -- (제거)

MyGame/
  LiveCode/LiveCode.csproj    -- (Scripts/Scripts.csproj로 변경)
  FrozenCode/FrozenCode.csproj -- (제거)
```

### 프로젝트 구조 (변경 후)

```
IronRose/
  src/
    IronRose.Scripting/
      ScriptDomain.cs         -- ALC 로드/언로드 (유지)
      StateManager.cs         -- 핫리로드 상태 관리 (유지)
      IHotReloadable.cs       -- 인터페이스 (유지)
      IronRose.Scripting.csproj -- Roslyn 패키지 제거됨
    IronRose.Engine/
      ScriptReloadManager.cs  -- (LiveCodeManager.cs에서 이름 변경 + 수정)
      EngineCore.cs           -- InitScripts() (InitFrozenCode + InitLiveCode 통합)
      ProjectContext.cs       -- ScriptsPath (LiveCodePath/FrozenCodePath 대체)
      RoseEngine/EngineConstants.cs -- ScriptsPath 상수
      Editor/ImGui/Panels/ImGuiScriptsPanel.cs -- Scripts 단일 트리
      Cli/CliCommandDispatcher.cs -- Scripts 어셈블리 검색
    IronRose.Standalone/
      IronRose.Standalone.csproj -- Scripts ProjectReference
  templates/default/
    Scripts/Scripts.csproj

MyGame/
  Scripts/Scripts.csproj      -- (LiveCode + FrozenCode 통합)
```

### 현재 핫리로드 흐름

```
1. LiveCodeManager.Initialize()
   - ScriptCompiler 생성 + 수동 AddReference 5~6개
   - FindLiveCodeDirectories()
   - FileSystemWatcher 등록
   - CompileAllLiveCode() 초기 호출

2. CompileAllLiveCode()
   - Roslyn 인메모리 컴파일
   - ScriptDomain.LoadScripts(bytes) 또는 Reload(bytes) -- ALC 로드

3. OnLiveCodeChanged() -> ProcessReload() -> ExecuteReload()
   - 0.5초 trailing edge debounce
   - 플레이모드 중이면 리로드 보류 (_pendingReloadAfterPlayStop)
   - 플레이모드 종료 시 FlushPendingReload()
```

### 현재 FrozenCode 초기화 흐름 (EngineCore.cs 736~775행)

```
InitFrozenCode()
  - ProjectContext.FrozenCodePath에서 *.cs 수집
  - ScriptCompiler 생성 + 수동 AddReference
  - compiler.CompileFromFiles(csFiles, "FrozenCode") -- Roslyn 인메모리 컴파일
  - Assembly.Load(result.AssemblyBytes) -- Default ALC에 로드
  - 에디터 타입 캐시 무효화
```

### 주요 의존성

| 파일 | 현재 참조 |
|------|-----------|
| `LiveCodeManager.cs` | `using IronRose.Scripting;` -- ScriptCompiler, ScriptDomain, IHotReloadable |
| `EngineCore.cs` | `new Scripting.ScriptCompiler()`, `typeof(IronRose.Scripting.IHotReloadable)` |
| `ImGuiScriptsPanel.cs` | `_liveCodeRoot`, `_frozenCodeRoot` -- 별도 트리 표시 |
| `CliCommandDispatcher.cs` | "FrozenCode", "LiveCode" 어셈블리명으로 타입 검색 |
| `ProjectContext.cs` | `LiveCodePath`, `FrozenCodePath` 속성 |
| `EngineConstants.cs` | `LiveCodePath = "LiveCode"`, `FrozenCodePath = "FrozenCode"` 상수 |
| `IronRose.Standalone.csproj` | `<ProjectReference ... FrozenCode.csproj />` |
| `EditorPlayMode.cs` | `OnAfterStopPlayMode` 콜백 — LiveCodeManager가 구독 |
| `SceneManager.cs` | LiveCode 관련 참조 |

---

## 설계

### 개요

4단계(Phase)로 진행한다:

1. **Phase 1**: Scripts 통합 — 디렉토리/csproj/상수/경로 변경
2. **Phase 2**: ScriptReloadManager — LiveCodeManager를 이름 변경 + dotnet build 방식 전환
3. **Phase 3**: EngineCore 통합 — InitFrozenCode + InitLiveCode → InitScripts
4. **Phase 4**: 정리 — ScriptCompiler 삭제, Roslyn 패키지 제거, 문서 갱신

### 변경되지 않는 것

- **ScriptDomain** — `byte[]`에서 ALC 로드/언로드, 그대로 사용
- **MigrateEditorComponents** — 컴포넌트 마이그레이션 동일
- **IHotReloadable / StateManager** — 동일
- **IronRose.Scripting 프로젝트 자체** — 유지 (ScriptDomain, StateManager, IHotReloadable)
- **IronRose.Engine.csproj의 IronRose.Scripting ProjectReference** — 유지

### 변경되는 동작

- **Play mode 중 FileSystemWatcher 완전 중단** (현재: watcher는 켜두고 리로드만 보류)
- **Play mode 종료 시**: watcher 재활성화 + 변경 감지되면 일괄 빌드/리로드
- **`/digest` 워크플로우 제거** — LiveCode/FrozenCode 구분이 없으므로 불필요

---

### Phase 1: Scripts 통합 — 디렉토리/csproj/상수/경로 변경

#### 1-1. EngineConstants.cs — 상수 변경

**대상 파일**: `src/IronRose.Engine/RoseEngine/EngineConstants.cs`

변경 전:
```csharp
/// <summary>라이브 코드 폴더명.</summary>
public const string LiveCodePath = "LiveCode";

/// <summary>프로즌 코드 폴더명.</summary>
public const string FrozenCodePath = "FrozenCode";
```

변경 후:
```csharp
/// <summary>스크립트 폴더명.</summary>
public const string ScriptsPath = "Scripts";
```

#### 1-2. ProjectContext.cs — 경로 속성 변경

**대상 파일**: `src/IronRose.Engine/ProjectContext.cs`

변경 전:
```csharp
/// <summary>LiveCode/ 절대 경로.</summary>
public static string LiveCodePath => Path.Combine(ProjectRoot, "LiveCode");

/// <summary>FrozenCode/ 절대 경로.</summary>
public static string FrozenCodePath => Path.Combine(ProjectRoot, "FrozenCode");
```

변경 후:
```csharp
/// <summary>Scripts/ 절대 경로.</summary>
public static string ScriptsPath => Path.Combine(ProjectRoot, "Scripts");
```

#### 1-3. 템플릿 디렉토리 변경

- `templates/default/LiveCode/` → `templates/default/Scripts/`
- `templates/default/LiveCode/LiveCode.csproj` → `templates/default/Scripts/Scripts.csproj`
- `templates/default/FrozenCode/` → 삭제

**Scripts.csproj** (LiveCode.csproj 기반, 주석 변경):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- dotnet build 핫리로드: DLL은 bin/Debug/net10.0/Scripts.dll로 출력됨 -->
    <!-- 의존성 DLL 복사 불필요 (엔진이 이미 로드한 어셈블리를 ALC Resolving으로 참조) -->
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Contracts/IronRose.Contracts.csproj" />
  </ItemGroup>
</Project>
```

#### 1-4. IronRose.Standalone.csproj — ProjectReference 변경

**대상 파일**: `src/IronRose.Standalone/IronRose.Standalone.csproj`

변경 전:
```xml
<ProjectReference Include="..\..\FrozenCode\FrozenCode.csproj" />
```

변경 후:
```xml
<ProjectReference Include="..\..\Scripts\Scripts.csproj" />
```

#### 1-5. ImGuiScriptsPanel.cs — 단일 트리로 변경

**대상 파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs`

- `_liveCodeRoot` + `_frozenCodeRoot` → `_scriptsRoot` 단일 경로
- `_liveCodeTree` + `_frozenCodeTree` → `_scriptsTree` 단일 트리
- `FindRootDirectories()` — `ProjectContext.ScriptsPath` 하나만 사용
- 트리 렌더링도 단일 "Scripts" 노드로 변경

#### 1-6. CliCommandDispatcher.cs — 어셈블리 검색 통합

**대상 파일**: `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

`ResolveComponentType()` 변경:
- 현재: 1) RoseEngine 네임스페이스 → 2) "FrozenCode" 어셈블리 → 3) "LiveCode" 어셈블리
- 변경: 1) RoseEngine 네임스페이스 → 2) "Scripts" 어셈블리

#### 1-7. SceneManager.cs — LiveCode 참조 변경

**대상 파일**: `src/IronRose.Engine/RoseEngine/SceneManager.cs`

LiveCode 관련 참조를 Scripts로 변경 (로그 메시지, 주석 등).

#### 1-8. MyGame 디렉토리 구조 변경 (수동)

> **참고**: 이 단계는 코드 변경이 아닌 파일 이동이다. 기존 게임 프로젝트의 마이그레이션은 별도 수행.

- `MyGame/LiveCode/` + `MyGame/FrozenCode/` → `MyGame/Scripts/`
- `MyGame/LiveCode/LiveCode.csproj` → `MyGame/Scripts/Scripts.csproj`
- `MyGame/FrozenCode/FrozenCode.csproj` → 삭제
- `MyGame/FrozenCode/*.cs` → `MyGame/Scripts/`로 이동
- `MyGame/LiveCode/*.cs` 및 하위 디렉토리 → `MyGame/Scripts/`로 이동
- `MyGame/MyGame.sln` — LiveCode/FrozenCode 프로젝트 참조를 Scripts로 변경

---

### Phase 2: ScriptReloadManager — dotnet build 방식 전환

#### 2-1. 파일 이름 변경

`src/IronRose.Engine/LiveCodeManager.cs` → `src/IronRose.Engine/ScriptReloadManager.cs`

클래스명: `LiveCodeManager` → `ScriptReloadManager`

#### 2-2. 필드 변경

**제거할 필드**:
```csharp
private ScriptCompiler? _compiler;                    // 제거
private readonly List<string> _liveCodePaths = new(); // 제거
```

**추가할 필드**:
```csharp
private string? _scriptsCsprojPath;  // Scripts.csproj 절대 경로
private string? _scriptsDllPath;     // bin/Debug/net10.0/Scripts.dll 절대 경로
```

**이름 변경**:
```csharp
// LiveCodeDemoTypes → ScriptDemoTypes
public Type[] ScriptDemoTypes { get; private set; } = Array.Empty<Type>();
```

#### 2-3. Initialize() 수정

- ScriptCompiler 생성 및 AddReference 코드 전부 제거
- `FindLiveCodeDirectories()` 호출 제거 (메서드 자체도 삭제)
- `ProjectContext.ScriptsPath` 사용
- csproj 경로: `Path.Combine(scriptsDir, "Scripts.csproj")`
- DLL 경로: `Path.Combine(scriptsDir, "bin", "Debug", "net10.0", "Scripts.dll")`
- FileSystemWatcher: `scriptsDir` 단일 경로 감시

#### 2-4. CompileAllLiveCode() → BuildScripts()

Roslyn 컴파일 → `dotnet build` 프로세스 실행:

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
        $"— assembly={assemblyBytes.Length}bytes, pdb={pdbBytes?.Length ?? 0}bytes", force: true);

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

#### 2-5. Play mode 동작 변경

**현재 동작**:
- Play mode 중 FileSystemWatcher는 계속 동작
- 파일 변경 감지 시 `_reloadRequested = true` 설정
- `ProcessReload()`에서 play mode 확인 → `_pendingReloadAfterPlayStop = true` 설정
- Play mode 종료 시 `FlushPendingReload()` 호출

**변경 후 동작**:
- Play mode 진입 시: FileSystemWatcher의 `EnableRaisingEvents = false`로 중단
- Play mode 종료 시: `EnableRaisingEvents = true`로 재활성화 + 파일 변경 여부 확인 + 변경되었으면 빌드/리로드

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

    // play mode 중 파일이 변경되었는지 확인 (DLL의 lastWrite vs 소스 파일의 lastWrite 비교)
    if (HasSourceChangedSinceBuild())
    {
        RoseEngine.EditorDebug.Log("[Scripting] Source changes detected during play mode — rebuilding", force: true);
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

기존 `_pendingReloadAfterPlayStop`, `FlushPendingReload()`, `ProcessReload()` 내의 play mode 체크 로직은 제거.

#### 2-6. ProcessReload() 단순화

Play mode 체크가 불필요해짐 (watcher 자체가 꺼지므로):

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

#### 2-7. RegisterLiveCodeBehaviours() → RegisterScriptBehaviours()

이름 변경. 내부 로직 동일. 로그 메시지의 "LiveCode"를 "Scripts"로 변경.

#### 2-8. FindLiveCodeDirectories() 삭제

메서드 전체 삭제. `ProjectContext.ScriptsPath` 단일 경로를 `Initialize()`에서 직접 사용.

---

### Phase 3: EngineCore 통합 — InitFrozenCode + InitLiveCode → InitScripts

#### 3-1. EngineCore.cs — InitFrozenCode() 삭제

`InitFrozenCode()` 메서드 전체 삭제. FrozenCode 관련 Roslyn 컴파일 코드 제거.

#### 3-2. EngineCore.cs — InitLiveCode() → InitScripts()

이름 변경. `LiveCodeManager` → `ScriptReloadManager` 참조 변경.

변경 전:
```csharp
private LiveCodeManager? _liveCodeManager;
private static LiveCodeManager? _staticLiveCodeManager;

public static Type[] LiveCodeDemoTypes
{
    get => _staticLiveCodeManager?.LiveCodeDemoTypes ?? Array.Empty<Type>();
}

private void InitLiveCode()
{
    _liveCodeManager = new LiveCodeManager();
    _liveCodeManager.Initialize();
    _staticLiveCodeManager = _liveCodeManager;
}
```

변경 후:
```csharp
private ScriptReloadManager? _scriptReloadManager;
private static ScriptReloadManager? _staticScriptReloadManager;

public static Type[] ScriptDemoTypes
{
    get => _staticScriptReloadManager?.ScriptDemoTypes ?? Array.Empty<Type>();
}

private void InitScripts()
{
    _scriptReloadManager = new ScriptReloadManager();
    _scriptReloadManager.Initialize();
    _staticScriptReloadManager = _scriptReloadManager;
}
```

호출부:
```csharp
// 변경 전
InitFrozenCode();
InitLiveCode();

// 변경 후
InitScripts();
```

#### 3-3. EngineCore.cs — Play mode 콜백 연결

`EditorPlayMode`에서 ScriptReloadManager의 `OnEnterPlayMode()` / `OnExitPlayMode()` 호출 연결.

#### 3-4. EngineCore.cs에서 기존 LiveCodeManager 참조 전부 변경

`_liveCodeManager` → `_scriptReloadManager` 등 모든 참조 업데이트.

#### 3-5. EditorPlayMode.cs — 콜백 연결

`OnAfterStopPlayMode` 콜백 또는 직접 호출로 `ScriptReloadManager.OnExitPlayMode()` 연결.
Play mode 진입 시 `ScriptReloadManager.OnEnterPlayMode()` 호출 추가.

---

### Phase 4: 정리 — ScriptCompiler 삭제, Roslyn 패키지 제거, 문서 갱신

#### 4-1. ScriptCompiler.cs 삭제

**대상 파일**: `src/IronRose.Scripting/ScriptCompiler.cs`

파일 전체 삭제. `ScriptCompiler` 클래스와 `CompilationResult` 클래스 포함.

#### 4-2. IronRose.Scripting.csproj에서 Roslyn 패키지 참조 제거

변경 전:
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.0.0" />
</ItemGroup>
```

변경 후: 해당 ItemGroup 전체 제거.

#### 4-3. doc/ScriptHotReloading.md 업데이트

핫리로드 흐름 설명 전면 재작성:
- LiveCode/FrozenCode 구분 제거 → Scripts 단일 구조
- Roslyn 런타임 컴파일 → dotnet build + DLL 교체
- Play mode 동작 변경 설명

```markdown
# IronRose 스크립트 핫 리로드

## Scripts 프로젝트 구조

```
IronRose.Engine  <---- Scripts (csproj 참조, 실행 시 직접 참조하지 않음)
IronRose.Contracts <-+
                       |
                  ScriptReloadManager -- dotnet build + DLL 로드 (FileSystemWatcher 감시)
                  Standalone -- Scripts (ProjectReference)
```

- **Scripts** -- 게임 스크립트 단일 프로젝트. `dotnet build`로 컴파일되며, 에디터에서는 ScriptReloadManager가 핫 리로드 수행.
- Standalone 빌드에서는 ProjectReference로 직접 참조.

## 스크립트 핫 리로드

```
1. Scripts/*.cs 수정
2. FileSystemWatcher 감지 (0.5초 trailing edge debounce)
3. dotnet build --no-restore Scripts.csproj
4. File.ReadAllBytes(Scripts.dll) -> ALC 로드
5. MigrateEditorComponents() -> 씬 컴포넌트 마이그레이션
```

## Play Mode 동작

- Play mode 진입 시: FileSystemWatcher 중단
- Play mode 종료 시: FileSystemWatcher 재활성화, 변경 감지 시 일괄 빌드/리로드
```

#### 4-4. /digest 스킬 제거 또는 수정

`.claude/commands/digest.md` — Scripts 통합으로 LiveCode→FrozenCode 이동이 불필요해지므로 제거하거나 목적 변경.

#### 4-5. CLAUDE.md 업데이트

LiveCode/FrozenCode 관련 용어를 Scripts로 변경.

---

## 수정 파일 총 목록

| 파일 | Phase | 변경 내용 |
|------|-------|-----------|
| `src/IronRose.Engine/RoseEngine/EngineConstants.cs` | 1 | LiveCodePath/FrozenCodePath → ScriptsPath |
| `src/IronRose.Engine/ProjectContext.cs` | 1 | LiveCodePath/FrozenCodePath → ScriptsPath |
| `templates/default/LiveCode/` → `templates/default/Scripts/` | 1 | 디렉토리+파일 이름 변경, csproj 수정 |
| `templates/default/FrozenCode/` | 1 | 삭제 |
| `src/IronRose.Standalone/IronRose.Standalone.csproj` | 1 | FrozenCode → Scripts ProjectReference |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs` | 1 | LiveCode/FrozenCode 이중 트리 → Scripts 단일 트리 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | 1 | FrozenCode/LiveCode 어셈블리 검색 → Scripts |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 1 | LiveCode 참조 변경 |
| `src/IronRose.Engine/LiveCodeManager.cs` → `ScriptReloadManager.cs` | 2 | 이름 변경 + Roslyn→dotnet build + play mode watcher 중단 |
| `src/IronRose.Engine/EngineCore.cs` | 3 | InitFrozenCode 삭제, InitLiveCode→InitScripts, 필드/타입 이름 변경 |
| `src/IronRose.Engine/Editor/EditorPlayMode.cs` | 3 | OnEnterPlayMode/OnExitPlayMode 콜백 연결 |
| `src/IronRose.Scripting/ScriptCompiler.cs` | 4 | 파일 삭제 |
| `src/IronRose.Scripting/IronRose.Scripting.csproj` | 4 | Microsoft.CodeAnalysis.CSharp 제거 |
| `doc/ScriptHotReloading.md` | 4 | 문서 재작성 |
| `.claude/commands/digest.md` | 4 | 제거 또는 수정 |
| `CLAUDE.md` | 4 | LiveCode/FrozenCode 용어 변경 |

### 게임 프로젝트 마이그레이션 (수동)

| 대상 | 작업 |
|------|------|
| `MyGame/LiveCode/` | `MyGame/Scripts/`로 이동, 하위 파일 모두 포함 |
| `MyGame/FrozenCode/*.cs` | `MyGame/Scripts/`로 이동 |
| `MyGame/LiveCode/LiveCode.csproj` | `MyGame/Scripts/Scripts.csproj`로 변경 |
| `MyGame/FrozenCode/FrozenCode.csproj` | 삭제 |
| `MyGame/MyGame.sln` | LiveCode/FrozenCode 프로젝트 참조를 Scripts로 변경 |

---

## 구현 체크리스트

### Phase 1: Scripts 통합

- [ ] 1-1. `EngineConstants.cs` — LiveCodePath/FrozenCodePath → ScriptsPath
- [ ] 1-2. `ProjectContext.cs` — LiveCodePath/FrozenCodePath → ScriptsPath
- [ ] 1-3. `templates/default/` — LiveCode/ → Scripts/, FrozenCode/ 삭제
- [ ] 1-4. `IronRose.Standalone.csproj` — FrozenCode → Scripts ProjectReference
- [ ] 1-5. `ImGuiScriptsPanel.cs` — 이중 트리 → 단일 Scripts 트리
- [ ] 1-6. `CliCommandDispatcher.cs` — FrozenCode/LiveCode → Scripts 어셈블리 검색
- [ ] 1-7. `SceneManager.cs` — LiveCode 참조 변경
- [ ] 1-8. `dotnet build` 확인

### Phase 2: ScriptReloadManager

- [ ] 2-1. `LiveCodeManager.cs` → `ScriptReloadManager.cs` 파일/클래스 이름 변경
- [ ] 2-2. 필드 변경 (ScriptCompiler 제거, csproj/DLL 경로 추가)
- [ ] 2-3. `Initialize()` — ScriptCompiler/AddReference 제거, dotnet build 경로 설정
- [ ] 2-4. `CompileAllLiveCode()` → `BuildScripts()` — dotnet build 방식
- [ ] 2-5. Play mode 동작 변경 (OnEnterPlayMode/OnExitPlayMode, HasSourceChangedSinceBuild)
- [ ] 2-6. `ProcessReload()` 단순화 (play mode 체크 제거)
- [ ] 2-7. `RegisterLiveCodeBehaviours()` → `RegisterScriptBehaviours()`
- [ ] 2-8. `FindLiveCodeDirectories()` 삭제
- [ ] 2-9. `dotnet build` 확인

### Phase 3: EngineCore 통합

- [ ] 3-1. `EngineCore.cs` — InitFrozenCode() 삭제
- [ ] 3-2. `EngineCore.cs` — InitLiveCode() → InitScripts(), 필드/타입 이름 변경
- [ ] 3-3. `EditorPlayMode.cs` — OnEnterPlayMode/OnExitPlayMode 콜백 연결
- [ ] 3-4. `EngineCore.cs` — 기존 LiveCodeManager 참조 전부 변경
- [ ] 3-5. `dotnet build` 확인

### Phase 4: 정리

- [ ] 4-1. `ScriptCompiler.cs` 파일 삭제
- [ ] 4-2. `IronRose.Scripting.csproj` — Microsoft.CodeAnalysis.CSharp 제거
- [ ] 4-3. `doc/ScriptHotReloading.md` 재작성
- [ ] 4-4. `.claude/commands/digest.md` 제거 또는 수정
- [ ] 4-5. `CLAUDE.md` 업데이트
- [ ] 4-6. `dotnet build` 확인 (전체 솔루션 빌드 통과)
- [ ] 4-7. 에디터 실행 후 전체 기능 검증

### 게임 프로젝트 마이그레이션

- [ ] M-1. `MyGame/LiveCode/` + `MyGame/FrozenCode/` → `MyGame/Scripts/` 이동
- [ ] M-2. `MyGame/Scripts/Scripts.csproj` 생성 (LiveCode.csproj 기반)
- [ ] M-3. `MyGame/MyGame.sln` 업데이트
- [ ] M-4. 이전 LiveCode.csproj, FrozenCode.csproj, bin/, obj/ 정리

---

## 미결 사항

없음. 모든 결정 사항이 확정되었다.
