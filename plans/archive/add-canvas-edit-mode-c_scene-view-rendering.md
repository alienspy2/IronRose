# Phase 3: Scene View 전용 렌더링 (체커보드 + 줌/팬 뷰 + 툴바 + 3D 렌더 스킵)

## 목표
- Canvas Edit Mode일 때 Scene View 패널이 3D 씬 대신 2D Canvas 전용 뷰를 렌더링하도록 한다.
- 체커보드 배경, aspect ratio에 맞는 Canvas 영역, 줌/팬이 적용된 Canvas UI 렌더링을 구현한다.
- 전용 툴바 (aspect ratio 드롭다운, Back 버튼, 줌 표시 등)를 제공한다.
- Canvas Edit Mode 중 3D 씬 렌더링을 스킵하여 리소스를 절약한다.

## 선행 조건
- Phase 1 (add-canvas-edit-mode-a_state-and-core) 완료
- `CanvasEditMode.ViewOffset`, `CanvasEditMode.ViewZoom`, `EditorState.CanvasEditAspectRatio` 등이 존재해야 함

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs`

- **변경 내용**: Canvas Edit Mode 전용 렌더링 경로와 전용 툴바 추가

#### 변경 1: Draw() 메서드에 Canvas Edit Mode 분기 추가

기존 `Draw()` 메서드의 이미지 렌더링 부분 (line 168 부근, `if (_textureId != IntPtr.Zero && contentSize.X > 1 && contentSize.Y > 1)`)을 Canvas Edit Mode 분기로 감싼다:

```csharp
if (EditorState.IsEditingCanvas)
{
    DrawCanvasEditView(contentSize);
}
else if (_textureId != IntPtr.Zero && contentSize.X > 1 && contentSize.Y > 1)
{
    // ... 기존 3D Scene View 렌더링 코드 전체 (Image, UI overlay, gizmo overlay, drag-drop, RMB context 등)
}
else
{
    _isImageHovered = false;
    ImGui.TextDisabled("No render target");
}
```

#### 변경 2: DrawToolbar() 메서드에 Canvas Edit Mode 분기 추가

`DrawToolbar()` 메서드 시작 부분에서 Canvas Edit Mode일 때 별도 툴바를 그리고 return:

```csharp
private void DrawToolbar()
{
    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 2));
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 4);
    ImGui.AlignTextToFramePadding();

    if (EditorState.IsEditingCanvas)
    {
        DrawCanvasEditToolbar();
        ImGui.PopStyleVar();
        return;
    }

    // ... 기존 툴바 코드
}
```

#### 변경 3: GetRenderTargetSize() 수정

Canvas Edit Mode일 때 최소 크기 반환하여 RT 리소스 절약:

```csharp
public (uint W, uint H) GetRenderTargetSize(uint swapchainW, uint swapchainH)
{
    // Canvas Edit Mode: 3D RT 불필요, 최소 크기 반환
    if (EditorState.IsEditingCanvas)
        return (MinRTSize, MinRTSize);

    // ... 기존 로직
}
```

#### 신규 메서드: DrawCanvasEditView()

```csharp
private void DrawCanvasEditView(Vector2 contentSize)
{
    // Canvas Edit Mode: ImGui DrawList 기반 2D 뷰
    _imageAreaSize = contentSize;

    // 더미 영역 확보 (입력 감지용)
    ImGui.InvisibleButton("##canvas_edit_area", contentSize);
    _isImageHovered = ImGui.IsItemHovered();
    _imageScreenMin = ImGui.GetItemRectMin();
    _imageScreenMax = ImGui.GetItemRectMax();

    var dl = ImGui.GetWindowDrawList();

    // 1) 배경 클리핑
    dl.PushClipRect(_imageScreenMin, _imageScreenMax, true);

    // 2) 전체 영역 어두운 배경
    uint bgColor = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f));
    dl.AddRectFilled(_imageScreenMin, _imageScreenMax, bgColor);

    // 3) Canvas 영역 계산 (aspect ratio + zoom + offset)
    float viewW = contentSize.X;
    float viewH = contentSize.Y;
    var (canvasScreenMin, canvasScreenMax) = CalculateCanvasRect(viewW, viewH);

    // 4) 체커보드 배경 (Canvas 영역 내부)
    DrawCheckerboard(dl, canvasScreenMin, canvasScreenMax);

    // 5) Canvas UI 렌더링
    float canvasW = canvasScreenMax.X - canvasScreenMin.X;
    float canvasH = canvasScreenMax.Y - canvasScreenMin.Y;
    if (canvasW > 0 && canvasH > 0)
    {
        RoseEngine.CanvasRenderer.IsInteractive = false;
        RoseEngine.CanvasRenderer.RenderAll(dl, canvasScreenMin.X, canvasScreenMin.Y, canvasW, canvasH);
        RoseEngine.CanvasRenderer.IsInteractive = true;
    }

    // 6) Canvas 영역 테두리
    uint borderColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1f));
    dl.AddRect(canvasScreenMin, canvasScreenMax, borderColor, 0f, ImDrawFlags.None, 1f);

    // 7) 해상도 정보 표시
    var (resW, resH) = GetCanvasResolution();
    string resText = $"{resW} x {resH} ({EditorState.CanvasEditAspectRatio})";
    var textPos = new Vector2(canvasScreenMin.X, canvasScreenMax.Y + 4f);
    dl.AddText(textPos, ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), resText);

    dl.PopClipRect();

    // 2D gizmo overlays
    DrawGizmoOverlay?.Invoke();

    // Prefab/Canvas overlays
    DrawPrefabOverlay?.Invoke();
}
```

#### 신규 메서드: CalculateCanvasRect()

```csharp
/// <summary>
/// Aspect ratio, zoom, offset을 적용하여 Canvas가 그려질 screen-space 사각형을 계산.
/// </summary>
private (Vector2 min, Vector2 max) CalculateCanvasRect(float viewW, float viewH)
{
    var (resW, resH) = GetCanvasResolution();
    float canvasAspect = (float)resW / resH;

    // Canvas 기본 크기: 뷰 영역에 맞추기 (fit)
    float fitW, fitH;
    float viewAspect = viewW / viewH;
    if (canvasAspect > viewAspect)
    {
        fitW = viewW * 0.9f;  // 여백 10%
        fitH = fitW / canvasAspect;
    }
    else
    {
        fitH = viewH * 0.9f;
        fitW = fitH * canvasAspect;
    }

    // 줌 적용
    float zoomedW = fitW * CanvasEditMode.ViewZoom;
    float zoomedH = fitH * CanvasEditMode.ViewZoom;

    // 중앙 정렬 + 오프셋
    float centerX = _imageScreenMin.X + viewW * 0.5f + CanvasEditMode.ViewOffset.X;
    float centerY = _imageScreenMin.Y + viewH * 0.5f + CanvasEditMode.ViewOffset.Y;

    var min = new Vector2(centerX - zoomedW * 0.5f, centerY - zoomedH * 0.5f);
    var max = new Vector2(centerX + zoomedW * 0.5f, centerY + zoomedH * 0.5f);
    return (min, max);
}
```

#### 신규 메서드: GetCanvasResolution()

```csharp
private static (int w, int h) GetCanvasResolution()
{
    string ar = EditorState.CanvasEditAspectRatio;
    if (ar == "Custom")
        return (EditorState.CanvasEditCustomWidth, EditorState.CanvasEditCustomHeight);

    // 프리셋에서 비율을 가져와 referenceResolution 기반으로 해상도 계산
    float ratioW = 16f, ratioH = 9f;
    switch (ar)
    {
        case "16:9":  ratioW = 16f; ratioH = 9f; break;
        case "16:10": ratioW = 16f; ratioH = 10f; break;
        case "4:3":   ratioW = 4f;  ratioH = 3f; break;
        case "32:9":  ratioW = 32f; ratioH = 9f; break;
    }

    // 편집 중인 Canvas의 referenceResolution을 기본 높이로 사용
    int baseH = 1080;
    if (EditorState.EditingCanvasGoId.HasValue)
    {
        var go = UndoUtility.FindGameObjectById(EditorState.EditingCanvasGoId.Value);
        var canvas = go?.GetComponent<RoseEngine.Canvas>();
        if (canvas != null)
            baseH = (int)canvas.referenceResolution.y;
    }

    int h = baseH;
    int w = (int)(h * ratioW / ratioH);
    return (w, h);
}
```

#### 신규 메서드: DrawCheckerboard()

```csharp
private static void DrawCheckerboard(ImDrawListPtr dl, Vector2 min, Vector2 max)
{
    const float tileSize = 16f;
    uint col1 = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 1f));
    uint col2 = ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.30f, 1f));

    dl.PushClipRect(min, max, true);

    float startX = min.X;
    float startY = min.Y;
    float endX = max.X;
    float endY = max.Y;

    for (float y = startY; y < endY; y += tileSize)
    {
        for (float x = startX; x < endX; x += tileSize)
        {
            int ix = (int)((x - startX) / tileSize);
            int iy = (int)((y - startY) / tileSize);
            uint col = ((ix + iy) % 2 == 0) ? col1 : col2;

            var tileMin = new Vector2(x, y);
            var tileMax = new Vector2(
                MathF.Min(x + tileSize, endX),
                MathF.Min(y + tileSize, endY));
            dl.AddRectFilled(tileMin, tileMax, col);
        }
    }

    dl.PopClipRect();
}
```

#### 신규 메서드: DrawCanvasEditToolbar()

```csharp
private void DrawCanvasEditToolbar()
{
    // Aspect ratio 드롭다운
    string[] aspectNames = { "16:9", "16:10", "4:3", "32:9", "Custom" };
    int currentIdx = Array.IndexOf(aspectNames, EditorState.CanvasEditAspectRatio);
    if (currentIdx < 0) currentIdx = 0;

    ImGui.SetNextItemWidth(90);
    if (ImGui.Combo("##AspectRatio", ref currentIdx, aspectNames, aspectNames.Length))
    {
        EditorState.CanvasEditAspectRatio = aspectNames[currentIdx];
        EditorState.Save();
    }

    // Custom일 때 너비/높이 입력
    if (EditorState.CanvasEditAspectRatio == "Custom")
    {
        ImGui.SameLine();
        int cw = EditorState.CanvasEditCustomWidth;
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("##CW", ref cw, 0))
        {
            EditorState.CanvasEditCustomWidth = Math.Max(cw, 1);
            EditorState.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("x");
        ImGui.SameLine();
        int ch = EditorState.CanvasEditCustomHeight;
        ImGui.SetNextItemWidth(60);
        if (ImGui.InputInt("##CH", ref ch, 0))
        {
            EditorState.CanvasEditCustomHeight = Math.Max(ch, 1);
            EditorState.Save();
        }
    }

    ImGui.SameLine();
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);

    // Transform tools (기존 W/E/R/T 유지)
    bool isTranslate = _selectedToolIdx == 0;
    bool isRotate = _selectedToolIdx == 1;
    bool isScale = _selectedToolIdx == 2;
    bool isRect = _selectedToolIdx == 3;

    if (ToolButton("W", isTranslate)) _selectedToolIdx = 0;
    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Translate (W)");
    ImGui.SameLine();
    if (ToolButton("E", isRotate)) _selectedToolIdx = 1;
    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rotate (E)");
    ImGui.SameLine();
    if (ToolButton("R", isScale)) _selectedToolIdx = 2;
    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scale (R)");
    ImGui.SameLine();
    if (ToolButton("T", isRect)) _selectedToolIdx = 3;
    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Rect (T)");

    // 줌 비율 표시
    ImGui.SameLine();
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
    ImGui.TextUnformatted($"{(int)(CanvasEditMode.ViewZoom * 100)}%");

    // Overlays (DebugDrawRects 토글)
    ImGui.SameLine();
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
    if (ImGui.Button("Overlays##canvas"))
        ImGui.OpenPopup("##canvas_overlay_popup");
    if (ImGui.BeginPopup("##canvas_overlay_popup"))
    {
        bool debugRects = RoseEngine.CanvasRenderer.DebugDrawRects;
        if (ImGui.Checkbox("Debug Rects", ref debugRects))
            RoseEngine.CanvasRenderer.DebugDrawRects = debugRects;
        ImGui.EndPopup();
    }

    // Back 버튼
    ImGui.SameLine();
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
    if (ImGui.Button("Back"))
        CanvasEditMode.Exit();
}
```

- **이유**: Scene View를 Canvas Edit Mode 전용 2D 뷰로 전환하고, 3D 렌더링을 스킵하여 리소스를 절약한다.

- **구현 힌트**:
  - `ImGui.GetWindowDrawList()`은 현재 윈도우의 DrawList를 반환. `DrawCanvasEditView()`는 Scene View 윈도우 컨텍스트 내에서 호출되므로 정상 동작.
  - `ImGui.InvisibleButton()`으로 영역을 확보하면 `IsItemHovered()`, `GetItemRectMin/Max()`가 정상 동작하여 기존 `_isImageHovered`, `_imageScreenMin/Max` 업데이트가 가능.
  - `CanvasRenderer.RenderAll(dl, x, y, w, h)`은 이미 임의의 screen-space 좌표에 대해 렌더링 가능하므로, zoom/pan이 적용된 좌표를 전달하면 된다.
  - `using IronRose.Engine.Editor;`가 파일에 이미 있으므로 `CanvasEditMode`, `EditorState`에 직접 접근 가능.
  - `ImDrawListPtr`은 `ImGuiNET` 네임스페이스에 있으며 `using ImGuiNET;`이 이미 존재.

### `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

