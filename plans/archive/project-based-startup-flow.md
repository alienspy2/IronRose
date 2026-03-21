# 프로젝트 기반 시작 흐름 전환

## 배경

현재 IronRose 에디터는 "프로젝트 없는 상태"를 런타임에서 허용한다.
엔진이 시작되면 `ProjectContext.Initialize()`가 CWD에서 `project.toml`을 탐색하고,
없으면 `.rose_last_project`를 시도한 뒤, 그래도 없으면 `IsProjectLoaded = false` 상태로 진입한다.
이 상태에서 `ImGuiStartupPanel`이 표시되고, 사용자가 New/Open Project를 선택하면
`ProjectContext.Initialize(projectDir)`를 런타임 중에 재호출하여 프로젝트를 로드한다.

이 구조의 문제점:
1. **mid-session 프로젝트 전환의 불안정성**: 에셋, LiveCode, 셰이더 캐시, 렌더러 프로파일 등
   수많은 서브시스템이 `Initialize()` 시점에 한 번만 초기화되도록 설계되어 있어,
   런타임 중 프로젝트 경로가 바뀌면 불완전한 상태가 발생할 수 있다.
2. **모든 패널에 `IsProjectLoaded` 가드 분산**: 6개 이상의 패널이 `Draw()` 첫 줄에
   `if (!ProjectContext.IsProjectLoaded) return;`을 두고 있어 유지보수 부담이 있다.
3. **로그 경로 문제**: `RoseEngine.Debug`가 정적 생성자에서 CWD의 `logs/` 폴더를 사용하므로,
   프로젝트 폴더와 무관한 위치에 로그가 기록된다.
4. **설정 파일이 엔진 루트에 혼재**: 엔진 루트에 `.rose_last_project`, `rose_config.toml`,
   `rose_projectSettings.toml` 3개 파일이 있으나, 각각 유저 환경/엔진 설정/프로젝트 설정으로
   성격이 다르고 위치가 적절하지 않다.

## 목표

1. **프로세스 = 프로젝트**: 하나의 프로세스 실행은 반드시 하나의 프로젝트에 바인딩된다.
   프로젝트 전환은 프로세스 재시작을 통해서만 이루어진다.
2. **"프로젝트 없는 상태"는 최초 실행/경로 유효하지 않을 때만**: 설정 파일이 없거나
   마지막 프로젝트 경로가 유효하지 않을 때만 StartupPanel을 표시한다.
3. **로그 경로를 프로젝트 폴더로 통일**: `{ProjectRoot}/Logs/`에 로그를 기록한다.
4. **mid-session 프로젝트 전환 코드를 제거**하여 코드 단순화.
5. **설정 파일 정리**: 엔진 루트의 3개 파일을 용도에 맞게 재배치/통합.

## 현재 상태

### 시작 흐름
```
EngineCore.Initialize()
  -> ProjectContext.Initialize()
       CWD에서 project.toml 탐색
       없으면 .rose_last_project 읽기 시도
       없으면 IsProjectLoaded = false
  -> if (IsProjectLoaded) { InitAssets(); InitLiveCode(); InitGpuCompressor(); }
  -> InitEditor()
       ImGuiOverlay.Initialize() -> _startupPanel = new ImGuiStartupPanel()

ImGuiOverlay.Update()
  -> if (!IsProjectLoaded) { _startupPanel.Draw(); return; }
  -> 정상 에디터 렌더링

EngineCore.Update()
  -> if (IsProjectLoaded && ProjectRoot != _loadedProjectRoot)
       // mid-session 프로젝트 전환 감지 -> InitAssets() 재호출
```

### 관련 파일 목록
| 파일 | 역할 |
|------|------|
| `src/IronRose.Contracts/Debug.cs` | 로그 시스템. 정적 생성자에서 `logs/` 경로 초기화 |
| `src/IronRose.Engine/ProjectContext.cs` | 프로젝트 경로 관리, `.rose_last_project` 읽기/쓰기 |
| `src/IronRose.Engine/EngineCore.cs` | 엔진 메인 루프, 서브시스템 초기화 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | 에디터 오버레이, `IsProjectLoaded` 가드 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiStartupPanel.cs` | 시작 화면, New/Open Project |
| `src/IronRose.Engine/Editor/ProjectCreator.cs` | 프로젝트 생성 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` | `IsProjectLoaded` 가드 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | `IsProjectLoaded` 가드 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs` | `IsProjectLoaded` 가드 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs` | `IsProjectLoaded` 가드 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs` | `IsProjectLoaded` 가드 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs` | `IsProjectLoaded` 가드 |

