# Phase 43 Post-Task Bugfixes

> Phase43 커밋 범위 `03e05ae`~`55bb0b4` changeset review + static analysis 통합.
> 우선순위/의존성 순서로 정렬.

---

## 높음

### 1. ShaderRegistry 크래시 가능성 — IsProjectLoaded 가드 없음

**파일**: `ShaderRegistry.cs` (`ShaderRegistry.Initialize()`)

`EngineCore.Initialize()` 내에서 `ShaderRegistry.Initialize()`는 `IsProjectLoaded` 가드 **밖**에서 항상 호출된다.

```
InitShaderCache()                   ← ProjectContext.CachePath 사용
ShaderRegistry.Initialize()         ← 가드 없음, 항상 실행
InitRenderSystem()                  ← ShaderRegistry.Resolve() 호출
...
[IsProjectLoaded] InitAssets()      ← 가드 있음
[IsProjectLoaded] InitLiveCode()    ← 가드 있음
```

`IsProjectLoaded = false`일 때 `EngineRoot = ProjectRoot = CWD`이므로, CWD에 `Shaders/` 디렉토리가 없으면 `DirectoryNotFoundException`으로 **엔진 전체 크래시**.

```csharp
throw new DirectoryNotFoundException(
    "[ShaderRegistry] Shaders directory not found. ...");
```

**수정 방향**: IsProjectLoaded 가드 추가 또는 try-catch로 graceful 처리.

---

### 2. ImGuiScriptsPanel CWD 기반 탐색 — ProjectContext 미사용 (B9)

**파일**: `ImGuiScriptsPanel.cs:515~533` (`FindRootDirectories()`)

```csharp
string[] searchRoots = { ".", "..", "../.." };   // CWD 기준 휴리스틱
string liveCodeDir = Path.GetFullPath(Path.Combine(root, EngineDirectories.LiveCodePath));
```

`ProjectContext.LiveCodePath`를 쓰는 `LiveCodeManager.FindLiveCodeDirectories()`와 동일 목적이면서 **전혀 다른 탐색 로직**을 사용한다. CWD ≠ ProjectRoot 환경에서 두 컴포넌트가 서로 다른 LiveCode 디렉토리를 바라볼 수 있다.

**수정 방향**: `ProjectContext.LiveCodePath` / `ProjectContext.FrozenCodePath` 직접 사용으로 변경.

---

### 3. .reimport_all sentinel 경로 모순 — 생성/감지 경로 불일치

생성 측과 감지 측이 다른 경로를 사용한다:

| 위치 | 코드 | 경로 |
|------|------|------|
| `ImGuiOverlay.cs:1092` | 생성 | `ProjectContext.ProjectRoot + "/.reimport_all"` |
| `RoseEditor/Program.cs:46` | 감지 | `Directory.GetCurrentDirectory() + "/.reimport_all"` |
| `templates/default/Program.cs:46` | 감지 | `Directory.GetCurrentDirectory() + "/.reimport_all"` |

CWD ≠ ProjectRoot이면 sentinel을 감지하지 못해 **reimport 요청이 무시**된다.

**수정 방향**: 감지 측을 `ProjectContext.ProjectRoot` 기반으로 통일. 템플릿도 함께 업데이트.

---

## 중간

### 4. ImGuiProjectSettingsPanel 상대 경로 하드코딩 (B11)

**파일**: `ImGuiProjectSettingsPanel.cs:185`

```csharp
var scenesDir = Path.Combine("Assets", "Scenes");  // CWD 기준 상대!
```

CWD ≠ ProjectRoot 시 씬 목록이 비어있게 된다.

**수정 방향**: `Path.Combine(ProjectContext.AssetsPath, "Scenes")` 사용.

---

### 5. Standalone Program.cs Assets 경로 하드코딩

**파일**: `Standalone/Program.cs:56`

```csharp
scenePath = Path.GetFullPath(Path.Combine("Assets", "Scenes", "DefaultScene.scene"));
```

CWD ≠ ProjectRoot 시 폴백 씬 경로 찾기 실패.

**수정 방향**: `ProjectContext.AssetsPath` 기반으로 변경.

---

### 6. project.toml dead config — start_scene 불일치

```toml
# templates/default/project.toml
[build]
start_scene = "Assets/Scenes/DefaultScene.scene"    # ← 존재하지 않는 파일

# templates/default/rose_projectSettings.toml
start_scene = "Assets/Scenes/Sample.scene"           # ← 실제 존재
```

`ProjectSettings.Load()`는 `rose_projectSettings.toml`만 읽으므로 `project.toml`의 `start_scene`은 dead config. 혼동을 유발하고, 이 필드를 읽는 코드가 추가되면 버그가 된다.

**수정 방향**: `project.toml`의 `start_scene`을 실제 파일명(`Sample.scene`)으로 수정하거나 필드 제거.

