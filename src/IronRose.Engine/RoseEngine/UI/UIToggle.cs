// ------------------------------------------------------------
// @file    UIToggle.cs
// @brief   체크박스 스타일 UI 토글 컴포넌트. 클릭 시 isOn 상태를 반전하고
//          onValueChanged 콜백을 호출한다.
// @deps    CanvasRenderer, IUIRenderable, Component
// @exports
//   class UIToggle : Component, IUIRenderable
//     bool isOn                      — 토글 상태
//     Action<bool>? onValueChanged   — 값 변경 시 호출되는 콜백
//     void OnRenderUI(...)           — 렌더링 + 입력 처리
// @note    CanvasRenderer.IsInteractive가 false이면 입력을 무시하고 렌더링만 수행한다.
//          겹친 UI에서는 CanvasRenderer.IsHitOrAncestorOfHit()으로 최상위 히트 대상만 입력 처리.
// ------------------------------------------------------------
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

            // Click detection (Scene View 등 비인터랙티브 컨텍스트에서는 입력 스킵)
            if (!interactable || !CanvasRenderer.IsInteractive) return;

            var mousePos = ImGui.GetMousePos();
            bool inRect = mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                          mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

            // 겹친 UI가 있을 때 최상위 히트 대상만 입력을 처리
            if (inRect && CanvasRenderer.IsHitOrAncestorOfHit(gameObject)
                && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
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
