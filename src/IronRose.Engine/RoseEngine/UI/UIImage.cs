using System;
using ImGuiNET;
using SNVector2 = System.Numerics.Vector2;

namespace RoseEngine
{
    public enum ImageType
    {
        Simple,
        Sliced,
        Tiled,
        Filled
    }

    public class UIImage : Component, IUIRenderable
    {
        public Sprite? sprite;
        public Color color = Color.white;
        public ImageType imageType = ImageType.Simple;
        public bool preserveAspect;

        public int renderOrder => 0;

        public void OnRenderUI(ImDrawListPtr drawList, Rect screenRect)
        {
            if (sprite?.texture == null) return;

            var texId = CanvasRenderer.GetTextureId(sprite.texture);
            if (texId == IntPtr.Zero) return;

            uint col = ColorToU32(color);

            switch (imageType)
            {
                case ImageType.Simple:
                    RenderSimple(drawList, screenRect, texId, col);
                    break;
                case ImageType.Sliced:
                    RenderSliced(drawList, screenRect, texId, col);
                    break;
                default:
                    RenderSimple(drawList, screenRect, texId, col);
                    break;
            }
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

            // border가 없으면 Simple 모드로 폴백
            if (border.x == 0 && border.y == 0 && border.z == 0 && border.w == 0)
            {
                RenderSimple(dl, r, tex, col);
                return;
            }

            // border = (left, bottom, right, top) in sprite pixels.
            // Unity 공식: border * (referencePixelsPerUnit / spritePPU) * canvasScale
            // referencePixelsPerUnit 기본값 = 100 (Unity 동일)
            // PPU=100이면 factor=1 (기존 동작), PPU=200이면 factor=0.5 (border 절반)
            const float REFERENCE_PPU = 100f;
            float ppu = sprite.pixelsPerUnit;
            float scale = CanvasRenderer.CurrentCanvasScale;
            float borderScale = ppu > 0 ? REFERENCE_PPU / ppu * scale : scale;

            float bL = border.x * borderScale;
            float bB = border.y * borderScale;
            float bR = border.z * borderScale;
            float bT = border.w * borderScale;

            // Screen-space border sizes (clamped to rect size)
            float sL = MathF.Min(bL, r.width * 0.5f);
            float sR = MathF.Min(bR, r.width * 0.5f);
            float sT = MathF.Min(bT, r.height * 0.5f);
            float sB = MathF.Min(bB, r.height * 0.5f);

            // Screen coordinates: 4 x-positions, 4 y-positions
            float x0 = r.x;
            float x1 = r.x + sL;
            float x2 = r.xMax - sR;
            float x3 = r.xMax;

            float y0 = r.y;
            float y1 = r.y + sT;
            float y2 = r.yMax - sB;
            float y3 = r.yMax;

            // UV coordinates: 4 u-positions, 4 v-positions
            var (innerMin, innerMax) = sprite.GetBorderUVs();
            float u0 = sprite.uvMin.x;
            float u1 = innerMin.x;
            float u2 = innerMax.x;
            float u3 = sprite.uvMax.x;

            float v0 = sprite.uvMin.y;
            float v1 = innerMin.y;
            float v2 = innerMax.y;
            float v3 = sprite.uvMax.y;

            // 9-slice: 3x3 grid of AddImage calls
            // Row 0 (top): TL, Top, TR
            AddImageQuad(dl, tex, x0, y0, x1, y1, u0, v0, u1, v1, col);
            AddImageQuad(dl, tex, x1, y0, x2, y1, u1, v0, u2, v1, col);
            AddImageQuad(dl, tex, x2, y0, x3, y1, u2, v0, u3, v1, col);

            // Row 1 (middle): Left, Center, Right
            AddImageQuad(dl, tex, x0, y1, x1, y2, u0, v1, u1, v2, col);
            AddImageQuad(dl, tex, x1, y1, x2, y2, u1, v1, u2, v2, col);
            AddImageQuad(dl, tex, x2, y1, x3, y2, u2, v1, u3, v2, col);

            // Row 2 (bottom): BL, Bottom, BR
            AddImageQuad(dl, tex, x0, y2, x1, y3, u0, v2, u1, v3, col);
            AddImageQuad(dl, tex, x1, y2, x2, y3, u1, v2, u2, v3, col);
            AddImageQuad(dl, tex, x2, y2, x3, y3, u2, v2, u3, v3, col);
        }

        private static void AddImageQuad(ImDrawListPtr dl, IntPtr tex,
            float x0, float y0, float x1, float y1,
            float u0, float v0, float u1, float v1, uint col)
        {
            if (x1 <= x0 || y1 <= y0) return; // degenerate quad
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