### 설정 파일 현황

엔진 루트에 3개의 설정 파일이 혼재:

| 파일 | 내용 | 성격 | 현재 위치 |
|------|------|------|-----------|
| `.rose_last_project` | 마지막 프로젝트 절대 경로 | 유저 로컬 환경 (git 추적 X) | 엔진 CWD |
| `rose_config.toml` | 캐시/압축 on/off (`[cache]`) | 개발 편의 플래그 | 엔진 루트 |
| `rose_projectSettings.toml` | 렌더러 프로파일, 시작 씬 (`[renderer]`, `[build]`) | 프로젝트 설정 (git 추적 O) | 엔진 루트 |

문제:
- `.rose_last_project`는 CWD 기반이라 실행 환경에 따라 위치가 달라진다.
- `rose_config.toml`의 `[cache]` 설정은 프로젝트별로 다를 수 있는데 엔진 루트에 있다.
- `rose_projectSettings.toml`은 프로젝트 설정인데 엔진 루트에 있다.

## 설계

### 개요

프로세스 시작 시 설정 파일에서 마지막 프로젝트 경로를 읽고, 유효하면 자동 로드 후 에디터 진입한다.
설정 파일이 없거나 경로가 유효하지 않으면 StartupPanel을 표시한다.
StartupPanel에서 New/Open Project 후에는 경로를 설정 파일에 저장하고
"프로젝트가 설정되었습니다. 에디터를 다시 실행해주세요." 안내 다이얼로그를 표시한 뒤 프로세스를 종료한다.
프로세스 자동 재실행은 하지 않는다 (F5 디버거 호환성).

### 상세 설계

#### Phase 1: 설정 파일 위치를 사용자 홈 디렉토리로 변경

**문제**: CWD 기반 `.rose_last_project`는 실행 환경에 따라 위치가 달라진다.
**해결**: 사용자 홈 디렉토리의 `.ironrose/` 폴더 아래에 설정 파일을 저장한다.

```
~/.ironrose/
  settings.toml          <- 마지막 프로젝트 경로 등 글로벌 설정
```

**`settings.toml` 형식**:
```toml
[editor]
last_project = "/home/user/git/MyGame"
```

**변경 파일**: `src/IronRose.Engine/ProjectContext.cs`

- `LastProjectFileName` 상수와 `ReadLastProjectPath()`, `SaveLastProjectPath()` 메서드를 변경
- 경로: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ironrose", "settings.toml")`
- Tomlyn으로 TOML 파싱/직렬화 (이미 ProjectContext에서 Tomlyn 사용 중)
- **하위 호환**: 마이그레이션 시 기존 CWD의 `.rose_last_project` 파일이 있으면 읽어서 `settings.toml`에 저장 후 기존 파일 삭제

```csharp
// ProjectContext.cs 변경 사항

private static string GlobalSettingsDir =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ironrose");

private static string GlobalSettingsPath =>
    Path.Combine(GlobalSettingsDir, "settings.toml");

private static string? ReadLastProjectPath()
{
    // 1. ~/.ironrose/settings.toml에서 읽기
    var settingsPath = GlobalSettingsPath;
    if (File.Exists(settingsPath))
    {
        var table = Toml.ToModel(File.ReadAllText(settingsPath));
        if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editorTable)
        {
            if (editorTable.TryGetValue("last_project", out var pathVal) && pathVal is string pathStr)
            {
                if (!string.IsNullOrEmpty(pathStr) && File.Exists(Path.Combine(pathStr, "project.toml")))
                    return pathStr;
            }
        }
    }

    // 2. 하위 호환: CWD의 .rose_last_project 마이그레이션
    var legacyPath = Path.Combine(Directory.GetCurrentDirectory(), ".rose_last_project");
    if (File.Exists(legacyPath))
    {
        var path = File.ReadAllText(legacyPath).Trim();
        if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "project.toml")))
        {
            SaveLastProjectPath(path); // settings.toml에 저장
            try { File.Delete(legacyPath); } catch { }
            return path;
        }
    }

    return null;
}

public static void SaveLastProjectPath(string projectPath)
{
    try
    {
        Directory.CreateDirectory(GlobalSettingsDir);
        var content = $"[editor]\nlast_project = \"{Path.GetFullPath(projectPath).Replace("\\", "/")}\"\n";
        File.WriteAllText(GlobalSettingsPath, content);
        Debug.Log($"[ProjectContext] Saved last project to settings: {projectPath}");
    }
    catch (Exception ex)
    {
        Debug.LogWarning($"[ProjectContext] Failed to save settings: {ex.Message}");
    }
}
```

