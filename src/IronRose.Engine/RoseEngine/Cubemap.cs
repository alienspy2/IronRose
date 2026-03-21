// ------------------------------------------------------------
// @file    Cubemap.cs
// @brief   6면 큐브맵 텍스처. Equirectangular HDR/LDR 이미지에서 큐브맵을 생성하고 GPU 업로드.
//          스카이박스, IBL 등에 사용.
// @deps    RoseEngine/Texture2D, RoseEngine/EditorDebug, RoseEngine/Color
// @exports
//   class Cubemap : IDisposable
//     faceSize: int                                                   — 큐브맵 면 크기
//     isHDR: bool                                                     — HDR 큐브맵 여부
//     static CreateFromEquirectangular(Texture2D, int): Cubemap       — Equirect에서 HDR 큐브맵 생성
//     static CreateWhiteCubemap(): Cubemap                            — 1x1 흰색 큐브맵
//     UploadToGPU(GraphicsDevice, bool): void                         — GPU 업로드
//     GetAverageColor(): Color                                        — 전체 면 평균 색상
// @note    Veldrid 큐브맵은 Texture2D + 6 array layers로 구현.
//          HDR 업로드 시 Texture2D.ConvertFloatToHalfBytes 사용.
// ------------------------------------------------------------
using System;
using Veldrid;

namespace RoseEngine
{
    public class Cubemap : IDisposable
    {
        public int faceSize { get; }
        internal TextureView? TextureView { get; private set; }

        private readonly byte[][] _faceData; // 6 faces, RGBA (LDR)
        private readonly float[][]? _hdrFaceData; // 6 faces, RGBA float32 (HDR)
        public bool isHDR => _hdrFaceData != null;
        private Veldrid.Texture? _veldridTexture;

        private Cubemap(int faceSize, byte[][] faceData)
        {
            this.faceSize = faceSize;
            _faceData = faceData;
        }

        private Cubemap(int faceSize, float[][] hdrFaceData)
        {
            this.faceSize = faceSize;
            _faceData = Array.Empty<byte[]>();
            _hdrFaceData = hdrFaceData;
        }

        // OpenGL cubemap face order: +X, -X, +Y, -Y, +Z, -Z
        private enum Face { PosX = 0, NegX, PosY, NegY, PosZ, NegZ }

        /// <summary>
        /// Creates an HDR cubemap from an equirectangular texture.
        /// Always produces HDR output — LDR sources are converted to float.
        /// </summary>
        public static Cubemap CreateFromEquirectangular(Texture2D equirect, int faceSize = 512)
        {
            // Always produce HDR cubemap for skybox/IBL quality
            float[] src;
            int srcW = equirect.width;
            int srcH = equirect.height;

            if (equirect._hdrPixelData != null)
            {
                src = equirect._hdrPixelData;
            }
            else if (equirect._pixelData != null)
            {
                // LDR → float conversion
                src = new float[equirect._pixelData.Length];
                for (int i = 0; i < equirect._pixelData.Length; i++)
                    src[i] = equirect._pixelData[i] / 255f;
            }
            else
            {
                throw new InvalidOperationException("Texture2D has no pixel data");
            }

            var faceData = new float[6][];

            for (int face = 0; face < 6; face++)
            {
                faceData[face] = new float[faceSize * faceSize * 4];

                for (int y = 0; y < faceSize; y++)
                {
                    for (int x = 0; x < faceSize; x++)
                    {
                        // Map pixel (x,y) on face to a 3D direction
                        float u = ((x + 0.5f) / faceSize) * 2f - 1f;
                        float v = ((y + 0.5f) / faceSize) * 2f - 1f;

                        float dx, dy, dz;
                        switch ((Face)face)
                        {
                            case Face.PosX: dx = 1f;  dy = -v; dz = -u; break;
                            case Face.NegX: dx = -1f; dy = -v; dz = u;  break;
                            case Face.PosY: dx = u;   dy = 1f; dz = v;  break;
                            case Face.NegY: dx = u;   dy = -1f; dz = -v; break;
                            case Face.PosZ: dx = u;   dy = -v; dz = 1f; break;
                            default:        dx = -u;  dy = -v; dz = -1f; break; // NegZ
                        }

                        // Normalize to unit vector
                        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        dx /= len; dy /= len; dz /= len;

                        // Direction → equirectangular UV
                        float eqU = MathF.Atan2(dz, dx) / (2f * MathF.PI) + 0.5f;
                        float eqV = MathF.Asin(Math.Clamp(dy, -1f, 1f)) / MathF.PI + 0.5f;
                        eqV = 1f - eqV; // flip V so top = zenith

                        // Bilinear sample from equirectangular source
                        SampleBilinearHDR(src, srcW, srcH, eqU, eqV,
                            out float r, out float g, out float b, out float a);

                        int dstOffset = (y * faceSize + x) * 4;
                        faceData[face][dstOffset + 0] = r;
                        faceData[face][dstOffset + 1] = g;
                        faceData[face][dstOffset + 2] = b;
                        faceData[face][dstOffset + 3] = a;
                    }
                }
            }

            string srcType = equirect.isHDR ? "HDR" : "LDR→HDR";
            EditorDebug.Log($"[Cubemap] Created from equirectangular ({srcType} {equirect.width}x{equirect.height}) → {faceSize}x{faceSize} HDR cubemap");
            return new Cubemap(faceSize, faceData);
        }

