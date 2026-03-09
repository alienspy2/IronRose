# Phase 11: Avalonia UI 에디터 — 별도 창 기반 씬 인스펙터

## Context

IronRose 엔진은 현재 콘솔 로그와 텍스트 HUD(DemoLauncher)만으로 디버깅합니다.
GameObject 계층 구조, 컴포넌트 프로퍼티, 성능 지표를 실시간으로 확인/수정할 수 있는 에디터 UI가 필요합니다.

**Avalonia UI 11.3+** 를 사용하여 엔진 윈도우와 **별도 창**으로 에디터를 띄웁니다.
- Windows / Linux 크로스 플랫폼
- .NET 10.0 호환
- Silk.NET 윈도우와 독립된 스레드에서 실행

## 아키텍처 개요

```
┌─────────────────────────────────────────────────────┐
│                    Main Thread                       │
│  Silk.NET Window (_window.Run)                      │
│  ├─ EngineCore.Update()                             │
│  │   └─ EditorBridge.ProcessCommands()  ← 커맨드 소비│
│  └─ EngineCore.Render()                             │
│       └─ EditorBridge.PushSnapshot()    → 스냅샷 발행│
└─────────────────────────────────────────────────────┘
            ↕  ConcurrentQueue (lock-free)
┌─────────────────────────────────────────────────────┐
│                   Editor Thread                      │
│  Avalonia AppBuilder (별도 스레드)                    │
│  ├─ HierarchyPanel    — GameObject 트리뷰            │
│  ├─ InspectorPanel    — 컴포넌트/프로퍼티 편집         │
│  ├─ ConsolePanel      — Debug.Log 출력               │
│  └─ PerformancePanel  — FPS / 메모리 / 오브젝트 수    │
└─────────────────────────────────────────────────────┘
```

**핵심 원칙**: 에디터 스레드는 엔진 객체를 직접 접근하지 않습니다.
모든 데이터는 **스냅샷(읽기 전용 DTO)** 으로 전달하고, 수정 요청은 **커맨드 큐**로 전달합니다.

---

## 변경 파일

| 파일 | 작업 |
|------|------|
| `src/IronRose.Editor/IronRose.Editor.csproj` | **신규** — Avalonia 에디터 프로젝트 |
| `src/IronRose.Editor/EditorApp.axaml` | **신규** — Avalonia Application 정의 |
| `src/IronRose.Editor/EditorApp.axaml.cs` | **신규** — Application 코드비하인드 |
| `src/IronRose.Editor/MainWindow.axaml` | **신규** — 에디터 메인 윈도우 레이아웃 |
| `src/IronRose.Editor/MainWindow.axaml.cs` | **신규** — 메인 윈도우 코드비하인드 |
| `src/IronRose.Editor/ViewModels/EditorViewModel.cs` | **신규** — 메인 VM (패널 VM 통합) |
| `src/IronRose.Editor/ViewModels/HierarchyViewModel.cs` | **신규** — 씬 계층 트리 VM |
| `src/IronRose.Editor/ViewModels/InspectorViewModel.cs` | **신규** — 프로퍼티 인스펙터 VM |
| `src/IronRose.Editor/ViewModels/ConsoleViewModel.cs` | **신규** — 콘솔 로그 VM |
| `src/IronRose.Editor/Views/HierarchyPanel.axaml` | **신규** — 계층 트리뷰 |
| `src/IronRose.Editor/Views/InspectorPanel.axaml` | **신규** — 프로퍼티 에디터 |
| `src/IronRose.Editor/Views/ConsolePanel.axaml` | **신규** — 콘솔 출력 |
| `src/IronRose.Engine/Editor/EditorBridge.cs` | **신규** — 엔진↔에디터 통신 브릿지 |
| `src/IronRose.Engine/Editor/SceneSnapshot.cs` | **신규** — 씬 상태 읽기 전용 DTO |
| `src/IronRose.Engine/Editor/EditorCommand.cs` | **신규** — 에디터→엔진 커맨드 정의 |
| `src/IronRose.Engine/EngineCore.cs` | **수정** — EditorBridge 초기화/업데이트 연동 |
| `src/IronRose.Engine/RoseConfig.cs` | **수정** — `[editor]` 섹션 추가 |
| `src/IronRose.Engine/RoseEngine/Debug.cs` | **수정** — LogCallback 추가 |
| `src/IronRose.Demo/Program.cs` | **수정** — 에디터 스레드 시작 |
| `IronRose.sln` | **수정** — IronRose.Editor 프로젝트 추가 |

