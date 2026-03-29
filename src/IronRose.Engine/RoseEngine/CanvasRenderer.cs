using System;
using System.Collections.Generic;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    /// <summary>
    /// Canvas UI 트리를 ImGui DrawList로 렌더링하는 정적 시스템.
    /// ImGuiGameViewPanel에서 게임 이미지 위에 호출.
    /// </summary>
    public static class CanvasRenderer
    {
        /// <summary>디버그 Rect 아웃라인 표시 (에디터용).</summary>
        public static bool DebugDrawRects;

        /// <summary>현재 렌더링 중인 Canvas의 scale factor. RenderNode 순회 중에만 유효.</summary>
        internal static float CurrentCanvasScale { get; private set; } = 1f;

        private static readonly List<Canvas> _sorted = new();
        private static readonly Dictionary<Texture2D, IntPtr> _textureBindings = new();

        /// <summary>에디터에서 설정: Texture2D → ImGui texture ID 변환 콜백.</summary>
        public static Func<Texture2D, IntPtr>? ResolveTextureBinding;


        /// <summary>Texture2D에 대한 ImGui 텍스처 ID를 가져온다 (캐시 사용).</summary>
        internal static IntPtr GetTextureId(Texture2D? tex)
        {
            if (tex == null || ResolveTextureBinding == null)
                return IntPtr.Zero;

            if (!_textureBindings.TryGetValue(tex, out var id))
            {
                id = ResolveTextureBinding(tex);
                _textureBindings[tex] = id;
            }
            return id;
        }

        /// <summary>텍스처 캐시 초기화 (씬 전환 시).</summary>
        internal static void ClearTextureCache() => _textureBindings.Clear();

        /// <summary>
        /// 모든 활성 Canvas를 sortingOrder 순으로 렌더.
        /// </summary>
        public static void RenderAll(ImDrawListPtr drawList, float screenX, float screenY, float screenW, float screenH)
        {
            if (Canvas._allCanvases.Count == 0) return;

            _sorted.Clear();
            foreach (var c in Canvas._allCanvases)
            {
                if (!c._isDestroyed && c.gameObject.activeInHierarchy)
                    _sorted.Add(c);
            }
            _sorted.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));

            // Clip to game view image area
            drawList.PushClipRect(
                new SNVector2(screenX, screenY),
                new SNVector2(screenX + screenW, screenY + screenH));

            foreach (var canvas in _sorted)
            {
                if (canvas.renderMode != CanvasRenderMode.ScreenSpaceOverlay)
                    continue;

                float scaleFactor = canvas.GetScaleFactor(screenW, screenH);
                float logicalW = scaleFactor > 0 ? screenW / scaleFactor : screenW;
                float logicalH = scaleFactor > 0 ? screenH / scaleFactor : screenH;

                var rootRect = new Rect(0, 0, logicalW, logicalH);

                CurrentCanvasScale = scaleFactor;
                RenderNode(drawList, canvas.gameObject, rootRect, screenX, screenY, scaleFactor);
            }

            CurrentCanvasScale = 1f;
            drawList.PopClipRect();
        }

        private static void RenderNode(ImDrawListPtr drawList, GameObject go, Rect parentRect,
            float ox, float oy, float scale)
        {
            if (!go.activeInHierarchy) return;

            var rt = go.GetComponent<RectTransform>();
            Rect localRect = rt != null ? rt.GetWorldRect(parentRect) : parentRect;

            // Convert canvas coordinates → screen coordinates
            var screenRect = new Rect(
                ox + localRect.x * scale,
                oy + localRect.y * scale,
                localRect.width * scale,
                localRect.height * scale);

            // Cache screen rect for gizmo editor
            if (rt != null) rt.lastScreenRect = screenRect;

            // Debug: draw rect outlines
            if (DebugDrawRects && rt != null)
            {
                uint debugCol = 0xFF00FF00; // green
                drawList.AddRect(
                    new SNVector2(screenRect.x, screenRect.y),
                    new SNVector2(screenRect.x + screenRect.width, screenRect.y + screenRect.height),
                    debugCol, 0f, ImDrawFlags.None, 1f);
                // Label
                var label = $"{go.name} ({localRect.width:F0}x{localRect.height:F0})";
                drawList.AddText(new SNVector2(screenRect.x + 2, screenRect.y + 1), 0xFF00FF00, label);
            }

            // Transform: rotation + scale from transform
            float rotDeg = go.transform.localEulerAngles.z;
            float rotRad = rotDeg * (MathF.PI / 180f);
            var ls = go.transform.localScale;
            bool hasRotation = MathF.Abs(rotRad) > 0.001f;
            bool hasScale = MathF.Abs(ls.x - 1f) > 0.001f || MathF.Abs(ls.y - 1f) > 0.001f;
            bool needsTransform = hasRotation || hasScale;

            // Pivot in screen space (transform center)
            SNVector2 pivotScreen = new SNVector2(
                screenRect.x + screenRect.width * (rt != null ? rt.pivot.x : 0.5f),
                screenRect.y + screenRect.height * (rt != null ? rt.pivot.y : 0.5f));

            // Render all IUIRenderable components on this GO
            foreach (var comp in go.InternalComponents)
            {
                if (comp is IUIRenderable renderable && !comp._isDestroyed)
                {
                    int vtxBefore = drawList.VtxBuffer.Size;
                    renderable.OnRenderUI(drawList, screenRect);

                    if (needsTransform)
                    {
                        int vtxAfter = drawList.VtxBuffer.Size;
                        TransformVertices(drawList, vtxBefore, vtxAfter, pivotScreen,
                            hasRotation ? -rotRad : 0f, ls.x, ls.y);
                    }
                }
            }

            // Apply layout groups before rendering children
            var layoutGroup = go.GetComponent<UILayoutGroup>();
            layoutGroup?.LayoutChildren();

            // ScrollView offset for children
            var scrollView = go.GetComponent<UIScrollView>();
            float childOx = ox;
            float childOy = oy;
            if (scrollView != null)
            {
                childOx -= scrollView.scrollPosition.x * scale;
                childOy -= scrollView.scrollPosition.y * scale;
            }

            // Recurse into children (DFS)
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                RenderNode(drawList, t.GetChild(i).gameObject, localRect, childOx, childOy, scale);
            }
        }

        /// <summary>Scale + rotate ImGui draw list vertices around a pivot point.</summary>
        private static unsafe void TransformVertices(ImDrawListPtr drawList, int from, int to,
            SNVector2 pivot, float rad, float scaleX, float scaleY)
        {
            if (from >= to) return;
            float cos = MathF.Cos(rad);
            float sin = MathF.Sin(rad);

            ImDrawVert* vtx = (ImDrawVert*)drawList.VtxBuffer.Data;
            for (int i = from; i < to; i++)
            {
                // Scale around pivot, then rotate around pivot
                float dx = (vtx[i].pos.X - pivot.X) * scaleX;
                float dy = (vtx[i].pos.Y - pivot.Y) * scaleY;
                vtx[i].pos.X = pivot.X + dx * cos - dy * sin;
                vtx[i].pos.Y = pivot.Y + dx * sin + dy * cos;
            }
        }

        // ── UI Hit-Testing ──────────────────────────────────────────

        /// <summary>
        /// 주어진 스크린 좌표에서 가장 위에 있는 UI GameObject를 반환.
        /// IUIRenderable 컴포넌트가 있는 GO만 대상.
        /// </summary>
        public static GameObject? HitTest(float mouseScreenX, float mouseScreenY,
            float screenX, float screenY, float screenW, float screenH)
        {
            if (Canvas._allCanvases.Count == 0) return null;

            _sorted.Clear();
            foreach (var c in Canvas._allCanvases)
            {
                if (!c._isDestroyed && c.gameObject.activeInHierarchy)
                    _sorted.Add(c);
            }
            _sorted.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));

            GameObject? hit = null;

            foreach (var canvas in _sorted)
            {
                if (canvas.renderMode != CanvasRenderMode.ScreenSpaceOverlay)
                    continue;

                float scaleFactor = canvas.GetScaleFactor(screenW, screenH);
                float logicalW = scaleFactor > 0 ? screenW / scaleFactor : screenW;
                float logicalH = scaleFactor > 0 ? screenH / scaleFactor : screenH;

                var rootRect = new Rect(0, 0, logicalW, logicalH);

                HitTestNode(canvas.gameObject, rootRect, screenX, screenY, scaleFactor,
                    mouseScreenX, mouseScreenY, ref hit);
            }

            return hit;
        }

        /// <summary>
        /// 스크린 사각형 영역과 겹치는 모든 UI GameObject의 인스턴스 ID를 수집.
        /// </summary>
        public static void CollectHitsInRect(
            float rectScreenMinX, float rectScreenMinY,
            float rectScreenMaxX, float rectScreenMaxY,
            float screenX, float screenY, float screenW, float screenH,
            List<int> hitIds)
        {
            if (Canvas._allCanvases.Count == 0) return;

            _sorted.Clear();
            foreach (var c in Canvas._allCanvases)
            {
                if (!c._isDestroyed && c.gameObject.activeInHierarchy)
                    _sorted.Add(c);
            }
            _sorted.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));

            foreach (var canvas in _sorted)
            {
                if (canvas.renderMode != CanvasRenderMode.ScreenSpaceOverlay)
                    continue;

                float scaleFactor = canvas.GetScaleFactor(screenW, screenH);
                float logicalW = scaleFactor > 0 ? screenW / scaleFactor : screenW;
                float logicalH = scaleFactor > 0 ? screenH / scaleFactor : screenH;

                var rootRect = new Rect(0, 0, logicalW, logicalH);

                RectTestNode(canvas.gameObject, rootRect, screenX, screenY, scaleFactor,
                    rectScreenMinX, rectScreenMinY, rectScreenMaxX, rectScreenMaxY, hitIds);
            }
        }

        private static void HitTestNode(GameObject go, Rect parentRect,
            float ox, float oy, float scale,
            float mouseX, float mouseY, ref GameObject? result)
        {
            if (!go.activeInHierarchy) return;

            var rt = go.GetComponent<RectTransform>();
            Rect localRect = rt != null ? rt.GetWorldRect(parentRect) : parentRect;

            var screenRect = new Rect(
                ox + localRect.x * scale,
                oy + localRect.y * scale,
                localRect.width * scale,
                localRect.height * scale);

            if (rt != null && HasUIRenderable(go) &&
                mouseX >= screenRect.x && mouseX <= screenRect.xMax &&
                mouseY >= screenRect.y && mouseY <= screenRect.yMax)
            {
                result = go;
            }

            var scrollView = go.GetComponent<UIScrollView>();
            float childOx = ox;
            float childOy = oy;
            if (scrollView != null)
            {
                childOx -= scrollView.scrollPosition.x * scale;
                childOy -= scrollView.scrollPosition.y * scale;
            }

            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                HitTestNode(t.GetChild(i).gameObject, localRect, childOx, childOy, scale,
                    mouseX, mouseY, ref result);
            }
        }

        private static void RectTestNode(GameObject go, Rect parentRect,
            float ox, float oy, float scale,
            float rectMinX, float rectMinY, float rectMaxX, float rectMaxY,
            List<int> hitIds)
        {
            if (!go.activeInHierarchy) return;

            var rt = go.GetComponent<RectTransform>();
            Rect localRect = rt != null ? rt.GetWorldRect(parentRect) : parentRect;

            var screenRect = new Rect(
                ox + localRect.x * scale,
                oy + localRect.y * scale,
                localRect.width * scale,
                localRect.height * scale);

            if (rt != null && HasUIRenderable(go) && !go._isEditorInternal &&
                screenRect.x <= rectMaxX && screenRect.xMax >= rectMinX &&
                screenRect.y <= rectMaxY && screenRect.yMax >= rectMinY)
            {
                hitIds.Add(go.GetInstanceID());
            }

            var scrollView = go.GetComponent<UIScrollView>();
            float childOx = ox;
            float childOy = oy;
            if (scrollView != null)
            {
                childOx -= scrollView.scrollPosition.x * scale;
                childOy -= scrollView.scrollPosition.y * scale;
            }

            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                RectTestNode(t.GetChild(i).gameObject, localRect, childOx, childOy, scale,
                    rectMinX, rectMinY, rectMaxX, rectMaxY, hitIds);
            }
        }

        private static bool HasUIRenderable(GameObject go)
        {
            foreach (var comp in go.InternalComponents)
            {
                if (comp is IUIRenderable && !comp._isDestroyed)
                    return true;
            }
            return false;
        }

        // ── Canvas Scale Utility ────────────────────────────────────

        /// <summary>
        /// GO의 조상 Canvas를 찾아 scaleFactor를 반환.
        /// Canvas가 없으면 1f.
        /// </summary>
        public static float GetCanvasScaleFor(GameObject go, float screenW, float screenH)
        {
            var current = go.transform;
            while (current != null)
            {
                var canvas = current.gameObject.GetComponent<Canvas>();
                if (canvas != null)
                    return canvas.GetScaleFactor(screenW, screenH);
                current = current.parent;
            }
            return 1f;
        }
    }
}
