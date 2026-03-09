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

            // Interaction
            if (!interactable) return;

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
