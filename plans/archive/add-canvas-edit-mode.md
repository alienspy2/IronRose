# Canvas UI 전용 편집 모드 (Canvas Edit Mode)

## 배경

- 현재 IronRose 에디터에서 Canvas UI를 편집할 때, Scene View 위에 overlay로 렌더링되어 확대/패닝 등 2D 편집에 필수적인 조작이 불가능
- 3D Scene View의 카메라 시스템(Fly/Orbit/Pan/Zoom)은 3D 공간 탐색용으로 설계되어, 2D UI 레이아웃 작업에 부적합
- 유니티는 2D 모드가 있지만 3D 씬과 혼재되어 사용 편의성이 떨어짐
- IronRose에서는 Prefab Edit Mode와 유사한 전용 편집 모드를 만들어 이 문제를 해결

## 목표

1. Canvas 컴포넌트 Inspector에 "Edit Canvas" 버튼을 추가하여 전용 편집 모드 진입
2. 전용 2D 뷰포트에서 Canvas UI를 확대/축소, 패닝, 포커스 등 자유롭게 편집 가능
3. 화면 비율(16:9, 16:10, 4:3, 32:9, Custom) 프리셋으로 다양한 타겟 해상도 미리보기
4. Hierarchy에서 Canvas 하위 자식만 표시하여 편집 집중도 향상
5. Prefab Edit Mode와 유사한 진입/퇴출 라이프사이클 (씬 상태 보존/복원)

## 현재 상태

### Canvas UI 시스템
- `Canvas` 컴포넌트 (`RoseEngine/Canvas.cs`): `renderMode`, `referenceResolution`, `scaleMode`, `matchWidthOrHeight` 등
- `CanvasRenderer` (`RoseEngine/CanvasRenderer.cs`): 정적 시스템. ImGui DrawList 기반으로 Canvas UI 트리를 렌더링. `RenderAll()`, `HitTest()`, `CollectHitsInRect()` 제공
- `RectTransform` (`RoseEngine/RectTransform.cs`): 2D UI 레이아웃 컴포넌트. `lastScreenRect`에 마지막 렌더 결과를 캐시

### Scene View 패널
- `ImGuiSceneViewPanel` (`Editor/ImGui/Panels/ImGuiSceneViewPanel.cs`): 3D 씬 뷰 패널. 툴바(렌더모드, 트랜스폼 도구, 스냅, 오버레이 토글), 이미지 렌더링, 드래그앤드롭 처리
- Canvas UI overlay는 `_showUI` 플래그가 true일 때 `CanvasRenderer.RenderAll()`로 Scene View 이미지 위에 그려짐

### EditorCamera
- `EditorCamera` (`Editor/SceneView/EditorCamera.cs`): 3D 카메라. Fly(RMB), Orbit(Alt+LMB), Pan(MMB), Zoom(휠), Focus(F) 지원
- `SceneViewInputState` 구조체로 입력 상태를 전달받아 `Update()` 처리

### Prefab Edit Mode (참고 모델)
- `PrefabEditMode` (`Editor/PrefabEditMode.cs`): 정적 클래스. `Enter(path)` / `Save()` / `Exit()` / `Back()` 메서드
- `EditorState`에서 상태 관리: `IsEditingPrefab`, `EditingPrefabPath`, `PrefabEditStack` (중첩 진입), 씬 스냅샷/Undo 스택 저장/복원
- `ImGuiOverlay`에서 Breadcrumb child window와 Variant Tree child window를 Scene View 위에 그림
- Hierarchy 패널에서 `EditorState.IsEditingPrefab`을 체크하여 루트 레벨 드래그/삭제 제한

### UI 편집 기즈모 (기존)
- `UITransformGizmo2D` (`Editor/SceneView/UITransformGizmo2D.cs`): Translate/Rotate/Scale 기즈모 (2D, screen-space)
- `RectGizmoEditor` (`Editor/SceneView/RectGizmoEditor.cs`): Rect 도구 (8-handle 리사이즈)
- `ImGuiOverlay.UpdateSceneViewInput()`에서 RectTransform 유무에 따라 자동 전환

### Inspector에서 컴포넌트별 전용 버튼 패턴
- `ImGuiInspectorPanel`에서 Collider 컴포넌트에 "Edit Collider" 버튼 패턴이 이미 존재 (line 858~866)
- Animator 컴포넌트에 "Edit Animation" 버튼도 동일 패턴