#### Phase 2: StartupPanel에서 "다시 실행" 흐름으로 전환

**현재**: `LoadProject()` -> `ProjectContext.Initialize(projectDir)` -> mid-session 전환
**변경**: `LoadProject()` -> 설정 파일에 저장 -> 안내 다이얼로그 -> 프로세스 종료

**변경 파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiStartupPanel.cs`

```csharp
// ImGuiStartupPanel.cs 변경 사항

private bool _showRestartNotice;
private string? _selectedProjectPath;

/// <summary>프로젝트 경로를 설정 파일에 저장하고 재시작 안내를 표시합니다.</summary>
private void SetProjectAndNotifyRestart(string projectDir)
{
    Debug.Log($"[StartupPanel] Project selected: {projectDir}");
    ProjectContext.SaveLastProjectPath(projectDir);
    _selectedProjectPath = projectDir;
    _showRestartNotice = true;
}

private void DrawRestartNoticeDialog()
{
    if (!_showRestartNotice) return;

    ImGui.OpenPopup("##RestartNotice");
    var center = ImGui.GetMainViewport().GetCenter();
    ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new Vector2(400, 160));

    if (ImGui.BeginPopupModal("##RestartNotice", ref _showRestartNotice,
        ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Project has been set. Please restart the editor.");
        ImGui.Spacing();
        ImGui.TextDisabled(_selectedProjectPath ?? "");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float buttonWidth = 120;
        ImGui.SetCursorPosX((400 - buttonWidth) * 0.5f);
        if (ImGui.Button("Exit", new Vector2(buttonWidth, 0)))
        {
            Environment.Exit(0);
        }
        ImGui.EndPopup();
    }
}
```

- `LoadProject()` 메서드를 `SetProjectAndNotifyRestart()`로 교체
- `Draw()` 끝에 `DrawRestartNoticeDialog()` 호출 추가
- Open Project의 `LoadProject(folder)` -> `SetProjectAndNotifyRestart(folder)`
- New Project의 `LoadProject(fullPath)` -> `SetProjectAndNotifyRestart(fullPath)`

#### Phase 3: mid-session 프로젝트 전환 코드 제거 및 `IsProjectLoaded` 가드 정리

프로세스 = 프로젝트이므로 mid-session 전환은 불가능하다.

**변경 파일**: `src/IronRose.Engine/EngineCore.cs`

- `_loadedProjectRoot` 필드 제거
- `Update()`에서 mid-session 프로젝트 전환 감지 코드 제거:
  ```csharp
  // 삭제할 코드 (EngineCore.Update() 165~171행)
  if (ProjectContext.IsProjectLoaded && ProjectContext.ProjectRoot != _loadedProjectRoot)
  {
      _loadedProjectRoot = ProjectContext.ProjectRoot;
      InitAssets();
      _warmupManager?.Start();
      Debug.Log($"[Engine] Project loaded mid-session: {ProjectContext.ProjectRoot}");
  }
  ```

**변경 파일**: 각 패널의 `IsProjectLoaded` 가드

각 패널의 `if (!ProjectContext.IsProjectLoaded) return;` 가드는 **Phase 3에서는 유지**한다.
이유: StartupPanel이 표시되는 동안(설정 파일 없음/유효하지 않음) 여전히 필요하기 때문이다.
프로젝트가 로드된 상태로 시작되면 이 가드는 항상 통과하므로 성능 영향은 없다.

단, `ImGuiOverlay.Update()`의 early-return 가드는 유지:
```csharp
// 유지 (ImGuiOverlay.cs 422~428행)
if (!ProjectContext.IsProjectLoaded)
{
    _startupPanel?.Draw();
    PopCurrentFont();
    return;
}
```

**File > New Project / Open Project 메뉴 변경** (`ImGuiOverlay.cs` 481~485행):

프로젝트가 이미 로드된 상태에서 File 메뉴의 New/Open Project를 클릭하면,
StartupPanel의 `SetProjectAndNotifyRestart()`를 호출하도록 변경한다.
이렇게 하면 프로젝트를 바꿀 때도 동일하게 "다시 실행해주세요" 안내가 나온다.

```csharp
// ImGuiOverlay.cs 메뉴 항목 (기존 코드 그대로 유지 가능)
// _startupPanel.ShowNewProjectDialog()와 _startupPanel.OpenExistingProject()는
// 내부적으로 SetProjectAndNotifyRestart()를 호출하므로 메뉴 코드 변경 불필요
```

#### Phase 4: 설정 파일 통합 및 재배치

**변경 전**:
```
엔진 루트/
  .rose_last_project          <- 유저 환경 (Phase 1에서 ~/.ironrose/settings.toml로 이동)
  rose_config.toml            <- [cache] 설정
  rose_projectSettings.toml   <- [renderer], [build] 설정
