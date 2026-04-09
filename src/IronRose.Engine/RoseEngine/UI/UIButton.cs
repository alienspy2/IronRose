// ------------------------------------------------------------
// @file    UIButton.cs
// @brief   클릭 가능한 UI 버튼 컴포넌트. ImGui 히트 테스트로 hover/press 상태를 감지하고
//          onClick 콜백을 호출한다.
// @deps    CanvasRenderer, IUIRenderable, UIImage, Component
// @exports
//   class UIButton : Component, IUIRenderable
//     Action? onClick          — 클릭 시 호출되는 콜백
//     void OnRenderUI(...)     — 렌더링 + 입력 처리
// @note    CanvasRenderer.IsInteractive가 false이면 입력을 무시하고 렌더링만 수행한다.
//          겹친 UI에서는 CanvasRenderer.IsHitOrAncestorOfHit()으로 최상위 히트 대상만 입력 처리.
// ------------------------------------------------------------
using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public enum ButtonTransition
    {
        ColorTint,
        SpriteSwap
    }

    public class UIButton : Component, IUIRenderable
    {
        public Action? onClick;

        public Color normalColor = Color.white;
        public Color hoverColor = new(0.9f, 0.9f, 0.9f, 1f);
        public Color pressedColor = new(0.7f, 0.7f, 0.7f, 1f);
        public Color disabledColor = new(0.5f, 0.5f, 0.5f, 0.5f);
        public ButtonTransition transition = ButtonTransition.ColorTint;

        public bool interactable = true;

        private bool _isHovered;
        private bool _isPressed;

        public int renderOrder => 10;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            // Hit test (Scene View 등 비인터랙티브 컨텍스트에서는 입력 스킵)
            if (!CanvasRenderer.IsInteractive)
            {
                _isHovered = false;
                _isPressed = false;
            }
            else
            {
                var mousePos = ImGui.GetMousePos();
                bool inRect = interactable &&
                    mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                    mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

                // 겹친 UI가 있을 때 최상위 히트 대상만 입력을 처리
                _isHovered = inRect && CanvasRenderer.IsHitOrAncestorOfHit(gameObject);

                _isPressed = _isHovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);

                if (_isHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    try { onClick?.Invoke(); }
                    catch (Exception ex) { Debug.LogError($"[UIButton] onClick error: {ex.Message}"); }
                }
            }

            // Apply tint color to sibling UIImage/UIText
            if (transition == ButtonTransition.ColorTint)
            {
                Color tint;
                if (!interactable) tint = disabledColor;
                else if (_isPressed) tint = pressedColor;
                else if (_isHovered) tint = hoverColor;
                else tint = normalColor;

                // UIImage tint
                var img = gameObject.GetComponent<UIImage>();
                if (img != null) img.color = tint;
            }
        }
    }
}
