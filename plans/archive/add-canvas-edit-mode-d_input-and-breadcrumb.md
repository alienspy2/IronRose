# Phase 4: 전용 입력 처리 + Breadcrumb 오버레이 + 카메라 상태 저장/복원

## 목표
- Canvas Edit Mode 전용 입력 처리를 구현한다: 마우스 휠 줌, MMB 패닝, F 포커스, LMB 클릭 선택.
- Prefab Edit Mode와 유사한 Breadcrumb 오버레이를 Scene View 좌상단에 표시한다.
- EditorCamera 상태 저장/복원을 완성하여 Canvas Edit Mode 진입/퇴출 시 카메라 위치가 보존되도록 한다.

## 선행 조건
- Phase 1 (add-canvas-edit-mode-a_state-and-core) 완료
- Phase 3 (add-canvas-edit-mode-c_scene-view-rendering) 완료
- `CanvasEditMode`, `EditorState`의 Canvas 관련 필드, Scene View의 `DrawCanvasEditView()` 등이 존재해야 함

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

#### 변경 1: UpdateSceneViewInput()에 Canvas Edit Mode 분기 추가

`UpdateSceneViewInput()` 메서드 (line 2152) 시작 부분, `_sceneView.ProcessShortcuts();` 직후에 Canvas Edit Mode 분기 추가:

```csharp
private void UpdateSceneViewInput(float deltaTime)
{
    if (_sceneView == null || _editorCamera == null) return;

    _sceneView.ProcessShortcuts();

    // Canvas Edit Mode: 전용 2D 입력 처리
    if (EditorState.IsEditingCanvas)
    {
        UpdateCanvasEditInput(deltaTime);
        return;
    }

    // ... 기존 3D Scene View 입력 처리 코드 전체
}
```

#### 변경 2: 신규 메서드 UpdateCanvasEditInput()

```csharp
/// <summary>
/// Canvas Edit Mode 전용 입력 처리.
/// 마우스 휠 줌, MMB 패닝, F 포커스, LMB 클릭 선택.
/// </summary>
private void UpdateCanvasEditInput(float deltaTime)
{
    if (_sceneView == null) return;

    // 기즈모 업데이트 (기존 UITransformGizmo2D / RectGizmoEditor 그대로 활용)
    var selectedGo = EditorSelection.SelectedGameObject;
    bool hasRectTransform = selectedGo?.GetComponent<RoseEngine.RectTransform>() != null;
    bool isRectTool = _sceneView.SelectedTool == TransformTool.Rect;
    bool useUI2DGizmo = hasRectTransform
        && (_sceneView.SelectedTool == TransformTool.Translate
            || _sceneView.SelectedTool == TransformTool.Rotate
            || _sceneView.SelectedTool == TransformTool.Scale);

    if (isRectTool && hasRectTransform && _rectGizmoEditor != null)
        _rectGizmoEditor.Update(_sceneView);
    else if (useUI2DGizmo && _uiTransformGizmo != null)
        _uiTransformGizmo.Update(_sceneView);

    if (!_sceneView.IsImageHovered) return;

    var io = ImGui.GetIO();

    // ── 마우스 휠: 줌 ──
    if (MathF.Abs(io.MouseWheel) > 0.001f)
    {
        if (io.KeyCtrl)
        {
            // Ctrl+휠: 스냅 줌 (25%, 50%, 100%, 200%, 400%)
            float[] snapLevels = { 0.25f, 0.5f, 1.0f, 2.0f, 4.0f };
            float currentZoom = CanvasEditMode.ViewZoom;
            int nearest = 0;
            float minDist = float.MaxValue;
            for (int i = 0; i < snapLevels.Length; i++)
            {
                float dist = MathF.Abs(currentZoom - snapLevels[i]);
                if (dist < minDist) { minDist = dist; nearest = i; }
            }
            int targetIdx = io.MouseWheel > 0
                ? Math.Min(nearest + 1, snapLevels.Length - 1)
                : Math.Max(nearest - 1, 0);
            CanvasEditMode.ViewZoom = snapLevels[targetIdx];
        }
        else
        {
            // 일반 휠: 포인트 줌 (마우스 커서 기준)
            float oldZoom = CanvasEditMode.ViewZoom;
            float zoomFactor = io.MouseWheel > 0 ? 1.15f : 1f / 1.15f;
            float newZoom = Math.Clamp(oldZoom * zoomFactor, 0.1f, 10f);

            // 포인트 줌: 마우스 위치가 줌 후에도 같은 Canvas 위치를 가리키도록 오프셋 보정
            var viewCenter = new Vector2(
                (_sceneView.ImageScreenMin.X + _sceneView.ImageScreenMax.X) * 0.5f,
                (_sceneView.ImageScreenMin.Y + _sceneView.ImageScreenMax.Y) * 0.5f);
            var mouseOffset = io.MousePos - viewCenter - CanvasEditMode.ViewOffset;
            CanvasEditMode.ViewOffset -= mouseOffset * (newZoom / oldZoom - 1f);

            CanvasEditMode.ViewZoom = newZoom;
        }
    }

    // ── MMB 드래그: 패닝 ──
    if (io.MouseDown[2])
    {
        CanvasEditMode.ViewOffset += io.MouseDelta;
    }

    // ── F 키: 포커스 ──
    bool fKeyPressed = ImGui.IsKeyPressed(ImGuiKey.F)
        && !ImGui.IsKeyDown(ImGuiKey.ModCtrl)
        && !ImGui.IsKeyDown(ImGuiKey.ModShift)
        && !ImGui.IsKeyDown(ImGuiKey.ModAlt);

    if (fKeyPressed)
    {
        // 선택된 UI 요소에 포커스 또는 전체 뷰 리셋
        var focusGo = EditorSelection.SelectedGameObject;
        var focusRt = focusGo?.GetComponent<RoseEngine.RectTransform>();
        if (focusRt != null && focusRt.lastScreenRect.z > 0 && focusRt.lastScreenRect.w > 0)
        {
            // TODO: 선택 요소 위치로 뷰 이동 (lastScreenRect 기반)
            // 현재는 전체 뷰 리셋으로 대체
            CanvasEditMode.ResetView();
        }
        else
        {
            CanvasEditMode.ResetView();
        }
    }

    // ── LMB 클릭: UI 요소 선택 (기즈모 미사용 시) ──
    bool gizmoInteracting = (_rectGizmoEditor?.IsDragging ?? false) || (_uiTransformGizmo?.IsDragging ?? false);

    if (io.MouseClicked[0] && !io.KeyAlt && !io.MouseDown[1] && !io.MouseDown[2] && !gizmoInteracting)
    {
        var min = _sceneView.ImageScreenMin;
        var max = _sceneView.ImageScreenMax;
        var mouse = io.MousePos;
        if (mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y)
        {
            float imgW = max.X - min.X;
            float imgH = max.Y - min.Y;
            bool ctrlHeld = io.KeyCtrl;

            var uiHit = RoseEngine.CanvasRenderer.HitTest(mouse.X, mouse.Y, min.X, min.Y, imgW, imgH);
            if (uiHit != null)
            {
                int id = uiHit.GetInstanceID();
                if (ctrlHeld)
                    EditorSelection.ToggleSelect(id);
                else
                    EditorSelection.Select(id);
            }
            else if (!ctrlHeld)
            {
                EditorSelection.Clear();
            }
        }
    }
}
```