## 설계

### 개요

Canvas Edit Mode는 Prefab Edit Mode와 **병렬적인** 별도 편집 모드로 구현한다. 씬을 클리어하지 않고, 대신 **Scene View의 렌더링/입력을 2D Canvas 전용 모드로 전환**한다. 이 접근의 이유:

1. Canvas 편집 중에도 씬의 다른 오브젝트 데이터가 메모리에 유지되어야 함 (Canvas가 씬 내 오브젝트를 참조할 수 있음)
2. Prefab Edit Mode처럼 씬을 클리어하고 다시 로드하면 비용이 크고 불필요한 복잡성 유발
3. 핵심은 **뷰포트의 렌더링과 입력 처리 방식의 전환**이지, 씬 데이터의 격리가 아님

### 상세 설계

#### 1. CanvasEditMode 정적 클래스 (새 파일)

**파일**: `src/IronRose.Engine/Editor/CanvasEditMode.cs`

```csharp
public static class CanvasEditMode
{
    // 현재 편집 중인 Canvas의 GameObject ID
    public static bool IsActive => EditorState.IsEditingCanvas;
    public static int? EditingCanvasGoId => EditorState.EditingCanvasGoId;

    // 2D 뷰 상태
    public static Vector2 ViewOffset;   // 패닝 오프셋 (픽셀)
    public static float ViewZoom = 1.0f; // 확대/축소 배율

    public static void Enter(GameObject canvasGo);
    public static void Exit();
    public static void ResetView(); // 뷰를 Canvas 전체에 맞게 초기화
}
```

**Enter() 동작**:
1. `EditorState.IsEditingCanvas = true`, `EditorState.EditingCanvasGoId = canvasGo.GetInstanceID()`
2. `EditorState.SavedEditorCameraState`에 현재 EditorCamera 상태 (Position, Rotation, Pivot) 저장
3. ViewOffset/ViewZoom 초기화 (Canvas 전체가 보이도록)
4. `EditorSelection.Clear()` 후 Canvas GO 선택

**Exit() 동작**:
1. 저장했던 EditorCamera 상태 복원
2. `EditorState.IsEditingCanvas = false`, `EditorState.EditingCanvasGoId = null`
3. `EditorSelection.Clear()`

#### 2. EditorState 확장

**파일**: `src/IronRose.Engine/Editor/EditorState.cs`

추가할 상태 필드:
```csharp
// ── Canvas Edit Mode ──
public static bool IsEditingCanvas { get; set; } = false;
public static int? EditingCanvasGoId { get; set; }

// Canvas Edit Mode 뷰 설정
public static string CanvasEditAspectRatio { get; set; } = "16:9";
public static int CanvasEditCustomWidth { get; set; } = 1920;
public static int CanvasEditCustomHeight { get; set; } = 1080;

// EditorCamera 상태 저장 (Canvas Edit Mode 진입/퇴출용)
internal static Vector3? SavedCameraPosition;
internal static Quaternion? SavedCameraRotation;
internal static Vector3? SavedCameraPivot;
```

- Load/Save에 `canvas_edit` 섹션 추가 (aspect_ratio, custom_width, custom_height 영속화)
- `CleanupCanvasEditMode()` 메서드 추가 (앱 종료 시 상태 정리)

#### 3. ImGuiInspectorPanel에 "Edit Canvas" 버튼

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

기존 "Edit Collider" 패턴을 따라 Canvas 컴포넌트 헤더 아래에 추가:

```csharp
// Canvas 컴포넌트 전용 "Edit Canvas" 버튼
if (comp is Canvas canvasComp)
{
    bool editing = EditorState.IsEditingCanvas
        && EditorState.EditingCanvasGoId == selected.GetInstanceID();
    if (editing)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, activeColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, activeHoverColor);
    }
    if (ImGui.Button("Edit Canvas"))
    {
        if (editing)
            CanvasEditMode.Exit();
        else
            CanvasEditMode.Enter(selected);
    }
    if (editing)
        ImGui.PopStyleColor(2);
}
```

위치: line 866 부근 ("Edit Collider" 블록 직후)