---

## 1. 프로젝트 구성

### IronRose.Editor.csproj (신규)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.11" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.11" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.11" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.3.11" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\IronRose.Engine\IronRose.Engine.csproj" />
  </ItemGroup>
</Project>
```

### 의존성 방향

```
IronRose.Demo → IronRose.Editor → IronRose.Engine
                                 → Avalonia (NuGet)
```

`IronRose.Engine`은 Avalonia에 의존하지 않습니다.
`EditorBridge`는 Engine 프로젝트 내에 있지만 순수 C# (Avalonia 무관)입니다.

---

## 2. RoseConfig 확장 — `[editor]` 섹션

### rose_config.toml

```toml
[cache]
dont_use_cache = false
dont_use_compress_texture = false
force_clear_cache = false

[editor]
enable_editor = true          # false → 에디터 없이 실행 (기존 동작)
```

### RoseConfig.cs 수정

```csharp
public static class RoseConfig
{
    // 기존 필드
    public static bool DontUseCache { get; private set; }
    public static bool DontUseCompressTexture { get; private set; }
    public static bool ForceClearCache { get; private set; }

    // 에디터 설정 추가
    public static bool EnableEditor { get; private set; } = true;

    public static void Load()
    {
        // ... 기존 [cache] 파싱 코드 유지 ...

        if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
        {
            if (editor.TryGetValue("enable_editor", out var v) && v is bool b)
                EnableEditor = b;
        }
    }
}
```

기본값은 `true` (에디터 활성화). `rose_config.toml`에서 `enable_editor = false`로 비활성화.

---

## 3. 스레드 모델 — 엔진/에디터 분리

### Program.cs 수정

```csharp
static void Main(string[] _)
{
    Console.WriteLine("[IronRose] Engine Starting...");

    // RoseConfig를 먼저 로드하여 에디터 활성화 여부 판별
    RoseConfig.Load();

    if (RoseConfig.EnableEditor)
    {
        var editorThread = new Thread(() =>
        {
            IronRose.Editor.EditorApp.Start();
        });
        editorThread.IsBackground = true;
        editorThread.Start();
    }

    // 기존 엔진 시작 (메인 스레드)
    var options = WindowOptions.DefaultVulkan;
    // ... (기존 코드 그대로)
    _window.Run();
}
```

### EditorApp.cs

```csharp
public class EditorApp : Application
{
    public static void Start()
    {
        AppBuilder.Configure<EditorApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

### 스레드 안전 보장

| 방향 | 메커니즘 | 데이터 |
|------|---------|--------|
| Engine → Editor | `ConcurrentQueue<SceneSnapshot>` | 읽기 전용 스냅샷 (매 프레임) |
| Editor → Engine | `ConcurrentQueue<EditorCommand>` | 커맨드 객체 |
| Debug.Log → Editor | `ConcurrentQueue<LogEntry>` | 로그 메시지 |

**엔진 객체 직접 참조 금지** — 모든 통신은 직렬화된 DTO를 통해서만.

---

## 3. EditorBridge — 엔진↔에디터 통신

위치: `src/IronRose.Engine/Editor/EditorBridge.cs`

```csharp
public static class EditorBridge
{
    // Engine → Editor (스냅샷)
    private static readonly ConcurrentQueue<SceneSnapshot> _snapshots = new();
    private static SceneSnapshot? _latestSnapshot;

    // Editor → Engine (커맨드)
    private static readonly ConcurrentQueue<EditorCommand> _commands = new();

    // Debug.Log → Editor
    private static readonly ConcurrentQueue<LogEntry> _logs = new();

    public static bool IsEditorConnected { get; set; }

    // ── 엔진 측 (메인 스레드에서 호출) ──

    /// <summary>Update() 끝에서 호출 — 씬 스냅샷 생성</summary>
    public static void PushSnapshot()
    {
        if (!IsEditorConnected) return;
        var snapshot = SceneSnapshot.Capture();
        _latestSnapshot = snapshot;
        // 큐를 1개만 유지 (에디터가 느려도 최신 스냅샷만 사용)
        while (_snapshots.TryDequeue(out _)) { }
        _snapshots.Enqueue(snapshot);
    }

    /// <summary>Update() 시작에서 호출 — 에디터 커맨드 처리</summary>
    public static void ProcessCommands()
    {
        if (!IsEditorConnected) return;
        while (_commands.TryDequeue(out var cmd))
        {
            cmd.Execute();
        }
    }

    // ── 에디터 측 (에디터 스레드에서 호출) ──

    public static SceneSnapshot? ConsumeSnapshot()
    {
        _snapshots.TryDequeue(out var snapshot);
        return snapshot;
    }

    public static void EnqueueCommand(EditorCommand cmd) => _commands.Enqueue(cmd);

    public static void DrainLogs(List<LogEntry> buffer)
    {
        while (_logs.TryDequeue(out var entry))
            buffer.Add(entry);
    }

    // ── Debug.Log 연동 ──
    public static void PushLog(LogEntry entry) => _logs.Enqueue(entry);
}
```

---

## 4. SceneSnapshot — 읽기 전용 씬 DTO

위치: `src/IronRose.Engine/Editor/SceneSnapshot.cs`

```csharp
public class SceneSnapshot
{
    public GameObjectSnapshot[] GameObjects { get; init; } = Array.Empty<GameObjectSnapshot>();
    public float Fps { get; init; }
    public int TotalGameObjects { get; init; }
    public double PhysicsTime { get; init; }
    public double RenderTime { get; init; }

    public static SceneSnapshot Capture()
    {
        var gos = SceneManager.AllGameObjects;
        var snapshots = new GameObjectSnapshot[gos.Count];
        for (int i = 0; i < gos.Count; i++)
            snapshots[i] = GameObjectSnapshot.From(gos[i]);

        return new SceneSnapshot
        {
            GameObjects = snapshots,
            TotalGameObjects = gos.Count,
            Fps = Time.fps,
        };
    }
}

public class GameObjectSnapshot
{
    public int InstanceId { get; init; }
    public string Name { get; init; } = "";
    public bool ActiveSelf { get; init; }
    public int? ParentId { get; init; }
    public ComponentSnapshot[] Components { get; init; } = Array.Empty<ComponentSnapshot>();

    public static GameObjectSnapshot From(GameObject go) { /* 리플렉션으로 수집 */ }
}

public class ComponentSnapshot
{
    public string TypeName { get; init; } = "";
    public FieldSnapshot[] Fields { get; init; } = Array.Empty<FieldSnapshot>();
}

public class FieldSnapshot
{
    public string Name { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string? Value { get; init; }        // ToString() 직렬화
    public string? Header { get; init; }        // [Header] 어트리뷰트
    public float? RangeMin { get; init; }       // [Range] 어트리뷰트
    public float? RangeMax { get; init; }
    public bool Hidden { get; init; }           // [HideInInspector]
    public string? Tooltip { get; init; }       // [Tooltip]
}
```

### 리플렉션 기반 필드 수집

기존 엔진의 어트리뷰트 시스템을 활용합니다:
- `[SerializeField]` → 에디터에 표시 (private이어도)
- `[HideInInspector]` → 에디터에서 숨김
- `[Header("...")]` → 섹션 구분
- `[Range(min, max)]` → 슬라이더 UI로 표시
- `[Tooltip("...")]` → 마우스 호버 시 설명

---

## 5. EditorCommand — 에디터→엔진 커맨드

위치: `src/IronRose.Engine/Editor/EditorCommand.cs`

```csharp
public abstract class EditorCommand
{
    public abstract void Execute();
}

public class SetFieldCommand : EditorCommand
{
    public int GameObjectId { get; init; }
    public string ComponentType { get; init; } = "";
    public string FieldName { get; init; } = "";
    public string NewValue { get; init; } = "";

    public override void Execute()
    {
        var go = SceneManager.AllGameObjects
            .FirstOrDefault(g => g.GetInstanceID() == GameObjectId);
        if (go == null) return;

        var comp = go.InternalComponents
            .FirstOrDefault(c => c.GetType().Name == ComponentType);
        if (comp == null) return;

        var field = comp.GetType().GetField(FieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null) return;

        // 타입별 파싱
        var value = ParseValue(field.FieldType, NewValue);
        if (value != null)
            field.SetValue(comp, value);
    }

    private static object? ParseValue(Type type, string raw)
    {
        if (type == typeof(float)) return float.Parse(raw);
        if (type == typeof(int)) return int.Parse(raw);
        if (type == typeof(bool)) return bool.Parse(raw);
        if (type == typeof(string)) return raw;
        if (type == typeof(Vector3)) return ParseVector3(raw);
        if (type == typeof(Color)) return ParseColor(raw);
        return null;
    }
}

public class SetActiveCommand : EditorCommand { /* GameObject.SetActive */ }
public class DestroyCommand : EditorCommand { /* Object.Destroy */ }
public class PauseCommand : EditorCommand { /* Application.Pause/Resume */ }
```

---

## 6. Debug.Log 연동

### Debug.cs 수정

```csharp
public static class Debug
{
    // 기존 필드 유지...

    public static void Log(object message)
    {
        var text = message?.ToString() ?? "null";
        Console.WriteLine($"[Log] {text}");
        EditorBridge.PushLog(new LogEntry(LogLevel.Info, text, DateTime.Now));
    }

    public static void LogWarning(object message)
    {
        var text = message?.ToString() ?? "null";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Warning] {text}");
        Console.ResetColor();
        EditorBridge.PushLog(new LogEntry(LogLevel.Warning, text, DateTime.Now));
    }

    public static void LogError(object message)
    {
        var text = message?.ToString() ?? "null";
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Error] {text}");
        Console.ResetColor();
        EditorBridge.PushLog(new LogEntry(LogLevel.Error, text, DateTime.Now));
    }
}

public record LogEntry(LogLevel Level, string Message, DateTime Timestamp);
public enum LogLevel { Info, Warning, Error }
```

---

## 7. EngineCore.cs 수정

`Initialize()`에 추가:

```csharp
// 에디터 브릿지 활성화 (RoseConfig는 이미 Load 완료 상태)
EditorBridge.IsEditorConnected = RoseConfig.EnableEditor;
```

`Update()`에 추가:

```csharp
// Update() 시작부에 추가
EditorBridge.ProcessCommands();

// Update() 끝에 추가 (SceneManager.Update 후)
EditorBridge.PushSnapshot();
```

`RoseConfig.EnableEditor == false`이면 `IsEditorConnected`가 false이므로
`PushSnapshot()`/`ProcessCommands()` 모두 early return — 오버헤드 제로.

---

## 8. 에디터 UI 레이아웃

### MainWindow.axaml

```
┌──────────────────────────────────────────────────────────┐
│  IronRose Editor                              [─][□][×]  │
├──────────────────────┬───────────────────────────────────┤
│                      │                                   │
│   Scene Hierarchy    │         Inspector                 │
│                      │                                   │
│  ▼ WarmUpCamera      │  Transform                        │
│    └─ WarmUpText     │  ┌─────────────────────────────┐  │
│  ▼ DemoLauncher      │  │ Position  X[0] Y[0] Z[0]   │  │
│  ▼ MainCamera        │  │ Rotation  X[0] Y[0] Z[0]   │  │
│    ├─ MeshObj_1      │  │ Scale     X[1] Y[1] Z[1]   │  │
│    ├─ MeshObj_2      │  └─────────────────────────────┘  │
│    └─ Light_0        │                                   │
│                      │  MeshRenderer                     │
│                      │  ┌─────────────────────────────┐  │
│                      │  │ enabled   [✓]               │  │
│                      │  │ material  (Material)        │  │
│                      │  └─────────────────────────────┘  │
│                      │                                   │
│                      │  Light                            │
│                      │  ┌─────────────────────────────┐  │
│                      │  │ intensity ════●════ [1.5]   │  │  ← [Range] → 슬라이더
│                      │  │ range     ════●════ [10.0]  │  │
│                      │  │ color     [■] #FFFFFF       │  │
│                      │  └─────────────────────────────┘  │
├──────────────────────┴───────────────────────────────────┤
│  Console                                    [Clear] [▼]  │
│  ┌───────────────────────────────────────────────────────┤
│  │ 14:23:01 [Log]     DemoLauncher started               │
│  │ 14:23:01 [Log]     Loading PBRDemo...                 │
│  │ 14:23:02 [Warning] Texture not found: missing.png     │
│  │ 14:23:05 [Error]   NullRef in MyScript.Update()       │
│  └───────────────────────────────────────────────────────┤
│  FPS: 60.0 | Objects: 12 | Draw Calls: 24 | ▶ Playing    │
└──────────────────────────────────────────────────────────┘
```

### 패널별 구현

**HierarchyPanel**
- `TreeView`에 `GameObjectSnapshot[]`를 ParentId 기준으로 트리 구성
- 선택 시 `InspectorPanel`에 해당 오브젝트의 `ComponentSnapshot[]` 표시
- 활성/비활성 토글 (체크박스 → `SetActiveCommand`)

**InspectorPanel**
- 선택된 GameObject의 모든 컴포넌트를 `Expander`로 표시
- 필드 타입별 에디터 위젯:

| 필드 타입 | UI 위젯 |
|----------|---------|
| `float` / `int` | TextBox (또는 [Range] 시 Slider) |
| `bool` | CheckBox |
| `string` | TextBox |
| `Vector3` | X/Y/Z TextBox 3개 |
| `Color` | ColorPicker (Hex + 미리보기) |
| `Quaternion` | Euler 각도 X/Y/Z TextBox |
| `enum` | ComboBox |

- 값 변경 시 → `SetFieldCommand` 큐에 추가
- `[Header("...")]` → 굵은 라벨로 섹션 구분
- `[Tooltip("...")]` → ToolTip 바인딩

**ConsolePanel**
- `ListBox`에 `LogEntry` 목록 표시
- 레벨별 색상: Info=흰색, Warning=노란색, Error=빨간색
- Clear 버튼, 레벨 필터 토글
- 자동 스크롤 (최신 로그가 하단에)

**StatusBar (하단)**
- FPS, 오브젝트 수, Pause/Play 상태 표시
- Pause/Resume 버튼 → `PauseCommand`

---

## 9. 에디터 ↔ 엔진 동기화 주기

```
Engine Main Thread (60 Hz)
  ├─ Update() 시작 → ProcessCommands() ← 에디터 커맨드 소비
  ├─ 게임 로직 실행
  ├─ Update() 끝 → PushSnapshot() → 스냅샷 발행
  └─ Render()

Editor Thread (DispatcherTimer, 10~20 Hz)
  ├─ ConsumeSnapshot() → 최신 스냅샷 수신
  ├─ ViewModel 업데이트 (바인딩으로 UI 자동 갱신)
  └─ DrainLogs() → 콘솔 로그 추가
```

에디터 갱신 빈도를 10~20Hz로 제한하여 엔진 성능에 영향을 최소화합니다.
스냅샷 큐는 항상 1개만 유지 — 에디터가 느려도 최신 데이터만 표시.

---

## 10. 단계별 구현 순서

### Phase 11A: 기반 — 빈 에디터 창 + 브릿지 (최소 동작)
1. `IronRose.Editor` 프로젝트 생성 + sln 등록
2. `EditorBridge`, `SceneSnapshot`, `EditorCommand` 구현
3. `Program.cs`에서 에디터 스레드 시작
4. 빈 Avalonia 윈도우 표시 확인
5. FPS 표시 (StatusBar)로 브릿지 동작 검증

### Phase 11B: 계층 + 인스펙터
1. `HierarchyPanel` — 트리뷰로 GameObject 계층 표시
2. `InspectorPanel` — 선택된 오브젝트의 컴포넌트/필드 표시
3. 필드 편집 → `SetFieldCommand`로 엔진에 반영
4. `[Header]`, `[Range]`, `[Tooltip]` 어트리뷰트 지원

### Phase 11C: 콘솔 + 폴리싱
1. `Debug.Log` 연동 → `ConsolePanel`
2. 로그 필터링 (Info/Warning/Error)
3. Pause/Resume 버튼

---

## 검증 방법

1. `dotnet build` — 전체 솔루션 컴파일 성공
2. `dotnet run --project src/IronRose.Demo` → 엔진 윈도우 + 에디터 윈도우 2개 동시 표시
3. 에디터 Hierarchy에 현재 씬의 GameObject 목록 실시간 표시
4. GameObject 선택 → Inspector에 컴포넌트/필드 표시
5. Inspector에서 float 값 수정 → 엔진에 즉시 반영 (예: Light.intensity)
6. `Debug.Log()` 호출 → Console 패널에 실시간 출력
7. `rose_config.toml`에서 `enable_editor = false` → 에디터 없이 기존과 동일하게 실행
8. 에디터 창 닫기 → 엔진은 계속 실행 (크래시 없음)
9. Linux + Windows 양쪽에서 동작 확인
