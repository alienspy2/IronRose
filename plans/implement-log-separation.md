# EditorDebug / Debug 클래스 분리 구현 계획

## 배경

현재 `Debug` 클래스(`IronRose.Contracts/Debug.cs`)가 단일 로그 경로를 사용하여 에디터/엔진 내부 로그와 게임 런타임 로그가 하나의 파일에 섞여 있다. 설계 문서 `plans/future_log-separation.md`에 따라 클래스 수준에서 로그를 분리한다.

## 목표

1. **`EditorDebug`** (신규): 엔진/에디터 내부 로그 전용. `{EngineRoot}/Logs/`에 기록. 경로 고정.
2. **`Debug`** (역할 재정의): 게임 런타임/유저 스크립트 로그 전용. `{ProjectRoot}/Logs/`에 기록. 프로젝트 미로드 시 `EditorDebug`로 폴백.
3. `LogEntry`에 `LogSource` 필드 추가하여 Console 패널에서 필터링 가능.
4. phase43 #12(static 생성자 CWD 의존), #14(로그 디렉토리 전환 잔존) 이슈 동시 해소.

## 현재 상태

### Debug.cs (IronRose.Contracts)
- 정적 생성자에서 `CWD/Logs/` 생성 시도, 실패 시 임시 디렉토리 폴백
- `SetLogDirectory(string)`로 프로젝트 로드 후 `{ProjectRoot}/Logs/`로 전환 (기존 로그 복사)
- `LogSink: Action<LogEntry>?` delegate로 EditorBridge 연동
- `Write()` 내부에서 `_lock`으로 파일 접근 동기화

### LogEntry (IronRose.Contracts/LogTypes.cs)
```csharp
public enum LogLevel { Info, Warning, Error }
public record LogEntry(LogLevel Level, string Message, DateTime Timestamp);
```

### EngineCore.cs 초기화 흐름
1. `Debug.LogSink = entry => EditorBridge.PushLog(entry);`
2. `Debug.Log("[Engine] EngineCore initializing...");`
3. `ProjectContext.Initialize();`
4. 프로젝트 로드 시 `Debug.SetLogDirectory(projectLogDir);`

### 프로젝트 참조 관계
```
IronRose.Contracts (기반, 외부 의존 없음)
  <- IronRose.Rendering
  <- IronRose.Scripting
  <- IronRose.Physics
  <- IronRose.Engine (+ Rendering, Physics, Scripting 참조)
  <- IronRose.Standalone (+ Engine 참조)
```

모든 모듈이 `IronRose.Contracts`를 참조하므로 `EditorDebug`를 여기에 배치하면 전체 모듈에서 접근 가능.

### 스크린샷 경로
- 디버그 캡처: `Path.Combine("Logs", ...)` (상대경로, CWD 기준) -- EngineCore.cs:316

## 설계

### 개요

3개 Phase로 나누어 각 Phase가 독립적으로 빌드 가능하도록 진행한다.

- **Phase 1**: `EditorDebug` 클래스 신규 생성 + `LogEntry` 확장 + `Debug` 폴백 로직
- **Phase 2**: `EngineCore` 초기화 코드 정리 + 스크린샷 경로 수정
- **Phase 3**: 모듈별 `Debug.Log` -> `EditorDebug.Log` 마이그레이션

---

## Phase 1: EditorDebug 클래스 + LogEntry 확장 + Debug 폴백

### 1-1. LogEntry에 LogSource 추가

**파일**: `src/IronRose.Contracts/LogTypes.cs`

```csharp
namespace RoseEngine
{
    public enum LogLevel { Info, Warning, Error }
    public enum LogSource { Editor, Project }

    public record LogEntry(LogLevel Level, LogSource Source, string Message, DateTime Timestamp);
}
```

> **호환성 주의**: `LogEntry` 생성자 시그니처가 변경되므로, 기존에 `new LogEntry(logLevel, message, timestamp)` 형태로 호출하는 곳을 모두 업데이트해야 한다.

**영향 받는 파일** (LogEntry 생성 위치):
- `src/IronRose.Contracts/Debug.cs` (line 106)

