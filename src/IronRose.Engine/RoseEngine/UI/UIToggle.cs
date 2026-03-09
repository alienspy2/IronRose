using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public class UIToggle : Component, IUIRenderable
    {
        public bool isOn;
        public bool interactable = true;

        public Color backgroundColor = new(0.3f, 0.3f, 0.3f, 1f);
        public Color checkmarkColor = new(0.3f, 0.7f, 0.3f, 1f);

        public Action<bool>? onValueChanged;

        public int renderOrder => 5;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            uint bgCol = ColorToU32(backgroundColor);
            uint checkCol = ColorToU32(checkmarkColor);

            // Background box
            drawList.AddRectFilled(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                bgCol, 3f);

            // Border
            drawList.AddRect(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                0xFFAAAAAA, 3f);

            // Checkmark
            if (isOn)
            {
                float pad = Math.Min(screenRect.width, screenRect.height) * 0.2f;
                float x0 = screenRect.x + pad;
                float y0 = screenRect.y + pad;
                float x1 = screenRect.xMax - pad;
                float y1 = screenRect.yMax - pad;
                float mx = screenRect.x + screenRect.width * 0.35f;
                float my = screenRect.yMax - pad;

                drawList.AddLine(new SNVector2(x0, y0 + (y1 - y0) * 0.5f), new SNVector2(mx, my), checkCol, 2f);
                drawList.AddLine(new SNVector2(mx, my), new SNVector2(x1, y0), checkCol, 2f);
            }

            // Click detection
            if (!interactable) return;

            var mousePos = ImGui.GetMousePos();
            bool inRect = mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                          mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

            if (inRect && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                isOn = !isOn;
                try { onValueChanged?.Invoke(isOn); }
                catch (Exception ex) { Debug.LogError($"[UIToggle] onValueChanged error: {ex.Message}"); }
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
