using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public class UIPanel : Component, IUIRenderable
    {
        public Color color = new(0.1f, 0.1f, 0.1f, 0.8f);
        public Sprite? sprite;
        public ImageType imageType = ImageType.Simple;

        public int renderOrder => -1;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            uint col = ColorToU32(color);

            if (sprite?.texture != null)
            {
                var texId = CanvasRenderer.GetTextureId(sprite.texture);
                if (texId != IntPtr.Zero)
                {
                    // color * sprite 텍스처로 렌더링
                    switch (imageType)
                    {
                        case ImageType.Sliced:
                            RenderSliced(drawList, screenRect, texId, col);
                            break;
                        default:
                            RenderSimple(drawList, screenRect, texId, col);
                            break;
                    }
                    return;
                }
            }

            // sprite 없으면 단색 배경
            drawList.AddRectFilled(
                new SNVector2(screenRect.x, screenRect.y),
                new SNVector2(screenRect.xMax, screenRect.yMax),
                col);
        }

        private void RenderSimple(ImDrawListPtr dl, Rect r, IntPtr tex, uint col)
        {
            dl.AddImage(tex,
                new SNVector2(r.x, r.y),
                new SNVector2(r.xMax, r.yMax),
                new SNVector2(sprite!.uvMin.x, sprite.uvMin.y),
                new SNVector2(sprite.uvMax.x, sprite.uvMax.y),
                col);
        }

        private void RenderSliced(ImDrawListPtr dl, Rect r, IntPtr tex, uint col)
        {
            var border = sprite!.border;

            if (border.x == 0 && border.y == 0 && border.z == 0 && border.w == 0)
            {
                RenderSimple(dl, r, tex, col);
                return;
            }

            // border를 sprite pixels → screen pixels로 변환
            // Unity 공식: border * (referencePixelsPerUnit / spritePPU) * canvasScale
            const float REFERENCE_PPU = 100f;
            float ppu = sprite.pixelsPerUnit;
            float scale = CanvasRenderer.CurrentCanvasScale;
            float borderScale = ppu > 0 ? REFERENCE_PPU / ppu * scale : scale;

            float bL = border.x * borderScale, bB = border.y * borderScale;
            float bR = border.z * borderScale, bT = border.w * borderScale;

            float sL = MathF.Min(bL, r.width * 0.5f);
            float sR = MathF.Min(bR, r.width * 0.5f);
            float sT = MathF.Min(bT, r.height * 0.5f);
            float sB = MathF.Min(bB, r.height * 0.5f);

            float x0 = r.x, x1 = r.x + sL, x2 = r.xMax - sR, x3 = r.xMax;
            float y0 = r.y, y1 = r.y + sT, y2 = r.yMax - sB, y3 = r.yMax;

            var (innerMin, innerMax) = sprite.GetBorderUVs();
            float u0 = sprite.uvMin.x, u1 = innerMin.x, u2 = innerMax.x, u3 = sprite.uvMax.x;
            float v0 = sprite.uvMin.y, v1 = innerMin.y, v2 = innerMax.y, v3 = sprite.uvMax.y;

            AddImageQuad(dl, tex, x0, y0, x1, y1, u0, v0, u1, v1, col);
            AddImageQuad(dl, tex, x1, y0, x2, y1, u1, v0, u2, v1, col);
            AddImageQuad(dl, tex, x2, y0, x3, y1, u2, v0, u3, v1, col);

            AddImageQuad(dl, tex, x0, y1, x1, y2, u0, v1, u1, v2, col);
            AddImageQuad(dl, tex, x1, y1, x2, y2, u1, v1, u2, v2, col);
            AddImageQuad(dl, tex, x2, y1, x3, y2, u2, v1, u3, v2, col);

            AddImageQuad(dl, tex, x0, y2, x1, y3, u0, v2, u1, v3, col);
            AddImageQuad(dl, tex, x1, y2, x2, y3, u1, v2, u2, v3, col);
            AddImageQuad(dl, tex, x2, y2, x3, y3, u2, v2, u3, v3, col);
        }

        private static void AddImageQuad(ImDrawListPtr dl, IntPtr tex,
            float x0, float y0, float x1, float y1,
            float u0, float v0, float u1, float v1, uint col)
        {
            if (x1 <= x0 || y1 <= y0) return;
            dl.AddImage(tex,
                new SNVector2(x0, y0), new SNVector2(x1, y1),
                new SNVector2(u0, v0), new SNVector2(u1, v1),
                col);
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
