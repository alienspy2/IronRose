# Phase 43 정적 분석 보고서

> 소스코드 현재 상태만 기준. 변경 히스토리 고려하지 않음.

---

## 1. 시스템 간 모순 / 설계 불일치

### 1.1 경로 조회 이중 체계

프로젝트 경로를 조회하는 두 가지 메커니즘이 공존하며, 일부 코드가 잘못된 쪽을 사용한다.

| 메커니즘 | 제공하는 것 | 의도 |
|----------|------------|------|
| `ProjectContext.*Path` | 절대 경로 | 모든 경로의 단일 진입점 |
| `EngineDirectories.*` | 폴더명 문자열 | 폴더명 상수만 제공, 경로 조합은 호출자 책임 |

**문제 코드**: `ImGuiScriptsPanel.FindRootDirectories()` (518~533)

```csharp
string[] searchRoots = { ".", "..", "../.." };   // CWD 기준 휴리스틱
string liveCodeDir = Path.GetFullPath(Path.Combine(root, EngineDirectories.LiveCodePath));
```

`ProjectContext.LiveCodePath`를 쓰는 `LiveCodeManager.FindLiveCodeDirectories()`와 동일 목적이면서 **전혀 다른 탐색 로직**을 사용한다. CWD ≠ ProjectRoot 환경에서 두 컴포넌트가 서로 다른 LiveCode 디렉토리를 바라볼 수 있다.

### 1.2 RoseConfig.Load() — ProjectContext 미사용

`RoseConfig.Load()`는 `rose_config.toml`을 순수 CWD 상대 경로(`"rose_config.toml"`, `"../rose_config.toml"`, `"../../rose_config.toml"`)로 탐색한다. 같은 초기화 흐름에서 `ProjectSettings.Load()`와 `EditorState.Load()`는 `ProjectContext.ProjectRoot` 기반이므로 **탐색 기준이 일관되지 않음**.

### 1.3 project.toml vs rose_projectSettings.toml — start_scene 불일치

```toml
# templates/default/project.toml
[build]
start_scene = "Assets/Scenes/DefaultScene.scene"    # ← 존재하지 않는 파일

# templates/default/rose_projectSettings.toml
start_scene = "Assets/Scenes/Sample.scene"           # ← 실제 존재
```

`ProjectSettings.Load()`는 `rose_projectSettings.toml`만 읽으므로 `project.toml`의 `start_scene`은 사실상 dead config이지만, 혼동을 유발하고 나중에 이 필드를 읽는 코드가 추가되면 버그가 된다.

### 1.4 .reimport_all sentinel 경로 모순

생성 측과 감지 측이 다른 경로를 사용한다:

| 위치 | 코드 | 경로 |
|------|------|------|
| `ImGuiOverlay.cs:1092` | 생성 | `ProjectContext.ProjectRoot + "/.reimport_all"` |
| `RoseEditor/Program.cs:46` | 감지 | `Directory.GetCurrentDirectory() + "/.reimport_all"` |
| `templates/default/Program.cs:46` | 감지 | `Directory.GetCurrentDirectory() + "/.reimport_all"` |

CWD ≠ ProjectRoot이면 sentinel을 감지하지 못해 reimport 요청이 무시된다.

### 1.5 EditorAssetsPath 소유권 — 코드와 문서 불일치

```csharp
// ProjectContext.cs:56 — 실제 구현
internal static string EditorAssetsPath => Path.Combine(EngineRoot, "EditorAssets");
```

설계 문서(`plans/editor-assets-repo-separation.md`)에서는 `ProjectRoot/EditorAssets`로 기술되어 있으나 실제 구현은 `EngineRoot/EditorAssets`. 코드가 맞고 문서가 틀린 것으로 보이나, 문서 미갱신 상태.

---

## 2. 경로 하드코딩 잔여 목록

`ProjectContext`를 사용하지 않고 CWD 기준 상대 경로를 직접 구성하는 코드:

