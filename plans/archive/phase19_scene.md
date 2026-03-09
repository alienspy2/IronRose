# Phase 19: Scene 시스템 — New Scene + 기본 오브젝트 세트 + 파일 저장

## Context

현재 IronRose에는 Unity의 Scene에 해당하는 개념이 없다. `SceneManager`는 GameObject 레지스트리 역할만 하며, 씬 파일 직렬화/역직렬화, File 메뉴의 New Scene/Save Scene이 존재하지 않는다. 에디터를 실제 워크플로우로 사용하려면 씬 단위의 작업이 필수적이다.

### 목표

- **Scene 개념 도입**: `.scene` 파일 (TOML, Tomlyn) 로 씬을 저장/로드
- **File > New Scene**: 파일 저장 다이얼로그를 **먼저** 띄워서 경로를 확정한 후, 기본 오브젝트 세트로 씬 생성 및 즉시 저장 (유니티와 다름)
- **기본 오브젝트 세트**: Main Camera, Cube (중앙), Plane (바닥), SpotLight
- **File > Save Scene / Save Scene As**: 현재 씬을 저장
- **File > Open Scene**: 기존 `.scene` 파일 열기

---

## Phase 19 이후 의존성 구조 변경

```
IronRose.Engine
  └─ RoseEngine/Scene.cs              (신규 — 씬 데이터 클래스)
  └─ RoseEngine/SceneManager.cs       (수정 — 현재 씬 참조 추가)
  └─ Editor/SceneSerializer.cs        (신규 — TOML 직렬화/역직렬화, Tomlyn)
  └─ Editor/ImGui/NativeFileDialog.cs  (신규 — OS 파일 다이얼로그 P/Invoke)
  └─ Editor/ImGui/ImGuiOverlay.cs     (수정 — File 메뉴 확장)

IronRose.Editor
  └─ EditorUtils.cs                   (수정 — CreateDefaultScene 추가)
```

---

## 구현 단계

### Step 1. Scene 데이터 클래스 추가

**신규 파일:** `src/IronRose.Engine/RoseEngine/Scene.cs`

```csharp
namespace RoseEngine
{
    /// <summary>
    /// 씬 메타데이터. Unity의 Scene에 대응하
    /// 씬 파일 경로와 이름을 보유한다.
    /// </summary>
    public class Scene
    {
        /// <summary>씬 파일의 절대 경로 (.scene). 아직 저장 전이면 null.</summary>
        public string? path { get; internal set; }

        /// <summary>씬 이름 (파일명에서 확장자 제거).</summary>
        public string name { get; internal set; } = "Untitled";

        /// <summary>마지막 저장 이후 변경이 있으면 true.</summary>
        public bool isDirty { get; internal set; }
    }
}
```

---

### Step 2. SceneManager에 현재 씬 참조 추가

**파일:** `src/IronRose.Engine/RoseEngine/SceneManager.cs`

기존 코드 최상단에 추가:

```csharp
// --- Current scene ---
private static Scene _activeScene = new Scene();

/// <summary>현재 활성 씬.</summary>
public static Scene GetActiveScene() => _activeScene;

/// <summary>활성 씬 설정 (내부용).</summary>
internal static void SetActiveScene(Scene scene) => _activeScene = scene;
```

`Clear()` 메서드 수정 — 씬 참조는 유지 (호출자가 새 씬을 설정):

```csharp
public static void Clear()
{
    // 기존 코드 유지...
    // _activeScene은 Clear()에서 건드리지 않음 — 호출자가 SetActiveScene() 호출
}
```

---

### Step 3. SceneSerializer — TOML 직렬화/역직렬화

**신규 파일:** `src/IronRose.Engine/Editor/SceneSerializer.cs`

씬의 모든 GameObject와 컴포넌트를 TOML로 직렬화한다. 이미 `.rose` 메타데이터에 사용 중인 `Tomlyn` 패키지를 재사용하여 외부 의존성을 추가하지 않는다.

#### 직렬화 포맷 (`.scene` 파일)