```

**변경 후**:
```
~/.ironrose/settings.toml     <- 유저 환경 (last_project)
{ProjectRoot}/rose_projectSettings.toml  <- 프로젝트 설정 ([renderer] + [build] + [cache] 통합)
```

- `rose_config.toml`의 `[cache]` 섹션을 `rose_projectSettings.toml`에 통합
- `RoseConfig.cs`에서 `[cache]` 읽기 로직을 `ProjectSettings.cs`로 이관
- `RoseConfig.cs` 삭제 (또는 `ProjectSettings`로 위임하는 래퍼로 유지)
- `rose_projectSettings.toml`은 프로젝트 폴더에 위치 (프로젝트 템플릿에 포함)

**통합 후 `rose_projectSettings.toml` 형식**:
```toml
[renderer]
active_profile_guid = "2ec4f1fe-2007-4cf0-80ee-d157511f0bdb"

[build]
start_scene = "Assets/Scenes/a.scene"

[cache]
dont_use_cache = false
dont_use_compress_texture = false
force_clear_cache = false
```

**변경 파일**:
- `src/IronRose.Engine/ProjectSettings.cs`: `[cache]` 섹션 읽기/쓰기 추가
- `src/IronRose.Engine/RoseConfig.cs`: `ProjectSettings`에서 값을 읽도록 변경 또는 삭제
- `src/IronRose.Engine/Editor/ProjectCreator.cs`: 프로젝트 생성 시 통합된 설정 파일 생성
- `templates/default/rose_projectSettings.toml`: `[cache]` 섹션 추가

#### Phase 5: 로그 경로를 프로젝트 폴더로 통일

**현재**: `Debug` 정적 생성자에서 `logs/` (CWD 기준 상대 경로) 사용.
**변경**: 초기에는 fallback 경로 사용, `ProjectContext.Initialize()` 이후 프로젝트 경로로 전환.

**변경 파일**: `src/IronRose.Contracts/Debug.cs`

```csharp
public static class Debug
{
    private static string _logPath;
    private static readonly object _lock = new();
    private static string _logFileName;

    public static bool Enabled { get; set; } = true;
    public static Action<LogEntry>? LogSink;

    static Debug()
    {
        // 초기 fallback: CWD/logs/ (ProjectContext 초기화 전)
        _logFileName = $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        Directory.CreateDirectory("logs");
        _logPath = Path.Combine("logs", _logFileName);
    }

    /// <summary>
    /// 로그 디렉토리를 변경합니다. 기존 로그 파일의 내용은 새 경로로 복사됩니다.
    /// ProjectContext.Initialize() 이후 호출하여 프로젝트 폴더로 전환합니다.
    /// </summary>
    public static void SetLogDirectory(string logDir)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(logDir);
            var newPath = Path.Combine(logDir, _logFileName);

            // 기존 fallback 로그 내용을 새 경로로 복사
            if (File.Exists(_logPath) && _logPath != newPath)
            {
                try
                {
                    var existingContent = File.ReadAllText(_logPath);
                    File.WriteAllText(newPath, existingContent);
                    // fallback 로그 파일 삭제 (선택적)
                    try { File.Delete(_logPath); } catch { }
                }
                catch { /* 복사 실패 시 새 파일부터 시작 */ }
            }