- **변경 내용**: `RenderSceneView()` 메서드에서 Canvas Edit Mode일 때 3D 렌더링 스킵

`RenderSceneView()` 메서드 (line 2457) 시작 부분에 early return 추가:

```csharp
public void RenderSceneView(CommandList cl)
{
    if (!IsVisible || _sceneRenderer == null || _editorCamera == null || _sceneView == null)
        return;

    // Canvas Edit Mode: 3D 씬 렌더링 불필요 (ImGui DrawList 기반 2D 렌더링)
    if (EditorState.IsEditingCanvas)
        return;

    // ... 기존 로직
}
```

- **이유**: Canvas Edit Mode에서는 3D 씬 렌더링이 불필요하다. Scene View에서 ImGui DrawList로 직접 2D Canvas를 그리므로 GPU 렌더 파이프라인을 실행할 필요가 없다.

## 생성할 파일
- 없음

## NuGet 패키지
- 없음

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] Canvas Edit Mode 진입 시 Scene View가 3D 씬 대신 어두운 배경에 체커보드 패턴의 Canvas 영역을 표시
- [ ] Aspect ratio 드롭다운에서 "16:9", "16:10", "4:3", "32:9", "Custom" 선택 가능
- [ ] Custom 선택 시 너비/높이 입력 필드 표시
- [ ] 툴바에 W/E/R/T 도구 버튼, 줌 비율, Overlays, Back 버튼이 표시됨
- [ ] Back 버튼 클릭 시 Canvas Edit Mode가 퇴출됨
- [ ] Canvas 영역에 테두리와 해상도 정보가 표시됨
- [ ] 3D 씬 렌더링이 스킵됨 (RenderSceneView가 early return)

## 참고
- `CanvasRenderer.RenderAll(drawList, screenX, screenY, screenW, screenH)` 시그니처를 정확히 사용한다. 첫 번째 인자는 `ImDrawListPtr` 타입.
- 체커보드 렌더링은 타일 수가 많을 수 있으므로, Canvas 영역 내부에만 클리핑하여 성능을 확보한다.
- `CalculateCanvasRect()`에서 fit 비율을 0.9f로 설정하여 Canvas 주변에 여백을 두어 시각적 구분을 명확히 한다.
- 이 phase에서는 줌/팬 입력 처리는 포함하지 않는다 (Phase D에서 구현). 줌 1.0x, 오프셋 0에서 기본 뷰만 표시된다.
- `UndoUtility.FindGameObjectById()`는 `GetCanvasResolution()` 내에서 Canvas 참조 해상도 조회에 사용된다.