```toml
name = "MyScene"

[[gameObjects]]
name = "Main Camera"
activeSelf = true

[gameObjects.transform]
localPosition = [0.0, 1.0, -5.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [1.0, 1.0, 1.0]
parentIndex = -1

[[gameObjects.components]]
type = "Camera"

[gameObjects.components.fields]
fieldOfView = 60.0
nearClipPlane = 0.1
farClipPlane = 1000.0
clearFlags = "Skybox"

[[gameObjects]]
name = "Cube"
activeSelf = true

[gameObjects.transform]
localPosition = [0.0, 0.5, 0.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [1.0, 1.0, 1.0]
parentIndex = -1

[[gameObjects.components]]
type = "MeshFilter"

[gameObjects.components.fields]
primitiveType = "Cube"

[[gameObjects.components]]
type = "MeshRenderer"

[gameObjects.components.fields]
color = [1.0, 1.0, 1.0, 1.0]
metallic = 0.0
roughness = 0.5

[[gameObjects]]
name = "Plane"
activeSelf = true

[gameObjects.transform]
localPosition = [0.0, 0.0, 0.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [1.0, 1.0, 1.0]
parentIndex = -1

[[gameObjects.components]]
type = "MeshFilter"

[gameObjects.components.fields]
primitiveType = "Plane"

[[gameObjects.components]]
type = "MeshRenderer"

[gameObjects.components.fields]
color = [1.0, 1.0, 1.0, 1.0]
metallic = 0.0
roughness = 0.5

[[gameObjects]]
name = "Spot Light"
activeSelf = true

[gameObjects.transform]
localPosition = [0.0, 5.0, -2.0]
localRotation = [0.0, 0.0, 0.0, 1.0]
localScale = [1.0, 1.0, 1.0]
parentIndex = -1

[[gameObjects.components]]
type = "Light"

[gameObjects.components.fields]
type = "Spot"
color = [1.0, 1.0, 1.0, 1.0]
intensity = 2.0
range = 15.0
spotAngle = 30.0
spotOuterAngle = 45.0
shadows = true
```

#### 주요 메서드

```csharp
namespace IronRose.Engine.Editor
{
    public static class SceneSerializer
    {
        /// <summary>현재 씬을 TOML로 직렬화하여 파일에 저장.</summary>
        public static void Save(string filePath);

        /// <summary>.scene 파일에서 씬을 로드. SceneManager.Clear() 후 역직렬화.</summary>
        public static void Load(string filePath);
    }
}
```

**직렬화 대상 컴포넌트:**
- `Transform` — localPosition, localRotation, localScale, parent 인덱스
- `Camera` — fieldOfView, nearClipPlane, farClipPlane, clearFlags, backgroundColor
- `Light` — type, color, intensity, range, spotAngle, spotOuterAngle, shadows
- `MeshFilter` — primitiveType (Cube/Sphere/Plane 등, 프리미티브만 직렬화)
- `MeshRenderer` — material 속성 (color, metallic, roughness)

> Asset 참조 메시(GLB/OBJ)의 직렬화는 Phase 19 범위 외 — 프리미티브 메시만 지원.

---

### Step 4. NativeFileDialog — OS 파일 저장/열기 다이얼로그

**신규 파일:** `src/IronRose.Engine/Editor/ImGui/NativeFileDialog.cs`

크로스 플랫폼 파일 다이얼로그. `RuntimeInformation.IsOSPlatform()`으로 런타임 분기한다.

```csharp
namespace IronRose.Engine.Editor.ImGuiEditor
{
    public static class NativeFileDialog
    {
        /// <summary>
        /// 파일 저장 다이얼로그를 표시한다.
        /// </summary>
        /// <param name="title">다이얼로그 타이틀</param>
        /// <param name="defaultName">기본 파일명</param>
        /// <param name="filter">파일 확장자 필터 (예: "*.scene")</param>
        /// <param name="initialDir">초기 디렉토리</param>
        /// <returns>선택된 파일 경로. 취소 시 null.</returns>
        public static string? SaveFileDialog(
            string title = "Save Scene",
            string defaultName = "NewScene.scene",
            string filter = "*.scene",
            string? initialDir = null);

        /// <summary>
        /// 파일 열기 다이얼로그를 표시한다.
        /// </summary>
        public static string? OpenFileDialog(
            string title = "Open Scene",
            string filter = "*.scene",
            string? initialDir = null);
    }
}
```

**구현 전략:**

`RuntimeInformation.IsOSPlatform()`으로 분기하여 각 OS 네이티브 다이얼로그를 사용한다.

- **Linux**: `zenity --file-selection --save --filename=...` (GNOME) 또는 `kdialog --getsavefilename` (KDE) 호출. `Process.Start()`로 실행하고 stdout에서 경로를 읽는다. zenity가 없으면 kdialog로 fallback.
- **Windows**: `comdlg32.dll`의 `GetSaveFileName` / `GetOpenFileName` P/Invoke 호출. `OPENFILENAME` 구조체를 마샬링하여 네이티브 Win32 파일 다이얼로그를 표시한다.