        public static Cubemap CreateWhiteCubemap()
        {
            var faceData = new byte[6][];
            for (int face = 0; face < 6; face++)
            {
                faceData[face] = new byte[4]; // 1x1 white pixel
                faceData[face][0] = 255;
                faceData[face][1] = 255;
                faceData[face][2] = 255;
                faceData[face][3] = 255;
            }
            return new Cubemap(1, faceData);
        }

        public void UploadToGPU(GraphicsDevice device, bool generateMipmaps = false)
        {
            var factory = device.ResourceFactory;

            // Dispose old resources
            TextureView?.Dispose();
            _veldridTexture?.Dispose();

            uint size = (uint)faceSize;
            uint mipLevels = 1;
            var usage = TextureUsage.Sampled | TextureUsage.Cubemap;

            if (generateMipmaps)
            {
                mipLevels = (uint)MathF.Floor(MathF.Log2(faceSize)) + 1;
                usage |= TextureUsage.GenerateMipmaps;
            }

            PixelFormat format = isHDR
                ? PixelFormat.R16_G16_B16_A16_Float
                : PixelFormat.R8_G8_B8_A8_UNorm;

            // Veldrid cubemap: Texture2D with 6 array layers
            _veldridTexture = factory.CreateTexture(new TextureDescription(
                size, size, 1, mipLevels, 6,
                format,
                usage,
                TextureType.Texture2D));

            // Upload each face as an array layer
            for (uint face = 0; face < 6; face++)
            {
                if (isHDR)
                {
                    var halfData = Texture2D.ConvertFloatToHalfBytes(_hdrFaceData![face]);
                    device.UpdateTexture(_veldridTexture, halfData,
                        0, 0, 0,
                        size, size, 1,
                        0, face);
                }
                else
                {
                    device.UpdateTexture(_veldridTexture, _faceData[face],
                        0, 0, 0,
                        size, size, 1,
                        0, face);
                }
            }

            // Generate mipmaps
            if (generateMipmaps && mipLevels > 1)
            {
                using var cl = factory.CreateCommandList();
                cl.Begin();
                cl.GenerateMipmaps(_veldridTexture);
                cl.End();
                device.SubmitCommands(cl);
            }

            TextureView = factory.CreateTextureView(_veldridTexture);
        }

        public Color GetAverageColor()
        {
            double r = 0, g = 0, b = 0;
            int totalSamples = 0;

            if (isHDR)
            {
                for (int face = 0; face < 6; face++)
                {
                    int pixelCount = _hdrFaceData![face].Length / 4;
                    int step = Math.Max(1, pixelCount / 256);

                    for (int i = 0; i < pixelCount; i += step)
                    {
                        int offset = i * 4;
                        r += _hdrFaceData[face][offset];
                        g += _hdrFaceData[face][offset + 1];
                        b += _hdrFaceData[face][offset + 2];
                        totalSamples++;
                    }
                }
            }
            else
            {
                for (int face = 0; face < 6; face++)
                {
                    int pixelCount = _faceData[face].Length / 4;
                    int step = Math.Max(1, pixelCount / 256);

                    for (int i = 0; i < pixelCount; i += step)
                    {
                        int offset = i * 4;
                        r += _faceData[face][offset] / 255.0;
                        g += _faceData[face][offset + 1] / 255.0;
                        b += _faceData[face][offset + 2] / 255.0;
                        totalSamples++;
                    }
                }
            }

            if (totalSamples == 0)
                return Color.gray;

            return new Color((float)(r / totalSamples), (float)(g / totalSamples), (float)(b / totalSamples), 1f);
        }

        public void Dispose()
        {
            TextureView?.Dispose();
            _veldridTexture?.Dispose();
            TextureView = null;
            _veldridTexture = null;
        }

        private static void SampleBilinearHDR(float[] src, int srcW, int srcH, float u, float v,
            out float outR, out float outG, out float outB, out float outA)
        {
            // Wrap u, keep v clamped
            float fx = u * srcW - 0.5f;
            float fy = v * srcH - 0.5f;

            int x0 = (int)MathF.Floor(fx);
            int y0 = (int)MathF.Floor(fy);
            float fracX = fx - x0;
            float fracY = fy - y0;

            // Wrap x, clamp y
            int x1 = x0 + 1;
            x0 = ((x0 % srcW) + srcW) % srcW;
            x1 = ((x1 % srcW) + srcW) % srcW;
            y0 = Math.Clamp(y0, 0, srcH - 1);
            int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);

            int i00 = (y0 * srcW + x0) * 4;
            int i10 = (y0 * srcW + x1) * 4;
            int i01 = (y1 * srcW + x0) * 4;
            int i11 = (y1 * srcW + x1) * 4;

            float w00 = (1 - fracX) * (1 - fracY);
            float w10 = fracX * (1 - fracY);
            float w01 = (1 - fracX) * fracY;
            float w11 = fracX * fracY;

            outR = src[i00] * w00 + src[i10] * w10 + src[i01] * w01 + src[i11] * w11;
            outG = src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11;
            outB = src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11;
            outA = src[i00 + 3] * w00 + src[i10 + 3] * w10 + src[i01 + 3] * w01 + src[i11 + 3] * w11;
        }
    }
}