#### 4. Scene View 패널의 Canvas Edit Mode 렌더링

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs`

`Draw()` 메서드를 수정하여 Canvas Edit Mode일 때 다른 렌더링 경로를 사용:

```csharp
if (EditorState.IsEditingCanvas)
    DrawCanvasEditView();
else
    DrawNormalSceneView(); // 기존 로직
```

**DrawCanvasEditView() 동작**:
1. Scene View 영역을 체커보드 배경으로 채움 (투명 영역 시각화)
2. 설정된 aspect ratio에 맞는 사각형 영역을 계산
3. `ViewZoom`과 `ViewOffset`을 적용하여 Canvas 렌더 영역 결정
4. `CanvasRenderer.RenderAll(drawList, renderX, renderY, renderW, renderH)` 호출
5. Canvas 영역 테두리 렌더링 (실선, 밝은 색)
6. 해상도 정보 표시 (예: "1920 x 1080 (16:9)")

#### 5. Canvas Edit Mode 전용 툴바

Scene View 툴바를 Canvas Edit Mode일 때 교체:

**표시 항목**:
- 화면 비율 드롭다운: "16:9", "16:10", "4:3", "32:9", "Custom"
  - Custom 선택 시 너비/높이 입력 필드 표시
- 기존 Transform 도구 (W/E/R/T) 유지
- "Back" 버튼 (Canvas Edit Mode 퇴출)
- 현재 줌 비율 표시 (예: "100%")
- Overlays 버튼 (DebugDrawRects 토글)

#### 6. Canvas Edit Mode 전용 입력 처리

**파일**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

`UpdateSceneViewInput()` 메서드를 수정하여 Canvas Edit Mode일 때 별도 입력 처리:

```csharp
if (EditorState.IsEditingCanvas)
{
    UpdateCanvasEditInput(deltaTime);
    return;
}
// ... 기존 3D Scene View 입력 처리
```

**UpdateCanvasEditInput() 동작**:
- **마우스 휠**: `CanvasEditMode.ViewZoom` 조절. 마우스 커서 위치를 기준으로 확대/축소 (포인트 줌). 최소 0.1배 ~ 최대 10배
- **가운데 버튼(MMB) 드래그**: `CanvasEditMode.ViewOffset` 조정 (패닝)
- **F 키**: 선택된 UI 요소의 `lastScreenRect`에 포커스. 한번 더 F → 해당 요소가 뷰에 꽉 차게 확대 (EditorCamera의 더블탭 로직 재활용)
- **LMB 클릭 (기즈모 미사용 시)**: `CanvasRenderer.HitTest()`로 클릭한 UI 요소 선택
- **Transform 도구 (W/E/R/T)**: 기존 `UITransformGizmo2D` / `RectGizmoEditor` 그대로 활용. 이 기즈모들은 이미 screen-space 좌표 기반이므로 zoom/pan 적용된 좌표에서 정상 동작
- **Ctrl+휠**: 스냅 줌 (25%, 50%, 100%, 200%, 400%)

#### 7. Hierarchy 패널 필터링

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiHierarchyPanel.cs`

Canvas Edit Mode일 때 Canvas GO의 자식만 표시:

```csharp
// 루트 레벨 순회 시
if (EditorState.IsEditingCanvas)
{
    // Canvas GO의 자식만 표시
    var canvasGoId = EditorState.EditingCanvasGoId;
    var canvasGo = UndoUtility.FindGameObjectById(canvasGoId.Value);
    if (canvasGo != null)
    {
        // Canvas GO 자체를 루트로 표시하고, 그 하위만 트리에 포함
        DrawTreeNode(canvasGo, ...);
    }
}
else
{
    // 기존 전체 씬 트리 표시
}
```

- Canvas Edit Mode 중 루트 레벨 드래그/삭제 제한 (Prefab Edit Mode와 동일 패턴)
- Canvas 외부 오브젝트 생성 차단

#### 8. Canvas Edit Mode Breadcrumb 오버레이

**파일**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

Prefab Edit Mode와 유사한 breadcrumb child window를 Scene View 좌상단에 표시:

```
[Scene Name] > [Canvas GO Name]    [Back]
```

- "Back" 클릭 시 `CanvasEditMode.Exit()` 호출
- Prefab Edit Mode 내에서 Canvas Edit Mode 진입도 가능 (Canvas가 프리팹 내에 있을 때)

#### 9. 3D Scene View 렌더링 억제