| 파일 | 라인 | 하드코딩 내용 | 영향도 |
|------|------|-------------|--------|
| `ImGuiScriptsPanel.cs` | 515~533 | `".", "..", "../.."` + `EngineDirectories.LiveCodePath` | **높음** — CWD≠ProjectRoot 시 실패 |
| `ImGuiProjectSettingsPanel.cs` | 185 | `Path.Combine("Assets", "Scenes")` | **중간** — CWD≠ProjectRoot 시 씬 목록 비어있음 |
| `Standalone/Program.cs` | 56 | `Path.Combine("Assets", "Scenes", "DefaultScene.scene")` | **중간** — 폴백 씬 경로 찾기 실패 |
| `RoseEditor/Program.cs` | 46 | `Directory.GetCurrentDirectory() + ".reimport_all"` | **중간** — sentinel 불일치 (1.4 참조) |
| `EditorUtils.cs` | 29 | `Directory.GetCurrentDirectory() + "Assets/Fonts/NotoSans_eng.ttf"` | **낮음** — catch로 fallback 있음 |
| `RoseConfig.cs` | Load() | `"rose_config.toml"` CWD 기준 탐색 | **낮음** — 기본값 폴백 |

---

## 3. 초기화 순서 및 의존성 문제

### 3.1 EngineCore.Initialize() 초기화 순서

```
1.  ProjectContext.Initialize()         ← 경로 컨텍스트 확립
2.  Debug.SetLogDirectory(...)          ← IsProjectLoaded 시만
3.  RoseConfig.Load()                   ← CWD 기준 (ProjectContext 미사용)
4.  ProjectSettings.Load()              ← ProjectContext.ProjectRoot 기반
5.  EditorState.Load()                  ← ProjectContext.ProjectRoot 기반
6.  InitApplication()
7.  InitInput()
8.  InitGraphics()
9.  InitShaderCache()                   ← ProjectContext.CachePath 사용
10. ShaderRegistry.Initialize()         ← ProjectContext.EngineRoot/ProjectRoot 사용
11. InitRenderSystem()                  ← ShaderRegistry.Resolve() 호출 시작
12. InitScreen()
13. InitPluginApi()
14. InitPhysics()
15. [IsProjectLoaded] InitAssets()      ← new AssetDatabase() → ProjectContext.CachePath
16. [IsProjectLoaded] InitLiveCode()    ← ProjectContext.LiveCodePath, EngineRoot
17. [IsProjectLoaded] InitGpuCompressor()
18. [!HeadlessEditor] InitEditor()      ← ImGuiScriptsPanel 생성 (CWD 기반 탐색)
19. [IsProjectLoaded] _warmupManager.Start()
```

### 3.2 AssetDatabase 필드 초기화자 — 암묵적 의존

```csharp
// AssetDatabase.cs:32
private readonly RoseCache _roseCache = new(ProjectContext.CachePath);
```

`new AssetDatabase()`가 호출되는 순간 `ProjectContext.CachePath`를 캡처한다. 현재 `InitAssets()`는 `IsProjectLoaded` 가드 내에 있어 안전하지만, 클래스 설계상 이 의존이 표면에 드러나지 않는다. 별도 컨텍스트에서 인스턴스화하면 `Path.Combine("", "RoseCache")` = `"RoseCache"` (CWD 상대)가 된다.

### 3.3 Debug static 생성자 — ProjectContext 이전 실행

```csharp
// Debug.cs:35~40
static Debug()
{
    Directory.CreateDirectory("Logs");   // CWD 기준
    _logPath = Path.Combine("Logs", _logFileName);
}
```

`Debug.Log()`가 `ProjectContext.Initialize()` 이전에 호출되므로(EngineCore.cs:123), CWD에 `Logs/` 폴더가 생성된다. 이후 `SetLogDirectory(ProjectRoot/Logs)`로 전환되면서 초기 로그는 복사되지만, CWD에 빈 `Logs/` 폴더가 잔존할 수 있다.

### 3.4 ShaderRegistry — IsProjectLoaded 가드 없음

`ShaderRegistry.Initialize()`는 `IsProjectLoaded` 가드 밖에서 항상 호출된다. `IsProjectLoaded = false`일 때 `EngineRoot = ProjectRoot = CWD`이므로 CWD에 Shaders/가 있으면 동작하지만, 없으면 `DirectoryNotFoundException`으로 크래시.