**영향 받는 파일** (LogEntry 사용 위치 - 읽기만):
- `src/IronRose.Engine/Editor/EditorBridge.cs` -- 타입 변경 없음, 통과
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiConsolePanel.cs` -- 나중에 필터 UI 추가 가능하지만 Phase 1에서는 변경 불필요

### 1-2. EditorDebug 클래스 신규 생성

**파일**: `src/IronRose.Contracts/EditorDebug.cs` (신규)

```csharp
// ------------------------------------------------------------
// @file    EditorDebug.cs
// @brief   에디터/엔진 내부 전용 로그 시스템. {EngineRoot}/Logs/에 기록.
//          Initialize()로 엔진 시작 시 로그 디렉토리를 설정한다.
//          Initialize() 이전 호출은 CWD/Logs/ 또는 임시 디렉토리에 버퍼링된다.
// @deps    (없음 -- Contracts 레이어, 외부 의존 없음)
// @exports
//   static class EditorDebug
//     Enabled: bool                                     -- 로그 출력 활성화 여부
//     LogSink: Action<LogEntry>?                        -- 외부 로그 수신 delegate
//     Initialize(string engineRoot): void               -- 로그 디렉토리 설정
//     Log(object): void                                 -- INFO 레벨 로그
//     LogWarning(object): void                          -- WARNING 레벨 로그
//     LogError(object): void                            -- ERROR 레벨 로그
// @note    Initialize() 호출 시 기존 버퍼 로그를 새 경로로 복사 후 원본 삭제.
//          Write()에서 _lock으로 파일 접근 동기화. IOException 발생 시 무시 (콘솔 출력은 완료).
// ------------------------------------------------------------
using System;
using System.IO;

namespace RoseEngine
{
    public static class EditorDebug
    {
        private static string _logPath;
        private static readonly object _lock = new();
        private static readonly string _logFileName;
        private static bool _initialized;

        /// <summary>로그 출력 활성화 여부 (기본 true)</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>에디터 Console 패널 등 외부 로그 수신용 delegate</summary>
        public static Action<LogEntry>? LogSink;

        static EditorDebug()
        {
            _logFileName = $"editor_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            try
            {
                Directory.CreateDirectory("Logs");
                _logPath = Path.Combine("Logs", _logFileName);
            }
            catch
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "IronRose", "Logs");
                try { Directory.CreateDirectory(tempDir); } catch { }
                _logPath = Path.Combine(tempDir, _logFileName);
            }
        }

        /// <summary>
        /// 엔진 루트가 확정된 후 호출. 로그 디렉토리를 {engineRoot}/Logs/로 이동한다.
        /// 기존 버퍼 로그를 새 경로로 복사 후 원본 삭제.
        /// </summary>
        public static void Initialize(string engineRoot)
        {
            lock (_lock)
            {
                var logDir = Path.Combine(engineRoot, "Logs");
                Directory.CreateDirectory(logDir);
                var newPath = Path.Combine(logDir, _logFileName);

                if (File.Exists(_logPath) && _logPath != newPath)
                {
                    try
                    {
                        var existingContent = File.ReadAllText(_logPath);
                        File.WriteAllText(newPath, existingContent);
                        try { File.Delete(_logPath); } catch { }
                    }
                    catch { }
                }

                _logPath = newPath;
                _initialized = true;
            }
        }

        /// <summary>EditorDebug가 초기화되었는지 여부.</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>현재 로그 파일의 디렉토리 경로.</summary>
        public static string LogDirectory => Path.GetDirectoryName(_logPath) ?? "";

        public static void Log(object message) => Write("LOG", message);
        public static void LogWarning(object message) => Write("WARNING", message);
        public static void LogError(object message) => Write("ERROR", message);

        private static void Write(string level, object message)
        {
            if (!Enabled) return;

            var line = $"[{level}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
                }
                catch (IOException)
                {
                    // 디스크 풀, 권한 문제 등 -- 콘솔 출력은 이미 완료되었으므로 무시
                }
            }

            var logLevel = level switch
            {
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            LogSink?.Invoke(new LogEntry(logLevel, LogSource.Editor, message?.ToString() ?? "null", DateTime.Now));
        }
    }
}
```

### 1-3. Debug 클래스에 폴백 로직 추가

**파일**: `src/IronRose.Contracts/Debug.cs`

변경 사항:
1. 정적 생성자의 CWD 기반 초기화를 유지하되, `_projectActive` 플래그 추가
2. `_projectActive == false`일 때 `Debug.Log()` 호출은 `EditorDebug`로 위임
3. `SetLogDirectory()` 호출 시 `_projectActive = true`로 전환
4. `LogEntry` 생성 시 `LogSource.Project` 전달

```csharp
// Debug.cs 핵심 변경 부분