            _logPath = newPath;
        }
    }

    // Log(), LogWarning(), LogError(), Write() 메서드는 변경 없음
}
```

**변경 파일**: `src/IronRose.Engine/EngineCore.cs`

`Initialize()` 메서드에서 `ProjectContext.Initialize()` 직후 로그 경로 전환:

```csharp
public void Initialize(IWindow window)
{
    RoseEngine.Debug.LogSink = entry => EditorBridge.PushLog(entry);
    RoseEngine.Debug.Log("[Engine] EngineCore initializing...");

    ProjectContext.Initialize();

    // 프로젝트 로드 성공 시 로그 경로를 프로젝트 폴더로 전환
    if (ProjectContext.IsProjectLoaded)
    {
        var projectLogDir = Path.Combine(ProjectContext.ProjectRoot, "Logs");
        RoseEngine.Debug.SetLogDirectory(projectLogDir);
        RoseEngine.Debug.Log($"[Engine] Log directory switched to: {projectLogDir}");
    }

    RoseConfig.Load();
    // ... 이하 동일
}
```

**프로젝트 `.gitignore` 업데이트**: `templates/default/.gitignore`에 `Logs/` 추가.

### 영향 범위

#### 수정 파일 목록

| 파일 | 변경 내용 | Phase |
|------|-----------|-------|
| `src/IronRose.Contracts/Debug.cs` | `SetLogDirectory()` 메서드 추가, `_logPath` readonly 제거 | 5 |
| `src/IronRose.Engine/ProjectContext.cs` | 설정 파일 경로를 `~/.ironrose/settings.toml`로 변경, 레거시 마이그레이션 | 1 |
| `src/IronRose.Engine/EngineCore.cs` | 로그 경로 전환 호출 추가, mid-session 전환 코드 제거 | 3, 5 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiStartupPanel.cs` | `LoadProject()` -> `SetProjectAndNotifyRestart()`, 재시작 안내 다이얼로그 | 2 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | StartupPanel 재시작 다이얼로그 DrawCall 추가 (필요 시) | 2 |
| `src/IronRose.Engine/ProjectSettings.cs` | `[cache]` 섹션 읽기/쓰기 추가 | 4 |
| `src/IronRose.Engine/RoseConfig.cs` | `ProjectSettings`로 위임 또는 삭제 | 4 |
| `src/IronRose.Engine/Editor/ProjectCreator.cs` | 프로젝트 생성 시 통합된 설정 파일 생성 | 4 |
| `templates/default/rose_projectSettings.toml` | `[cache]` 섹션 추가 | 4 |
| `templates/default/.gitignore` | `Logs/` 항목 추가 | 5 |

#### 기존 기능에 미치는 영향

- **Standalone 빌드**: `HeadlessEditor = true`일 때 StartupPanel은 표시되지 않으며,
  `ProjectContext.Initialize()`는 CWD 또는 명시적 경로로 프로젝트를 로드한다.
  설정 파일 흐름은 에디터 전용이므로 Standalone에 영향 없음.
- **에셋 프로젝트에서 직접 실행**: `project.toml`이 CWD에 있으므로 설정 파일 없이도 정상 로드.
  설정 파일은 엔진 레포에서 직접 실행할 때(project.toml 없는 경우)만 참조됨.
- **기존 `.rose_last_project` 사용자**: Phase 1의 마이그레이션 코드가 자동 변환.

### 시퀀스 다이어그램 (변경 후)

```
[프로세스 시작]
     |
ProjectContext.Initialize()
     |
     +-- CWD에 project.toml 있음? --> Yes --> IsProjectLoaded = true
     |                                            |
     |                                     Debug.SetLogDirectory(ProjectRoot/Logs/)
     |                                            |
     |                                     InitAssets, InitLiveCode, ...
     |                                            |
     |                                     정상 에디터 진입
     |
     +-- No --> ~/.ironrose/settings.toml에서 last_project 읽기
                    |
                    +-- 유효한 경로 있음? --> Yes --> Initialize(lastProject)
                    |                                     |
                    |                              IsProjectLoaded = true
                    |                                     |
                    |                              (위와 동일 흐름)
                    |
                    +-- No --> IsProjectLoaded = false
                                    |
                              StartupPanel 표시
                                    |
                              [New Project]          [Open Project]
                                    |                       |
                              프로젝트 생성            폴더 선택
                                    |                       |
                                    +-------+-------+
                                            |
                                  SaveLastProjectPath()
                                            |
                                  "다시 실행해주세요" 다이얼로그
                                            |
                                    Environment.Exit(0)
```

## 구현 단계

