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
        private Vector2 _dragGizmoCenter;  // gizmo screen pos captured at drag start (orbit pivot)

        public bool IsDragging => _isDragging;

        // ── Multi-element drag state ──
        private struct UIDragEntry
        {
            public int Id;
            public RoseEngine.Vector2 StartAnchoredPos;
            public RoseEngine.Quaternion StartLocalRotation;
            public RoseEngine.Vector3 StartLocalScale;
            public Vector2 StartPivotScreen;   // element's own pivot in screen space at drag start
            public float CanvasScale;
        }
        private readonly List<UIDragEntry> _dragStartAll = new();

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

            var pivot = GetMultiGizmoScreenPos(rt, sceneView.SelectedPivotMode);
            float angle = GetLocalAngleRad(selectedGo, sceneView.SelectedSpace);
            var xDir = GetXArrowDir(angle);
            var yDir = GetYArrowVisualDir(angle);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndTranslateDrag();
                    return;
                }

                ProcessTranslateDrag(io.MousePos, angle);
                return;
            }

            // Hit test
            _hoveredAxis = Axis.None;

            Vector2 xEnd = pivot + xDir * ArrowLength;
            Vector2 yEnd = pivot + yDir * ArrowLength;
            // XY handle square: along the bisector of +X and +Y_visual arrows
            Vector2 xyCenter = pivot + (xDir + yDir) * (XYSquareSize * 0.5f);

            float mx = io.MousePos.X;
            float my = io.MousePos.Y;

            // XY square (top priority): point-in-rotated-square test
            float dxFromCenter = mx - xyCenter.X;
            float dyFromCenter = my - xyCenter.Y;
            float localXY_X = dxFromCenter * xDir.X + dyFromCenter * xDir.Y;
            float localXY_Y = dxFromCenter * yDir.X + dyFromCenter * yDir.Y;
            float halfSq = XYSquareSize * 0.5f;
            if (MathF.Abs(localXY_X) <= halfSq && MathF.Abs(localXY_Y) <= halfSq)
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
                _dragGizmoCenter = pivot;
                CaptureDragEntries(sceneView, panelW, panelH);
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

            var pivot = _isDragging ? _dragGizmoCenter : GetMultiGizmoScreenPos(rt, sceneView.SelectedPivotMode);
            float angle = GetLocalAngleRad(selectedGo, sceneView.SelectedSpace);
            var xDir = GetXArrowDir(angle);
            var yDir = GetYArrowVisualDir(angle);
            var xPerp = new Vector2(-xDir.Y, xDir.X);
            var yPerp = new Vector2(-yDir.Y, yDir.X);

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

            // XY square (in the +X/+Y visual quadrant)
            Vector2 xyP0 = pivot;
            Vector2 xyP1 = pivot + xDir * XYSquareSize;
            Vector2 xyP2 = pivot + (xDir + yDir) * XYSquareSize;
            Vector2 xyP3 = pivot + yDir * XYSquareSize;
            drawList.AddQuadFilled(xyP0, xyP1, xyP2, xyP3, xyCol);

            // X axis: line + arrowhead
            Vector2 xEnd = pivot + xDir * ArrowLength;
            drawList.AddLine(pivot, xEnd, xCol, LineThickness);
            drawList.AddTriangleFilled(
                xEnd + xDir * ArrowHeadLength,
                xEnd + xPerp * ArrowHeadHalf,
                xEnd - xPerp * ArrowHeadHalf,
                xCol);

            // Y axis: line + arrowhead
            Vector2 yEnd = pivot + yDir * ArrowLength;
            drawList.AddLine(pivot, yEnd, yCol, LineThickness);
            drawList.AddTriangleFilled(
                yEnd + yDir * ArrowHeadLength,
                yEnd + yPerp * ArrowHeadHalf,
                yEnd - yPerp * ArrowHeadHalf,
                yCol);

            drawList.PopClipRect();
        }

        private void ProcessTranslateDrag(Vector2 mousePos, float angle)
        {
            var screenDelta = mousePos - _dragStartMousePos;
            // Project screen delta onto primary's local X/Y axes (Y-down convention).
            // World mode (angle=0) reduces to identity, preserving prior behavior.
            float c = MathF.Cos(angle);
            float s = MathF.Sin(angle);
            float dxScreen = screenDelta.X * c - screenDelta.Y * s;
            float dyScreen = screenDelta.X * s + screenDelta.Y * c;

            bool wantX = _activeAxis == Axis.X || _activeAxis == Axis.XY;
            bool wantY = _activeAxis == Axis.Y || _activeAxis == Axis.XY;
            bool snap = ImGui.GetIO().KeyCtrl;
            float grid = EditorState.SnapGrid2D;

            foreach (var entry in _dragStartAll)
            {
                var go = UndoUtility.FindGameObjectById(entry.Id);
                if (go == null) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;

                float scale = entry.CanvasScale < 0.001f ? 1f : entry.CanvasScale;
                float cdx = dxScreen / scale;
                float cdy = dyScreen / scale;

                float newX = entry.StartAnchoredPos.x + (wantX ? cdx : 0f);
                float newY = entry.StartAnchoredPos.y + (wantY ? cdy : 0f);

                if (snap)
                {
                    newX = MathF.Round(newX / grid) * grid;
                    newY = MathF.Round(newY / grid) * grid;
                }

                rt.anchoredPosition = new RoseEngine.Vector2(newX, newY);
            }
            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndTranslateDrag()
        {
            _isDragging = false;
            _activeAxis = Axis.None;

            var actions = new List<IUndoAction>();
            foreach (var entry in _dragStartAll)
            {
                var go = UndoUtility.FindGameObjectById(entry.Id);
                if (go == null) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;
                var newPos = rt.anchoredPosition;
                if (entry.StartAnchoredPos.Equals(newPos)) continue;
                actions.Add(new SetPropertyAction(
                    $"Move {go.name}", entry.Id, "RectTransform", "anchoredPosition",
                    entry.StartAnchoredPos, newPos));
            }
            if (actions.Count == 1)
                UndoSystem.Record(actions[0]);
            else if (actions.Count > 1)
                UndoSystem.Record(new CompoundUndoAction($"Move {actions.Count} UI Elements", actions));
            _dragStartAll.Clear();
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
            var min = sceneView.ImageScreenMin;
            var max = sceneView.ImageScreenMax;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;

            var pivot = _isDragging ? _dragGizmoCenter : GetMultiGizmoScreenPos(rt, sceneView.SelectedPivotMode);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndRotateDrag();
                    return;
                }

                ProcessRotateDrag(io.MousePos);
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
                _dragGizmoCenter = pivot;
                _rotateStartAngle = MathF.Atan2(
                    io.MousePos.Y - pivot.Y,
                    io.MousePos.X - pivot.X);
                _startRotation = selectedGo.transform.localRotation;
                CaptureDragEntries(sceneView, panelW, panelH);
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

            var pivot = _isDragging ? _dragGizmoCenter : GetMultiGizmoScreenPos(rt, sceneView.SelectedPivotMode);

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

        private void ProcessRotateDrag(Vector2 mousePos)
        {
            var pivot = _dragGizmoCenter;
            float currentAngle = MathF.Atan2(
                mousePos.Y - pivot.Y,
                mousePos.X - pivot.X);
            float deltaAngleScreen = currentAngle - _rotateStartAngle;  // screen Y-down → CW positive

            // Z rotation in Unity-like convention (CCW visual = positive Z) is the negation.
            float deltaDeg = -deltaAngleScreen * (180f / MathF.PI);

            // Ctrl snap to configurable degree increments
            if (ImGui.GetIO().KeyCtrl)
            {
                float snapDeg = EditorState.SnapRotate;
                deltaDeg = MathF.Round(deltaDeg / snapDeg) * snapDeg;
                deltaAngleScreen = -deltaDeg * (MathF.PI / 180f);
            }

            float cosA = MathF.Cos(deltaAngleScreen);
            float sinA = MathF.Sin(deltaAngleScreen);

            foreach (var entry in _dragStartAll)
            {
                var go = UndoUtility.FindGameObjectById(entry.Id);
                if (go == null) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;

                // Apply delta rotation to localRotation (Z only)
                float startZ = entry.StartLocalRotation.eulerAngles.z;
                float finalZ = startZ + deltaDeg;
                go.transform.localRotation = RoseEngine.Quaternion.Euler(
                    entry.StartLocalRotation.eulerAngles.x,
                    entry.StartLocalRotation.eulerAngles.y,
                    finalZ);

                // Orbit anchored position so each element's pivot rotates around the gizmo center.
                // Single-element drag with Pivot mode keeps anchored unchanged (offset = 0).
                float offX = entry.StartPivotScreen.X - pivot.X;
                float offY = entry.StartPivotScreen.Y - pivot.Y;
                float rotOffX = offX * cosA - offY * sinA;
                float rotOffY = offX * sinA + offY * cosA;
                float screenShiftX = rotOffX - offX;
                float screenShiftY = rotOffY - offY;
                float scale = entry.CanvasScale < 0.001f ? 1f : entry.CanvasScale;
                rt.anchoredPosition = new RoseEngine.Vector2(
                    entry.StartAnchoredPos.x + screenShiftX / scale,
                    entry.StartAnchoredPos.y + screenShiftY / scale);
            }

            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndRotateDrag()
        {
            _isDragging = false;
            _rotateHovered = false;

            var actions = new List<IUndoAction>();
            foreach (var entry in _dragStartAll)
            {
                var go = UndoUtility.FindGameObjectById(entry.Id);
                if (go == null) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;
                var t = go.transform;
                bool rotChanged = !entry.StartLocalRotation.Equals(t.localRotation);
                bool posChanged = !entry.StartAnchoredPos.Equals(rt.anchoredPosition);
                if (!rotChanged && !posChanged) continue;

                var subActions = new List<IUndoAction>();
                if (rotChanged)
                {
                    subActions.Add(new SetTransformAction(
                        $"Rotate {go.name}", entry.Id,
                        t.localPosition, entry.StartLocalRotation, t.localScale,
                        t.localPosition, t.localRotation, t.localScale));
                }
                if (posChanged)
                {
                    subActions.Add(new SetPropertyAction(
                        $"Orbit {go.name}", entry.Id, "RectTransform", "anchoredPosition",
                        entry.StartAnchoredPos, rt.anchoredPosition));
                }
                if (subActions.Count == 1) actions.Add(subActions[0]);
                else actions.Add(new CompoundUndoAction($"Rotate {go.name}", subActions));
            }
            if (actions.Count == 1)
                UndoSystem.Record(actions[0]);
            else if (actions.Count > 1)
                UndoSystem.Record(new CompoundUndoAction($"Rotate {actions.Count} UI Elements", actions));
            _dragStartAll.Clear();
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
            var min = sceneView.ImageScreenMin;
            var max = sceneView.ImageScreenMax;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;

            var pivot = _isDragging ? _dragGizmoCenter : GetMultiGizmoScreenPos(rt, sceneView.SelectedPivotMode);
            float angle = GetLocalAngleRad(selectedGo, sceneView.SelectedSpace);
            var xDir = GetXArrowDir(angle);
            var yDir = GetYArrowVisualDir(angle);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    EndScaleDrag();
                    return;
                }

                ProcessScaleDrag(io.MousePos, angle);
                return;
            }

            // Hit test (geometry rotated by element angle)
            _scaleHoveredAxis = Axis.None;

            Vector2 xEnd = pivot + xDir * ArrowLength;
            Vector2 yEnd = pivot + yDir * ArrowLength;

            float mx = io.MousePos.X;
            float my = io.MousePos.Y;
            float halfHandle = ScaleHandleSize + HitThreshold * 0.5f;

            // Center square = uniform XY (axis-aligned: it's a small square at pivot)
            if (mx >= pivot.X - XYSquareSize * 0.5f && mx <= pivot.X + XYSquareSize * 0.5f &&
                my >= pivot.Y - XYSquareSize * 0.5f && my <= pivot.Y + XYSquareSize * 0.5f)
            {
                _scaleHoveredAxis = Axis.XY;
            }
            else
            {
                // End-handle boxes (Chebyshev distance from each rotated handle position)
                float dxHandle = MathF.Max(MathF.Abs(mx - xEnd.X), MathF.Abs(my - xEnd.Y));
                float dyHandle = MathF.Max(MathF.Abs(mx - yEnd.X), MathF.Abs(my - yEnd.Y));

                if (dxHandle <= halfHandle)
                    _scaleHoveredAxis = Axis.X;
                else if (dyHandle <= halfHandle)
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
                _dragGizmoCenter = pivot;
                CaptureDragEntries(sceneView, panelW, panelH);
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

            var pivot = _isDragging ? _dragGizmoCenter : GetMultiGizmoScreenPos(rt, sceneView.SelectedPivotMode);
            float angle = GetLocalAngleRad(selectedGo, sceneView.SelectedSpace);
            var xDir = GetXArrowDir(angle);
            var yDir = GetYArrowVisualDir(angle);

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

            // X axis: line + box handle (axis-aligned box at rotated handle position)
            Vector2 xEnd = pivot + xDir * ArrowLength;
            drawList.AddLine(pivot, xEnd, xCol, LineThickness);
            drawList.AddRectFilled(
                new Vector2(xEnd.X - ScaleHandleSize, xEnd.Y - ScaleHandleSize),
                new Vector2(xEnd.X + ScaleHandleSize, xEnd.Y + ScaleHandleSize),
                xCol);

            // Y axis: line + box handle
            Vector2 yEnd = pivot + yDir * ArrowLength;
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

        private void ProcessScaleDrag(Vector2 mousePos, float angle)
        {
            var screenDelta = mousePos - _dragStartMousePos;
            // Scale sensitivity: 1/100 per pixel
            const float sensitivity = 0.01f;

            // Project screen delta onto visual X arrow direction and Y-visual-up direction.
            // X arrow = (cos α, -sin α);  Y arrow (visual up) = (-sin α, -cos α).
            float c = MathF.Cos(angle);
            float s = MathF.Sin(angle);
            float xMag = screenDelta.X * c - screenDelta.Y * s;
            float yMag = -screenDelta.X * s - screenDelta.Y * c;

            bool wantX = _scaleActiveAxis == Axis.X || _scaleActiveAxis == Axis.XY;
            bool wantY = _scaleActiveAxis == Axis.Y || _scaleActiveAxis == Axis.XY;
            bool snap = ImGui.GetIO().KeyCtrl;
            float snapInc = EditorState.SnapScale;

            foreach (var entry in _dragStartAll)
            {
                var go = UndoUtility.FindGameObjectById(entry.Id);
                if (go == null) continue;

                float sx = entry.StartLocalScale.x + (wantX ? xMag * sensitivity : 0f);
                float sy = entry.StartLocalScale.y + (wantY ? yMag * sensitivity : 0f);
                if (snap)
                {
                    sx = MathF.Round(sx / snapInc) * snapInc;
                    sy = MathF.Round(sy / snapInc) * snapInc;
                }
                go.transform.localScale = new RoseEngine.Vector3(sx, sy, entry.StartLocalScale.z);
            }
            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndScaleDrag()
        {
            _isDragging = false;
            _scaleActiveAxis = Axis.None;

            var actions = new List<IUndoAction>();
            foreach (var entry in _dragStartAll)
            {
                var go = UndoUtility.FindGameObjectById(entry.Id);
                if (go == null) continue;
                var t = go.transform;
                if (entry.StartLocalScale.Equals(t.localScale)) continue;
                actions.Add(new SetTransformAction(
                    $"Scale {go.name}", entry.Id,
                    t.localPosition, t.localRotation, entry.StartLocalScale,
                    t.localPosition, t.localRotation, t.localScale));
            }
            if (actions.Count == 1)
                UndoSystem.Record(actions[0]);
            else if (actions.Count > 1)
                UndoSystem.Record(new CompoundUndoAction($"Scale {actions.Count} UI Elements", actions));
            _dragStartAll.Clear();
        }

        // ================================================================
        // Shared helpers
        // ================================================================

        /// <summary>
        /// 단일 element 기준 기즈모 화면 위치.
        /// Pivot: RectTransform.pivot 기반. Center: rect 기하학적 중심.
        /// </summary>
        private static Vector2 GetGizmoScreenPos(RectTransform rt, TransformPivotMode mode)
        {
            var sr = rt.lastScreenRect;
            if (mode == TransformPivotMode.Center)
                return new Vector2(sr.x + sr.width * 0.5f, sr.y + sr.height * 0.5f);
            return new Vector2(
                sr.x + sr.width * rt.pivot.x,
                sr.y + sr.height * rt.pivot.y);
        }

        /// <summary>
        /// 멀티 선택을 고려한 기즈모 화면 위치.
        /// Pivot: primary(마지막 선택)의 위치.
        /// Center: 잠긴 프리팹 자식을 제외한 모든 선택 element의 평균(rect center 평균).
        /// </summary>
        private static Vector2 GetMultiGizmoScreenPos(RectTransform primaryRt, TransformPivotMode mode)
        {
            if (mode == TransformPivotMode.Pivot)
                return GetGizmoScreenPos(primaryRt, mode);

            var ids = EditorSelection.SelectedGameObjectIds;
            if (ids.Count <= 1)
                return GetGizmoScreenPos(primaryRt, mode);

            float sumX = 0f, sumY = 0f;
            int count = 0;
            foreach (var id in ids)
            {
                var go = UndoUtility.FindGameObjectById(id);
                if (go == null || PrefabUtility.HasPrefabInstanceAncestor(go)) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;
                var sr = rt.lastScreenRect;
                if (sr.width <= 0 || sr.height <= 0) continue;
                sumX += sr.x + sr.width * 0.5f;
                sumY += sr.y + sr.height * 0.5f;
                count++;
            }
            if (count == 0)
                return GetGizmoScreenPos(primaryRt, mode);
            return new Vector2(sumX / count, sumY / count);
        }

        /// <summary>드래그 시작 시 선택된 모든 RT를 캡처.</summary>
        private void CaptureDragEntries(ImGuiSceneViewPanel sceneView, float panelW, float panelH)
        {
            _dragStartAll.Clear();
            var ids = EditorSelection.SelectedGameObjectIds;
            foreach (var id in ids)
            {
                var go = UndoUtility.FindGameObjectById(id);
                if (go == null || !go.activeInHierarchy) continue;
                if (PrefabUtility.HasPrefabInstanceAncestor(go)) continue;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) continue;
                var sr = rt.lastScreenRect;
                if (sr.width <= 0 || sr.height <= 0) continue;
                _dragStartAll.Add(new UIDragEntry
                {
                    Id = id,
                    StartAnchoredPos = rt.anchoredPosition,
                    StartLocalRotation = go.transform.localRotation,
                    StartLocalScale = go.transform.localScale,
                    StartPivotScreen = new Vector2(
                        sr.x + sr.width * rt.pivot.x,
                        sr.y + sr.height * rt.pivot.y),
                    CanvasScale = CanvasRenderer.GetCanvasScaleFor(go, panelW, panelH),
                });
            }
        }

        /// <summary>
        /// 화면 평면에서 기즈모를 회전시킬 각도(라디안). World면 0.
        /// 양수 = 시각적 CCW(IronRose는 화면 Y-down 컨벤션이므로 표준 수학 회전과 부호가 반전).
        /// </summary>
        private static float GetLocalAngleRad(GameObject go, TransformSpace space)
        {
            if (space == TransformSpace.World) return 0f;
            return go.transform.localEulerAngles.z * (MathF.PI / 180f);
        }

        /// <summary>X 화살표의 화면 방향. α=0이면 (1,0).</summary>
        private static Vector2 GetXArrowDir(float angle)
            => new Vector2(MathF.Cos(angle), -MathF.Sin(angle));

        /// <summary>Y 화살표의 화면 방향(시각적으로 "위"). α=0이면 (0,-1).</summary>
        private static Vector2 GetYArrowVisualDir(float angle)
            => new Vector2(-MathF.Sin(angle), -MathF.Cos(angle));

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