Canvas Edit Mode 중에는 `RenderSceneView(CommandList cl)` 에서 3D 씬 렌더링을 스킵:

```csharp
public void RenderSceneView(CommandList cl)
{
    if (EditorState.IsEditingCanvas) return; // 3D 렌더 불필요
    // ... 기존 3D 렌더링
}
```

Scene View 패널의 `GetRenderTargetSize()`도 Canvas Edit Mode일 때 최소 크기 반환하여 RT 리소스 절약.

#### 10. Aspect Ratio 설정 구조

```csharp
public static class CanvasEditAspectPresets
{
    public static readonly (string Name, float W, float H)[] Presets = {
        ("16:9",  16f, 9f),
        ("16:10", 16f, 10f),
        ("4:3",   4f, 3f),
        ("32:9",  32f, 9f),
        ("Custom", 0f, 0f),
    };
}
```

Custom일 때는 `EditorState.CanvasEditCustomWidth/Height`를 직접 사용. Canvas의 `referenceResolution`을 기본값으로 표시.

#### 11. 체커보드 배경 렌더링

Canvas 뷰 배경에 체커보드 패턴 표시 (투명 영역 시각화):

```csharp
// ImGui DrawList로 체커보드 패턴을 타일링
private void DrawCheckerboard(ImDrawListPtr dl, Vector2 min, Vector2 max)
{
    const float tileSize = 16f;
    uint col1 = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 1f));
    uint col2 = ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.30f, 1f));
    // 타일 반복 렌더링
}
```

### 영향 범위

| 파일 | 변경 내용 |
|------|----------|
| `Editor/CanvasEditMode.cs` | **신규** - Canvas Edit Mode 진입/퇴출 로직 |
| `Editor/EditorState.cs` | Canvas Edit Mode 상태 필드 추가, Load/Save 확장, 카메라 상태 저장 |
| `Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | Canvas 컴포넌트에 "Edit Canvas" 버튼 추가 |
| `Editor/ImGui/Panels/ImGuiSceneViewPanel.cs` | Canvas Edit Mode 전용 렌더링/툴바 추가 |
| `Editor/ImGui/Panels/ImGuiHierarchyPanel.cs` | Canvas Edit Mode 시 Canvas 하위만 표시하는 필터링 |
| `Editor/ImGui/ImGuiOverlay.cs` | Canvas Edit Mode 입력 처리, Breadcrumb 오버레이, 3D 렌더 스킵 |

### 기존 기능에 미치는 영향

- **UITransformGizmo2D / RectGizmoEditor**: 변경 불필요. screen-space 좌표 기반이므로 zoom/pan이 적용된 Canvas 렌더 결과 위에서 정상 동작. `lastScreenRect`는 `CanvasRenderer.RenderAll()` 호출 시 자동 갱신됨
- **CanvasRenderer**: 변경 불필요. 이미 임의의 screen 좌표/크기에 대해 렌더 가능
- **Prefab Edit Mode**: 병렬 동작 가능하도록 설계. 프리팹 내 Canvas 편집 시 Prefab Edit Mode + Canvas Edit Mode 동시 활성화
- **EditorPlayMode**: Canvas Edit Mode 중 Play 진입 시 자동으로 Canvas Edit Mode 퇴출

## 대안 검토

### 대안 A: Prefab Edit Mode처럼 씬 클리어 + Canvas만 로드
- **기각 이유**: Canvas가 씬 내 다른 오브젝트를 참조할 수 있음 (스크립트 등). 씬 클리어 시 참조가 끊어짐. 또한 Canvas 편집은 데이터 격리가 아닌 뷰 전환이 핵심이므로 과도한 구현

### 대안 B: Scene View에 2D/3D 토글 추가 (유니티 스타일)
- **기각 이유**: 요구사항이 "Canvas 전용 편집"이므로 범용 2D 모드보다 Canvas에 특화된 모드가 더 적합. 향후 2D 모드가 필요해지면 Canvas Edit Mode를 기반으로 확장 가능

### 대안 C: 별도 패널 (Canvas Editor Panel)로 구현
- **기각 이유**: 기존 Scene View, Hierarchy, Inspector의 인프라를 재사용하는 것이 더 효율적. 별도 패널은 중복 구현이 많아짐

## 미결 사항

없음. 설계에 필요한 모든 정보가 코드 분석으로 확인됨.
