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
            if (ImGui.Begin("Scene View", ref _isOpen))
            {
                _isWindowFocused = ImGui.IsWindowFocused();

                DrawToolbar();
                ImGui.Separator();

                var contentSize = ImGui.GetContentRegionAvail();
                _imageAreaSize = contentSize;

                if (_textureId != IntPtr.Zero && contentSize.X > 1 && contentSize.Y > 1)
                {
                    ImGui.Image(_textureId, contentSize);
                    _isImageHovered = ImGui.IsItemHovered();
                    _imageScreenMin = ImGui.GetItemRectMin();
                    _imageScreenMax = ImGui.GetItemRectMax();

                    // Canvas UI overlay
                    if (_showUI)
                    {
                        var dl = ImGui.GetWindowDrawList();
                        float imgW = _imageScreenMax.X - _imageScreenMin.X;
                        float imgH = _imageScreenMax.Y - _imageScreenMin.Y;
                        RoseEngine.CanvasRenderer.RenderAll(dl, _imageScreenMin.X, _imageScreenMin.Y, imgW, imgH);
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

    }
}