#### 변경 3: DrawPrefabOverlaysInSceneView() 확장하여 Canvas Breadcrumb 추가

기존 메서드 (line 1209)를 확장:

```csharp
private void DrawPrefabOverlaysInSceneView()
{
    // Canvas Edit Mode breadcrumb
    if (EditorState.IsEditingCanvas)
    {
        DrawCanvasBreadcrumbChild();
        // Canvas Edit Mode에서도 Prefab Edit Mode 중일 수 있음
    }

    if (!EditorState.IsEditingPrefab) return;
    if (_sceneView == null) return;

    DrawPrefabBreadcrumbChild();
    DrawPrefabVariantTreeChild();
}
```

#### 변경 4: 신규 메서드 DrawCanvasBreadcrumbChild()

Prefab Breadcrumb (`DrawPrefabBreadcrumbChild()` line 1221)의 패턴을 따라 구현:

```csharp
/// <summary>Canvas Edit Mode Breadcrumb: Scene View 이미지 좌상단에 child window로 배치.</summary>
private void DrawCanvasBreadcrumbChild()
{
    if (_sceneView == null) return;
    var imgMin = _sceneView.ImageScreenMin;
    var imgMax = _sceneView.ImageScreenMax;
    if (imgMax.X - imgMin.X < 1 || imgMax.Y - imgMin.Y < 1) return;

    // 씬 이름
    string sceneName = "Scene";
    var scene = RoseEngine.SceneManager.GetActiveScene();
    if (!string.IsNullOrEmpty(scene.name))
        sceneName = scene.name;

    // Canvas GO 이름
    string canvasName = "Canvas";
    if (EditorState.EditingCanvasGoId.HasValue)
    {
        var go = UndoUtility.FindGameObjectById(EditorState.EditingCanvasGoId.Value);
        if (go != null) canvasName = go.name;
    }

    const float pad = 6f;
    const float childPadX = 8f;
    const float childPadY = 4f;

    // Prefab Edit Mode의 breadcrumb가 이미 있을 수 있으므로 다른 Y 위치 사용
    float topY = imgMin.Y + pad;
    var bcScreenPos = new Vector2(imgMin.X + pad, topY);

    ImGui.SetCursorScreenPos(bcScreenPos);

    float maxWidth = imgMax.X - imgMin.X - pad * 2;
    ImGui.PushStyleColor(ImGuiCol.ChildBg,
        new Vector4(0.18f, 0.25f, 0.12f, 0.92f));  // 녹색 계열 (Prefab의 파란색과 구별)
    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(childPadX, childPadY));
    ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);

    if (ImGui.BeginChild("##CanvasBreadcrumb", new Vector2(maxWidth, 0),
            ImGuiChildFlags.AutoResizeX | ImGuiChildFlags.AutoResizeY
            | ImGuiChildFlags.AlwaysAutoResize,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
    {
        // Scene Name
        ImGui.TextDisabled(sceneName);
        ImGui.SameLine();
        ImGui.TextDisabled(">");
        ImGui.SameLine();

        // Canvas GO Name (현재 편집 중)
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.4f, 1.0f), canvasName);

        // Back 버튼
        ImGui.SameLine(0, 20);
        if (ImGui.SmallButton("Back##canvas_bc"))
            CanvasEditMode.Exit();
    }
    ImGui.EndChild();
    ImGui.PopStyleVar(2);
    ImGui.PopStyleColor();
}
```

