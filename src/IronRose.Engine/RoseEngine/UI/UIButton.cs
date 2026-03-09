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
            // Hit test
            var mousePos = ImGui.GetMousePos();
            _isHovered = interactable &&
                mousePos.X >= screenRect.x && mousePos.X <= screenRect.xMax &&
                mousePos.Y >= screenRect.y && mousePos.Y <= screenRect.yMax;

            _isPressed = _isHovered && ImGui.IsMouseDown(ImGuiMouseButton.Left);

            if (_isHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                try { onClick?.Invoke(); }
                catch (Exception ex) { Debug.LogError($"[UIButton] onClick error: {ex.Message}"); }
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