---

### 7. SaveLastProjectPath 전체 덮어쓰기

**파일**: `ProjectContext.cs:202`

```csharp
var content = $"[editor]\nlast_project = \"{...}\"\n";
File.WriteAllText(GlobalSettingsPath, content);
```

`~/.ironrose/settings.toml` 전체를 `[editor]` 섹션만으로 덮어쓴다. 향후 다른 섹션이 추가되면 **기존 설정이 유실**된다.

**수정 방향**: read-modify-write 패턴으로 변경.

---

### 8. NativeFileDialog 타임아웃 후 좀비 프로세스

**파일**: `NativeFileDialog.cs` (`RunProcess()`)

```csharp
process.WaitForExit(30000);
// 타임아웃 시 process.Kill() 미호출
```

`WaitForExit(30000)` 타임아웃 이후 zenity/kdialog 프로세스를 종료하지 않는다. `finally` 블록에서 `_runningProcess = null`로 참조는 정리되지만, 프로세스 자체는 생존. `KillRunning()`은 이 시점에 이미 `_runningProcess == null`이므로 킬하지 못한다.

**수정 방향**: 타임아웃 시 `process.Kill()` 추가.

---

### 9. PrefabImporter / ScriptCompiler TOCTOU

`File.Exists()` 후 `File.ReadAllText()`를 catch 없이 호출하는 패턴:

| 파일 | 라인 | 위험도 |
|------|------|--------|
| `PrefabImporter.cs` | 134~135, 144~145 | 중간 — FileNotFoundException 미처리 |
| `ScriptCompiler.cs` | 125~134 | 중간 — CompileFromFile에서 예외 전파 |
| `ScriptCompiler.cs` | 59~60 (LINQ) | 중간 — Where 후 Select에서 삭제 시 예외 |

**수정 방향**: try-catch 래핑.

---

## 낮음

### 10. RoseConfig.Load() CWD 의존

`RoseConfig.Load()`는 `rose_config.toml`을 순수 CWD 상대 경로(`"rose_config.toml"`, `"../rose_config.toml"`, `"../../rose_config.toml"`)로 탐색한다. 같은 초기화 흐름에서 `ProjectSettings.Load()`와 `EditorState.Load()`는 `ProjectContext.ProjectRoot` 기반이므로 **탐색 기준이 일관되지 않음**. 기본값 폴백이 있어 크래시는 안 함.

**수정 방향**: `ProjectContext.ProjectRoot` 기반으로 변경.

---

### 11. Debug.cs IOException 미처리

**파일**: `Debug.cs:80`

```csharp
File.AppendAllText(_logPath, $"...");   // lock 내부, catch 없음
```

디스크 풀, 권한 문제 시 `IOException`이 호출자에게 전파된다. 로깅 메서드에서의 예외는 호출자 코드를 크래시시킬 수 있다.

**수정 방향**: try-catch 추가.

---

### 12. Debug static 생성자 실패 가능

**파일**: `Debug.cs:35~40`

```csharp
static Debug()
{
    Directory.CreateDirectory("Logs");   // CWD 기준, UnauthorizedAccessException 가능
    _logPath = Path.Combine("Logs", _logFileName);
}
```

static 생성자 실패는 `TypeInitializationException`을 유발하고, 이후 `Debug` 클래스를 사용하는 모든 코드가 실패한다. `ProjectContext.Initialize()` 이전에 호출되므로 CWD에 `Logs/`가 생성되며, 이후 `SetLogDirectory(ProjectRoot/Logs)`로 전환되면서 CWD에 빈 `Logs/` 폴더가 잔존할 수 있다.

**수정 방향**: try-catch 래핑.

---

### 13. EditorUtils.cs 폰트 경로 — 잘못된 에셋 소스 + CWD 의존

**파일**: `EditorUtils.cs:29`

```csharp
var fontPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
```

두 가지 문제:

1. **잘못된 에셋 소스**: `Assets/Fonts/`(프로젝트 에셋)에서 로드하지만, EditorUtils는 에디터 유틸리티이므로 `EditorAssets/Fonts/`(엔진 에셋)를 사용해야 함. `NotoSans_eng.ttf`는 `templates/default/Assets/Fonts/`에만 존재하고, `EditorAssets/Fonts/`에는 NotoSans.ttf, NotoSansKR.ttf, Roboto.ttf 등 다른 폰트가 있음.
2. **CWD 의존**: `Directory.GetCurrentDirectory()` 기반 경로.

catch fallback(`Font.CreateDefault`)이 있어 크래시는 안 함.

**수정 방향**: `EditorAssets/Fonts/`의 적절한 폰트로 변경하고, `ProjectContext.EditorAssetsPath` 기반 경로 사용.

---

### 14. NativeFileDialog initialDir fallback — CWD 기본값

**파일**: `NativeFileDialog.cs:157,174,188`

