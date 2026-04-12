# Phase 1: EditorState 확장 + CanvasEditMode 정적 클래스

## 목표
- Canvas Edit Mode의 핵심 상태 필드를 `EditorState`에 추가한다.
- `CanvasEditMode` 정적 클래스를 신규 생성하여 Enter/Exit/ResetView 로직과 2D 뷰 상태(ViewOffset, ViewZoom)를 관리한다.
- 이 phase 완료 시 Canvas Edit Mode 진입/퇴출 로직이 코드 레벨에서 완성되나, UI에서 호출하는 경로는 아직 없다.

## 선행 조건
- 없음 (첫 번째 phase)

## 수정할 파일

### `src/IronRose.Engine/Editor/EditorState.cs`

- **변경 내용**: Canvas Edit Mode 관련 상태 필드 추가, Load/Save 확장, 정리 메서드 추가

추가할 필드 (Prefab Edit Mode 섹션 바로 아래, line 101 부근):
```csharp
// ── Canvas Edit Mode ──

/// <summary>Canvas 편집 모드 활성화 여부.</summary>
public static bool IsEditingCanvas { get; set; } = false;

/// <summary>현재 편집 중인 Canvas의 GameObject instance ID.</summary>
public static int? EditingCanvasGoId { get; set; }

// Canvas Edit Mode 뷰 설정 (영속화)
/// <summary>Canvas Edit Mode aspect ratio 프리셋 이름.</summary>
public static string CanvasEditAspectRatio { get; set; } = "16:9";
/// <summary>Custom aspect ratio 너비.</summary>
public static int CanvasEditCustomWidth { get; set; } = 1920;
/// <summary>Custom aspect ratio 높이.</summary>
public static int CanvasEditCustomHeight { get; set; } = 1080;

// EditorCamera 상태 저장 (Canvas Edit Mode 진입/퇴출용)
internal static RoseEngine.Vector3? SavedCanvasCameraPosition;
internal static RoseEngine.Quaternion? SavedCanvasCameraRotation;
internal static RoseEngine.Vector3? SavedCanvasCameraPivot;
```

- **Load() 메서드 확장**: `panels` 섹션 로드 후에 `canvas_edit` 섹션 추가
```csharp
var canvasEdit = config.GetSection("canvas_edit");
if (canvasEdit != null)
{
    var ar = canvasEdit.GetString("aspect_ratio", "");
    if (!string.IsNullOrEmpty(ar))
        CanvasEditAspectRatio = ar;
    CanvasEditCustomWidth = Math.Max(canvasEdit.GetInt("custom_width", CanvasEditCustomWidth), 1);
    CanvasEditCustomHeight = Math.Max(canvasEdit.GetInt("custom_height", CanvasEditCustomHeight), 1);
}
```

- **Save() 메서드 확장**: panels 섹션 뒤에 `canvas_edit` 섹션 저장
```csharp
toml += "\n[canvas_edit]\n";
toml += $"aspect_ratio = \"{CanvasEditAspectRatio}\"\n";
toml += $"custom_width = {CanvasEditCustomWidth}\n";
toml += $"custom_height = {CanvasEditCustomHeight}\n";
```

- **CleanupCanvasEditMode() 메서드 추가** (CleanupPrefabEditMode() 패턴 참조):
```csharp
/// <summary>
/// 앱 종료 시 Canvas Edit Mode 상태 정리.
/// </summary>
public static void CleanupCanvasEditMode()
{
    if (!IsEditingCanvas) return;
    IsEditingCanvas = false;
    EditingCanvasGoId = null;
    SavedCanvasCameraPosition = null;
    SavedCanvasCameraRotation = null;
    SavedCanvasCameraPivot = null;
}
```

- **이유**: Canvas Edit Mode의 런타임 상태와 영속 설정을 관리하기 위해 필요. PrefabEditMode 상태 관리 패턴을 그대로 따른다.

### `src/IronRose.Engine/Editor/EditorPlayMode.cs`

- **변경 내용**: `EnterPlayMode()` 메서드 시작 부분에서 Canvas Edit Mode 자동 퇴출 추가

`EnterPlayMode()` 내부, `if (State != PlayModeState.Edit) return;` 직후에 추가:
```csharp
// Canvas Edit Mode 중 Play 진입 시 자동 퇴출
if (EditorState.IsEditingCanvas)
    CanvasEditMode.Exit();
```

- **이유**: 설계 문서에 명시된 요구사항. Play Mode 진입 시 Canvas Edit Mode를 자동 종료해야 한다.

## 생성할 파일