public static class Debug
{
    private static string _logPath;
    private static readonly object _lock = new();
    private static string _logFileName;
    private static bool _projectActive;  // 추가: SetLogDirectory() 호출 후 true

    public static bool Enabled { get; set; } = true;
    public static Action<LogEntry>? LogSink;

    static Debug()
    {
        _logFileName = $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        // 초기에는 로그 파일을 생성하지 않음 -- SetLogDirectory() 전까지 EditorDebug로 폴백
        _logPath = "";
        _projectActive = false;
    }

    public static void SetLogDirectory(string logDir)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, _logFileName);
            _projectActive = true;
        }
    }

    public static void Log(object message) => Write("LOG", message);
    public static void LogWarning(object message) => Write("WARNING", message);
    public static void LogError(object message) => Write("ERROR", message);

    private static void Write(string level, object message)
    {
        if (!Enabled) return;

        // 프로젝트 미로드 시 EditorDebug로 폴백
        if (!_projectActive)
        {
            switch (level)
            {
                case "WARNING": EditorDebug.LogWarning(message); return;
                case "ERROR": EditorDebug.LogError(message); return;
                default: EditorDebug.Log(message); return;
            }
        }

        var line = $"[{level}] {message}";
        Console.WriteLine(line);

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch (IOException) { }
        }

        var logLevel = level switch
        {
            "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info,
        };
        LogSink?.Invoke(new LogEntry(logLevel, LogSource.Project, message?.ToString() ?? "null", DateTime.Now));
    }
}
```

**핵심 변경점**:
- 정적 생성자에서 더 이상 `CWD/Logs/` 디렉토리를 생성하지 않음 (phase43 #12 해소)
- `SetLogDirectory()` 호출 전까지 `EditorDebug`로 폴백 (phase43 #14 해소)
- `SetLogDirectory()` 호출 시 기존 로그 복사 로직 제거 (복사할 파일이 없음)

### 1-4. Phase 1 수정 파일 목록

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Contracts/LogTypes.cs` | `LogSource` enum 추가, `LogEntry` record에 `Source` 파라미터 추가 |
| `src/IronRose.Contracts/EditorDebug.cs` | **신규 생성** |
| `src/IronRose.Contracts/Debug.cs` | 정적 생성자 단순화, 폴백 로직 추가, `LogEntry`에 `LogSource.Project` 전달 |

### 1-5. Phase 1 빌드 확인

`dotnet build`로 전체 솔루션 빌드 확인. `LogEntry` 시그니처 변경으로 인해 Phase 2 파일을 동시에 수정하지 않으면 빌드가 깨진다. 따라서 Phase 1에서는 `Debug.cs`의 `LogEntry` 생성도 함께 수정해야 한다.

---

## Phase 2: EngineCore 초기화 정리 + 스크린샷 경로 수정

### 2-1. EngineCore.Initialize() 변경

**파일**: `src/IronRose.Engine/EngineCore.cs`

`Initialize()` 메서드의 초기화 순서를 다음과 같이 변경:

```csharp
public void Initialize(IWindow window)
{
    // 1. EditorDebug + Debug 양쪽의 LogSink를 EditorBridge에 연결
    RoseEngine.EditorDebug.LogSink = entry => EditorBridge.PushLog(entry);
    RoseEngine.Debug.LogSink = entry => EditorBridge.PushLog(entry);

    // 2. 프로젝트 컨텍스트 초기화 (EngineRoot 확정)
    ProjectContext.Initialize();

    // 3. EditorDebug 초기화 -- EngineRoot 확정 후 즉시 호출
    RoseEngine.EditorDebug.Initialize(ProjectContext.EngineRoot);
    RoseEngine.EditorDebug.Log("[Engine] EngineCore initializing...");

    // 4. 프로젝트 로드 성공 시 Debug 로그 경로 설정
    if (ProjectContext.IsProjectLoaded)
    {
        var projectLogDir = Path.Combine(ProjectContext.ProjectRoot, "Logs");
        RoseEngine.Debug.SetLogDirectory(projectLogDir);
        RoseEngine.EditorDebug.Log($"[Engine] Project log directory: {projectLogDir}");

        // Reimport All sentinel 확인
        var sentinelPath = Path.Combine(ProjectContext.ProjectRoot, ".reimport_all");
        if (File.Exists(sentinelPath))
        {
            File.Delete(sentinelPath);
            RoseEngine.EditorDebug.Log("[Engine] Reimport All requested -- will clear cache on startup");
            ProjectSettings.ForceClearCache = true;
        }
    }

    // ... 이하 기존 코드
}
```

### 2-2. EngineCore 스크린샷 경로 수정

**파일**: `src/IronRose.Engine/EngineCore.cs` (Render 메서드, line 316 부근)

변경 전:
```csharp
var filename = Path.Combine("Logs", $"screenshot_frame{_frameCount}_{timestamp}.png");
```

변경 후:
```csharp
var filename = Path.Combine(RoseEngine.EditorDebug.LogDirectory,
    $"screenshot_frame{_frameCount}_{timestamp}.png");
```

이렇게 하면 디버그 스크린샷이 항상 `{EngineRoot}/Logs/`에 저장된다.

### 2-3. EngineCore Shutdown 로그 변경

**파일**: `src/IronRose.Engine/EngineCore.cs`

```csharp
public void Shutdown()
{
    RoseEngine.EditorDebug.Log("[Engine] EngineCore shutting down...");
    // ... 기존 코드
}
```

