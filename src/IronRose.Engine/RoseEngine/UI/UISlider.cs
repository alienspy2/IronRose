// ------------------------------------------------------------
// @file    UISlider.cs
// @brief   드래그 가능한 UI 슬라이더 컴포넌트. 수평/수직 방향을 지원하며
//          값 변경 시 onValueChanged 콜백을 호출한다.
// @deps    CanvasRenderer, IUIRenderable, Component
// @exports
//   class UISlider : Component, IUIRenderable
//     float value                      — 현재 슬라이더 값
//     Action<float>? onValueChanged    — 값 변경 시 호출되는 콜백
//     void OnRenderUI(...)             — 렌더링 + 입력 처리
// @note    CanvasRenderer.IsInteractive가 false이면 입력을 무시하고 렌더링만 수행한다.
// ------------------------------------------------------------
using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public enum SliderDirection
    {
        LeftToRight,
        RightToLeft,
        BottomToTop,
        TopToBottom
    }

    public class UISlider : Component, IUIRenderable
    {
        public float value;
        public float minValue;
        public float maxValue = 1f;
        public bool wholeNumbers;
        public SliderDirection direction = SliderDirection.LeftToRight;

        public Color backgroundColor = new(0.2f, 0.2f, 0.2f, 1f);
        public Color fillColor = new(0.3f, 0.6f, 0.9f, 1f);
        public Color handleColor = Color.white;
        public float handleSize = 10f;

        public bool interactable = true;
        public Action<float>? onValueChanged;

        private bool _isDragging;

        public int renderOrder => 5;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            uint bgCol = ColorToU32(backgroundColor);
            uint fillCol = ColorToU32(fillColor);
            uint handleCol = ColorToU32(handleColor);

            // Background track
            drawList.AddRectFilled(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                bgCol, 2f);

            // Normalize value
            float range = maxValue - minValue;
            float t = range > 0 ? Math.Clamp((value - minValue) / range, 0f, 1f) : 0f;

            if (direction == SliderDirection.RightToLeft || direction == SliderDirection.TopToBottom)
                t = 1f - t;

            bool horizontal = direction == SliderDirection.LeftToRight || direction == SliderDirection.RightToLeft;

            // Fill
            if (horizontal)
            {
                float fillX = screenRect.x + t * screenRect.width;
                drawList.AddRectFilled(
                    new SNVector2(screenRect.x, screenRect.y),
                    new SNVector2(fillX, screenRect.yMax),
                    fillCol, 2f);

                // Handle
                float hx = fillX;
                float hy = screenRect.y + screenRect.height * 0.5f;
                drawList.AddCircleFilled(new SNVector2(hx, hy), handleSize * 0.5f, handleCol);
            }
            else
            {
                float fillY = screenRect.y + t * screenRect.height;
                drawList.AddRectFilled(
                    new SNVector2(screenRect.x, screenRect.y),
                    new SNVector2(screenRect.xMax, fillY),
                    fillCol, 2f);

                float hx = screenRect.x + screenRect.width * 0.5f;
                float hy = fillY;
                drawList.AddCircleFilled(new SNVector2(hx, hy), handleSize * 0.5f, handleCol);
            }

            // Interaction (Scene View 등 비인터랙티브 컨텍스트에서는 입력 스킵)
            if (!interactable || !CanvasRenderer.IsInteractive) return;

            var mousePos = ImGui.GetMousePos();
            bool inRect = mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                          mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

            if (inRect && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _isDragging = true;

            if (_isDragging)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    float newT;
                    if (horizontal)
                        newT = Math.Clamp((mousePos.X - screenRect.x) / screenRect.width, 0f, 1f);
                    else
                        newT = Math.Clamp((mousePos.Y - screenRect.y) / screenRect.height, 0f, 1f);

                    if (direction == SliderDirection.RightToLeft || direction == SliderDirection.TopToBottom)
                        newT = 1f - newT;

                    float newValue = minValue + newT * range;
                    if (wholeNumbers)
                        newValue = MathF.Round(newValue);

                    if (Math.Abs(newValue - value) > float.Epsilon)
                    {
                        value = newValue;
                        try { onValueChanged?.Invoke(value); }
                        catch (Exception ex) { Debug.LogError($"[UISlider] onValueChanged error: {ex.Message}"); }
                    }
                }
                else
                {
                    _isDragging = false;
                }
            }
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
