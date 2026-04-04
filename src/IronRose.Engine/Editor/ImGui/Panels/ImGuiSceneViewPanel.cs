// ------------------------------------------------------------
// @file    ImGuiSceneViewPanel.cs
// @brief   에디터 Scene View 패널. 3D 씬을 렌더링하고 트랜스폼 도구,
//          렌더 모드 선택, 기즈모/UI 오버레이, 에셋 드래그앤드롭을 제공한다.
// @deps    CanvasRenderer, EditorState, EditorSelection, EditorAssets,
//          EditorWidgets, UndoSystem, ImGuiHierarchyPanel, ImGuiProjectPanel
// @exports
//   class ImGuiSceneViewPanel : IEditorPanel
//     void Draw()                — 패널 렌더링
//     void ProcessShortcuts()    — 키보드 단축키 처리
//     (uint,uint) GetRenderTargetSize(...)  — RT 크기 계산
//     (Vector2,Vector2) CalculateCanvasRect(float,float) — 줌/오프셋 적용된 캔버스 스크린 영역 계산 (internal)
// @note    Canvas UI 오버레이 렌더링 시 CanvasRenderer.IsInteractive를 false로 설정하여
//          Scene View에서 게임 UI 입력이 처리되지 않도록 한다.
// ------------------------------------------------------------
using System;
using System.Numerics;
using ImGuiNET;
using IronRose.AssetPipeline;
using IronRose.Engine.Editor;
using IronRose.Rendering;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public enum TransformTool
    {
        Translate,
        Rotate,
        Scale,
        Rect,
    }

    public enum TransformSpace
    {
        World,
        Local,
    }

    public class ImGuiSceneViewPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private IntPtr _textureId;
        private Vector2 _imageAreaSize;

        // Input passthrough state
        private bool _isImageHovered;
        private bool _isWindowFocused;
        private Vector2 _imageScreenMin;
        private Vector2 _imageScreenMax;

        // Layout stabilization
        private int _layoutStableFrames = 0;
        private const int LayoutWarmupFrames = 5;
        private const uint MinRTSize = 128;

        // Render mode
        private int _selectedRenderModeIdx = RenderStyleToIndex(EditorState.SceneViewRenderStyle);
        private int _selectedMatCapIdx = 0;
        private static readonly string[] RenderModeNames = { "Wireframe", "MatCap", "Diffuse Only", "Rendered" };
        private static readonly string[] RenderStyleKeys = { "wireframe", "matcap", "diffuse_only", "rendered" };

        // Transform tools
        private int _selectedToolIdx = 0;
        private int _selectedSpaceIdx = 0;
        // Overlay toggles
        private bool _showGizmos = true;
        private bool _showUI = true;

        // Context menu RMB tracking
        private bool _rmbWasPressed;
        private Vector2 _rmbPressPos;
        private const float RmbDragThreshold = 5f;

        public SceneViewRenderMode SelectedRenderMode => (SceneViewRenderMode)_selectedRenderModeIdx;
        public int SelectedMatCapIndex => _selectedMatCapIdx;
        public TransformTool SelectedTool => (TransformTool)_selectedToolIdx;
        public TransformSpace SelectedSpace => (TransformSpace)_selectedSpaceIdx;
        public bool IsImageHovered => _isImageHovered;
        public bool IsWindowFocused => _isWindowFocused;
        public Vector2 ImageScreenMin => _imageScreenMin;
        public Vector2 ImageScreenMax => _imageScreenMax;
        public bool ShowGizmos => _showGizmos;
        public bool ShowUI => _showUI;

        /// <summary>
        /// Scene View 이미지 위에 2D 오버레이를 그리기 위한 콜백.
        /// ImGui 윈도우 컨텍스트 내에서 호출되므로 GetWindowDrawList() 사용 가능.
        /// </summary>
        internal Action? DrawGizmoOverlay;

        /// <summary>
        /// Prefab Edit Mode 오버레이 (Breadcrumb, Variant Tree).
        /// Scene View 윈도우 컨텍스트 내에서 호출되므로 BeginChild 등 사용 가능.
        /// </summary>
        internal Action? DrawPrefabOverlay;

        // Drag-drop: Scene View에 에셋이 드롭되었을 때의 정보
        private string? _pendingDropAssetPath;
        private Vector2 _pendingDropScreenPos;

        // Material drag hover (AcceptPeekOnly — 매 프레임 호버 감지)
        private bool _isMaterialDragHovering;
        private string? _hoveringMaterialPath;
        private Vector2 _hoveringScreenPos;
        public bool IsMaterialDragHovering => _isMaterialDragHovering;
        public string? HoveringMaterialPath => _hoveringMaterialPath;
        public Vector2 HoveringScreenPos => _hoveringScreenPos;

        public static bool IsMaterialAsset(string path)
        {
            if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) return true;
            return SubAssetPath.TryParse(path, out _, out var subType, out _) && subType == "Material";
        }

        /// <summary>
        /// 대기 중인 에셋 드롭 정보를 소비합니다 (consume-once 패턴).
        /// 드롭이 없으면 null을 반환합니다.
        /// </summary>
        public string? ConsumePendingDropAssetPath(out Vector2 screenPos)
        {
            var path = _pendingDropAssetPath;
            screenPos = _pendingDropScreenPos;
            _pendingDropAssetPath = null;
            return path;
        }

        public void SetTextureId(IntPtr textureId) => _textureId = textureId;

        public void ResetLayoutStabilization() => _layoutStableFrames = 0;

        public (uint W, uint H) GetRenderTargetSize(uint swapchainW, uint swapchainH)
        {
            _layoutStableFrames++;
            if (_layoutStableFrames <= LayoutWarmupFrames)
                return (swapchainW, swapchainH);

            // Canvas Edit Mode: 3D RT 불필요, 최소 크기 반환
            if (EditorState.IsEditingCanvas)
                return (MinRTSize, MinRTSize);

            if (_imageAreaSize.X >= MinRTSize && _imageAreaSize.Y >= MinRTSize)
                return ((uint)_imageAreaSize.X, (uint)_imageAreaSize.Y);

            return (swapchainW, swapchainH);
        }

        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;

            if (!IsOpen)
            {
                _isImageHovered = false;
                _isWindowFocused = false;
                return;
            }

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var sceneViewVisible = ImGui.Begin("Scene View", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Scene View");
            if (sceneViewVisible)
            {
                _isWindowFocused = ImGui.IsWindowFocused();

                DrawToolbar();
                ImGui.Separator();

                var contentSize = ImGui.GetContentRegionAvail();
                _imageAreaSize = contentSize;

                if (EditorState.IsEditingCanvas)
                {
                    DrawCanvasEditView(contentSize);
                }
                else if (_textureId != IntPtr.Zero && contentSize.X > 1 && contentSize.Y > 1)
                {
                    ImGui.Image(_textureId, contentSize);
                    _isImageHovered = ImGui.IsItemHovered();
                    _imageScreenMin = ImGui.GetItemRectMin();
                    _imageScreenMax = ImGui.GetItemRectMax();

                    // Canvas UI overlay (Scene View: 렌더링만, 입력 처리 비활성화)
                    if (_showUI)
                    {
                        var dl = ImGui.GetWindowDrawList();
                        float imgW = _imageScreenMax.X - _imageScreenMin.X;
                        float imgH = _imageScreenMax.Y - _imageScreenMin.Y;
                        RoseEngine.CanvasRenderer.IsInteractive = false;
                        RoseEngine.CanvasRenderer.RenderAll(dl, _imageScreenMin.X, _imageScreenMin.Y, imgW, imgH);
                        RoseEngine.CanvasRenderer.IsInteractive = true;
                    }

                    // 2D gizmo overlays (within window context for correct viewport draw list)
                    DrawGizmoOverlay?.Invoke();

                    // Prefab Edit Mode 오버레이 (Scene View 윈도우 컨텍스트 내에서 그려 Z-order 문제 방지)
                    DrawPrefabOverlay?.Invoke();

                    // Drag-drop target: Project 패널에서 에셋을 드롭 받음
                    _isMaterialDragHovering = false;
                    if (ImGui.BeginDragDropTarget())
                    {
                        unsafe
                        {
                            // Peek pass: 매 프레임 호버 감지 (드롭 소비 없이)
                            var peek = ImGui.AcceptDragDropPayload("ASSET_PATH",
                                ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
                            if (peek.NativePtr != null)
                            {
                                var path = ImGuiProjectPanel._draggedAssetPath;
                                if (path != null && IsMaterialAsset(path))
                                {
                                    _isMaterialDragHovering = true;
                                    _hoveringMaterialPath = path;
                                    _hoveringScreenPos = ImGui.GetMousePos();
                                }
                            }

                            // Delivery pass: 실제 드롭
                            var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                            if (payload.NativePtr != null)
                            {
                                _pendingDropAssetPath = ImGuiProjectPanel._draggedAssetPath;
                                _pendingDropScreenPos = ImGui.GetMousePos();
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }

                    // ── RMB 클릭 감지 (fly mode 드래그와 구분) ──
                    var io = ImGui.GetIO();
                    if (_isImageHovered && io.MouseClicked[1])
                    {
                        _rmbWasPressed = true;
                        _rmbPressPos = io.MousePos;
                    }

                    if (_rmbWasPressed && io.MouseReleased[1])
                    {
                        _rmbWasPressed = false;
                        var delta = io.MousePos - _rmbPressPos;
                        float dist = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
                        if (dist < RmbDragThreshold)
                            ImGui.OpenPopup("##sceneview_ctx");
                    }

                    if (_rmbWasPressed && !_isImageHovered && !io.MouseDown[1])
                        _rmbWasPressed = false;

                    if (ImGui.BeginPopup("##sceneview_ctx"))
                    {
                        RoseEngine.GameObject? ctxGo = null;
                        var selId = EditorSelection.SelectedGameObjectId;
                        if (selId.HasValue)
                            ctxGo = UndoUtility.FindGameObjectById(selId.Value);
                        ImGuiHierarchyPanel.DrawCreateContextMenu(null, ctxGo);
                        ImGui.EndPopup();
                    }
                }
                else
                {
                    _isImageHovered = false;
                    ImGui.TextDisabled("No render target");
                }
            }
            else
            {
                _isImageHovered = false;
                _isWindowFocused = false;
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }

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

            // Render mode combo
            ImGui.SetNextItemWidth(110);
            int prevMode = _selectedRenderModeIdx;
            ImGui.Combo("##RenderMode", ref _selectedRenderModeIdx, RenderModeNames, RenderModeNames.Length);
            if (_selectedRenderModeIdx != prevMode)
            {
                EditorState.SceneViewRenderStyle = RenderStyleKeys[_selectedRenderModeIdx];
                EditorState.Save();
            }

            // MatCap preset selector (only visible in MatCap mode)
            if (SelectedRenderMode == SceneViewRenderMode.MatCap)
            {
                ImGui.SameLine();
                DrawMatCapSelector();
            }

            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);

            // Transform tools
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

            // Transform space
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            string spaceLabel = _selectedSpaceIdx == 0 ? "World" : "Local";
            if (ImGui.Button(spaceLabel))
                _selectedSpaceIdx = _selectedSpaceIdx == 0 ? 1 : 0;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Toggle World/Local (Z)");

            // Snap settings
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
            if (ImGui.Button("Snap"))
                SnapSelectedTransforms(_selectedSpaceIdx == 0 ? TransformSpace.World : TransformSpace.Local);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Snap selected to grid");

            // Snap settings popup button
            ImGui.SameLine();
            if (ImGui.Button("\u2699##snap_settings"))
                ImGui.OpenPopup("##snap_settings_popup");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Snap Settings");

            if (ImGui.BeginPopup("##snap_settings_popup"))
            {
                ImGui.TextUnformatted("Snap Settings");
                ImGui.Separator();

                float snapT = EditorState.SnapTranslate;
                if (EditorWidgets.DragFloatClickable("snap.translate", "Position", ref snapT, 0.1f, "%.2f"))
                {
                    EditorState.SnapTranslate = Math.Max(snapT, 0.001f);
                    EditorState.Save();
                }

                float snapR = EditorState.SnapRotate;
                if (EditorWidgets.DragFloatClickable("snap.rotate", "Rotation", ref snapR, 1f, "%.0f\u00B0"))
                {
                    EditorState.SnapRotate = Math.Max(snapR, 0.001f);
                    EditorState.Save();
                }

                float snapS = EditorState.SnapScale;
                if (EditorWidgets.DragFloatClickable("snap.scale", "Scale", ref snapS, 0.05f, "%.2f"))
                {
                    EditorState.SnapScale = Math.Max(snapS, 0.001f);
                    EditorState.Save();
                }

                float snapG = EditorState.SnapGrid2D;
                if (EditorWidgets.DragFloatClickable("snap.grid2d", "2D Grid", ref snapG, 1f, "%.1f"))
                {
                    EditorState.SnapGrid2D = Math.Max(snapG, 0.001f);
                    EditorState.Save();
                }

                ImGui.EndPopup();
            }

            // Overlay toggles dropdown
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
            if (ImGui.Button("Overlays"))
                ImGui.OpenPopup("##overlay_popup");
            if (ImGui.BeginPopup("##overlay_popup"))
            {
                ImGui.Checkbox("Gizmos", ref _showGizmos);
                ImGui.Checkbox("UI", ref _showUI);
                ImGui.EndPopup();
            }

            ImGui.PopStyleVar();
        }

        private static void SnapSelectedTransforms(TransformSpace space)
        {
            var ids = EditorSelection.SelectedGameObjectIds;
            if (ids.Count == 0) return;

            float snapT = EditorState.SnapTranslate;
            float snapR = EditorState.SnapRotate;
            float snapS = EditorState.SnapScale;
            bool world = space == TransformSpace.World;

            var actions = new System.Collections.Generic.List<IUndoAction>();
            foreach (var id in ids)
            {
                var go = UndoUtility.FindGameObjectById(id);
                if (go == null) continue;
                var t = go.transform;

                var oldLocalPos = t.localPosition;
                var oldLocalRot = t.localRotation;
                var oldLocalScale = t.localScale;

                // Snap position (world or local)
                var pos = world ? t.position : t.localPosition;
                var snappedPos = new RoseEngine.Vector3(
                    MathF.Round(pos.x / snapT) * snapT,
                    MathF.Round(pos.y / snapT) * snapT,
                    MathF.Round(pos.z / snapT) * snapT);
                if (world)
                    t.position = snappedPos;
                else
                    t.localPosition = snappedPos;

                // Snap rotation (world or local euler degrees)
                var rot = world ? t.rotation : t.localRotation;
                var euler = rot.eulerAngles;
                var snappedEuler = new RoseEngine.Vector3(
                    MathF.Round(euler.x / snapR) * snapR,
                    MathF.Round(euler.y / snapR) * snapR,
                    MathF.Round(euler.z / snapR) * snapR);
                if (world)
                    t.rotation = RoseEngine.Quaternion.Euler(snappedEuler);
                else
                    t.localRotation = RoseEngine.Quaternion.Euler(snappedEuler);

                // Snap scale (always local)
                var scale = t.localScale;
                t.localScale = new RoseEngine.Vector3(
                    MathF.Round(scale.x / snapS) * snapS,
                    MathF.Round(scale.y / snapS) * snapS,
                    MathF.Round(scale.z / snapS) * snapS);

                // Check if anything actually changed (compare local values for undo)
                var newLocalPos = t.localPosition;
                var newLocalRot = t.localRotation;
                var newLocalScale = t.localScale;

                if (oldLocalPos == newLocalPos && oldLocalRot == newLocalRot && oldLocalScale == newLocalScale)
                    continue;

                actions.Add(new SetTransformAction(
                    $"Snap {go.name}", id,
                    oldLocalPos, oldLocalRot, oldLocalScale,
                    newLocalPos, newLocalRot, newLocalScale));
            }

            if (actions.Count == 1)
                UndoSystem.Record(actions[0]);
            else if (actions.Count > 1)
                UndoSystem.Record(new CompoundUndoAction($"Snap {actions.Count} objects", actions));
        }

        private void DrawMatCapSelector()
        {
            int count = EditorAssets.MatCapCount;
            if (count == 0) return;

            // Clamp index
            if (_selectedMatCapIdx >= count) _selectedMatCapIdx = 0;

            // Show current matcap as a small thumbnail button
            var binding = EditorAssets.GetMatCapImGuiBinding(_selectedMatCapIdx);
            string name = EditorAssets.GetMatCapName(_selectedMatCapIdx);

            const float thumbSize = 18f;
            bool clicked = false;

            if (binding != IntPtr.Zero)
            {
                clicked = ImGui.ImageButton("##MatCapBtn", binding, new Vector2(thumbSize, thumbSize));
            }
            else
            {
                ImGui.SetNextItemWidth(80);
                clicked = ImGui.Button(name + "##MatCapBtn");
            }

            // Tooltip for current selection
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(name);

            if (clicked)
                ImGui.OpenPopup("##MatCapPopup");

            // Thumbnail grid popup
            if (ImGui.BeginPopup("##MatCapPopup"))
            {
                const float cellSize = 48f;
                const float padding = 4f;
                int columns = 4;
                float popupWidth = columns * (cellSize + padding) + padding;
                ImGui.SetNextItemWidth(popupWidth);

                for (int i = 0; i < count; i++)
                {
                    if (i % columns != 0)
                        ImGui.SameLine();

                    var texBinding = EditorAssets.GetMatCapImGuiBinding(i);
                    bool isSelected = (i == _selectedMatCapIdx);

                    ImGui.PushID(i);

                    if (isSelected)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.5f, 0.8f, 1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
                    }

                    if (texBinding != IntPtr.Zero)
                    {
                        if (ImGui.ImageButton("##mc", texBinding, new Vector2(cellSize, cellSize)))
                        {
                            _selectedMatCapIdx = i;
                        }
                    }
                    else
                    {
                        if (ImGui.Button(EditorAssets.GetMatCapName(i), new Vector2(cellSize, cellSize)))
                        {
                            _selectedMatCapIdx = i;
                        }
                    }

                    if (isSelected)
                        ImGui.PopStyleColor(2);

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(EditorAssets.GetMatCapName(i));

                    ImGui.PopID();
                }

                ImGui.EndPopup();
            }
        }

        private static bool ToolButton(string label, bool selected)
        {
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.5f, 0.8f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.4f, 0.6f, 0.9f, 1f));
            }
            bool clicked = ImGui.Button(label);
            if (selected) ImGui.PopStyleColor(2);
            return clicked;
        }

        /// <summary>
        /// Handle keyboard shortcuts (global — works from any panel, like Unity).
        /// W = Translate, E = Rotate, R = Scale, Z = toggle World/Local.
        /// </summary>
        public void ProcessShortcuts()
        {
            var io = ImGui.GetIO();
            // Skip when text input is active, camera fly mode (RMB), or modifiers held
            if (io.WantTextInput || io.MouseDown[1] || io.KeyCtrl || io.KeyAlt) return;

            if (ImGui.IsKeyPressed(ImGuiKey.W)) _selectedToolIdx = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _selectedToolIdx = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.R)) _selectedToolIdx = 2;
            if (ImGui.IsKeyPressed(ImGuiKey.T)) _selectedToolIdx = 3;
            if (ImGui.IsKeyPressed(ImGuiKey.Z)) _selectedSpaceIdx = _selectedSpaceIdx == 0 ? 1 : 0;
        }

        // ================================================================
        // Context menu — Create GameObject
        // ================================================================

        private static int RenderStyleToIndex(string style) => style switch
        {
            "wireframe" => 0,
            "matcap" => 1,
            "diffuse_only" => 2,
            "rendered" => 3,
            _ => 1,
        };

        // ── Canvas Edit Mode ──

        private void DrawCanvasEditView(Vector2 contentSize)
        {
            _imageAreaSize = contentSize;

            ImGui.InvisibleButton("##canvas_edit_area", contentSize);
            _isImageHovered = ImGui.IsItemHovered();
            _imageScreenMin = ImGui.GetItemRectMin();
            _imageScreenMax = ImGui.GetItemRectMax();

            var dl = ImGui.GetWindowDrawList();

            dl.PushClipRect(_imageScreenMin, _imageScreenMax, true);

            // 어두운 배경
            uint bgColor = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f));
            dl.AddRectFilled(_imageScreenMin, _imageScreenMax, bgColor);

            // Canvas 영역 계산 (aspect ratio + zoom + offset)
            float viewW = contentSize.X;
            float viewH = contentSize.Y;
            var (canvasScreenMin, canvasScreenMax) = CalculateCanvasRect(viewW, viewH);

            // 체커보드 배경
            DrawCheckerboard(dl, canvasScreenMin, canvasScreenMax);

            // Canvas UI 렌더링
            float canvasW = canvasScreenMax.X - canvasScreenMin.X;
            float canvasH = canvasScreenMax.Y - canvasScreenMin.Y;
            if (canvasW > 0 && canvasH > 0)
            {
                RoseEngine.CanvasRenderer.IsInteractive = false;
                RoseEngine.CanvasRenderer.RenderAll(dl, canvasScreenMin.X, canvasScreenMin.Y, canvasW, canvasH);
                RoseEngine.CanvasRenderer.IsInteractive = true;
            }

            // Canvas 영역 테두리
            uint borderColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1f));
            dl.AddRect(canvasScreenMin, canvasScreenMax, borderColor, 0f, ImDrawFlags.None, 1f);

            // 해상도 정보
            var (resW, resH) = GetCanvasResolution();
            string resText = $"{resW} x {resH} ({EditorState.CanvasEditAspectRatio})";
            var textPos = new Vector2(canvasScreenMin.X, canvasScreenMax.Y + 4f);
            dl.AddText(textPos, ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f)), resText);

            dl.PopClipRect();

            DrawGizmoOverlay?.Invoke();
            DrawPrefabOverlay?.Invoke();
        }

        internal (Vector2 min, Vector2 max) CalculateCanvasRect(float viewW, float viewH)
        {
            var (resW, resH) = GetCanvasResolution();
            float canvasAspect = (float)resW / resH;

            float fitW, fitH;
            float viewAspect = viewW / viewH;
            if (canvasAspect > viewAspect)
            {
                fitW = viewW * 0.9f;
                fitH = fitW / canvasAspect;
            }
            else
            {
                fitH = viewH * 0.9f;
                fitW = fitH * canvasAspect;
            }

            float zoomedW = fitW * CanvasEditMode.ViewZoom;
            float zoomedH = fitH * CanvasEditMode.ViewZoom;

            float centerX = _imageScreenMin.X + viewW * 0.5f + CanvasEditMode.ViewOffset.X;
            float centerY = _imageScreenMin.Y + viewH * 0.5f + CanvasEditMode.ViewOffset.Y;

            var min = new Vector2(centerX - zoomedW * 0.5f, centerY - zoomedH * 0.5f);
            var max = new Vector2(centerX + zoomedW * 0.5f, centerY + zoomedH * 0.5f);
            return (min, max);
        }

        private static (int w, int h) GetCanvasResolution()
        {
            string ar = EditorState.CanvasEditAspectRatio;
            if (ar == "Custom")
                return (EditorState.CanvasEditCustomWidth, EditorState.CanvasEditCustomHeight);

            float ratioW = 16f, ratioH = 9f;
            switch (ar)
            {
                case "16:9":  ratioW = 16f; ratioH = 9f; break;
                case "16:10": ratioW = 16f; ratioH = 10f; break;
                case "4:3":   ratioW = 4f;  ratioH = 3f; break;
                case "32:9":  ratioW = 32f; ratioH = 9f; break;
            }

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

        private static void DrawCheckerboard(ImDrawListPtr dl, Vector2 min, Vector2 max)
        {
            const float tileSize = 16f;
            uint col1 = ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.25f, 1f));
            uint col2 = ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.30f, 1f));

            dl.PushClipRect(min, max, true);

            for (float y = min.Y; y < max.Y; y += tileSize)
            {
                for (float x = min.X; x < max.X; x += tileSize)
                {
                    int ix = (int)((x - min.X) / tileSize);
                    int iy = (int)((y - min.Y) / tileSize);
                    uint col = ((ix + iy) % 2 == 0) ? col1 : col2;

                    var tileMin = new Vector2(x, y);
                    var tileMax = new Vector2(
                        MathF.Min(x + tileSize, max.X),
                        MathF.Min(y + tileSize, max.Y));
                    dl.AddRectFilled(tileMin, tileMax, col);
                }
            }

            dl.PopClipRect();
        }

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

            // Transform tools
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

            // 줌 비율
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 12);
            ImGui.TextUnformatted($"{(int)(CanvasEditMode.ViewZoom * 100)}%");

            // Overlays
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

    }
}
