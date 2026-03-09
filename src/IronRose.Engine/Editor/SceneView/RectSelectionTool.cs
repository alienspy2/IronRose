using System;
using System.Collections.Generic;
using ImGuiNET;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector3 = RoseEngine.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Scene View rectangle selection tool.
    /// Tracks LMB drag to define a selection rectangle, then resolves
    /// selected objects by projecting their world-space AABBs to screen space.
    /// </summary>
    internal sealed class RectSelectionTool
    {
        private const float DragThreshold = 4f;

        private bool _isTracking;
        private bool _isRectActive;
        private Vector2 _startScreenPos;
        private Vector2 _currentScreenPos;

        public bool IsActive => _isRectActive;
        public bool IsTracking => _isTracking;

        public void BeginTracking(Vector2 screenPos)
        {
            _isTracking = true;
            _isRectActive = false;
            _startScreenPos = screenPos;
            _currentScreenPos = screenPos;
        }

        public void UpdateDrag(Vector2 screenPos)
        {
            if (!_isTracking) return;
            _currentScreenPos = screenPos;

            if (!_isRectActive)
            {
                var delta = _currentScreenPos - _startScreenPos;
                if (MathF.Abs(delta.X) > DragThreshold || MathF.Abs(delta.Y) > DragThreshold)
                    _isRectActive = true;
            }
        }

        /// <summary>
        /// Finalize rectangle selection on LMB release.
        /// Projects each GameObject's AABB to screen space and selects overlapping objects.
        /// </summary>
        public void EndTracking(
            EditorCamera camera, ImGuiSceneViewPanel sceneView,
            uint fbWidth, uint fbHeight,
            bool ctrlHeld, bool shiftHeld)
        {
            bool wasActive = _isRectActive;
            _isTracking = false;
            _isRectActive = false;
            if (!wasActive) return;

            var panelMin = sceneView.ImageScreenMin;
            var panelMax = sceneView.ImageScreenMax;
            float panelW = panelMax.X - panelMin.X;
            float panelH = panelMax.Y - panelMin.Y;
            if (panelW <= 0 || panelH <= 0) return;

            // Selection rect in panel-local coords
            float rectMinX = MathF.Max(MathF.Min(_startScreenPos.X, _currentScreenPos.X) - panelMin.X, 0);
            float rectMinY = MathF.Max(MathF.Min(_startScreenPos.Y, _currentScreenPos.Y) - panelMin.Y, 0);
            float rectMaxX = MathF.Min(MathF.Max(_startScreenPos.X, _currentScreenPos.X) - panelMin.X, panelW);
            float rectMaxY = MathF.Min(MathF.Max(_startScreenPos.Y, _currentScreenPos.Y) - panelMin.Y, panelH);

            float aspect = (float)fbWidth / fbHeight;
            var vp = camera.GetViewMatrix().ToNumerics() * camera.GetProjectionMatrix(aspect).ToNumerics();

            var hitIds = new List<int>();

            foreach (var go in SceneManager.AllGameObjects)
            {
                if (go._isDestroyed || go._isEditorInternal || !go.activeInHierarchy)
                    continue;

                var filter = go.GetComponent<MeshFilter>();
                Bounds localBounds;
                if (filter?.mesh != null)
                    localBounds = filter.mesh.bounds;
                else
                    localBounds = new Bounds(Vector3.zero, new Vector3(0.5f, 0.5f, 0.5f));

                if (ProjectBoundsOverlaps(go.transform, localBounds, vp, panelW, panelH,
                        rectMinX, rectMinY, rectMaxX, rectMaxY))
                {
                    hitIds.Add(go.GetInstanceID());
                }
            }

            // Include UI objects in rectangle selection
            if (sceneView.ShowUI)
            {
                float screenRectMinX = rectMinX + panelMin.X;
                float screenRectMinY = rectMinY + panelMin.Y;
                float screenRectMaxX = rectMaxX + panelMin.X;
                float screenRectMaxY = rectMaxY + panelMin.Y;
                CanvasRenderer.CollectHitsInRect(
                    screenRectMinX, screenRectMinY, screenRectMaxX, screenRectMaxY,
                    panelMin.X, panelMin.Y, panelW, panelH, hitIds);
            }

            if (ctrlHeld)
            {
                foreach (var id in hitIds)
                    EditorSelection.ToggleSelect(id);
            }
            else if (shiftHeld)
            {
                var current = new List<int>(EditorSelection.SelectedGameObjectIds);
                var currentSet = new HashSet<int>(current);
                foreach (var id in hitIds)
                {
                    if (currentSet.Add(id))
                        current.Add(id);
                }
                EditorSelection.SetSelection(current);
            }
            else
            {
                EditorSelection.SetSelection(hitIds);
            }
        }

        public void Cancel()
        {
            _isTracking = false;
            _isRectActive = false;
        }

        public void DrawOverlay()
        {
            if (!_isRectActive) return;

            var drawList = ImGui.GetForegroundDrawList();
            var rectMin = new Vector2(
                MathF.Min(_startScreenPos.X, _currentScreenPos.X),
                MathF.Min(_startScreenPos.Y, _currentScreenPos.Y));
            var rectMax = new Vector2(
                MathF.Max(_startScreenPos.X, _currentScreenPos.X),
                MathF.Max(_startScreenPos.Y, _currentScreenPos.Y));

            uint fillColor = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.15f));
            uint borderColor = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.8f));

            drawList.AddRectFilled(rectMin, rectMax, fillColor);
            drawList.AddRect(rectMin, rectMax, borderColor, 0f, ImDrawFlags.None, 1f);
        }

        /// <summary>
        /// Project an object's local-space AABB corners to screen space
        /// and test overlap with the selection rectangle.
        /// </summary>
        private static bool ProjectBoundsOverlaps(
            Transform transform, Bounds localBounds,
            System.Numerics.Matrix4x4 vp, float panelW, float panelH,
            float rectMinX, float rectMinY, float rectMaxX, float rectMaxY)
        {
            float objMinX = float.MaxValue, objMinY = float.MaxValue;
            float objMaxX = float.MinValue, objMaxY = float.MinValue;

            var bMin = localBounds.min;
            var bMax = localBounds.max;
            int validCount = 0;

            for (int i = 0; i < 8; i++)
            {
                var localCorner = new Vector3(
                    (i & 1) == 0 ? bMin.x : bMax.x,
                    (i & 2) == 0 ? bMin.y : bMax.y,
                    (i & 4) == 0 ? bMin.z : bMax.z);

                var worldPos = transform.TransformPoint(localCorner);

                var clip = System.Numerics.Vector4.Transform(
                    new System.Numerics.Vector4(worldPos.x, worldPos.y, worldPos.z, 1f), vp);

                if (clip.W <= 0.001f)
                    continue;

                float ndcX = clip.X / clip.W;
                float ndcY = clip.Y / clip.W;

                float sx = (ndcX * 0.5f + 0.5f) * panelW;
                float sy = (1f - (ndcY * 0.5f + 0.5f)) * panelH;

                objMinX = MathF.Min(objMinX, sx);
                objMinY = MathF.Min(objMinY, sy);
                objMaxX = MathF.Max(objMaxX, sx);
                objMaxY = MathF.Max(objMaxY, sy);
                validCount++;
            }

            if (validCount == 0) return false;

            return objMinX <= rectMaxX && objMaxX >= rectMinX &&
                   objMinY <= rectMaxY && objMaxY >= rectMinY;
        }
    }
}