```csharp
initialDir ?? Directory.GetCurrentDirectory()
```

다이얼로그의 초기 디렉토리 제안용이므로 기능적 문제는 없으나, `ProjectContext.AssetsPath`나 `ProjectRoot`가 더 적절한 기본값이 될 수 있음.

**수정 방향**: `ProjectContext.ProjectRoot` 또는 `AssetsPath`를 기본값으로 사용.

---

### 15. IsProjectLoaded 패널별 자체 가드 미비

`ImGuiOverlay.cs:422~428`에서 `IsProjectLoaded == false`이면 `_startupPanel.Draw()`만 실행하고 즉시 return하므로 현재는 안전. 그러나 아래 패널들은 Draw()에 자체 가드가 없어, Overlay 구조가 변경되면 방어층이 없다.

| 패널 | 자체 가드 | 비고 |
|------|-----------|------|
| ImGuiProjectSettingsPanel | **X** | 내부 `RefreshSceneListIfNeeded()`에만 가드 |
| ImGuiGameViewPanel | **X** | |
| ImGuiSceneEnvironmentPanel | **X** | RenderSettings 접근 |
| ImGuiVariantTreePanel | **X** | PrefabVariantTree 접근 |
| ImGuiConsolePanel | **X** | 의도적 — 로그는 프로젝트 무관 |

**수정 방향**: ConsolePanel 제외, 나머지 4개 패널에 `if (!ProjectContext.IsProjectLoaded) return;` 가드 추가.

---

## 참고 (구조적 개선)

### 16. 설계 문서 미갱신 — EditorAssetsPath 소유권

설계 문서(`plans/editor-assets-repo-separation.md`)에서는 `EditorAssetsPath = ProjectRoot/EditorAssets`로 기술되어 있으나 실제 구현(`ProjectContext.cs:56`)은 `EngineRoot/EditorAssets`. 코드가 맞고 문서가 틀린 상태.

---

### 17. ProjectContext.Initialize() 재귀 구조 — 가독성 개선

```csharp
var lastProjectPath = ReadLastProjectPath();
if (lastProjectPath != null)
{
    Initialize(lastProjectPath);   // 재귀 호출
    if (IsProjectLoaded) return;
}
```

현재는 안전하나(ReadLastProjectPath가 File.Exists 검증), 코드 가독성과 방어적 설계 측면에서 재귀 대신 반복문이나 메서드 분리가 바람직.

---

### 18. AssetDatabase 필드 초기화자 암묵적 의존

**파일**: `AssetDatabase.cs:32`

```csharp
private readonly RoseCache _roseCache = new(ProjectContext.CachePath);
```

`new AssetDatabase()` 호출 시점에 `ProjectContext.CachePath`를 캡처. 현재 `InitAssets()`는 `IsProjectLoaded` 가드 내에 있어 안전하지만, 별도 컨텍스트에서 인스턴스화하면 `Path.Combine("", "RoseCache")` = `"RoseCache"` (CWD 상대)가 된다. 클래스 설계상 의존이 표면에 드러나지 않음.

---

### 19. ProjectContext — CWD 폴백 제거 및 EngineRoot/ProjectRoot 분리

현재 `project.toml` 미발견 시 `ProjectRoot = CWD`로 폴백되어, 프로젝트가 없는데도 `AssetsPath`, `CachePath` 등이 CWD 기준으로 유효한 것처럼 동작한다. 다수 항목(#1, #2 등)의 근본 원인.

**수정 방향**:

1. **EngineRoot/ProjectRoot 역할 분리**:
   - `EngineRoot` — 항상 해석. 엔진 자신의 위치 기반 (Shaders, EditorAssets 등에 필요)
   - `ProjectRoot` — `project.toml` 발견 시에만 설정. 미발견 시 `""` 유지, **CWD 폴백 금지**

2. **프로젝트 의존 프로퍼티 접근 가드**:
   - `IsProjectLoaded = false`일 때 `AssetsPath`, `CachePath`, `LiveCodePath`, `FrozenCodePath` 접근 시 `InvalidOperationException` throw
   - `EditorAssetsPath`는 `EngineRoot` 기반이므로 항상 사용 가능

3. **Initialize 전 접근 방어**:
   - `Initialize()` 호출 전에 프로퍼티 접근 시에도 `InvalidOperationException` throw

---

### 20. A2 TomlConfig 래퍼 구현

ProjectContext, EditorState, RoseConfig, ProjectSettings가 모두 Tomlyn 직접 사용 패턴. TOML 파싱 에러 핸들링이 각 파일에서 개별적으로 구현되어 있어 일관성 리스크.

**수정 방향**: 공통 TomlConfig 래퍼 클래스를 구현하여 TOML 로드/저장/에러 핸들링을 통일. 기존 사용처를 래퍼로 마이그레이션.

---