```csharp
// Windows P/Invoke 선언
[DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
private static extern bool GetSaveFileName(ref OPENFILENAME ofn);

[DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
private struct OPENFILENAME
{
    public int lStructSize;
    public IntPtr hwndOwner;
    public IntPtr hInstance;
    public string lpstrFilter;      // "Scene Files\0*.scene\0All Files\0*.*\0"
    public string lpstrCustomFilter;
    public int nMaxCustFilter;
    public int nFilterIndex;
    public string lpstrFile;        // 결과 경로 버퍼 (MAX_PATH)
    public int nMaxFile;
    public string lpstrFileTitle;
    public int nMaxFileTitle;
    public string lpstrInitialDir;
    public string lpstrTitle;
    public int Flags;               // OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST
    // ... 나머지 필드 0 초기화
}
```

- 공통: 확장자 `.scene`가 없으면 자동 추가.

---

### Step 5. ImGuiOverlay — File 메뉴 확장

**파일:** `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

기존 File 메뉴 (`Exit`만 있음)를 확장한다:

```
File
├── New Scene          (Ctrl+N)  → 저장 다이얼로그 → 기본 씬 생성 + 즉시 저장
├── Open Scene         (Ctrl+O)  → 열기 다이얼로그 → 씬 로드
├── ──────────────────
├── Save Scene         (Ctrl+S)  → 현재 경로에 저장 (경로 없으면 Save As)
├── Save Scene As      (Ctrl+Shift+S) → 저장 다이얼로그 → 다른 이름으로 저장
├── ──────────────────
└── Exit
```

#### New Scene 플로우

1. 사용자가 `File > New Scene` 클릭
2. `NativeFileDialog.SaveFileDialog()` 호출 → 파일 저장 경로 선택
3. 취소하면 아무 일도 일어나지 않음
4. 경로가 확정되면:
   a. `SceneManager.Clear()` — 기존 씬 정리
   b. 새 `Scene` 객체 생성, 경로 설정
   c. `SceneManager.SetActiveScene(scene)`
   d. `EditorUtils.CreateDefaultScene()` — 기본 오브젝트 세트 생성
   e. `SceneSerializer.Save(path)` — 즉시 저장
   f. 윈도우 타이틀에 씬 이름 반영: `"IronRose Editor — MyScene"`

#### 단축키 처리

ImGuiOverlay의 `Update()` 메서드에서 ImGui의 키 입력을 체크:

```csharp
var io = ImGui.GetIO();
bool ctrl = io.KeyCtrl;
bool shift = io.KeyShift;