#### 변경 5: EditorCamera 상태 저장/복원 연동

`CanvasEditMode.Enter()` / `Exit()` 호출 시점에서 ImGuiOverlay가 EditorCamera 상태를 저장/복원해야 한다. 두 가지 접근이 가능:

**접근 A (권장)**: `CanvasEditMode.Enter()`/`Exit()` 내부에서 직접 EditorCamera를 조작할 수 없으므로 (private 소유), Enter/Exit 후 ImGuiOverlay가 상태 변화를 감지하여 처리한다.

`UpdateSceneViewInput()`의 Canvas Edit Mode 분기 시작 부분에서 **최초 진입 감지**:

```csharp
// Canvas Edit Mode: 최초 진입 시 카메라 상태 저장
if (EditorState.IsEditingCanvas && _editorCamera != null
    && EditorState.SavedCanvasCameraPosition == null)
{
    EditorState.SavedCanvasCameraPosition = _editorCamera.Position;
    EditorState.SavedCanvasCameraRotation = _editorCamera.Rotation;
    EditorState.SavedCanvasCameraPivot = _editorCamera.Pivot;
}
```

`UpdateSceneViewInput()`의 Canvas Edit Mode 분기가 아닌 일반 경로 시작 부분에서 **퇴출 후 복원 감지**:

```csharp
// Canvas Edit Mode 퇴출 후 카메라 복원
if (!EditorState.IsEditingCanvas && EditorState.SavedCanvasCameraPosition.HasValue && _editorCamera != null)
{
    _editorCamera.Position = EditorState.SavedCanvasCameraPosition.Value;
    _editorCamera.Rotation = EditorState.SavedCanvasCameraRotation!.Value;
    _editorCamera.Pivot = EditorState.SavedCanvasCameraPivot!.Value;
    EditorState.SavedCanvasCameraPosition = null;
    EditorState.SavedCanvasCameraRotation = null;
    EditorState.SavedCanvasCameraPivot = null;
}
```

위 두 코드 블록의 정확한 위치:
1. 카메라 저장: `UpdateSceneViewInput()` 내부, `_sceneView.ProcessShortcuts();` 직후, Canvas Edit Mode 분기 직전에 배치
2. 카메라 복원: Canvas Edit Mode 분기의 else (일반 경로) 시작, `_sceneView.ProcessShortcuts();` 직후에 배치

더 정확한 패턴:
```csharp
private void UpdateSceneViewInput(float deltaTime)
{
    if (_sceneView == null || _editorCamera == null) return;

    _sceneView.ProcessShortcuts();

    // Canvas Edit Mode 카메라 상태 관리
    if (EditorState.IsEditingCanvas)
    {
        // 최초 진입 시 카메라 상태 저장
        if (EditorState.SavedCanvasCameraPosition == null)
        {
            EditorState.SavedCanvasCameraPosition = _editorCamera.Position;
            EditorState.SavedCanvasCameraRotation = _editorCamera.Rotation;
            EditorState.SavedCanvasCameraPivot = _editorCamera.Pivot;
        }
        UpdateCanvasEditInput(deltaTime);
        return;
    }

    // Canvas Edit Mode 퇴출 후 카메라 복원
    if (EditorState.SavedCanvasCameraPosition.HasValue)
    {
        _editorCamera.Position = EditorState.SavedCanvasCameraPosition.Value;
        _editorCamera.Rotation = EditorState.SavedCanvasCameraRotation!.Value;
        _editorCamera.Pivot = EditorState.SavedCanvasCameraPivot!.Value;
        EditorState.SavedCanvasCameraPosition = null;
        EditorState.SavedCanvasCameraRotation = null;
        EditorState.SavedCanvasCameraPivot = null;
    }

    // ... 기존 3D Scene View 입력 처리
```

