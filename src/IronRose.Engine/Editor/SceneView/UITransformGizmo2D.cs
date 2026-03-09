using System;
using ImGuiNET;
using IronRose.Engine.Editor;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// 2D Transform Gizmo for UI elements (RectTransform).
    /// Renders axis handles at the selected UI element's screen-space pivot
    /// and allows translating/rotating via drag. Rendered using ImGui draw lists (2D).
    /// </summary>
    internal sealed class UITransformGizmo2D
    {
        // ── Shared state ──
        private bool _isDragging;
        private Vector2 _dragStartMousePos;
        private int _dragGoId;

        public bool IsDragging => _isDragging;

        // ── Translate state ──
        private enum Axis { None, X, Y, XY }
        private Axis _hoveredAxis = Axis.None;
        private Axis _activeAxis = Axis.None;
        private RoseEngine.Vector2 _startAnchoredPos;

        // ── Rotate state ──
        private bool _rotateHovered;
        private float _rotateStartAngle;
        private RoseEngine.Quaternion _startRotation;

        // ── Scale state ──
        private Axis _scaleHoveredAxis = Axis.None;
        private Axis _scaleActiveAxis = Axis.None;
        private RoseEngine.Vector3 _startScale;

        // ── Dimensions (screen pixels) ──
        private const float ArrowLength = 60f;
        private const float ArrowHeadLength = 10f;
        private const float ArrowHeadHalf = 5f;
        private const float HitThreshold = 8f;
        private const float XYSquareSize = 14f;
        private const float LineThickness = 2.5f;
        private const float RotateRadius = 50f;
        private const float RotateHitThreshold = 8f;
        private const float ScaleHandleSize = 6f;

        // ================================================================
        // Update (dispatch by tool)
        // ================================================================

        public void Update(ImGuiSceneViewPanel sceneView)
        {
            switch (sceneView.SelectedTool)
            {
                case TransformTool.Translate:
                    UpdateTranslate(sceneView);
                    break;
                case TransformTool.Rotate:
                    UpdateRotate(sceneView);
                    break;
                case TransformTool.Scale:
                    UpdateScale(sceneView);
                    break;
                default:
                    Reset();
                    break;
            }
        }

        // ================================================================
        // DrawOverlay (dispatch by tool)
        // ================================================================

        public void DrawOverlay(ImGuiSceneViewPanel sceneView)
        {
            switch (sceneView.SelectedTool)
            {
                case TransformTool.Translate:
                    DrawTranslateOverlay(sceneView);
                    break;
                case TransformTool.Rotate:
                    DrawRotateOverlay(sceneView);
                    break;
                case TransformTool.Scale:
                    DrawScaleOverlay(sceneView);
                    break;
            }
        }

        // ================================================================
        // Translate
        // ================================================================

        private void UpdateTranslate(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) { Reset(); return; }

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) { Reset(); return; }

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var io = ImGui.GetIO();
            var min = sceneView.ImageScreenMin;
            var max = sceneView.ImageScreenMax;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;
            if (panelW <= 0 || panelH <= 0) return;

            var pivot = GetPivotScreenPos(rt);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndTranslateDrag(rt);
                    return;
                }

                float canvasScale = CanvasRenderer.GetCanvasScaleFor(
                    selectedGo, panelW, panelH);
                ProcessTranslateDrag(rt, io.MousePos, canvasScale);
                return;
            }

            // Hit test
            _hoveredAxis = Axis.None;

            Vector2 xEnd = new Vector2(pivot.X + ArrowLength, pivot.Y);
            Vector2 yEnd = new Vector2(pivot.X, pivot.Y - ArrowLength);

            float mx = io.MousePos.X;
            float my = io.MousePos.Y;

            // XY square (top priority)
            if (mx >= pivot.X && mx <= pivot.X + XYSquareSize &&
                my >= pivot.Y - XYSquareSize && my <= pivot.Y)
            {
                _hoveredAxis = Axis.XY;
            }
            else
            {
                float distX = DistToSegment(io.MousePos, pivot, xEnd);
                float distY = DistToSegment(io.MousePos, pivot, yEnd);

                if (distX < HitThreshold && distX <= distY)
                    _hoveredAxis = Axis.X;
                else if (distY < HitThreshold)
                    _hoveredAxis = Axis.Y;
            }

            // Begin drag
            if (_hoveredAxis != Axis.None && io.MouseClicked[0])
            {
                _activeAxis = _hoveredAxis;
                _isDragging = true;
                _dragStartMousePos = io.MousePos;
                _dragGoId = rt.gameObject.GetInstanceID();
                _startAnchoredPos = rt.anchoredPosition;
            }

            // Cursor
            switch (_hoveredAxis)
            {
                case Axis.X:  ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW); break;
                case Axis.Y:  ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS); break;
                case Axis.XY: ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll); break;
            }
        }

        private void DrawTranslateOverlay(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) return;

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) return;

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var drawList = ImGui.GetWindowDrawList();
            var panelMin = sceneView.ImageScreenMin;
            var panelMax = sceneView.ImageScreenMax;
            drawList.PushClipRect(panelMin, panelMax);

            var pivot = GetPivotScreenPos(rt);

            bool xHot = _hoveredAxis == Axis.X || (_activeAxis == Axis.X && _isDragging);
            bool yHot = _hoveredAxis == Axis.Y || (_activeAxis == Axis.Y && _isDragging);
            bool xyHot = _hoveredAxis == Axis.XY || (_activeAxis == Axis.XY && _isDragging);

            uint xCol = ImGui.GetColorU32(xHot
                ? new Vector4(1f, 0.5f, 0.5f, 1f)
                : new Vector4(0.9f, 0.2f, 0.2f, 1f));
            uint yCol = ImGui.GetColorU32(yHot
                ? new Vector4(0.5f, 1f, 0.5f, 1f)
                : new Vector4(0.2f, 0.9f, 0.2f, 1f));
            uint xyCol = ImGui.GetColorU32(xyHot
                ? new Vector4(1f, 1f, 0.5f, 0.8f)
                : new Vector4(0.9f, 0.9f, 0.2f, 0.4f));

            // XY square
            drawList.AddRectFilled(
                new Vector2(pivot.X, pivot.Y - XYSquareSize),
                new Vector2(pivot.X + XYSquareSize, pivot.Y),
                xyCol);

            // X axis: line + arrowhead
            Vector2 xEnd = new Vector2(pivot.X + ArrowLength, pivot.Y);
            drawList.AddLine(pivot, xEnd, xCol, LineThickness);
            drawList.AddTriangleFilled(
                new Vector2(xEnd.X + ArrowHeadLength, xEnd.Y),
                new Vector2(xEnd.X, xEnd.Y - ArrowHeadHalf),
                new Vector2(xEnd.X, xEnd.Y + ArrowHeadHalf),
                xCol);

            // Y axis: line + arrowhead (up)
            Vector2 yEnd = new Vector2(pivot.X, pivot.Y - ArrowLength);
            drawList.AddLine(pivot, yEnd, yCol, LineThickness);
            drawList.AddTriangleFilled(
                new Vector2(yEnd.X, yEnd.Y - ArrowHeadLength),
                new Vector2(yEnd.X - ArrowHeadHalf, yEnd.Y),
                new Vector2(yEnd.X + ArrowHeadHalf, yEnd.Y),
                yCol);

            drawList.PopClipRect();
        }

        private void ProcessTranslateDrag(RectTransform rt, Vector2 mousePos, float canvasScale)
        {
            if (canvasScale < 0.001f) canvasScale = 1f;

            var screenDelta = mousePos - _dragStartMousePos;
            float cdx = screenDelta.X / canvasScale;
            float cdy = screenDelta.Y / canvasScale;

            float newX = _startAnchoredPos.x;
            float newY = _startAnchoredPos.y;

            if (_activeAxis == Axis.X || _activeAxis == Axis.XY)
                newX = _startAnchoredPos.x + cdx;
            if (_activeAxis == Axis.Y || _activeAxis == Axis.XY)
                newY = _startAnchoredPos.y + cdy;

            // Ctrl snap: snap to 2D grid
            if (ImGui.GetIO().KeyCtrl)
            {
                float grid = EditorState.SnapGrid2D;
                newX = MathF.Round(newX / grid) * grid;
                newY = MathF.Round(newY / grid) * grid;
            }

            rt.anchoredPosition = new RoseEngine.Vector2(newX, newY);
            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndTranslateDrag(RectTransform rt)
        {
            _isDragging = false;
            _activeAxis = Axis.None;

            var newPos = rt.anchoredPosition;
            if (!_startAnchoredPos.Equals(newPos))
            {
                UndoSystem.Record(new SetPropertyAction(
                    "Move UI Element",
                    _dragGoId, "RectTransform", "anchoredPosition",
                    _startAnchoredPos, newPos));
            }
        }

        // ================================================================
        // Rotate
        // ================================================================

        private void UpdateRotate(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) { Reset(); return; }

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) { Reset(); return; }

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var io = ImGui.GetIO();
            var pivot = GetPivotScreenPos(rt);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndRotateDrag(selectedGo);
                    return;
                }

                ProcessRotateDrag(selectedGo, io.MousePos, pivot);
                return;
            }

            // Hit test: ring
            float dist = (io.MousePos - pivot).Length();
            _rotateHovered = MathF.Abs(dist - RotateRadius) < RotateHitThreshold;

            if (_rotateHovered && io.MouseClicked[0])
            {
                _isDragging = true;
                _dragGoId = selectedGo.GetInstanceID();
                _dragStartMousePos = io.MousePos;
                _rotateStartAngle = MathF.Atan2(
                    io.MousePos.Y - pivot.Y,
                    io.MousePos.X - pivot.X);
                _startRotation = selectedGo.transform.localRotation;
            }

            if (_rotateHovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        private void DrawRotateOverlay(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) return;

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) return;

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var drawList = ImGui.GetWindowDrawList();
            var panelMin = sceneView.ImageScreenMin;
            var panelMax = sceneView.ImageScreenMax;
            drawList.PushClipRect(panelMin, panelMax);

            var pivot = GetPivotScreenPos(rt);

            bool hot = _rotateHovered || _isDragging;
            uint ringCol = ImGui.GetColorU32(hot
                ? new Vector4(0.5f, 0.7f, 1f, 1f)
                : new Vector4(0.26f, 0.59f, 0.98f, 0.9f));

            // Draw rotation ring
            drawList.AddCircle(pivot, RotateRadius, ringCol, 64, hot ? 3f : 2f);

            // Small dot at pivot center
            uint centerCol = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.8f));
            drawList.AddCircleFilled(pivot, 3f, centerCol);

            // Show drag angle indicator
            if (_isDragging)
            {
                var io = ImGui.GetIO();
                float currentAngle = MathF.Atan2(
                    io.MousePos.Y - pivot.Y,
                    io.MousePos.X - pivot.X);

                // Line from pivot to current mouse direction on ring
                Vector2 ringPoint = new Vector2(
                    pivot.X + RotateRadius * MathF.Cos(currentAngle),
                    pivot.Y + RotateRadius * MathF.Sin(currentAngle));
                uint indicatorCol = ImGui.GetColorU32(new Vector4(1f, 1f, 0.2f, 1f));
                drawList.AddLine(pivot, ringPoint, indicatorCol, 1.5f);
            }

            drawList.PopClipRect();
        }

        private void ProcessRotateDrag(GameObject go, Vector2 mousePos, Vector2 pivot)
        {
            float currentAngle = MathF.Atan2(
                mousePos.Y - pivot.Y,
                mousePos.X - pivot.X);
            float deltaAngle = currentAngle - _rotateStartAngle;

            // Convert radians to degrees (screen Y-down → clockwise positive)
            // Negate for Unity-like convention (counter-clockwise = positive Z)
            float deltaDeg = -deltaAngle * (180f / MathF.PI);

            float startZ = _startRotation.eulerAngles.z;
            float finalZ = startZ + deltaDeg;

            // Ctrl snap: snap rotation to configurable degree increments
            if (ImGui.GetIO().KeyCtrl)
            {
                float snapDeg = EditorState.SnapRotate;
                finalZ = MathF.Round(finalZ / snapDeg) * snapDeg;
            }

            go.transform.localRotation = RoseEngine.Quaternion.Euler(
                _startRotation.eulerAngles.x,
                _startRotation.eulerAngles.y,
                finalZ);

            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndRotateDrag(GameObject go)
        {
            _isDragging = false;
            _rotateHovered = false;

            var newRotation = go.transform.localRotation;
            if (!_startRotation.Equals(newRotation))
            {
                var t = go.transform;
                UndoSystem.Record(new SetTransformAction(
                    "Rotate UI Element", _dragGoId,
                    t.localPosition, _startRotation, t.localScale,
                    t.localPosition, newRotation, t.localScale));
            }
        }

        // ================================================================
        // Scale
        // ================================================================

        private void UpdateScale(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) { Reset(); return; }

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) { Reset(); return; }

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var io = ImGui.GetIO();
            var pivot = GetPivotScreenPos(rt);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndScaleDrag(selectedGo);
                    return;
                }

                ProcessScaleDrag(selectedGo, io.MousePos);
                return;
            }

            // Hit test (same geometry as translate: lines + center square)
            _scaleHoveredAxis = Axis.None;

            Vector2 xEnd = new Vector2(pivot.X + ArrowLength, pivot.Y);
            Vector2 yEnd = new Vector2(pivot.X, pivot.Y - ArrowLength);

            float mx = io.MousePos.X;
            float my = io.MousePos.Y;

            // Center square = uniform XY
            if (mx >= pivot.X - XYSquareSize * 0.5f && mx <= pivot.X + XYSquareSize * 0.5f &&
                my >= pivot.Y - XYSquareSize * 0.5f && my <= pivot.Y + XYSquareSize * 0.5f)
            {
                _scaleHoveredAxis = Axis.XY;
            }
            else
            {
                // End-handle boxes
                float dxHandle = MathF.Max(MathF.Abs(mx - xEnd.X), MathF.Abs(my - xEnd.Y));
                float dyHandle = MathF.Max(MathF.Abs(mx - yEnd.X), MathF.Abs(my - yEnd.Y));

                if (dxHandle <= ScaleHandleSize + HitThreshold * 0.5f)
                    _scaleHoveredAxis = Axis.X;
                else if (dyHandle <= ScaleHandleSize + HitThreshold * 0.5f)
                    _scaleHoveredAxis = Axis.Y;
                else
                {
                    // Line hit test
                    float distX = DistToSegment(io.MousePos, pivot, xEnd);
                    float distY = DistToSegment(io.MousePos, pivot, yEnd);

                    if (distX < HitThreshold && distX <= distY)
                        _scaleHoveredAxis = Axis.X;
                    else if (distY < HitThreshold)
                        _scaleHoveredAxis = Axis.Y;
                }
            }

            // Begin drag
            if (_scaleHoveredAxis != Axis.None && io.MouseClicked[0])
            {
                _scaleActiveAxis = _scaleHoveredAxis;
                _isDragging = true;
                _dragStartMousePos = io.MousePos;
                _dragGoId = selectedGo.GetInstanceID();
                _startScale = selectedGo.transform.localScale;
            }

            // Cursor
            switch (_scaleHoveredAxis)
            {
                case Axis.X:  ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW); break;
                case Axis.Y:  ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS); break;
                case Axis.XY: ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll); break;
            }
        }

        private void DrawScaleOverlay(ImGuiSceneViewPanel sceneView)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) return;

            var rt = selectedGo.GetComponent<RectTransform>();
            if (rt == null) return;

            var screenRect = rt.lastScreenRect;
            if (screenRect.width <= 0 || screenRect.height <= 0) return;

            var drawList = ImGui.GetWindowDrawList();
            var panelMin = sceneView.ImageScreenMin;
            var panelMax = sceneView.ImageScreenMax;
            drawList.PushClipRect(panelMin, panelMax);

            var pivot = GetPivotScreenPos(rt);

            bool xHot = _scaleHoveredAxis == Axis.X || (_scaleActiveAxis == Axis.X && _isDragging);
            bool yHot = _scaleHoveredAxis == Axis.Y || (_scaleActiveAxis == Axis.Y && _isDragging);
            bool xyHot = _scaleHoveredAxis == Axis.XY || (_scaleActiveAxis == Axis.XY && _isDragging);

            uint xCol = ImGui.GetColorU32(xHot
                ? new Vector4(1f, 0.5f, 0.5f, 1f)
                : new Vector4(0.9f, 0.2f, 0.2f, 1f));
            uint yCol = ImGui.GetColorU32(yHot
                ? new Vector4(0.5f, 1f, 0.5f, 1f)
                : new Vector4(0.2f, 0.9f, 0.2f, 1f));
            uint xyCol = ImGui.GetColorU32(xyHot
                ? new Vector4(1f, 1f, 0.5f, 1f)
                : new Vector4(0.9f, 0.9f, 0.2f, 0.8f));

            // X axis: line + box handle
            Vector2 xEnd = new Vector2(pivot.X + ArrowLength, pivot.Y);
            drawList.AddLine(pivot, xEnd, xCol, LineThickness);
            drawList.AddRectFilled(
                new Vector2(xEnd.X - ScaleHandleSize, xEnd.Y - ScaleHandleSize),
                new Vector2(xEnd.X + ScaleHandleSize, xEnd.Y + ScaleHandleSize),
                xCol);

            // Y axis: line + box handle (up)
            Vector2 yEnd = new Vector2(pivot.X, pivot.Y - ArrowLength);
            drawList.AddLine(pivot, yEnd, yCol, LineThickness);
            drawList.AddRectFilled(
                new Vector2(yEnd.X - ScaleHandleSize, yEnd.Y - ScaleHandleSize),
                new Vector2(yEnd.X + ScaleHandleSize, yEnd.Y + ScaleHandleSize),
                yCol);

            // Center box (uniform scale)
            drawList.AddRectFilled(
                new Vector2(pivot.X - ScaleHandleSize, pivot.Y - ScaleHandleSize),
                new Vector2(pivot.X + ScaleHandleSize, pivot.Y + ScaleHandleSize),
                xyCol);

            drawList.PopClipRect();
        }

        private void ProcessScaleDrag(GameObject go, Vector2 mousePos)
        {
            var screenDelta = mousePos - _dragStartMousePos;
            // Scale sensitivity: 1/100 per pixel
            const float sensitivity = 0.01f;

            float sx = _startScale.x;
            float sy = _startScale.y;

            if (_scaleActiveAxis == Axis.X || _scaleActiveAxis == Axis.XY)
                sx = _startScale.x + screenDelta.X * sensitivity;
            if (_scaleActiveAxis == Axis.Y || _scaleActiveAxis == Axis.XY)
                sy = _startScale.y - screenDelta.Y * sensitivity; // up = bigger

            // Ctrl snap: snap scale to increments
            if (ImGui.GetIO().KeyCtrl)
            {
                float snap = EditorState.SnapScale;
                sx = MathF.Round(sx / snap) * snap;
                sy = MathF.Round(sy / snap) * snap;
            }

            go.transform.localScale = new RoseEngine.Vector3(sx, sy, _startScale.z);
            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndScaleDrag(GameObject go)
        {
            _isDragging = false;
            _scaleActiveAxis = Axis.None;

            var newScale = go.transform.localScale;
            if (!_startScale.Equals(newScale))
            {
                var t = go.transform;
                UndoSystem.Record(new SetTransformAction(
                    "Scale UI Element", _dragGoId,
                    t.localPosition, t.localRotation, _startScale,
                    t.localPosition, t.localRotation, newScale));
            }
        }

        // ================================================================
        // Shared helpers
        // ================================================================

        private static Vector2 GetPivotScreenPos(RectTransform rt)
        {
            var sr = rt.lastScreenRect;
            return new Vector2(
                sr.x + sr.width * rt.pivot.x,
                sr.y + sr.height * rt.pivot.y);
        }

        private void Reset()
        {
            _hoveredAxis = Axis.None;
            _activeAxis = Axis.None;
            _scaleHoveredAxis = Axis.None;
            _scaleActiveAxis = Axis.None;
            _rotateHovered = false;
            _isDragging = false;
        }

        /// <summary>Point-to-segment distance.</summary>
        private static float DistToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var ap = point - a;
            float dot = ap.X * ab.X + ap.Y * ab.Y;
            float lenSq = ab.X * ab.X + ab.Y * ab.Y;
            float t = lenSq > 0 ? Math.Clamp(dot / lenSq, 0f, 1f) : 0f;
            var closest = new Vector2(a.X + t * ab.X, a.Y + t * ab.Y);
            return (point - closest).Length();
        }
    }
}