---

## 4. ProjectContext.Initialize() 내부 구조 문제

### 4.1 재귀 호출

```csharp
// ProjectContext.cs:120~127
// project.toml이 없을 때:
var lastProjectPath = ReadLastProjectPath();
if (lastProjectPath != null)
{
    Initialize(lastProjectPath);   // 재귀
    if (IsProjectLoaded) return;
}
```

`ReadLastProjectPath()`가 `settings.toml`에서 경로를 읽고, 해당 경로에 `project.toml`이 있는지 `File.Exists`로 검증하므로 현재는 무한 루프가 발생하지 않는다. 그러나 코드 가독성과 방어적 설계 측면에서 재귀 대신 반복문이나 메서드 분리가 바람직하다.

### 4.2 SaveLastProjectPath() 전체 덮어쓰기

```csharp
// ProjectContext.cs:202
var content = $"[editor]\nlast_project = \"{...}\"\n";
File.WriteAllText(GlobalSettingsPath, content);
```

`~/.ironrose/settings.toml` 전체를 `[editor]` 섹션만으로 덮어쓴다. 향후 다른 섹션이 추가되면 기존 설정이 유실된다.

### 4.3 ProjectRoot = "" 상태의 파생 프로퍼티

`Initialize()` 호출 전 `ProjectRoot`는 `""`. `Path.Combine("", "Assets")`는 `"Assets"` (CWD 상대 경로)를 반환한다. 크래시는 아니지만 의도되지 않은 경로가 사용될 수 있다.

---

## 5. 스레드 안전성 / 동시성

### 5.1 NativeFileDialog — 30초 타임아웃 후 좀비

```csharp
process.WaitForExit(30000);
// 타임아웃 시 process.Kill() 미호출
```

`WaitForExit(30000)` 타임아웃 이후 zenity/kdialog 프로세스를 종료하지 않는다. `finally` 블록에서 `_runningProcess = null`로 참조는 정리되지만, 프로세스 자체는 생존. `KillRunning()`은 이 시점에 이미 `_runningProcess == null`이므로 킬하지 못한다.

### 5.2 ImGuiStartupPanel — volatile 필드 활용

```csharp
private volatile string? _pendingBrowsePath;
private volatile string? _pendingOpenProjectPath;
private volatile string? _pendingErrorFromOpen;
private volatile bool _waitingForDialog;
```

`Task.Run()` 백그라운드에서 설정하고 메인 스레드 `DrainPendingResults()`에서 소비하는 패턴. volatile로 가시성은 확보되나, 여러 필드를 원자적으로 업데이트하지는 않으므로 부분 읽기 가능성이 있다. 현재 코드에서는 각 필드가 독립적으로 사용되므로 실질적 문제는 없다.

---

## 6. File I/O 안전성

### 6.1 TOCTOU (Time-of-check to time-of-use)

`File.Exists()` 후 `File.ReadAllText()`를 catch 없이 호출하는 패턴:

| 파일 | 라인 | 위험도 |
|------|------|--------|
| `PrefabImporter.cs` | 134~135, 144~145 | 중간 — FileNotFoundException 미처리 |
| `ScriptCompiler.cs` | 125~134 | 중간 — CompileFromFile에서 예외 전파 |
| `ScriptCompiler.cs` | 59~60 (LINQ) | 중간 — Where 후 Select에서 삭제 시 예외 |

### 6.2 Debug.cs — IOException 미처리

```csharp
// Debug.cs:80
File.AppendAllText(_logPath, $"...");   // lock 내부, catch 없음
```

디스크 풀, 권한 문제 시 `IOException`이 호출자에게 전파된다. 로깅 메서드에서의 예외는 호출자 코드를 크래시시킬 수 있다.

### 6.3 Debug static 생성자 — 치명적 실패 가능

```csharp
static Debug()
{
    Directory.CreateDirectory("Logs");   // UnauthorizedAccessException 가능
}
```

static 생성자 실패는 `TypeInitializationException`을 유발하고, 이후 `Debug` 클래스를 사용하는 모든 코드가 실패한다. 읽기 전용 파일시스템이나 권한 제한 환경에서 엔진 전체가 시작 불가.