- [ ] **Phase 1**: 설정 파일 위치를 `~/.ironrose/settings.toml`로 변경
  - [ ] `ProjectContext.cs`: `GlobalSettingsDir`, `GlobalSettingsPath` 프로퍼티 추가
  - [ ] `ProjectContext.cs`: `ReadLastProjectPath()` TOML 기반으로 재작성
  - [ ] `ProjectContext.cs`: `SaveLastProjectPath()` TOML 기반으로 재작성
  - [ ] `ProjectContext.cs`: 레거시 `.rose_last_project` 마이그레이션 로직 추가
  - [ ] `ProjectContext.cs`: `LastProjectFileName` 상수 제거 (또는 레거시용으로 유지)
- [ ] **Phase 2**: StartupPanel "다시 실행" 흐름
  - [ ] `ImGuiStartupPanel.cs`: `_showRestartNotice`, `_selectedProjectPath` 필드 추가
  - [ ] `ImGuiStartupPanel.cs`: `SetProjectAndNotifyRestart()` 메서드 추가
  - [ ] `ImGuiStartupPanel.cs`: `DrawRestartNoticeDialog()` 메서드 추가
  - [ ] `ImGuiStartupPanel.cs`: `LoadProject()` 호출부를 `SetProjectAndNotifyRestart()`로 교체
  - [ ] `ImGuiStartupPanel.cs`: `Draw()` 끝에 `DrawRestartNoticeDialog()` 호출 추가
  - [ ] `ImGuiStartupPanel.cs`: 기존 `LoadProject()` 메서드 삭제
- [ ] **Phase 3**: mid-session 프로젝트 전환 코드 제거
  - [ ] `EngineCore.cs`: `_loadedProjectRoot` 필드 제거
  - [ ] `EngineCore.cs`: `Update()` 내 프로젝트 전환 감지 블록 (165~171행) 삭제
  - [ ] `EngineCore.cs`: `Initialize()` 내 `_loadedProjectRoot` 할당 (158행) 삭제
- [ ] **Phase 4**: 설정 파일 통합 및 재배치
  - [ ] `ProjectSettings.cs`: `[cache]` 섹션 (dont_use_cache, dont_use_compress_texture, force_clear_cache) 읽기/쓰기 추가
  - [ ] `RoseConfig.cs`: `ProjectSettings`에서 값을 읽도록 변경 또는 클래스 삭제
  - [ ] `ProjectCreator.cs`: 프로젝트 생성 시 통합된 `rose_projectSettings.toml` 생성
  - [ ] `templates/default/rose_projectSettings.toml`: `[cache]` 섹션 추가
  - [ ] 엔진 루트의 `rose_config.toml` 삭제
- [ ] **Phase 5**: 로그 경로를 프로젝트 폴더로 통일
  - [ ] `Debug.cs`: `_logPath`에서 `readonly` 제거
  - [ ] `Debug.cs`: `_logFileName` 필드 추가 (정적 생성자에서 파일명만 저장)
  - [ ] `Debug.cs`: `SetLogDirectory(string logDir)` 정적 메서드 추가
  - [ ] `EngineCore.cs`: `ProjectContext.Initialize()` 이후 `Debug.SetLogDirectory()` 호출 추가
  - [ ] `templates/default/.gitignore`: `Logs/` 항목 추가

## 대안 검토

### 프로세스 자동 재실행
프로젝트 선택 후 `Process.Start()`로 자동 재실행하는 방안을 검토했으나, 채택하지 않음.
- **이유**: F5 디버거로 실행 중일 때 자동 재실행하면 디버거가 분리되어 디버깅이 불가능해진다.
- 사용자가 수동으로 재실행하는 것이 디버거 호환성과 예측 가능성 면에서 우수하다.

### mid-session 프로젝트 전환 유지
현재의 런타임 중 `ProjectContext.Initialize()` 재호출 방식을 유지하는 방안.
- **이유 (미채택)**: 에셋 DB, LiveCode, 셰이더 캐시, GPU compressor, 렌더러 프로파일 등
  너무 많은 서브시스템의 cleanup/재초기화가 필요하며, 누락 시 메모리 누수나 상태 불일치가 발생한다.
  프로세스 재시작이 가장 깔끔한 해결책이다.

### 로그 파일 분리 (엔진/에디터/스크립트)
각 서브시스템별로 별도 로그 파일을 두는 방안.
- **이유 (미채택)**: 요구사항에 명시적으로 "한 파일"로 지정됨. 타임스탬프로 충분히 구분 가능.

## 미결 사항

없음. 요구사항이 명확하게 정의되어 있다.
