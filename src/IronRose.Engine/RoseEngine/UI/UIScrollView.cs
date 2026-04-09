// ------------------------------------------------------------
// @file    UIScrollView.cs
// @brief   스크롤 가능한 UI 컨테이너 컴포넌트. 수평/수직 스크롤바와 마우스 휠을 지원한다.
// @deps    CanvasRenderer, IUIRenderable, Component
// @exports
//   class UIScrollView : Component, IUIRenderable
//     Vector2 scrollPosition   — 현재 스크롤 위치
//     Vector2 contentSize      — 콘텐츠 전체 크기
//     void OnRenderUI(...)     — 렌더링 + 입력 처리
// @note    CanvasRenderer.IsInteractive가 false이면 스크롤/드래그 입력을 무시하고
//          스크롤바 렌더링만 수행한다.
//          겹친 UI에서는 CanvasRenderer.IsHitOrAncestorOfHit()으로 최상위 히트 대상만 스크롤/드래그 허용.
// ------------------------------------------------------------
using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public class UIScrollView : Component, IUIRenderable
    {
        public bool horizontal;
        public bool vertical = true;
        public Vector2 scrollPosition = Vector2.zero;
        public Vector2 contentSize = new(0f, 600f);
        public float scrollSensitivity = 30f;

        public Color scrollbarColor = new(0.4f, 0.4f, 0.4f, 0.6f);
        public Color scrollbarHoverColor = new(0.5f, 0.5f, 0.5f, 0.8f);
        public float scrollbarWidth = 8f;

        private bool _isDraggingV;
        private bool _isDraggingH;
        private float _dragStartOffset;

        public int renderOrder => 0;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            // Clamp scroll position
            float maxScrollX = Math.Max(0, contentSize.x - screenRect.width);
            float maxScrollY = Math.Max(0, contentSize.y - screenRect.height);
            scrollPosition = new Vector2(
                Math.Clamp(scrollPosition.x, 0, maxScrollX),
                Math.Clamp(scrollPosition.y, 0, maxScrollY));

            // Clip children (CanvasRenderer handles child rendering — we just provide scroll offset)
            drawList.PushClipRect(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                true);

            // Input handling (Scene View 등 비인터랙티브 컨텍스트에서는 입력 스킵)
            var mousePos = ImGui.GetMousePos();
            bool inRect = mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                          mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

            bool interactive = CanvasRenderer.IsInteractive;
            // 겹친 UI가 있을 때 최상위 히트 대상만 스크롤 입력 허용
            bool isHitTarget = CanvasRenderer.IsHitOrAncestorOfHit(gameObject);

            // Mouse wheel scrolling
            if (interactive && inRect && isHitTarget)
            {
                var wheel = ImGui.GetIO().MouseWheel;
                if (Math.Abs(wheel) > 0.01f)
                {
                    if (vertical)
                        scrollPosition = new Vector2(scrollPosition.x,
                            Math.Clamp(scrollPosition.y - wheel * scrollSensitivity, 0, maxScrollY));
                    else if (horizontal)
                        scrollPosition = new Vector2(
                            Math.Clamp(scrollPosition.x - wheel * scrollSensitivity, 0, maxScrollX),
                            scrollPosition.y);
                }
            }

            // Draw vertical scrollbar
            if (vertical && contentSize.y > screenRect.height)
            {
                float barHeight = screenRect.height;
                float thumbHeight = Math.Max(20f, barHeight * (screenRect.height / contentSize.y));
                float thumbY = screenRect.y + (barHeight - thumbHeight) * (maxScrollY > 0 ? scrollPosition.y / maxScrollY : 0);
                float barX = screenRect.xMax - scrollbarWidth;

                bool hoverThumb = mousePos.X >= barX && mousePos.X <= screenRect.xMax &&
                                  mousePos.Y >= thumbY && mousePos.Y <= thumbY + thumbHeight;

                if (interactive)
                {
                    // 드래그 시작은 히트 대상일 때만 허용 (드래그 중에는 계속 허용)
                    if (hoverThumb && isHitTarget && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        _isDraggingV = true;
                        _dragStartOffset = mousePos.Y - thumbY;
                    }
                    if (_isDraggingV)
                    {
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            float newThumbY = mousePos.Y - _dragStartOffset - screenRect.y;
                            float t = (barHeight - thumbHeight) > 0 ? newThumbY / (barHeight - thumbHeight) : 0;
                            scrollPosition = new Vector2(scrollPosition.x, Math.Clamp(t * maxScrollY, 0, maxScrollY));
                        }
                        else
                            _isDraggingV = false;
                    }
                }

                uint col = ColorToU32(hoverThumb || _isDraggingV ? scrollbarHoverColor : scrollbarColor);
                drawList.AddRectFilled(
                    new SNVector2(barX, thumbY),
                    new SNVector2(screenRect.xMax, thumbY + thumbHeight),
                    col, scrollbarWidth * 0.5f);
            }

            // Draw horizontal scrollbar
            if (horizontal && contentSize.x > screenRect.width)
            {
                float barWidth = screenRect.width;
                float thumbWidth = Math.Max(20f, barWidth * (screenRect.width / contentSize.x));
                float thumbX = screenRect.x + (barWidth - thumbWidth) * (maxScrollX > 0 ? scrollPosition.x / maxScrollX : 0);
                float barY = screenRect.yMax - scrollbarWidth;

                bool hoverThumb = mousePos.Y >= barY && mousePos.Y <= screenRect.yMax &&
                                  mousePos.X >= thumbX && mousePos.X <= thumbX + thumbWidth;

                if (interactive)
                {
                    // 드래그 시작은 히트 대상일 때만 허용 (드래그 중에는 계속 허용)
                    if (hoverThumb && isHitTarget && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        _isDraggingH = true;
                        _dragStartOffset = mousePos.X - thumbX;
                    }
                    if (_isDraggingH)
                    {
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            float newThumbX = mousePos.X - _dragStartOffset - screenRect.x;
                            float t = (barWidth - thumbWidth) > 0 ? newThumbX / (barWidth - thumbWidth) : 0;
                            scrollPosition = new Vector2(Math.Clamp(t * maxScrollX, 0, maxScrollX), scrollPosition.y);
                        }
                        else
                            _isDraggingH = false;
                    }
                }

                uint col = ColorToU32(hoverThumb || _isDraggingH ? scrollbarHoverColor : scrollbarColor);
                drawList.AddRectFilled(
                    new SNVector2(thumbX, barY),
                    new SNVector2(thumbX + thumbWidth, screenRect.yMax),
                    col, scrollbarWidth * 0.5f);
            }

            drawList.PopClipRect();
        }

        private static uint ColorToU32(Color c)
        {
            byte r = (byte)(Math.Clamp(c.r, 0f, 1f) * 255f);
            byte g = (byte)(Math.Clamp(c.g, 0f, 1f) * 255f);
            byte b = (byte)(Math.Clamp(c.b, 0f, 1f) * 255f);
            byte a = (byte)(Math.Clamp(c.a, 0f, 1f) * 255f);
            return (uint)(r | (g << 8) | (b << 16) | (a << 24));
        }
    }
}
