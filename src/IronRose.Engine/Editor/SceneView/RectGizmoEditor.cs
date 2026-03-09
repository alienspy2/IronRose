using System;
using System.Collections.Generic;
using ImGuiNET;
using IronRose.Engine.Editor;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Interactive RectTransform handle editor for UI elements.
    /// Draws draggable handles on the selected UI element's screen rect
    /// and allows resizing/moving via drag. Rendered using ImGui draw lists (2D).
    /// </summary>
    internal sealed class RectGizmoEditor
    {
        // --- Handle state ---

        private enum Handle
        {
            None,
            TopLeft, Top, TopRight,
            Right, BottomRight, Bottom,
            BottomLeft, Left,
            Body
        }

        private Handle _hoveredHandle = Handle.None;
        private Handle _activeHandle = Handle.None;
        private bool _isDragging;
        private Vector2 _dragStartMousePos;

        // Snapshot for undo
        private int _dragGoId;
        private RoseEngine.Vector2 _startAnchoredPos;
        private RoseEngine.Vector2 _startSizeDelta;

        // Colors
        private static readonly uint OutlineColor = ImGui.GetColorU32(new Vector4(0.26f, 0.59f, 0.98f, 0.9f));
        private static readonly uint HandleFillColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        private static readonly uint HandleBorderColor = ImGui.GetColorU32(new Vector4(0.26f, 0.59f, 0.98f, 1f));
        private static readonly uint HandleHoverColor = ImGui.GetColorU32(new Vector4(1f, 1f, 0.2f, 1f));

        private const float HandleHalfSize = 4f;  // half-size of handle square in screen pixels
        private const float HitThreshold = 7f;    // pick radius in screen pixels
        private const float BodyHitPadding = 2f;   // inset from rect edge for body hit area

        public bool IsDragging => _isDragging;

        // ================================================================
        // Update (input processing)
        // ================================================================

        public void Update(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null)
            {
                Reset();
                return;
            }

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null)
            {
                Reset();
                return;
            }

            var io = ImGui.GetIO();
            var screenRect = rt.lastScreenRect;

            // Skip if the rect has no area (not yet rendered)
            if (screenRect.width <= 0 || screenRect.height <= 0)
                return;

            var min = sceneView.ImageScreenMin;
            var max = sceneView.ImageScreenMax;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;
            if (panelW <= 0 || panelH <= 0) return;

            // Handle positions in screen coordinates
            var handles = GetHandlePositions(screenRect);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndDrag(rt);
                    return;
                }

                float canvasScale = CanvasRenderer.GetCanvasScaleFor(
                    selectedGo, panelW, panelH);
                ProcessDrag(rt, io.MousePos, canvasScale);
                return;
            }

            // Hit test handles (screen-space distance)
            _hoveredHandle = Handle.None;
            float bestDist = HitThreshold;

            foreach (var (handleId, pos) in handles)
            {
                if (handleId == Handle.Body) continue; // Body tested separately
                float dist = (io.MousePos - pos).Length();
                if (dist < bestDist)
                {
                    bestDist = dist;
                    _hoveredHandle = handleId;
                }
            }

            // Body hit test (if no edge/corner handle is hovered)
            if (_hoveredHandle == Handle.None)
            {
                float mx = io.MousePos.X;
                float my = io.MousePos.Y;
                if (mx >= screenRect.x + BodyHitPadding && mx <= screenRect.xMax - BodyHitPadding &&
                    my >= screenRect.y + BodyHitPadding && my <= screenRect.yMax - BodyHitPadding)
                {
                    _hoveredHandle = Handle.Body;
                }
            }

            // Begin drag
            if (_hoveredHandle != Handle.None && io.MouseClicked[0])
            {
                _activeHandle = _hoveredHandle;
                _isDragging = true;
                _dragStartMousePos = io.MousePos;
                BeginDrag(rt);
            }

            // Set cursor based on hovered handle
            SetCursor(_hoveredHandle);
        }

        // ================================================================
        // Drawing (ImGui overlay)
        // ================================================================

        public void DrawOverlay(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) return;

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) return;

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var drawList = ImGui.GetWindowDrawList();

            // Clip to scene view area
            var panelMin = sceneView.ImageScreenMin;
            var panelMax = sceneView.ImageScreenMax;
            drawList.PushClipRect(panelMin, panelMax);

            // Draw rect outline
            drawList.AddRect(
                new Vector2(screenRect.x, screenRect.y),
                new Vector2(screenRect.xMax, screenRect.yMax),
                OutlineColor, 0f, ImDrawFlags.None, 1.5f);

            // Draw handles
            var handles = GetHandlePositions(screenRect);
            foreach (var (handleId, pos) in handles)
            {
                if (handleId == Handle.Body) continue;

                bool isHovered = handleId == _hoveredHandle;
                bool isActive = handleId == _activeHandle && _isDragging;
                uint fillCol = (isHovered || isActive) ? HandleHoverColor : HandleFillColor;
                uint borderCol = HandleBorderColor;

                drawList.AddRectFilled(
                    new Vector2(pos.X - HandleHalfSize, pos.Y - HandleHalfSize),
                    new Vector2(pos.X + HandleHalfSize, pos.Y + HandleHalfSize),
                    fillCol);
                drawList.AddRect(
                    new Vector2(pos.X - HandleHalfSize, pos.Y - HandleHalfSize),
                    new Vector2(pos.X + HandleHalfSize, pos.Y + HandleHalfSize),
                    borderCol, 0f, ImDrawFlags.None, 1f);
            }

            drawList.PopClipRect();
        }

        // ================================================================
        // Handle positions
        // ================================================================

        private static List<(Handle, Vector2)> GetHandlePositions(Rect screenRect)
        {
            float x0 = screenRect.x;
            float y0 = screenRect.y;
            float x1 = screenRect.xMax;
            float y1 = screenRect.yMax;
            float mx = (x0 + x1) * 0.5f;
            float my = (y0 + y1) * 0.5f;

            return new List<(Handle, Vector2)>
            {
                (Handle.TopLeft,     new Vector2(x0, y0)),
                (Handle.Top,         new Vector2(mx, y0)),
                (Handle.TopRight,    new Vector2(x1, y0)),
                (Handle.Right,       new Vector2(x1, my)),
                (Handle.BottomRight, new Vector2(x1, y1)),
                (Handle.Bottom,      new Vector2(mx, y1)),
                (Handle.BottomLeft,  new Vector2(x0, y1)),
                (Handle.Left,        new Vector2(x0, my)),
            };
        }

        // ================================================================
        // Drag logic
        // ================================================================

        private void BeginDrag(RectTransform rt)
        {
            _dragGoId = rt.gameObject.GetInstanceID();
            _startAnchoredPos = rt.anchoredPosition;
            _startSizeDelta = rt.sizeDelta;
        }

        private void ProcessDrag(RectTransform rt, Vector2 mousePos, float canvasScale)
        {
            if (canvasScale < 0.001f) canvasScale = 1f;

            var screenDelta = mousePos - _dragStartMousePos;
            float cdx = screenDelta.X / canvasScale;
            float cdy = screenDelta.Y / canvasScale;

            // Restore start values before applying delta
            rt.anchoredPosition = _startAnchoredPos;
            rt.sizeDelta = _startSizeDelta;

            bool ctrlSnap = ImGui.GetIO().KeyCtrl;
            float grid = EditorState.SnapGrid2D;

            switch (_activeHandle)
            {
                case Handle.Body:
                {
                    float nx = _startAnchoredPos.x + cdx;
                    float ny = _startAnchoredPos.y + cdy;
                    if (ctrlSnap)
                    {
                        nx = MathF.Round(nx / grid) * grid;
                        ny = MathF.Round(ny / grid) * grid;
                    }
                    rt.anchoredPosition = new RoseEngine.Vector2(nx, ny);
                    break;
                }

                case Handle.Left:
                {
                    var om = rt.offsetMin;
                    float nx = om.x + cdx;
                    if (ctrlSnap) nx = MathF.Round(nx / grid) * grid;
                    rt.offsetMin = new RoseEngine.Vector2(nx, om.y);
                    break;
                }
                case Handle.Right:
                {
                    var om = rt.offsetMax;
                    float nx = om.x + cdx;
                    if (ctrlSnap) nx = MathF.Round(nx / grid) * grid;
                    rt.offsetMax = new RoseEngine.Vector2(nx, om.y);
                    break;
                }
                case Handle.Top:
                {
                    var om = rt.offsetMin;
                    float ny = om.y + cdy;
                    if (ctrlSnap) ny = MathF.Round(ny / grid) * grid;
                    rt.offsetMin = new RoseEngine.Vector2(om.x, ny);
                    break;
                }
                case Handle.Bottom:
                {
                    var om = rt.offsetMax;
                    float ny = om.y + cdy;
                    if (ctrlSnap) ny = MathF.Round(ny / grid) * grid;
                    rt.offsetMax = new RoseEngine.Vector2(om.x, ny);
                    break;
                }

                case Handle.TopLeft:
                {
                    var om = rt.offsetMin;
                    float nx = om.x + cdx;
                    float ny = om.y + cdy;
                    if (ctrlSnap) { nx = MathF.Round(nx / grid) * grid; ny = MathF.Round(ny / grid) * grid; }
                    rt.offsetMin = new RoseEngine.Vector2(nx, ny);
                    break;
                }
                case Handle.TopRight:
                {
                    var omMin = rt.offsetMin;
                    var omMax = rt.offsetMax;
                    float nx = omMax.x + cdx;
                    float ny = omMin.y + cdy;
                    if (ctrlSnap) { nx = MathF.Round(nx / grid) * grid; ny = MathF.Round(ny / grid) * grid; }
                    rt.offsetMax = new RoseEngine.Vector2(nx, omMax.y);
                    rt.offsetMin = new RoseEngine.Vector2(omMin.x, ny);
                    break;
                }
                case Handle.BottomLeft:
                {
                    var omMin = rt.offsetMin;
                    var omMax = rt.offsetMax;
                    float nx = omMin.x + cdx;
                    float ny = omMax.y + cdy;
                    if (ctrlSnap) { nx = MathF.Round(nx / grid) * grid; ny = MathF.Round(ny / grid) * grid; }
                    rt.offsetMin = new RoseEngine.Vector2(nx, omMin.y);
                    rt.offsetMax = new RoseEngine.Vector2(omMax.x, ny);
                    break;
                }
                case Handle.BottomRight:
                {
                    var om = rt.offsetMax;
                    float nx = om.x + cdx;
                    float ny = om.y + cdy;
                    if (ctrlSnap) { nx = MathF.Round(nx / grid) * grid; ny = MathF.Round(ny / grid) * grid; }
                    rt.offsetMax = new RoseEngine.Vector2(nx, ny);
                    break;
                }
            }

            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndDrag(RectTransform rt)
        {
            _isDragging = false;
            _activeHandle = Handle.None;

            var newAnchoredPos = rt.anchoredPosition;
            var newSizeDelta = rt.sizeDelta;

            bool posChanged = !_startAnchoredPos.Equals(newAnchoredPos);
            bool sizeChanged = !_startSizeDelta.Equals(newSizeDelta);

            if (posChanged || sizeChanged)
            {
                var actions = new List<IUndoAction>();

                if (posChanged)
                {
                    actions.Add(new SetPropertyAction(
                        "Edit RectTransform.anchoredPosition",
                        _dragGoId, "RectTransform", "anchoredPosition",
                        _startAnchoredPos, newAnchoredPos));
                }

                if (sizeChanged)
                {
                    actions.Add(new SetPropertyAction(
                        "Edit RectTransform.sizeDelta",
                        _dragGoId, "RectTransform", "sizeDelta",
                        _startSizeDelta, newSizeDelta));
                }

                if (actions.Count == 1)
                    UndoSystem.Record(actions[0]);
                else
                    UndoSystem.Record(new CompoundUndoAction("Edit RectTransform", actions));
            }
        }

        private void Reset()
        {
            _hoveredHandle = Handle.None;
            _activeHandle = Handle.None;
            _isDragging = false;
        }

        // ================================================================
        // Cursor
        // ================================================================

        private static void SetCursor(Handle handle)
        {
            var cursor = handle switch
            {
                Handle.TopLeft or Handle.BottomRight => ImGuiMouseCursor.ResizeNWSE,
                Handle.TopRight or Handle.BottomLeft => ImGuiMouseCursor.ResizeNESW,
                Handle.Top or Handle.Bottom => ImGuiMouseCursor.ResizeNS,
                Handle.Left or Handle.Right => ImGuiMouseCursor.ResizeEW,
                Handle.Body => ImGuiMouseCursor.ResizeAll,
                _ => ImGuiMouseCursor.Arrow,
            };

            if (handle != Handle.None)
                ImGui.SetMouseCursor(cursor);
        }
    }
}