- **이유**: Canvas Edit Mode에서 3D 카메라 조작을 완전히 비활성화하고, 대신 2D 줌/팬/포커스 입력을 처리한다. Breadcrumb 오버레이는 현재 편집 위치를 시각적으로 표시한다.

### `src/IronRose.Engine/Editor/CanvasEditMode.cs` (Phase 1에서 생성)

- **변경 내용**: `HitTest()` 호출을 위해 CanvasRenderer의 정확한 시그니처를 확인하고, 필요시 보조 메서드 추가

`CanvasRenderer.HitTest()` 시그니처:
```csharp
public static GameObject? HitTest(float mouseScreenX, float mouseScreenY,
    float screenX, float screenY, float screenW, float screenH)
```

이 시그니처는 `UpdateCanvasEditInput()`에서 직접 사용하므로 `CanvasEditMode.cs` 수정은 불필요할 수 있다. 단, `ResetView()` 로직을 확장할 수 있다:

```csharp
public static void ResetView()
{
    ViewOffset = System.Numerics.Vector2.Zero;
    ViewZoom = 1.0f;
}
```

수정 없음 (Phase 1에서 이미 올바르게 구현).

## 생성할 파일
- 없음

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] Canvas Edit Mode에서 마우스 휠로 Canvas 뷰를 확대/축소할 수 있음 (0.1x ~ 10x)
- [ ] Ctrl+마우스 휠로 25%, 50%, 100%, 200%, 400% 스냅 줌이 동작함
- [ ] MMB(가운데 버튼) 드래그로 Canvas 뷰를 패닝할 수 있음
- [ ] F 키로 뷰가 초기 상태로 리셋됨
- [ ] LMB 클릭으로 Canvas 내 UI 요소를 선택할 수 있음 (CanvasRenderer.HitTest 기반)
- [ ] Scene View 좌상단에 "[Scene Name] > [Canvas Name]  [Back]" 형태의 녹색 Breadcrumb가 표시됨
- [ ] Breadcrumb의 "Back" 버튼으로 Canvas Edit Mode를 퇴출할 수 있음
- [ ] Canvas Edit Mode 진입 시 EditorCamera 상태가 저장되고, 퇴출 시 복원됨
- [ ] Canvas Edit Mode 중 3D 카메라 조작 (RMB fly, Alt+LMB orbit, 휠 zoom)이 비활성화됨

## 참고
- `DrawPrefabBreadcrumbChild()` (line 1221~1291)를 정확히 참조하여 동일 패턴으로 Canvas Breadcrumb를 구현한다. 색상만 녹색 계열로 변경하여 Prefab과 구별한다.
- 포인트 줌 구현: 마우스 커서 위치를 기준으로 줌할 때, `ViewOffset`을 보정하여 커서 아래의 Canvas 좌표가 줌 전후에 동일하게 유지되도록 한다. 공식: `offset -= mouseOffsetFromCenter * (newZoom/oldZoom - 1)`.
- `EditorCamera.Position`, `Rotation`, `Pivot`은 모두 public 필드이므로 직접 읽기/쓰기 가능하다 (EditorCamera.cs line 15~17).
- `_rectGizmoEditor?.IsDragging`과 `_uiTransformGizmo?.IsDragging`은 기즈모가 활성 상태인지 확인하는 프로퍼티. 기즈모 조작 중에는 클릭 선택을 방지한다.
- Prefab Edit Mode 내에서 Canvas Edit Mode 진입이 가능하다 (Canvas가 프리팹 내에 있을 때). `DrawPrefabOverlaysInSceneView()` 수정 시 두 모드의 오버레이가 공존할 수 있도록 한다. Canvas Breadcrumb가 Prefab Breadcrumb 위에 표시되도록 우선순위를 조정한다.
- `CanvasRenderer.HitTest()`는 `DrawCanvasEditView()`에서 `RenderAll()`에 전달한 것과 동일한 screen 좌표를 사용해야 정확한 hit 결과를 얻을 수 있다. `_imageScreenMin/Max`를 그대로 사용하면 된다.
