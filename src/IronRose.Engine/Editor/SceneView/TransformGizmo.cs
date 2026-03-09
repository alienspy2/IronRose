using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using IronRose.Rendering;
using RoseEngine;
using Veldrid;
using Quaternion = RoseEngine.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = RoseEngine.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Transform Gizmo — 이동/회전/스케일 조작 핸들.
    /// Scene View 패널 위에 렌더링되며, depth test OFF로 항상 위에 표시.
    /// </summary>
    internal sealed class TransformGizmo : IDisposable
    {
        private enum GizmoAxis { None, X, Y, Z, XY, XZ, YZ }

        // Meshes
        private Mesh? _arrowMesh;
        private Mesh? _ringMesh;
        private Mesh? _scaleMesh;
        private Mesh? _planeHandleMesh;
        private bool _meshesUploaded;

        // State
        private GizmoAxis _hoveredAxis = GizmoAxis.None;
        private GizmoAxis _activeAxis = GizmoAxis.None;
        private bool _isDragging;
        private Vector3 _dragStartWorldPos;
        private Vector3 _dragStartObjPos;
        private Quaternion _dragStartObjRot;
        private Vector3 _dragStartObjScale;
        private float _dragStartAngle;

        /// <summary>Record 모드 통지용 Animation Editor 참조.</summary>
        public ImGuiAnimationEditorPanel? AnimEditor { get; set; }

        // Multi-object drag state
        private struct DragStartEntry
        {
            public int Id;
            public Vector3 Position;      // world (for manipulation math)
            public Quaternion Rotation;    // world (for manipulation math)
            public Vector3 Scale;          // local
            public Vector3 LocalPosition;  // for undo
            public Quaternion LocalRotation; // for undo
        }
        private readonly List<DragStartEntry> _dragStartAll = new();

        // Colors
        private static readonly Vector4 ColorX = new(0.9f, 0.2f, 0.2f, 1f);
        private static readonly Vector4 ColorY = new(0.2f, 0.9f, 0.2f, 1f);
        private static readonly Vector4 ColorZ = new(0.2f, 0.2f, 0.9f, 1f);
        private static readonly Vector4 ColorHover = new(1f, 1f, 0.2f, 1f);
        private static readonly Vector4 ColorXY = new(0.2f, 0.2f, 0.9f, 1f); // Blue (Z normal)
        private static readonly Vector4 ColorXZ = new(0.2f, 0.9f, 0.2f, 1f); // Green (Y normal)
        private static readonly Vector4 ColorYZ = new(0.9f, 0.2f, 0.2f, 1f); // Red (X normal)

        // Hit testing threshold (screen-space pixels)
        private const float HitThreshold = 12f;

        public void Initialize(GraphicsDevice device)
        {
            _arrowMesh = GizmoMeshBuilder.CreateArrow();
            _ringMesh = GizmoMeshBuilder.CreateRing();
            _scaleMesh = GizmoMeshBuilder.CreateScaleHandle();
            _planeHandleMesh = GizmoMeshBuilder.CreatePlaneHandle();

            _arrowMesh.UploadToGPU(device);
            _ringMesh.UploadToGPU(device);
            _scaleMesh.UploadToGPU(device);
            _planeHandleMesh.UploadToGPU(device);
            _meshesUploaded = true;
        }

        /// <summary>
        /// Process input and update gizmo interaction state.
        /// Call during ImGui Update phase (not render).
        /// </summary>
        public void Update(EditorCamera camera, ImGuiSceneViewPanel sceneView,
            float viewportWidth, float viewportHeight)
        {
            if (!_meshesUploaded) return;

            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null || IsPrefabChildLocked(selectedGo))
            {
                _hoveredAxis = GizmoAxis.None;
                _activeAxis = GizmoAxis.None;
                _isDragging = false;
                return;
            }

            var io = ImGuiNET.ImGui.GetIO();
            var tool = sceneView.SelectedTool;
            var space = sceneView.SelectedSpace;
            var transform = selectedGo.transform;

            // Gizmo scale to maintain constant screen size
            float dist = (camera.Position - transform.position).magnitude;
            float gizmoScale = dist * 0.12f;

            // Get mouse in scene view local coords
            var min = sceneView.ImageScreenMin;
            var max = sceneView.ImageScreenMax;
            var mouseScreen = io.MousePos;
            float localX = mouseScreen.X - min.X;
            float localY = mouseScreen.Y - min.Y;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;

            if (panelW <= 0 || panelH <= 0) return;

            float aspect = viewportWidth / viewportHeight;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();
            var vp = viewMatrix * projMatrix;

            var objPos = transform.position;
            var objRot = space == TransformSpace.Local ? transform.rotation : RoseEngine.Quaternion.identity;

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    // Release — register undo for all affected objects
                    var toolName = tool switch
                    {
                        TransformTool.Translate => "Move",
                        TransformTool.Rotate => "Rotate",
                        TransformTool.Scale => "Scale",
                        _ => "Transform"
                    };

                    var actions = new List<IUndoAction>();
                    foreach (var entry in _dragStartAll)
                    {
                        var go = UndoUtility.FindGameObjectById(entry.Id);
                        if (go == null) continue;
                        var t = go.transform;
                        if (entry.LocalPosition != t.localPosition || entry.LocalRotation != t.localRotation || entry.Scale != t.localScale)
                        {
                            actions.Add(new SetTransformAction(
                                $"{toolName} {go.name}", entry.Id,
                                entry.LocalPosition, entry.LocalRotation, entry.Scale,
                                t.localPosition, t.localRotation, t.localScale));
                        }
                    }
                    if (actions.Count == 1)
                        UndoSystem.Record(actions[0]);
                    else if (actions.Count > 1)
                        UndoSystem.Record(new CompoundUndoAction($"{toolName} {actions.Count} objects", actions));

                    AnimEditor?.FlushRecordUndo();
                    _dragStartAll.Clear();
                    _isDragging = false;
                    _activeAxis = GizmoAxis.None;
                    return;
                }

                // Calculate drag delta
                var ray = ScreenToRay(localX, localY, panelW, panelH, camera, aspect);

                if (tool == TransformTool.Translate)
                {
                    Vector3 delta;
                    if (_activeAxis == GizmoAxis.XY || _activeAxis == GizmoAxis.XZ || _activeAxis == GizmoAxis.YZ)
                    {
                        // Plane movement: project ray onto the plane
                        var planeNormal = GetPlaneNormal(_activeAxis, objRot);
                        var projected = ProjectRayOntoPlane(ray, _dragStartObjPos, planeNormal);
                        delta = projected - _dragStartWorldPos;
                    }
                    else
                    {
                        // Single-axis movement
                        var axisDir = GetWorldAxisDirection(_activeAxis, objRot);
                        var projected = ProjectRayOntoAxis(ray, _dragStartObjPos, axisDir);
                        delta = projected - _dragStartWorldPos;
                    }

                    // Ctrl snap: snap delta to grid increments
                    if (io.KeyCtrl)
                    {
                        float snap = EditorState.SnapTranslate;
                        delta = new Vector3(
                            MathF.Round(delta.x / snap) * snap,
                            MathF.Round(delta.y / snap) * snap,
                            MathF.Round(delta.z / snap) * snap);
                    }

                    // Apply delta to all selected objects
                    foreach (var entry in _dragStartAll)
                    {
                        var go = UndoUtility.FindGameObjectById(entry.Id);
                        if (go == null) continue;
                        go.transform.position = entry.Position + delta;
                        if (AnimEditor?.IsRecording == true)
                        {
                            var lp = go.transform.localPosition;
                            AnimEditor.RecordProperty(go, "Transform", "localPosition", lp.x, "x");
                            AnimEditor.RecordProperty(go, "Transform", "localPosition", lp.y, "y");
                            AnimEditor.RecordProperty(go, "Transform", "localPosition", lp.z, "z");
                        }
                    }
                }
                else if (tool == TransformTool.Rotate)
                {
                    var axisDir = GetWorldAxisDirection(_activeAxis, objRot);
                    float angle = ComputeRotationAngle(ray, objPos, axisDir, camera);
                    float deltaAngle = angle - _dragStartAngle;

                    // Ctrl snap: configurable degree increments
                    if (io.KeyCtrl)
                    {
                        float snapRad = EditorState.SnapRotate * Mathf.Deg2Rad;
                        deltaAngle = MathF.Round(deltaAngle / snapRad) * snapRad;
                    }

                    var deltaRot = RoseEngine.Quaternion.AngleAxis(deltaAngle * Mathf.Rad2Deg, axisDir);

                    // Rotate all selected objects around the primary's pivot
                    foreach (var entry in _dragStartAll)
                    {
                        var go = UndoUtility.FindGameObjectById(entry.Id);
                        if (go == null) continue;
                        go.transform.rotation = deltaRot * entry.Rotation;
                        // Orbit around pivot (primary start position)
                        var offset = entry.Position - _dragStartObjPos;
                        go.transform.position = _dragStartObjPos + deltaRot * offset;
                        if (AnimEditor?.IsRecording == true)
                        {
                            var le = go.transform.localEulerAngles;
                            AnimEditor.RecordProperty(go, "Transform", "localEulerAngles", le.x, "x");
                            AnimEditor.RecordProperty(go, "Transform", "localEulerAngles", le.y, "y");
                            AnimEditor.RecordProperty(go, "Transform", "localEulerAngles", le.z, "z");
                        }
                    }
                }
                else if (tool == TransformTool.Scale)
                {
                    var axisDir = GetWorldAxisDirection(_activeAxis, objRot);
                    var projected = ProjectRayOntoAxis(ray, objPos, axisDir);
                    float startDist = Vector3.Dot((_dragStartWorldPos - objPos), axisDir);
                    float curDist = Vector3.Dot((projected - objPos), axisDir);
                    float factor = startDist != 0 ? curDist / startDist : 1f;
                    factor = Math.Clamp(factor, 0.01f, 100f);

                    // Apply scale factor to all selected objects
                    foreach (var entry in _dragStartAll)
                    {
                        var go = UndoUtility.FindGameObjectById(entry.Id);
                        if (go == null) continue;
                        var newScale = entry.Scale;
                        switch (_activeAxis)
                        {
                            case GizmoAxis.X: newScale = new Vector3(newScale.x * factor, newScale.y, newScale.z); break;
                            case GizmoAxis.Y: newScale = new Vector3(newScale.x, newScale.y * factor, newScale.z); break;
                            case GizmoAxis.Z: newScale = new Vector3(newScale.x, newScale.y, newScale.z * factor); break;
                        }

                        // Ctrl snap: snap each axis to scale snap increments
                        if (io.KeyCtrl)
                        {
                            float snap = EditorState.SnapScale;
                            newScale = new Vector3(
                                MathF.Round(newScale.x / snap) * snap,
                                MathF.Round(newScale.y / snap) * snap,
                                MathF.Round(newScale.z / snap) * snap);
                        }

                        go.transform.localScale = newScale;
                        if (AnimEditor?.IsRecording == true)
                        {
                            var ls = go.transform.localScale;
                            AnimEditor.RecordProperty(go, "Transform", "localScale", ls.x, "x");
                            AnimEditor.RecordProperty(go, "Transform", "localScale", ls.y, "y");
                            AnimEditor.RecordProperty(go, "Transform", "localScale", ls.z, "z");
                        }
                    }
                }

                return;
            }

            // Hover detection
            if (!sceneView.IsImageHovered)
            {
                _hoveredAxis = GizmoAxis.None;
                return;
            }

            _hoveredAxis = HitTestAxes(localX, localY, panelW, panelH, objPos, objRot, gizmoScale, vp, tool);

            // Start drag on LMB press
            if (io.MouseClicked[0] && _hoveredAxis != GizmoAxis.None && !io.KeyAlt)
            {
                _isDragging = true;
                _activeAxis = _hoveredAxis;
                _dragStartObjPos = transform.position;
                _dragStartObjRot = transform.rotation;
                _dragStartObjScale = transform.localScale;

                // Capture all selected objects' transforms
                _dragStartAll.Clear();
                foreach (var id in EditorSelection.SelectedGameObjectIds)
                {
                    var go = UndoUtility.FindGameObjectById(id);
                    if (go == null || IsPrefabChildLocked(go)) continue;
                    _dragStartAll.Add(new DragStartEntry
                    {
                        Id = id,
                        Position = go.transform.position,
                        Rotation = go.transform.rotation,
                        Scale = go.transform.localScale,
                        LocalPosition = go.transform.localPosition,
                        LocalRotation = go.transform.localRotation,
                    });
                }

                var ray = ScreenToRay(localX, localY, panelW, panelH, camera, aspect);
                if (_activeAxis == GizmoAxis.XY || _activeAxis == GizmoAxis.XZ || _activeAxis == GizmoAxis.YZ)
                {
                    var planeNormal = GetPlaneNormal(_activeAxis, objRot);
                    _dragStartWorldPos = ProjectRayOntoPlane(ray, objPos, planeNormal);
                }
                else
                {
                    var axisDir = GetWorldAxisDirection(_activeAxis, objRot);
                    _dragStartWorldPos = ProjectRayOntoAxis(ray, objPos, axisDir);
                    _dragStartAngle = ComputeRotationAngle(ray, objPos, axisDir, camera);
                }
            }
        }

        /// <summary>
        /// Returns true if gizmo is consuming the mouse click (prevent GPU pick).
        /// </summary>
        public bool IsInteracting => _isDragging || _hoveredAxis != GizmoAxis.None;

        /// <summary>
        /// Render the gizmo using SceneViewRenderer's gizmo pipeline.
        /// </summary>
        public void Render(CommandList cl, EditorCamera camera, SceneViewRenderer renderer,
            TransformTool tool, TransformSpace space, float viewportWidth, float viewportHeight)
        {
            if (!_meshesUploaded) return;

            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null || IsPrefabChildLocked(selectedGo)) return;

            var transform = selectedGo.transform;
            var objPos = transform.position;
            var objRot = space == TransformSpace.Local ? transform.rotation : RoseEngine.Quaternion.identity;

            float dist = (camera.Position - objPos).magnitude;
            float gizmoScale = dist * 0.12f;

            Mesh mesh = tool switch
            {
                TransformTool.Translate => _arrowMesh!,
                TransformTool.Rotate => _ringMesh!,
                TransformTool.Scale => _scaleMesh!,
                _ => _arrowMesh!,
            };

            // Draw 3 axes
            DrawAxisHandle(cl, renderer, camera, mesh, objPos, objRot, gizmoScale,
                GizmoAxis.X, tool, viewportWidth, viewportHeight);
            DrawAxisHandle(cl, renderer, camera, mesh, objPos, objRot, gizmoScale,
                GizmoAxis.Y, tool, viewportWidth, viewportHeight);
            DrawAxisHandle(cl, renderer, camera, mesh, objPos, objRot, gizmoScale,
                GizmoAxis.Z, tool, viewportWidth, viewportHeight);

            // Draw plane handles (Translate tool only)
            if (tool == TransformTool.Translate && _planeHandleMesh != null)
            {
                DrawPlaneHandle(cl, renderer, camera, objPos, objRot, gizmoScale,
                    GizmoAxis.XY, viewportWidth, viewportHeight);
                DrawPlaneHandle(cl, renderer, camera, objPos, objRot, gizmoScale,
                    GizmoAxis.XZ, viewportWidth, viewportHeight);
                DrawPlaneHandle(cl, renderer, camera, objPos, objRot, gizmoScale,
                    GizmoAxis.YZ, viewportWidth, viewportHeight);
            }
        }

        private void DrawAxisHandle(CommandList cl, SceneViewRenderer renderer,
            EditorCamera camera, Mesh mesh, Vector3 objPos, RoseEngine.Quaternion objRot,
            float gizmoScale, GizmoAxis axis, TransformTool tool,
            float viewportWidth, float viewportHeight)
        {
            // Determine color
            var color = axis switch
            {
                GizmoAxis.X => ColorX,
                GizmoAxis.Y => ColorY,
                GizmoAxis.Z => ColorZ,
                _ => ColorX,
            };
            bool isHovered = (_isDragging && _activeAxis == axis) || (!_isDragging && _hoveredAxis == axis);
            if (isHovered) color = ColorHover;

            // Build world matrix: position + rotation to align +Y to the target axis + scale
            var axisRot = GetAxisRotation(axis, objRot);
            var world = RoseEngine.Matrix4x4.TRS(objPos, axisRot,
                new Vector3(gizmoScale, gizmoScale, gizmoScale));

            float aspect = viewportWidth / viewportHeight;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();

            renderer.DrawGizmoMesh(cl, mesh, world.ToNumerics(), viewMatrix, projMatrix, color);
        }

        private void DrawPlaneHandle(CommandList cl, SceneViewRenderer renderer,
            EditorCamera camera, Vector3 objPos, RoseEngine.Quaternion objRot,
            float gizmoScale, GizmoAxis planeAxis,
            float viewportWidth, float viewportHeight)
        {
            // Determine color
            var color = planeAxis switch
            {
                GizmoAxis.XY => ColorXY,
                GizmoAxis.XZ => ColorXZ,
                GizmoAxis.YZ => ColorYZ,
                _ => ColorXY,
            };
            bool isHovered = (_isDragging && _activeAxis == planeAxis) || (!_isDragging && _hoveredAxis == planeAxis);
            if (isHovered) color = ColorHover;

            var planeRot = GetPlaneHandleRotation(planeAxis, objRot);
            var world = RoseEngine.Matrix4x4.TRS(objPos, planeRot,
                new Vector3(gizmoScale, gizmoScale, gizmoScale));

            float aspect = viewportWidth / viewportHeight;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();

            renderer.DrawGizmoLines(cl, _planeHandleMesh!, world.ToNumerics(), viewMatrix, projMatrix, color);
        }

        // ================================================================
        // Hit testing
        // ================================================================

        private GizmoAxis HitTestAxes(float localX, float localY, float panelW, float panelH,
            Vector3 objPos, RoseEngine.Quaternion objRot, float gizmoScale,
            System.Numerics.Matrix4x4 vp, TransformTool tool)
        {
            float bestDist = HitThreshold;
            var bestAxis = GizmoAxis.None;
            var mousePos = new Vector2(localX, localY);

            // Test plane handles first for Translate (they should take priority when mouse is inside the quad)
            if (tool == TransformTool.Translate)
            {
                foreach (var plane in new[] { GizmoAxis.XY, GizmoAxis.XZ, GizmoAxis.YZ })
                {
                    if (HitTestPlaneHandle(mousePos, objPos, objRot, plane, gizmoScale, vp, panelW, panelH))
                    {
                        // Plane handle hit — return immediately (priority over axis lines)
                        return plane;
                    }
                }
            }

            foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
            {
                float dist;

                if (tool == TransformTool.Rotate)
                {
                    // Ring-based hit test: sample points along a circle and find min distance
                    dist = HitTestRing(mousePos, objPos, objRot, axis, gizmoScale, vp, panelW, panelH);
                }
                else
                {
                    // Line-segment hit test for Translate/Scale
                    var dir = GetWorldAxisDirection(axis, objRot);
                    var tipWorld = objPos + dir * gizmoScale;

                    var originScreen = WorldToScreen(objPos, vp, panelW, panelH);
                    var tipScreen = WorldToScreen(tipWorld, vp, panelW, panelH);

                    if (!originScreen.HasValue || !tipScreen.HasValue) continue;

                    dist = DistancePointToSegment(mousePos, originScreen.Value, tipScreen.Value);
                }

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestAxis = axis;
                }
            }

            return bestAxis;
        }

        /// <summary>
        /// Hit test for plane handle quads. Returns true if mouse is inside the projected quad.
        /// Uses the same rotation as rendering to guarantee hit test matches visual.
        /// </summary>
        private bool HitTestPlaneHandle(Vector2 mousePos, Vector3 objPos,
            RoseEngine.Quaternion objRot, GizmoAxis planeAxis, float gizmoScale,
            System.Numerics.Matrix4x4 vp, float panelW, float panelH)
        {
            const float size = 0.30f;

            // Use the same rotation as DrawPlaneHandle to guarantee match
            var rot = GetPlaneHandleRotation(planeAxis, objRot);

            // Transform mesh-local corners to world space (identical to TRS in rendering)
            var a = objPos + (rot * new Vector3(0, 0, 0)) * gizmoScale;
            var b = objPos + (rot * new Vector3(size, 0, 0)) * gizmoScale;
            var c = objPos + (rot * new Vector3(size, size, 0)) * gizmoScale;
            var d = objPos + (rot * new Vector3(0, size, 0)) * gizmoScale;

            // Project to screen
            var sa = WorldToScreen(a, vp, panelW, panelH);
            var sb = WorldToScreen(b, vp, panelW, panelH);
            var sc = WorldToScreen(c, vp, panelW, panelH);
            var sd = WorldToScreen(d, vp, panelW, panelH);

            if (!sa.HasValue || !sb.HasValue || !sc.HasValue || !sd.HasValue) return false;

            // Point-in-quad test (two triangles)
            return PointInTriangle(mousePos, sa.Value, sb.Value, sc.Value) ||
                   PointInTriangle(mousePos, sa.Value, sc.Value, sd.Value);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = CrossSign(p, a, b);
            float d2 = CrossSign(p, b, c);
            float d3 = CrossSign(p, c, a);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static float CrossSign(Vector2 p, Vector2 a, Vector2 b)
        {
            return (a.X - p.X) * (b.Y - p.Y) - (a.Y - p.Y) * (b.X - p.X);
        }

        private float HitTestRing(Vector2 mousePos, Vector3 center, RoseEngine.Quaternion objRot,
            GizmoAxis axis, float radius, System.Numerics.Matrix4x4 vp, float panelW, float panelH)
        {
            // Get two perpendicular vectors in the ring's plane
            Vector3 tangent1, tangent2;
            switch (axis)
            {
                case GizmoAxis.X:
                    tangent1 = objRot * Vector3.up;
                    tangent2 = objRot * Vector3.forward;
                    break;
                case GizmoAxis.Y:
                    tangent1 = objRot * Vector3.right;
                    tangent2 = objRot * Vector3.forward;
                    break;
                default: // Z
                    tangent1 = objRot * Vector3.right;
                    tangent2 = objRot * Vector3.up;
                    break;
            }

            const int segments = 32;
            float minDist = float.MaxValue;
            Vector2? prevScreen = null;

            for (int i = 0; i <= segments; i++)
            {
                float angle = (i / (float)segments) * MathF.PI * 2f;
                var worldPt = center + (tangent1 * MathF.Cos(angle) + tangent2 * MathF.Sin(angle)) * radius;
                var screenPt = WorldToScreen(worldPt, vp, panelW, panelH);

                if (screenPt.HasValue && prevScreen.HasValue)
                {
                    float d = DistancePointToSegment(mousePos, prevScreen.Value, screenPt.Value);
                    if (d < minDist) minDist = d;
                }

                prevScreen = screenPt;
            }

            return minDist;
        }

        private static Vector2? WorldToScreen(Vector3 worldPos,
            System.Numerics.Matrix4x4 vp, float panelW, float panelH)
        {
            var clip = Vector4.Transform(
                new Vector4(worldPos.x, worldPos.y, worldPos.z, 1f), vp);
            if (clip.W <= 0.001f) return null;

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;

            // NDC → screen (0,0 = top-left)
            float sx = (ndcX * 0.5f + 0.5f) * panelW;
            float sy = (1f - (ndcY * 0.5f + 0.5f)) * panelH;
            return new Vector2(sx, sy);
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Vector2.Dot(p - a, ab) / Math.Max(Vector2.Dot(ab, ab), 1e-8f);
            t = Math.Clamp(t, 0f, 1f);
            var closest = a + ab * t;
            return (p - closest).Length();
        }

        // ================================================================
        // Ray math
        // ================================================================

        private struct Ray
        {
            public Vector3 Origin;
            public Vector3 Direction;
        }

        private static Ray ScreenToRay(float localX, float localY, float panelW, float panelH,
            EditorCamera camera, float aspect)
        {
            // Screen → NDC
            float ndcX = (localX / panelW) * 2f - 1f;
            float ndcY = 1f - (localY / panelH) * 2f;

            // NDC → view space (at near plane)
            var proj = camera.GetProjectionMatrix(aspect);
            float tanHalfFov = MathF.Tan(camera.FieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewX = ndcX * aspect * tanHalfFov;
            float viewY = ndcY * tanHalfFov;

            // View → world
            var forward = camera.Forward;
            var right = camera.Right;
            var up = camera.Up;

            var dir = (right * viewX + up * viewY + forward).normalized;

            return new Ray { Origin = camera.Position, Direction = dir };
        }

        private static Vector3 ProjectRayOntoAxis(Ray ray, Vector3 axisOrigin, Vector3 axisDir)
        {
            // Find closest point on the axis line to the ray line.
            // Line1: P = axisOrigin + t * axisDir
            // Line2: Q = ray.Origin + s * ray.Direction
            var w = ray.Origin - axisOrigin;
            float a = Vector3.Dot(axisDir, axisDir);
            float b = Vector3.Dot(axisDir, ray.Direction);
            float c = Vector3.Dot(ray.Direction, ray.Direction);
            float d = Vector3.Dot(axisDir, w);
            float e = Vector3.Dot(ray.Direction, w);

            float denom = a * c - b * b;
            if (MathF.Abs(denom) < 1e-8f) return axisOrigin;

            float t = (c * d - b * e) / denom;
            return axisOrigin + axisDir * t;
        }

        private static float ComputeRotationAngle(Ray ray, Vector3 center, Vector3 axisDir,
            EditorCamera camera)
        {
            // Intersect ray with the plane perpendicular to axis through center
            float denom = Vector3.Dot(ray.Direction, axisDir);
            if (MathF.Abs(denom) < 1e-6f) return 0f;

            float t = Vector3.Dot((center - ray.Origin), axisDir) / denom;
            var hitPoint = ray.Origin + ray.Direction * t;

            // Compute angle in the plane
            var d = hitPoint - center;
            // Project onto two axes perpendicular to axisDir
            var right = Vector3.Cross(axisDir, camera.Up);
            if (right.sqrMagnitude < 0.001f)
                right = Vector3.Cross(axisDir, camera.Right);
            right = right.normalized;
            var up = Vector3.Cross(axisDir, right).normalized;

            float x = Vector3.Dot(d, right);
            float y = Vector3.Dot(d, up);
            return MathF.Atan2(y, x);
        }

        private static Vector3 GetWorldAxisDirection(GizmoAxis axis, RoseEngine.Quaternion rotation)
        {
            return axis switch
            {
                GizmoAxis.X => rotation * Vector3.right,
                GizmoAxis.Y => rotation * Vector3.up,
                GizmoAxis.Z => rotation * Vector3.forward,
                _ => Vector3.right,
            };
        }

        private static RoseEngine.Quaternion GetAxisRotation(GizmoAxis axis, RoseEngine.Quaternion objRot)
        {
            // The meshes point along +Y. Rotate to align with the target axis.
            var axisRot = axis switch
            {
                GizmoAxis.X => RoseEngine.Quaternion.Euler(0, 0, -90), // +Y → +X
                GizmoAxis.Y => RoseEngine.Quaternion.identity,          // +Y stays
                GizmoAxis.Z => RoseEngine.Quaternion.Euler(90, 0, 0),   // +Y → +Z
                _ => RoseEngine.Quaternion.identity,
            };
            return objRot * axisRot;
        }

        /// <summary>
        /// Get the plane normal vector for a given plane axis in the given rotation space.
        /// </summary>
        private static Vector3 GetPlaneNormal(GizmoAxis planeAxis, RoseEngine.Quaternion rotation)
        {
            return planeAxis switch
            {
                GizmoAxis.XY => rotation * Vector3.forward,  // XY plane → Z normal
                GizmoAxis.XZ => rotation * Vector3.up,        // XZ plane → Y normal
                GizmoAxis.YZ => rotation * Vector3.right,     // YZ plane → X normal
                _ => Vector3.up,
            };
        }

        /// <summary>
        /// Get the rotation to orient the plane handle mesh (which lies in XY plane)
        /// to the desired plane orientation.
        /// </summary>
        private static RoseEngine.Quaternion GetPlaneHandleRotation(GizmoAxis planeAxis, RoseEngine.Quaternion objRot)
        {
            var planeRot = planeAxis switch
            {
                GizmoAxis.XY => RoseEngine.Quaternion.identity,           // XY — mesh already in XY
                GizmoAxis.XZ => RoseEngine.Quaternion.Euler(90, 0, 0),    // XY → XZ: (x,y,0)→(x,0,y)
                GizmoAxis.YZ => RoseEngine.Quaternion.Euler(0, -90, 0),   // XY → YZ: (x,y,0)→(0,y,x)
                _ => RoseEngine.Quaternion.identity,
            };
            return objRot * planeRot;
        }

        /// <summary>
        /// Project a ray onto a plane (defined by a point and normal), returning the intersection point.
        /// </summary>
        private static Vector3 ProjectRayOntoPlane(Ray ray, Vector3 planePoint, Vector3 planeNormal)
        {
            float denom = Vector3.Dot(ray.Direction, planeNormal);
            if (MathF.Abs(denom) < 1e-6f)
                return planePoint; // Ray parallel to plane — fallback

            float t = Vector3.Dot((planePoint - ray.Origin), planeNormal) / denom;
            return ray.Origin + ray.Direction * t;
        }

        /// <summary>조상 중 PrefabInstance가 있으면 잠금. 프리팹 루트 자체는 이동 가능, 그 아래는 전부 잠금.</summary>
        private static bool IsPrefabChildLocked(GameObject go)
            => PrefabUtility.HasPrefabInstanceAncestor(go);

        public void Dispose()
        {
            _arrowMesh?.Dispose();
            _ringMesh?.Dispose();
            _scaleMesh?.Dispose();
            _planeHandleMesh?.Dispose();
        }
    }
}