---

## 7. IsProjectLoaded 가드 분석

### 7.1 Overlay 전역 가드

`ImGuiOverlay.cs:422~428`에서 `IsProjectLoaded == false`이면 `_startupPanel.Draw()`만 실행하고 즉시 return. 대부분의 패널 Draw()는 이 분기 이후에 호출되므로 Overlay 레벨에서 차단된다.

### 7.2 패널별 자체 가드 현황

| 패널 | Draw() 자체 가드 | 비고 |
|------|------------------|------|
| ImGuiHierarchyPanel | O | |
| ImGuiProjectPanel | O | |
| ImGuiScriptsPanel | O | |
| ImGuiSceneViewPanel | O | |
| ImGuiInspectorPanel | O | |
| ImGuiProjectSettingsPanel | **X** | 내부 `RefreshSceneListIfNeeded()`에만 가드 |
| ImGuiConsolePanel | **X** | 의도적 — 로그는 프로젝트 무관 |
| ImGuiGameViewPanel | **X** | |
| ImGuiSceneEnvironmentPanel | **X** | RenderSettings 접근 |
| ImGuiVariantTreePanel | **X** | PrefabVariantTree 접근 |

Overlay 전역 가드에 의존하므로 현재는 문제없지만, 패널이 단독으로 사용되거나 Overlay 구조가 변경되면 방어층이 없다.

---

## 8. 요약: 우선순위별 조치 항목

| 우선순위 | 항목 | 위치 | 설명 |
|----------|------|------|------|
| **높음** | ImGuiScriptsPanel CWD 기반 탐색 | ImGuiScriptsPanel.cs:515~533 | ProjectContext.LiveCodePath/FrozenCodePath 사용으로 변경 필요 |
| **높음** | .reimport_all sentinel 경로 모순 | ImGuiOverlay.cs:1092 vs RoseEditor/Program.cs:46 | 생성/감지 경로 통일 필요 |
| **높음** | ShaderRegistry 크래시 가능성 | ShaderRegistry.cs:67 | IsProjectLoaded 가드 or try-catch 추가 |
| **중간** | ImGuiProjectSettingsPanel 상대 경로 | ImGuiProjectSettingsPanel.cs:185 | ProjectContext.AssetsPath 사용 |
| **중간** | Standalone Program.cs 하드코딩 | Standalone/Program.cs:56 | ProjectContext.AssetsPath 사용 |
| **중간** | project.toml dead config (start_scene) | templates/default/project.toml | 제거하거나 실제 파일명으로 수정 |
| **중간** | SaveLastProjectPath 전체 덮어쓰기 | ProjectContext.cs:202 | read-modify-write 패턴으로 변경 |
| **중간** | NativeFileDialog 타임아웃 후 좀비 | NativeFileDialog.cs RunProcess() | 타임아웃 시 process.Kill() 추가 |
| **중간** | PrefabImporter/ScriptCompiler TOCTOU | PrefabImporter.cs:134, ScriptCompiler.cs:125 | try-catch 래핑 |
| **낮음** | RoseConfig.Load() CWD 의존 | RoseConfig.cs Load() | ProjectContext.ProjectRoot 기반으로 변경 |
| **낮음** | Debug.cs IOException 미처리 | Debug.cs:80 | try-catch 추가 |
| **낮음** | Debug static 생성자 실패 가능 | Debug.cs:38 | try-catch 래핑 |
| **낮음** | EditorUtils.cs 폰트 경로 | EditorUtils.cs:29 | catch fallback 있어 무해 |
| **낮음** | 로그 디렉토리 전환 시 CWD에 빈 Logs/ 잔존 | Debug.cs + EngineCore.cs:130 | 미관 문제 |
| **참고** | 설계 문서 미갱신 (EditorAssetsPath) | plans/editor-assets-repo-separation.md | 코드와 문서 불일치 |
| **참고** | AssetDatabase 필드 초기화자 암묵적 의존 | AssetDatabase.cs:32 | 현재는 안전, 구조적 리스크 |
| **참고** | ProjectContext.Initialize() 재귀 구조 | ProjectContext.cs:125 | 가독성 리스크 |
