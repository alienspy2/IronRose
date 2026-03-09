using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public enum TextAnchor
    {
        UpperLeft,
        UpperCenter,
        UpperRight,
        MiddleLeft,
        MiddleCenter,
        MiddleRight,
        LowerLeft,
        LowerCenter,
        LowerRight
    }

    public enum TextOverflow
    {
        Wrap,
        Overflow,
        Ellipsis
    }

    public class UIText : Component, IUIRenderable
    {
        public Font? font;
        public string text = "";
        public float fontSize = 16f;
        public Color color = Color.white;
        public TextAnchor alignment = TextAnchor.UpperLeft;
        public TextOverflow overflow = TextOverflow.Overflow;

        internal static readonly List<UIText> _allUITexts = new();

        internal override void OnAddedToGameObject() => _allUITexts.Add(this);
        internal override void OnComponentDestroy() => _allUITexts.Remove(this);
        internal static void ClearAll() => _allUITexts.Clear();

        public int renderOrder => 1;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            if (font == null || font.atlasTexture == null || string.IsNullOrEmpty(text)) return;

            var texId = CanvasRenderer.GetTextureId(font.atlasTexture);
            if (texId == IntPtr.Zero) return;

            uint col = ColorToU32(color);
            float scale = fontSize / font.fontSize;

            // Measure text size for alignment
            float textW, textH;
            if (overflow == TextOverflow.Wrap)
                MeasureTextWrapped(font, text, scale, screenRect.width, out textW, out textH);
            else
                MeasureTextSingleLine(font, text, scale, out textW, out textH);

            // Alignment offset
            float ox = 0f, oy = 0f;

            switch (alignment)
            {
                case TextAnchor.UpperLeft:
                    break;
                case TextAnchor.UpperCenter:
                    ox = (screenRect.width - textW) * 0.5f;
                    break;
                case TextAnchor.UpperRight:
                    ox = screenRect.width - textW;
                    break;
                case TextAnchor.MiddleLeft:
                    oy = (screenRect.height - textH) * 0.5f;
                    break;
                case TextAnchor.MiddleCenter:
                    ox = (screenRect.width - textW) * 0.5f;
                    oy = (screenRect.height - textH) * 0.5f;
                    break;
                case TextAnchor.MiddleRight:
                    ox = screenRect.width - textW;
                    oy = (screenRect.height - textH) * 0.5f;
                    break;
                case TextAnchor.LowerLeft:
                    oy = screenRect.height - textH;
                    break;
                case TextAnchor.LowerCenter:
                    ox = (screenRect.width - textW) * 0.5f;
                    oy = screenRect.height - textH;
                    break;
                case TextAnchor.LowerRight:
                    ox = screenRect.width - textW;
                    oy = screenRect.height - textH;
                    break;
            }

            float startX = screenRect.x + ox;
            float startY = screenRect.y + oy;
            float lineH = font.lineHeight * scale;

            if (overflow == TextOverflow.Wrap)
                DrawTextWrapped(drawList, texId, font, text, scale, col, startX, startY, lineH, screenRect.width);
            else
                DrawTextLine(drawList, texId, font, text, scale, col, startX, startY, lineH);
        }

        private static void DrawTextLine(ImDrawListPtr drawList, IntPtr texId, Font font,
            string text, float scale, uint col, float x, float y, float lineH)
        {
            float cursorX = x;
            float cursorY = y;

            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    cursorX = x;
                    cursorY += lineH;
                    continue;
                }

                if (!font.glyphs.TryGetValue(ch, out var g))
                {
                    cursorX += font.fontSize * 0.5f * scale;
                    continue;
                }

                float w = g.width * scale;
                float h = g.height * scale;

                drawList.AddImage(texId,
                    new SNVector2(cursorX, cursorY),
                    new SNVector2(cursorX + w, cursorY + h),
                    new SNVector2(g.uvMin.x, g.uvMin.y),
                    new SNVector2(g.uvMax.x, g.uvMax.y),
                    col);

                cursorX += g.advance * scale;
            }
        }

        private static void DrawTextWrapped(ImDrawListPtr drawList, IntPtr texId, Font font,
            string text, float scale, uint col, float x, float y, float lineH, float maxWidth)
        {
            float cursorX = x;
            float cursorY = y;

            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    cursorX = x;
                    cursorY += lineH;
                    continue;
                }

                if (!font.glyphs.TryGetValue(ch, out var g))
                {
                    float fallbackW = font.fontSize * 0.5f * scale;
                    if (cursorX - x + fallbackW > maxWidth && cursorX > x)
                    {
                        cursorX = x;
                        cursorY += lineH;
                    }
                    cursorX += fallbackW;
                    continue;
                }

                float advance = g.advance * scale;
                if (cursorX - x + advance > maxWidth && cursorX > x)
                {
                    cursorX = x;
                    cursorY += lineH;
                }

                float w = g.width * scale;
                float h = g.height * scale;

                drawList.AddImage(texId,
                    new SNVector2(cursorX, cursorY),
                    new SNVector2(cursorX + w, cursorY + h),
                    new SNVector2(g.uvMin.x, g.uvMin.y),
                    new SNVector2(g.uvMax.x, g.uvMax.y),
                    col);

                cursorX += advance;
            }
        }

        private static void MeasureTextSingleLine(Font font, string text, float scale,
            out float width, out float height)
        {
            float maxLineW = 0f;
            float lineW = 0f;
            int lineCount = 1;

            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    if (lineW > maxLineW) maxLineW = lineW;
                    lineW = 0f;
                    lineCount++;
                    continue;
                }

                if (font.glyphs.TryGetValue(ch, out var g))
                    lineW += g.advance * scale;
                else
                    lineW += font.fontSize * 0.5f * scale;
            }
            if (lineW > maxLineW) maxLineW = lineW;

            width = maxLineW;
            height = font.lineHeight * scale * lineCount;
        }

        private static void MeasureTextWrapped(Font font, string text, float scale,
            float maxWidth, out float width, out float height)
        {
            float maxLineW = 0f;
            float lineW = 0f;
            int lineCount = 1;

            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    if (lineW > maxLineW) maxLineW = lineW;
                    lineW = 0f;
                    lineCount++;
                    continue;
                }

                float advance;
                if (font.glyphs.TryGetValue(ch, out var g))
                    advance = g.advance * scale;
                else
                    advance = font.fontSize * 0.5f * scale;

                if (lineW + advance > maxWidth && lineW > 0)
                {
                    if (lineW > maxLineW) maxLineW = lineW;
                    lineW = 0f;
                    lineCount++;
                }
                lineW += advance;
            }
            if (lineW > maxLineW) maxLineW = lineW;

            width = maxLineW;
            height = font.lineHeight * scale * lineCount;
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