### `src/IronRose.Engine/Editor/CanvasEditMode.cs`

- **역할**: Canvas Edit Mode의 진입/퇴출 로직과 2D 뷰 상태(ViewOffset, ViewZoom) 관리
- **클래스**: `public static class CanvasEditMode` (네임스페이스: `IronRose.Engine.Editor`)
- **주요 멤버**:
  - `public static bool IsActive => EditorState.IsEditingCanvas;` -- 활성 여부
  - `public static int? EditingCanvasGoId => EditorState.EditingCanvasGoId;` -- 편집 중인 Canvas GO ID
  - `public static System.Numerics.Vector2 ViewOffset;` -- 패닝 오프셋 (screen 픽셀)
  - `public static float ViewZoom = 1.0f;` -- 확대/축소 배율
  - `public static void Enter(RoseEngine.GameObject canvasGo)` -- 진입
  - `public static void Exit()` -- 퇴출
  - `public static void ResetView()` -- 뷰를 Canvas 전체에 맞게 초기화
- **의존**: `EditorState`, `EditorSelection`, `RoseEngine.GameObject`, `RoseEngine.Canvas`
- **구현 힌트**:

**Enter() 구현**:
1. `canvasGo`에서 `Canvas` 컴포넌트 존재 확인. 없으면 `EditorDebug.LogWarning()` 후 return.
2. 현재 EditorCamera 상태를 `EditorState.SavedCanvasCameraPosition/Rotation/Pivot`에 저장. 단, EditorCamera 인스턴스에 직접 접근할 수 없으므로 (ImGuiOverlay 내부 소유), **EditorCamera의 static 필드로 현재 상태를 노출하거나**, 별도 접근 경로가 필요. 현 단계에서는 **저장/복원은 ImGuiOverlay에서 Enter/Exit 호출 시 처리하는 것으로 위임**하고, CanvasEditMode에서는 상태 플래그만 설정한다.
3. `EditorState.IsEditingCanvas = true`
4. `EditorState.EditingCanvasGoId = canvasGo.GetInstanceID()`
5. ViewOffset = `System.Numerics.Vector2.Zero`, ViewZoom = 1.0f
6. `EditorSelection.Clear()` 후 `EditorSelection.Select(canvasGo.GetInstanceID())`

**Exit() 구현**:
1. `if (!EditorState.IsEditingCanvas) return;`
2. `EditorState.IsEditingCanvas = false`
3. `EditorState.EditingCanvasGoId = null`
4. EditorCamera 복원은 ImGuiOverlay에서 처리 (SavedCanvasCameraPosition/Rotation/Pivot 값이 있으면 복원)
5. `EditorState.SavedCanvasCameraPosition = null` 등 저장 상태 클리어
6. `EditorSelection.Clear()`

**ResetView() 구현**:
1. ViewOffset = `System.Numerics.Vector2.Zero`
2. ViewZoom = 1.0f

**파일 상단 using 목록**:
```csharp
using System.Numerics;
using RoseEngine;
```

**네임스페이스**: `IronRose.Engine.Editor`

**전체 구조 참고**: `PrefabEditMode.cs`의 구조를 따르되, 씬 스냅샷/Undo 스택 저장은 불필요 (Canvas Edit Mode는 씬 데이터를 격리하지 않음).

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `EditorState`에 `IsEditingCanvas`, `EditingCanvasGoId`, `CanvasEditAspectRatio`, `CanvasEditCustomWidth`, `CanvasEditCustomHeight`, `SavedCanvasCameraPosition/Rotation/Pivot` 필드가 존재
- [ ] `EditorState.Save()`/`Load()`에 `canvas_edit` 섹션이 포함됨
- [ ] `CanvasEditMode.Enter()` 호출 시 `EditorState.IsEditingCanvas`가 true로 설정됨
- [ ] `CanvasEditMode.Exit()` 호출 시 상태가 정리됨
- [ ] `EditorPlayMode.EnterPlayMode()` 시 Canvas Edit Mode가 자동 퇴출됨

## 참고
- `PrefabEditMode.cs`를 구조적 참고 모델로 사용한다.
- EditorCamera 상태 저장/복원의 실제 구현은 Phase D (ImGuiOverlay 수정)에서 완성된다. 이 phase에서는 `EditorState`의 저장 필드만 준비한다.
- `RoseEngine.Vector3`와 `System.Numerics.Vector2`를 혼용하지 않도록 주의. EditorState의 카메라 저장 필드는 `RoseEngine.Vector3`/`RoseEngine.Quaternion` 타입 사용.