### 2-4. Phase 2 수정 파일 목록

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/EngineCore.cs` | Initialize(): EditorDebug.Initialize() 호출 추가, LogSink 양분, 스크린샷 경로 절대 경로화, 엔진 내부 로그 EditorDebug 전환 |

---

## Phase 3: 모듈별 Debug.Log -> EditorDebug.Log 마이그레이션

설계 문서의 모듈별 사용 클래스 기준에 따라 마이그레이션한다.

### 마이그레이션 기준

| 모듈 | 사용 클래스 | 근거 |
|------|-------------|------|
| `IronRose.Engine` (에디터, UI, 에셋, 렌더시스템) | `EditorDebug` | 에디터 내부 동작 |
| `IronRose.Rendering` | `EditorDebug` | 렌더러/그래픽 시스템 |
| `IronRose.Scripting` -- 컴파일러 (`ScriptCompiler.cs`) | `EditorDebug` | 빌드 도구 |
| `IronRose.Scripting` -- `ScriptDomain.cs` (LoadScripts, Reload, Unload, Instantiate) | `EditorDebug` | 엔진 인프라 |
| `IronRose.Scripting` -- `ScriptDomain.Update()` 내부 에러 | `Debug` | 유저 스크립트 실행 중 발생 |
| `IronRose.Scripting` -- `StateManager.cs` | `EditorDebug` | 핫 리로드 인프라 |
| `IronRose.Physics` | `Debug` 유지 | 게임 런타임 (물리는 게임 로직과 밀접) |
| `IronRose.Standalone` | `Debug` 유지 | 빌드된 게임 실행 |
| `IronRose.Engine/RoseEngine/*` (SceneManager, Rigidbody 등) | 상황별 분리 (아래 상세) | |

### RoseEngine 네임스페이스 내 분류 기준

`IronRose.Engine/RoseEngine/` 하위 파일은 Unity 호환 API로서 유저 스크립트에서도 간접 호출된다. 분류 기준:

- **에디터/에셋 인프라 관련** (import, serialize, editor 기능) -> `EditorDebug`
- **런타임 행동 관련** (MonoBehaviour 콜백 에러, 물리 연동, 게임 로직) -> `Debug` 유지
  - `SceneManager.cs` -- MonoBehaviour 콜백(Start, Update, OnDestroy 등)의 exception 로그는 유저 스크립트 에러이므로 `Debug` 유지. 단, 진단 로그(`[Diag]`)는 `EditorDebug`로.
  - `Rigidbody.cs` -- 물리 등록 로그는 런타임이므로 `Debug` 유지
  - `Cursor.cs` -- 에디터 기능이므로 `EditorDebug`
  - `Texture2D.cs` -- 에셋 로드이므로 `EditorDebug`
  - `Cubemap.cs` -- 에셋 생성이므로 `EditorDebug`
  - `PrefabUtility.cs` -- 에디터 기능이므로 `EditorDebug`
  - `Animator.cs` -- 런타임 행동이므로 `Debug` 유지
  - `CharacterController.cs` -- 런타임 물리이므로 `Debug` 유지
  - UI 컴포넌트(`UIButton`, `UIToggle`, `UISlider`, `UIInputField`) -- 런타임 UI이므로 `Debug` 유지

### 3-1. IronRose.Rendering (EditorDebug로 전환)

**필요 변경**: `using RoseEngine;`은 이미 있으므로 `EditorDebug`에 바로 접근 가능.

| 파일 | 호출 수 | 변경 |
|------|---------|------|
| `GraphicsManager.cs` | 15 | `Debug.Log` -> `EditorDebug.Log` 등 |
| `GBuffer.cs` | 1 | `Debug.Log` -> `EditorDebug.Log` |
| `ShaderCompiler.cs` | 7 | `Debug.Log` -> `EditorDebug.Log` 등 |
| `PostProcessing/TonemapEffect.cs` | 2 | `Debug.Log` -> `EditorDebug.Log` |
| `PostProcessing/PostProcessStack.cs` | 1 | `Debug.Log` -> `EditorDebug.Log` |
| `PostProcessing/BloomEffect.cs` | 2 | `Debug.Log` -> `EditorDebug.Log` |

**총 28개 호출 변환**

### 3-2. IronRose.Scripting (부분 전환)

| 파일 | 호출 | 변경 |
|------|------|------|
| `ScriptCompiler.cs` | 17 | 전부 `EditorDebug` (컴파일러는 빌드 도구) |
| `StateManager.cs` | 4 | 전부 `EditorDebug` (핫 리로드 인프라) |
| `ScriptDomain.cs` | 15 (LoadScripts, Reload, Unload, Instantiate) | `EditorDebug` |
| `ScriptDomain.cs` | 1 (Update() 내 에러, line 150) | `Debug` 유지 |

**총 36개 호출 중 35개 변환, 1개 유지**

### 3-3. IronRose.Physics (Debug 유지)

| 파일 | 호출 수 | 변경 |
|------|---------|------|
| `PhysicsWorld3D.cs` | 14 | **변경 없음** (게임 런타임) |
| `PhysicsWorld2D.cs` | 1 | **변경 없음** (게임 런타임) |

### 3-4. IronRose.Standalone (Debug 유지)

| 파일 | 호출 수 | 변경 |
|------|---------|------|
| `Program.cs` | 10 | **변경 없음** (빌드된 게임 실행) |

### 3-5. IronRose.Engine -- 에디터/에셋/인프라 (EditorDebug로 전환)

| 파일 | 호출 수 | 변경 |
|------|---------|------|
| `EngineCore.cs` | 나머지 모든 `Debug.Log`/`RoseEngine.Debug.Log` | `EditorDebug` |
| `RenderSystem.cs` | 2 | `EditorDebug` |
| `RenderSystem.Lighting.cs` | 2 | `EditorDebug` |
| `ProjectSettings.cs` | 4 | `EditorDebug` |
| `RoseConfig.cs` | 4 | `EditorDebug` |
| `ShaderRegistry.cs` | 4 | `EditorDebug` |
| `PostProcessManager.cs` | 1 | `EditorDebug` |
| `ProjectContext.cs` | 10 | `EditorDebug` |
| `AssetWarmupManager.cs` | 5 | `EditorDebug` |
| `LiveCodeManager.cs` | 18 | `EditorDebug` |
| `Editor/EditorBridge.cs` | 1 | `EditorDebug` |
| `Editor/PrefabEditMode.cs` | 6 | `EditorDebug` |
| `Editor/SceneSerializer.cs` | ~30 | `EditorDebug` |
| `Editor/ProjectCreator.cs` | 5 | `EditorDebug` |
| `Editor/ThumbnailGenerator.cs` | 7 | `EditorDebug` |
| `Editor/EditorAssets.cs` | 4 | `EditorDebug` |
| `Editor/ImGui/ImGuiOverlay.cs` | ~12 | `EditorDebug` |
| `Editor/ImGui/ImGuiLayoutManager.cs` | 4 | `EditorDebug` |
| `Editor/ImGui/ImGuiRendererBackend.cs` | 10 | `EditorDebug` |
| `Editor/ImGui/ImGuiRenderTargetManager.cs` | 1 | `EditorDebug` |
| `Editor/ImGui/ImGuiPlatformBackend.cs` | ~18 | `EditorDebug` |
| `Editor/ImGui/SceneViewRenderTargetManager.cs` | 1 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiConsolePanel.cs` | 0 | 변경 없음 (LogEntry 표시만) |
| `Editor/ImGui/Panels/ImGuiSpriteEditorPanel.cs` | 1 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | 3 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiScriptsPanel.cs` | 16 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiProjectPanel.cs` | 3 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiTextureToolPanel.cs` | 4 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` | 2 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs` | 8 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiSceneEnvironmentPanel.cs` | 1 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiStartupPanel.cs` | 2 | `EditorDebug` |
| `Editor/ImGui/Panels/ImGuiAnimationEditorPanel.cs` | 3 | `EditorDebug` |
| `Editor/SceneView/EditorCamera.cs` | 7 | `EditorDebug` |
| `Editor/SceneView/GizmoCallbackRunner.cs` | 2 | `EditorDebug` |
| `AssetPipeline/AssetDatabase.cs` | ~30 | `EditorDebug` |
| `AssetPipeline/RoseCache.cs` | ~19 | `EditorDebug` |
| `AssetPipeline/GltfMeshImporter.cs` | 8 | `EditorDebug` |
| `AssetPipeline/MeshImporter.cs` | 9 | `EditorDebug` |
| `AssetPipeline/TextureImporter.cs` | 10 | `EditorDebug` |
| `AssetPipeline/FontImporter.cs` | 2 | `EditorDebug` |
| `AssetPipeline/PrefabImporter.cs` | 8 | `EditorDebug` |
| `AssetPipeline/MipMeshGenerator.cs` | 9 | `EditorDebug` |
| `AssetPipeline/AnimationClipImporter.cs` | 4 | `EditorDebug` |
| `AssetPipeline/GlbTextureExtractor.cs` | 1 | `EditorDebug` |
| `AssetPipeline/GpuTextureCompressor.cs` | 1 | `EditorDebug` |
| `AssetPipeline/RendererProfileImporter.cs` | 3 | `EditorDebug` |
| `AssetPipeline/PostProcessProfileImporter.cs` | 3 | `EditorDebug` |
| `Automation/TestCommandRunner.cs` | 14 | `EditorDebug` (테스트 자동화 인프라) |

### 3-6. IronRose.Engine/RoseEngine -- 런타임 (Debug 유지)

| 파일 | 호출 수 | 변경 |
|------|---------|------|
| `SceneManager.cs` -- MonoBehaviour 콜백 에러 (Awake, Start, Update, LateUpdate, FixedUpdate, OnEnable, OnDestroy) | ~10 | **Debug 유지** |
| `SceneManager.cs` -- `[Diag]` 진단 로그 | ~3 | `EditorDebug` |
| `Rigidbody.cs` | 4 | **Debug 유지** |
| `Animator.cs` | 4 | **Debug 유지** |
| `CharacterController.cs` | 1 | **Debug 유지** |
| `UI/UIButton.cs` | 1 | **Debug 유지** |
| `UI/UIToggle.cs` | 1 | **Debug 유지** |
| `UI/UISlider.cs` | 1 | **Debug 유지** |
| `UI/UIInputField.cs` | 2 | **Debug 유지** |

### 3-7. IronRose.Engine/RoseEngine -- 에셋 관련 (EditorDebug로 전환)

| 파일 | 호출 수 | 변경 |
|------|---------|------|
| `Texture2D.cs` | 8 | `EditorDebug` |
| `Cubemap.cs` | 1 | `EditorDebug` |
| `PrefabUtility.cs` | 6 | `EditorDebug` |
| `Cursor.cs` | 2 | `EditorDebug` |

### 3-8. 마이그레이션 방법

각 파일에서:
1. `Debug.Log(` -> `EditorDebug.Log(`
2. `Debug.LogWarning(` -> `EditorDebug.LogWarning(`
3. `Debug.LogError(` -> `EditorDebug.LogError(`
4. `RoseEngine.Debug.Log(` -> `RoseEngine.EditorDebug.Log(`
5. `RoseEngine.Debug.LogWarning(` -> `RoseEngine.EditorDebug.LogWarning(`
6. `RoseEngine.Debug.LogError(` -> `RoseEngine.EditorDebug.LogError(`

`using RoseEngine;`이 이미 있는 파일은 짧은 이름으로 호출 가능.
없는 파일은 `RoseEngine.EditorDebug.Log(...)` 정규화 이름 사용.

---

## 구현 단계 (체크리스트)

### Phase 1: EditorDebug + LogEntry + Debug 폴백
- [x] `src/IronRose.Contracts/LogTypes.cs` -- `LogSource` enum 추가, `LogEntry`에 `Source` 파라미터 추가
- [x] `src/IronRose.Contracts/EditorDebug.cs` -- 신규 생성 (UTF-8 BOM)
- [x] `src/IronRose.Contracts/Debug.cs` -- 정적 생성자 단순화, 폴백 로직, LogEntry Source 추가
- [x] `dotnet build` 확인

### Phase 2: EngineCore 초기화 정리
- [x] `src/IronRose.Engine/EngineCore.cs` -- Initialize(): EditorDebug/Debug LogSink 양분, EditorDebug.Initialize() 호출, 엔진 로그 EditorDebug 전환
- [x] `src/IronRose.Engine/EngineCore.cs` -- Render(): 스크린샷 경로 절대 경로화
- [x] `src/IronRose.Engine/EngineCore.cs` -- Shutdown(): EditorDebug 전환
- [x] `dotnet build` 확인

### Phase 3: 모듈별 마이그레이션
- [x] `src/IronRose.Rendering/` -- 6개 파일, 28개 호출 전환
- [x] `src/IronRose.Scripting/` -- 3개 파일, 35개 호출 전환 (ScriptDomain.Update 에러 1개 제외)
- [x] `src/IronRose.Engine/` -- 에디터/에셋 인프라 파일, ~250개 호출 전환
- [x] `src/IronRose.Engine/RoseEngine/` -- Texture2D, Cubemap, PrefabUtility, Cursor, SceneManager 진단 로그 전환
- [x] `dotnet build` 확인
- [x] 전체 빌드 + 실행 테스트

---

## 대안 검토

### 대안 1: 채널 enum 방식 (단일 클래스 + LogChannel)
- `Debug.Log(LogChannel.Editor, message)` 형태
- 장점: 클래스 하나로 관리
- 단점: 모든 호출부에 채널 파라미터 추가 필요, API 변경 범위가 더 큼
- **불채택 사유**: 유저 스크립트 API(`Debug.Log(message)`)를 변경하지 않으려면 별도 클래스가 더 깔끔

### 대안 2: 점진적 마이그레이션 없이 일괄 변환
- 모든 Phase를 한 번에 수행
- 장점: 빠른 완성
- 단점: 빌드 실패 시 원인 파악 어려움
- **불채택 사유**: Phase별 독립 빌드 가능성이 더 안전

## 미결 사항

- **Console 패널 LogSource 필터 UI**: Phase 3 이후 별도 작업으로, Console 패널에 "Editor/Project" 토글 필터를 추가할 수 있다. 현재 Phase에서는 `LogEntry`에 `Source` 필드만 추가하고 UI는 후속 작업.