if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.N)) NewScene();
if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.O)) OpenScene();
if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.S)) SaveScene();
if (ctrl && shift && ImGui.IsKeyPressed(ImGuiKey.S)) SaveSceneAs();
```

---

### Step 6. EditorUtils에 CreateDefaultScene 추가

**파일:** `src/IronRose.Editor/EditorUtils.cs`

```csharp
/// <summary>
/// 기본 씬 오브젝트 세트 생성:
/// Main Camera, Cube, Plane, Spot Light.
/// </summary>
public static void CreateDefaultScene()
{
    // 1. Main Camera — (0, 1, -5), look at origin
    CreateCamera(
        new Vector3(0, 3, -6),
        lookAt: Vector3.zero,
        clearFlags: CameraClearFlags.Skybox);

    // 2. Cube — 중앙, y=0.5 (바닥 위)
    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
    cube.name = "Cube";
    cube.transform.position = new Vector3(0, 0.5f, 0);

    // 3. Plane — 바닥, 원점
    var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
    plane.name = "Plane";
    plane.transform.position = Vector3.zero;

    // 4. Spot Light — 위에서 아래로 비추는 스팟라이트
    var lightObj = new GameObject("Spot Light");
    var light = lightObj.AddComponent<Light>();
    light.type = LightType.Spot;
    light.color = Color.white;
    light.intensity = 2f;
    light.range = 15f;
    light.spotAngle = 30f;
    light.spotOuterAngle = 45f;
    light.shadows = true;
    lightObj.transform.position = new Vector3(0, 5, -2);
    lightObj.transform.LookAt(Vector3.zero);
}
```

---

### Step 7. RoseEditor Program.cs 수정 — 윈도우 타이틀 업데이트 지원

**파일:** `src/IronRose.RoseEditor/Program.cs`

`ImGuiOverlay`에서 씬 변경 시 윈도우 타이틀을 업데이트할 수 있도록, `EngineCore`에 윈도우 참조를 통한 타이틀 변경 메서드를 추가한다.

```csharp
// EngineCore.cs에 추가
public void SetWindowTitle(string title)
{
    _window?.Invoke(() => { if (_window != null) _window.Title = title; });
}
```

ImGuiOverlay에서 씬 변경 시 호출:

```csharp
// 타이틀 포맷
var sceneName = SceneManager.GetActiveScene().name;
var dirty = SceneManager.GetActiveScene().isDirty ? " *" : "";
engineCore.SetWindowTitle($"IronRose Editor — {sceneName}{dirty}");
```

---

### Step 8. SetupDefaultScene 변경

**파일:** `src/IronRose.RoseEditor/Program.cs`

에디터 시작 시 빈 씬(Untitled) 대신 기본 오브젝트 세트를 가진 씬으로 시작:

```csharp
static void SetupDefaultScene()
{
    Debug.Log("[IronRose Editor] Setting up default scene...");

    var scene = new Scene { name = "Untitled" };
    SceneManager.SetActiveScene(scene);

    // 기본 오브젝트 세트 생성 (카메라 + 큐브 + 플레인 + 스팟라이트)
    EditorUtils.CreateDefaultScene();
}
```

---

## 변경 파일 요약

| 파일 | 액션 | 설명 |
|------|------|------|
| `src/IronRose.Engine/RoseEngine/Scene.cs` | 신규 | Scene 데이터 클래스 (path, name, isDirty) |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | 수정 | `_activeScene`, `GetActiveScene()`, `SetActiveScene()` 추가 |
| `src/IronRose.Engine/Editor/SceneSerializer.cs` | 신규 | TOML 직렬화/역직렬화 — Tomlyn (Save, Load) |
| `src/IronRose.Engine/Editor/ImGui/NativeFileDialog.cs` | 신규 | OS 네이티브 파일 다이얼로그 (Linux: zenity/kdialog, Windows: comdlg32 P/Invoke) |
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | 수정 | File 메뉴 확장 (New/Open/Save/Save As + 단축키) |
| `src/IronRose.Engine/EngineCore.cs` | 수정 | `SetWindowTitle()` 메서드 추가 |
| `src/IronRose.Editor/EditorUtils.cs` | 수정 | `CreateDefaultScene()` 추가 (Camera+Cube+Plane+SpotLight) |
| `src/IronRose.RoseEditor/Program.cs` | 수정 | `SetupDefaultScene()`에서 Scene 객체 생성 + `CreateDefaultScene()` 호출 |

---

## New Scene 전체 플로우 다이어그램

```
사용자: File > New Scene (또는 Ctrl+N)
         │
         ▼
  NativeFileDialog.SaveFileDialog()
  ┌──────────────────────────┐
  │  OS 파일 저장 다이얼로그  │
  │  기본 파일명: NewScene    │
  │  필터: *.scene           │
  └──────────┬───────────────┘
             │
     ┌───────┴───────┐
     │ 취소          │ 경로 확정
     │ → 아무것도    │
     │   안 함       ▼
     │        SceneManager.Clear()
     │               │
     │        new Scene { path, name }
     │               │
     │        SceneManager.SetActiveScene(scene)
     │               │
     │        EditorUtils.CreateDefaultScene()
     │        ┌──────┼──────────────────┐
     │        │      │                  │
     │     Camera  Cube+Plane      SpotLight
     │        │      │                  │
     │        └──────┼──────────────────┘
     │               │
     │        SceneSerializer.Save(path)
     │               │
     │        윈도우 타이틀 업데이트
     │        "IronRose Editor — MyScene"
     └───────────────┘
```

---

## 검증

1. `dotnet build IronRose.sln` — 전체 빌드 성공 확인
2. `IronRose.RoseEditor` 실행 — 기본 씬에 Camera, Cube, Plane, SpotLight 표시 확인
3. `File > New Scene` — 파일 저장 다이얼로그가 먼저 뜨는지 확인
4. 다이얼로그에서 경로 지정 후 OK — `.scene` 파일 생성 확인, 기본 오브젝트 세트 확인
5. 다이얼로그 취소 시 — 아무 변경 없음 확인
6. `File > Save Scene` — 현재 씬 덮어쓰기 저장 확인
7. `File > Open Scene` — 저장된 `.scene` 파일 로드 확인, 오브젝트 복원 확인
8. `Ctrl+N`, `Ctrl+S`, `Ctrl+O`, `Ctrl+Shift+S` 단축키 동작 확인
9. 윈도우 타이틀에 씬 이름 반영 확인
