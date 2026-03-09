using System;
using System.Collections.Generic;
using ImGuiNET;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector3 = RoseEngine.Vector3;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Interactive collider handle editor.
    /// When active, draws draggable handles on the selected object's collider
    /// and allows resizing via drag.
    /// </summary>
    internal sealed class ColliderGizmoEditor
    {
        // --- Handle state ---

        private enum HandleId { None, PosX, NegX, PosY, NegY, PosZ, NegZ }

        private HandleId _hoveredHandle = HandleId.None;
        private HandleId _activeHandle = HandleId.None;
        private bool _isDragging;
        private Vector3 _dragStartWorldPos;

        // Snapshot for undo
        private int _dragGoId;
        private string _dragComponentType = "";
        private string _dragMemberName = "";
        private object? _dragStartValue;

        // Second member for BoxCollider (center changes alongside size)
        private string _dragMemberName2 = "";
        private object? _dragStartValue2;

        // Colors
        private static readonly Color HandleColor = new(0.5f, 1f, 0.5f, 1f);
        private static readonly Color HandleHoverColor = new(1f, 1f, 0.2f, 1f);

        private const float HandleScreenSize = 4f;   // half-size of handle square in screen pixels
        private const float HitThreshold = 8f;        // pick radius in screen pixels

        public bool IsDragging => _isDragging;

        // ================================================================
        // Update (input processing)
        // ================================================================

        public void Update(EditorCamera camera, ImGuiSceneViewPanel sceneView,
            float viewportWidth, float viewportHeight)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null)
            {
                Reset();
                return;
            }

            var collider = selectedGo.GetComponent<Collider>();
            if (collider == null)
            {
                Reset();
                return;
            }

            var io = ImGui.GetIO();

            var min = sceneView.ImageScreenMin;
            var max = sceneView.ImageScreenMax;
            float localX = io.MousePos.X - min.X;
            float localY = io.MousePos.Y - min.Y;
            float panelW = max.X - min.X;
            float panelH = max.Y - min.Y;
            if (panelW <= 0 || panelH <= 0) return;

            float aspect = viewportWidth / viewportHeight;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();
            var vp = viewMatrix * projMatrix;

            var handles = GetHandlePositions(collider);

            if (_isDragging)
            {
                if (!io.MouseDown[0])
                {
                    // End drag — record undo
                    EndDrag(collider);
                    return;
                }

                // Process drag
                var ray = ScreenToRay(localX, localY, panelW, panelH, camera, aspect);
                ProcessDrag(collider, ray);
                return;
            }

            // Hit test handles
            _hoveredHandle = HandleId.None;
            float bestDist = HitThreshold;

            foreach (var (handleId, worldPos) in handles)
            {
                var screen = WorldToScreen(worldPos, vp, panelW, panelH);
                if (screen.X < -1000) continue; // behind camera

                var mouseLocal = new Vector2(localX, localY);
                float dist = (mouseLocal - screen).Length();
                if (dist < bestDist)
                {
                    bestDist = dist;
                    _hoveredHandle = handleId;
                }
            }

            // Begin drag
            if (_hoveredHandle != HandleId.None && io.MouseClicked[0])
            {
                _activeHandle = _hoveredHandle;
                _isDragging = true;

                var ray = ScreenToRay(localX, localY, panelW, panelH, camera, aspect);
                var axisDir = GetHandleAxis(_activeHandle, collider);
                var axisOrigin = GetColliderWorldCenter(collider);
                _dragStartWorldPos = ProjectRayOntoAxis(ray, axisOrigin, axisDir);

                BeginDrag(collider);
            }
        }

        // ================================================================
        // Drawing (called within OnDrawGizmosSelected context)
        // ================================================================

        /// <summary>
        /// Draw handle squares on the collider wireframe.
        /// Call this from the gizmo render phase.
        /// </summary>
        public void DrawHandles(EditorCamera camera, float viewportWidth, float viewportHeight)
        {
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo == null) return;

            var collider = selectedGo.GetComponent<Collider>();
            if (collider == null) return;

            Gizmos.matrix = Matrix4x4.identity;

            var handles = GetHandlePositions(collider);
            foreach (var (handleId, worldPos) in handles)
            {
                bool isHovered = handleId == _hoveredHandle;
                bool isActive = handleId == _activeHandle && _isDragging;
                Gizmos.color = (isHovered || isActive) ? HandleHoverColor : HandleColor;

                // Draw a small cross/diamond at the handle position (visible at any distance)
                float dist = (camera.Position - worldPos).magnitude;
                float size = dist * 0.008f; // scale with distance to maintain screen size

                // Draw a small diamond shape (4 lines)
                var right = camera.Right * size;
                var up = camera.Up * size;
                Gizmos.DrawLine(worldPos - right, worldPos + up);
                Gizmos.DrawLine(worldPos + up, worldPos + right);
                Gizmos.DrawLine(worldPos + right, worldPos - up);
                Gizmos.DrawLine(worldPos - up, worldPos - right);
            }
        }

        // ================================================================
        // Handle positions for each collider type
        // ================================================================

        private List<(HandleId, Vector3)> GetHandlePositions(Collider collider)
        {
            var list = new List<(HandleId, Vector3)>();
            var t = collider.transform;
            var pos = t.position;
            var rot = t.rotation;
            var scale = t.lossyScale;

            if (collider is BoxCollider box)
            {
                var c = pos + rot * Vector3.Scale(box.center, scale);
                var halfSize = Vector3.Scale(box.size, scale) * 0.5f;
                var rx = rot * Vector3.right;
                var ry = rot * Vector3.up;
                var rz = rot * Vector3.forward;

                list.Add((HandleId.PosX, c + rx * halfSize.x));
                list.Add((HandleId.NegX, c - rx * halfSize.x));
                list.Add((HandleId.PosY, c + ry * halfSize.y));
                list.Add((HandleId.NegY, c - ry * halfSize.y));
                list.Add((HandleId.PosZ, c + rz * halfSize.z));
                list.Add((HandleId.NegZ, c - rz * halfSize.z));
            }
            else if (collider is SphereCollider sphere)
            {
                float maxScale = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                float r = sphere.radius * maxScale;
                var c = pos + sphere.center;

                list.Add((HandleId.PosX, c + Vector3.right * r));
                list.Add((HandleId.NegX, c - Vector3.right * r));
                list.Add((HandleId.PosY, c + Vector3.up * r));
                list.Add((HandleId.NegY, c - Vector3.up * r));
                list.Add((HandleId.PosZ, c + Vector3.forward * r));
                list.Add((HandleId.NegZ, c - Vector3.forward * r));
            }
            else if (collider is CapsuleCollider capsule)
            {
                var c = pos + rot * Vector3.Scale(capsule.center, scale);
                float halfH = Mathf.Max(capsule.height, capsule.radius * 2f) * 0.5f * scale.y;
                float r = capsule.radius * Mathf.Max(scale.x, scale.z);
                var ry = rot * Vector3.up;
                var rx = rot * Vector3.right;

                list.Add((HandleId.PosY, c + ry * halfH));           // top (height)
                list.Add((HandleId.NegY, c - ry * halfH));           // bottom (height)
                list.Add((HandleId.PosX, c + rx * r));               // right (radius)
                list.Add((HandleId.NegX, c - rx * r));               // left (radius)
            }
            else if (collider is CylinderCollider cylinder)
            {
                var c = pos + rot * Vector3.Scale(cylinder.center, scale);
                float halfH = cylinder.height * 0.5f * scale.y;
                float r = cylinder.radius * Mathf.Max(scale.x, scale.z);
                var ry = rot * Vector3.up;
                var rx = rot * Vector3.right;

                list.Add((HandleId.PosY, c + ry * halfH));           // top (height)
                list.Add((HandleId.NegY, c - ry * halfH));           // bottom (height)
                list.Add((HandleId.PosX, c + rx * r));               // right (radius)
                list.Add((HandleId.NegX, c - rx * r));               // left (radius)
            }

            return list;
        }

        // ================================================================
        // Drag logic
        // ================================================================

        private void BeginDrag(Collider collider)
        {
            _dragGoId = collider.gameObject.GetInstanceID();
            _dragComponentType = collider.GetType().Name;
            _dragMemberName2 = "";
            _dragStartValue2 = null;

            if (collider is BoxCollider box)
            {
                _dragMemberName = "size";
                _dragStartValue = box.size;
                _dragMemberName2 = "center";
                _dragStartValue2 = box.center;
            }
            else if (collider is SphereCollider sphere)
            {
                _dragMemberName = "radius";
                _dragStartValue = sphere.radius;
            }
            else if (collider is CapsuleCollider capsule)
            {
                if (_activeHandle == HandleId.PosY || _activeHandle == HandleId.NegY)
                {
                    _dragMemberName = "height";
                    _dragStartValue = capsule.height;
                }
                else
                {
                    _dragMemberName = "radius";
                    _dragStartValue = capsule.radius;
                }
            }
            else if (collider is CylinderCollider cylinder)
            {
                if (_activeHandle == HandleId.PosY || _activeHandle == HandleId.NegY)
                {
                    _dragMemberName = "height";
                    _dragStartValue = cylinder.height;
                }
                else
                {
                    _dragMemberName = "radius";
                    _dragStartValue = cylinder.radius;
                }
            }
        }

        private void ProcessDrag(Collider collider, Ray ray)
        {
            var axisDir = GetHandleAxis(_activeHandle, collider);
            var axisOrigin = GetColliderWorldCenter(collider);
            var projected = ProjectRayOntoAxis(ray, axisOrigin, axisDir);
            var delta = projected - _dragStartWorldPos;
            float axisDelta = Vector3.Dot(delta, axisDir);

            // Determine sign based on handle direction
            bool isNegative = _activeHandle == HandleId.NegX ||
                              _activeHandle == HandleId.NegY ||
                              _activeHandle == HandleId.NegZ;
            float signedDelta = axisDelta;

            var scale = collider.transform.lossyScale;

            if (collider is BoxCollider box)
            {
                var startSize = (Vector3)_dragStartValue!;
                var startCenter = (Vector3)_dragStartValue2!;

                // Get scale component for the active axis
                float axisScale = GetAxisScale(_activeHandle, scale);
                if (axisScale < 0.001f) return;

                float localDelta = signedDelta / axisScale;
                var newSize = startSize;
                var newCenter = startCenter;

                // Increase size on one axis, shift center by half the delta
                switch (_activeHandle)
                {
                    case HandleId.PosX:
                    case HandleId.NegX:
                        newSize = new Vector3(Mathf.Max(0.01f, startSize.x + localDelta), startSize.y, startSize.z);
                        newCenter = new Vector3(startCenter.x + (isNegative ? -localDelta : localDelta) * 0.5f,
                            startCenter.y, startCenter.z);
                        break;
                    case HandleId.PosY:
                    case HandleId.NegY:
                        newSize = new Vector3(startSize.x, Mathf.Max(0.01f, startSize.y + localDelta), startSize.z);
                        newCenter = new Vector3(startCenter.x,
                            startCenter.y + (isNegative ? -localDelta : localDelta) * 0.5f, startCenter.z);
                        break;
                    case HandleId.PosZ:
                    case HandleId.NegZ:
                        newSize = new Vector3(startSize.x, startSize.y, Mathf.Max(0.01f, startSize.z + localDelta));
                        newCenter = new Vector3(startCenter.x, startCenter.y,
                            startCenter.z + (isNegative ? -localDelta : localDelta) * 0.5f);
                        break;
                }

                box.size = newSize;
                box.center = newCenter;
            }
            else if (collider is SphereCollider sphere)
            {
                float startRadius = (float)_dragStartValue!;
                float maxScale = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                if (maxScale < 0.001f) return;
                sphere.radius = Mathf.Max(0.01f, startRadius + signedDelta / maxScale);
            }
            else if (collider is CapsuleCollider capsule)
            {
                if (_dragMemberName == "height")
                {
                    float startHeight = (float)_dragStartValue!;
                    float axisScale = scale.y;
                    if (axisScale < 0.001f) return;
                    capsule.height = Mathf.Max(capsule.radius * 2f, startHeight + signedDelta / axisScale);
                }
                else // radius
                {
                    float startRadius = (float)_dragStartValue!;
                    float radiusScale = Mathf.Max(scale.x, scale.z);
                    if (radiusScale < 0.001f) return;
                    capsule.radius = Mathf.Max(0.01f, startRadius + signedDelta / radiusScale);
                }
            }
            else if (collider is CylinderCollider cylinder)
            {
                if (_dragMemberName == "height")
                {
                    float startHeight = (float)_dragStartValue!;
                    float axisScale = scale.y;
                    if (axisScale < 0.001f) return;
                    cylinder.height = Mathf.Max(0.01f, startHeight + signedDelta / axisScale);
                }
                else // radius
                {
                    float startRadius = (float)_dragStartValue!;
                    float radiusScale = Mathf.Max(scale.x, scale.z);
                    if (radiusScale < 0.001f) return;
                    cylinder.radius = Mathf.Max(0.01f, startRadius + signedDelta / radiusScale);
                }
            }

            SceneManager.GetActiveScene().isDirty = true;
        }

        private void EndDrag(Collider collider)
        {
            _isDragging = false;
            _activeHandle = HandleId.None;

            // Record undo
            object? newValue = null;
            if (collider is BoxCollider box)
                newValue = box.size;
            else if (collider is SphereCollider sphere)
                newValue = sphere.radius;
            else if (collider is CapsuleCollider capsule)
                newValue = _dragMemberName == "height" ? (object)capsule.height : capsule.radius;
            else if (collider is CylinderCollider cylinder)
                newValue = _dragMemberName == "height" ? (object)cylinder.height : cylinder.radius;

            if (newValue != null && !Equals(newValue, _dragStartValue))
            {
                var actions = new List<IUndoAction>();
                actions.Add(new SetPropertyAction(
                    $"Edit {_dragComponentType}.{_dragMemberName}",
                    _dragGoId, _dragComponentType, _dragMemberName,
                    _dragStartValue, newValue));

                // BoxCollider also needs center undo
                if (!string.IsNullOrEmpty(_dragMemberName2) && collider is BoxCollider box2)
                {
                    var newCenter = box2.center;
                    if (!Equals(newCenter, _dragStartValue2))
                    {
                        actions.Add(new SetPropertyAction(
                            $"Edit {_dragComponentType}.{_dragMemberName2}",
                            _dragGoId, _dragComponentType, _dragMemberName2,
                            _dragStartValue2, newCenter));
                    }
                }

                if (actions.Count == 1)
                    UndoSystem.Record(actions[0]);
                else if (actions.Count > 1)
                    UndoSystem.Record(new CompoundUndoAction($"Edit {_dragComponentType}", actions));
            }
        }

        private void Reset()
        {
            _hoveredHandle = HandleId.None;
            _activeHandle = HandleId.None;
            _isDragging = false;
        }

        // ================================================================
        // Utilities
        // ================================================================

        private static Vector3 GetColliderWorldCenter(Collider collider)
        {
            var t = collider.transform;
            return t.position + t.rotation * Vector3.Scale(collider.center, t.lossyScale);
        }

        private static Vector3 GetHandleAxis(HandleId handle, Collider collider)
        {
            var rot = collider.transform.rotation;
            return handle switch
            {
                HandleId.PosX => rot * Vector3.right,
                HandleId.NegX => rot * Vector3.left,
                HandleId.PosY => rot * Vector3.up,
                HandleId.NegY => rot * Vector3.down,
                HandleId.PosZ => rot * Vector3.forward,
                HandleId.NegZ => rot * Vector3.back,
                _ => Vector3.up,
            };
        }

        private static float GetAxisScale(HandleId handle, Vector3 scale)
        {
            return handle switch
            {
                HandleId.PosX or HandleId.NegX => scale.x,
                HandleId.PosY or HandleId.NegY => scale.y,
                HandleId.PosZ or HandleId.NegZ => scale.z,
                _ => 1f,
            };
        }

        // ================================================================
        // Screen-space / ray math (same patterns as TransformGizmo)
        // ================================================================

        private struct Ray
        {
            public Vector3 Origin;
            public Vector3 Direction;
        }

        private static Vector2 WorldToScreen(Vector3 worldPos,
            System.Numerics.Matrix4x4 viewProj, float panelW, float panelH)
        {
            var p = new System.Numerics.Vector4(worldPos.x, worldPos.y, worldPos.z, 1f);
            var clip = System.Numerics.Vector4.Transform(p, viewProj);
            if (clip.W <= 0.001f) return new Vector2(-9999, -9999);

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float sx = (ndcX * 0.5f + 0.5f) * panelW;
            float sy = (1f - (ndcY * 0.5f + 0.5f)) * panelH;
            return new Vector2(sx, sy);
        }

        private static Ray ScreenToRay(float localX, float localY, float panelW, float panelH,
            EditorCamera camera, float aspect)
        {
            float ndcX = (localX / panelW) * 2f - 1f;
            float ndcY = 1f - (localY / panelH) * 2f;

            float tanHalfFov = MathF.Tan(camera.FieldOfView * 0.5f * Mathf.Deg2Rad);
            float viewX = ndcX * aspect * tanHalfFov;
            float viewY = ndcY * tanHalfFov;

            var forward = camera.Forward;
            var right = camera.Right;
            var up = camera.Up;

            var dir = (right * viewX + up * viewY + forward).normalized;
            return new Ray { Origin = camera.Position, Direction = dir };
        }

        private static Vector3 ProjectRayOntoAxis(Ray ray, Vector3 axisOrigin, Vector3 axisDir)
        {
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
    }
}
