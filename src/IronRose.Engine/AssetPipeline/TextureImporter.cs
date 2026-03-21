using System;
using System.IO;
using RoseEngine;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace IronRose.AssetPipeline
{
    public class TextureImporter
    {
        public Texture2D Import(string texturePath)
        {
            return Import(texturePath, null);
        }

        public Texture2D Import(string texturePath, RoseMetadata? meta)
        {
            if (!File.Exists(texturePath))
            {
                EditorDebug.LogError($"Texture not found: {texturePath}");
                return null!;
            }

            string textureType = "Color";
            if (meta?.importer.TryGetValue("texture_type", out var ttVal) == true)
                textureType = ttVal?.ToString() ?? "Color";

            bool isPanoramic = textureType == "Panoramic";

            // HDR formats
            var ext = Path.GetExtension(texturePath).ToLowerInvariant();
            if (ext is ".hdr" or ".exr")
            {
                var hdrTex = Texture2D.LoadFromFile(texturePath);

                if (isPanoramic)
                    EnforceEquirectangularAspect(ref hdrTex);

                EditorDebug.Log($"[TextureImporter] Loaded HDR{(isPanoramic ? " (Panoramic)" : "")}: {texturePath} ({hdrTex.width}x{hdrTex.height})");
                return hdrTex;
            }

            // Panoramic LDR → HDR 변환 경로
            if (isPanoramic)
                return ImportPanoramicLdr(texturePath, meta);

            int maxSize = 2048;
            if (meta?.importer.TryGetValue("max_size", out var msVal) == true)
                maxSize = Convert.ToInt32(msVal);

            using var image = Image.Load<Rgba32>(texturePath);

            // max_size 적용: 큰 쪽 기준으로 비율 축소
            if (image.Width > maxSize || image.Height > maxSize)
            {
                float scale = (float)maxSize / Math.Max(image.Width, image.Height);
                int newW = Math.Max(1, (int)(image.Width * scale));
                int newH = Math.Max(1, (int)(image.Height * scale));
                EditorDebug.Log($"[TextureImporter] Resize {image.Width}x{image.Height} → {newW}x{newH} (max_size={maxSize})");
                image.Mutate(ctx => ctx.Resize(newW, newH));
            }

            int w = image.Width;
            int h = image.Height;
            var data = new byte[w * h * 4];

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        int offset = (y * w + x) * 4;
                        data[offset + 0] = row[x].R;
                        data[offset + 1] = row[x].G;
                        data[offset + 2] = row[x].B;
                        data[offset + 3] = row[x].A;
                    }
                }
            });

            EditorDebug.Log($"[TextureImporter] Loaded: {texturePath} ({w}x{h})");
            return new Texture2D(w, h) { _pixelData = data, name = Path.GetFileNameWithoutExtension(texturePath) };
        }

        /// <summary>
        /// LDR 파노라마 이미지를 HDR float 데이터로 변환하여 import.
        /// 2:1 비율 강제, sRGB → linear 변환.
        /// </summary>
        private Texture2D ImportPanoramicLdr(string texturePath, RoseMetadata? meta)
        {
            int maxSize = 4096;
            if (meta?.importer.TryGetValue("max_size", out var msVal) == true)
                maxSize = Convert.ToInt32(msVal);

            using var image = Image.Load<Rgba32>(texturePath);

            // max_size 적용
            if (image.Width > maxSize || image.Height > maxSize)
            {
                float scale = (float)maxSize / Math.Max(image.Width, image.Height);
                int newW = Math.Max(1, (int)(image.Width * scale));
                int newH = Math.Max(1, (int)(image.Height * scale));
                EditorDebug.Log($"[TextureImporter] Panoramic resize {image.Width}x{image.Height} → {newW}x{newH} (max_size={maxSize})");
                image.Mutate(ctx => ctx.Resize(newW, newH));
            }

            // 2:1 비율 강제 (equirectangular)
            int targetH = image.Width / 2;
            if (image.Height != targetH)
            {
                EditorDebug.Log($"[TextureImporter] Panoramic aspect fix {image.Width}x{image.Height} → {image.Width}x{targetH}");
                image.Mutate(ctx => ctx.Resize(image.Width, targetH));
            }

            int w = image.Width;
            int h = image.Height;

            // sRGB byte → linear float HDR 변환
            var hdrData = new float[w * h * 4];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        int offset = (y * w + x) * 4;
                        hdrData[offset + 0] = SrgbToLinear(row[x].R / 255f);
                        hdrData[offset + 1] = SrgbToLinear(row[x].G / 255f);
                        hdrData[offset + 2] = SrgbToLinear(row[x].B / 255f);
                        hdrData[offset + 3] = row[x].A / 255f;
                    }
                }
            });

            EditorDebug.Log($"[TextureImporter] Loaded Panoramic (LDR→HDR): {texturePath} ({w}x{h})");
            return new Texture2D(w, h, hdrData) { name = Path.GetFileNameWithoutExtension(texturePath) };
        }

        /// <summary>
        /// HDR 텍스쳐의 2:1 equirectangular 비율을 강제합니다.
        /// 비율이 맞지 않으면 높이를 width/2로 리사이즈.
        /// </summary>
        private static void EnforceEquirectangularAspect(ref Texture2D hdrTex)
        {
            int targetH = hdrTex.width / 2;
            if (hdrTex.height == targetH) return;

            EditorDebug.Log($"[TextureImporter] Panoramic HDR aspect fix {hdrTex.width}x{hdrTex.height} → {hdrTex.width}x{targetH}");

            var src = hdrTex._hdrPixelData;
            if (src == null) return;

            int srcW = hdrTex.width;
            int srcH = hdrTex.height;
            int dstW = srcW;
            int dstH = targetH;

            var dst = new float[dstW * dstH * 4];
            float yScale = (float)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                float srcY = y * yScale;
                int y0 = Math.Clamp((int)srcY, 0, srcH - 1);
                int y1 = Math.Clamp(y0 + 1, 0, srcH - 1);
                float fy = srcY - y0;

                for (int x = 0; x < dstW; x++)
                {
                    int si0 = (y0 * srcW + x) * 4;
                    int si1 = (y1 * srcW + x) * 4;
                    int di = (y * dstW + x) * 4;

                    for (int c = 0; c < 4; c++)
                        dst[di + c] = src[si0 + c] * (1f - fy) + src[si1 + c] * fy;
                }
            }

            hdrTex = new Texture2D(dstW, dstH, dst) { name = hdrTex.name };
        }

        /// <summary>
        /// Displacement(height) map을 normal map으로 변환하여 PNG로 저장합니다.
        /// Sobel 필터를 사용하여 gradient를 계산하고 tangent-space normal을 생성합니다.
        /// </summary>
        public static void ConvertHeightToNormalMap(string inputPath, string outputPath, float strength = 8.0f)
        {
            using var image = Image.Load<Rgba32>(inputPath);
            int w = image.Width;
            int h = image.Height;

            // grayscale height 값 추출
            var heights = new float[w * h];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        var p = row[x];
                        heights[y * w + x] = (p.R * 0.299f + p.G * 0.587f + p.B * 0.114f) / 255f;
                    }
                }
            });

            // 헬퍼: 경계 clamp 샘플링
            float H(int x, int y)
            {
                x = Math.Clamp(x, 0, w - 1);
                y = Math.Clamp(y, 0, h - 1);
                return heights[y * w + x];
            }

            // Sobel 필터로 normal map 생성
            using var result = new Image<Rgba32>(w, h);
            result.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        // Sobel X: 좌→우 gradient
                        float dX = -H(x - 1, y - 1) - 2f * H(x - 1, y) - H(x - 1, y + 1)
                                   + H(x + 1, y - 1) + 2f * H(x + 1, y) + H(x + 1, y + 1);

                        // Sobel Y: 상→하 gradient
                        float dY = -H(x - 1, y - 1) - 2f * H(x, y - 1) - H(x + 1, y - 1)
                                   + H(x - 1, y + 1) + 2f * H(x, y + 1) + H(x + 1, y + 1);

                        // tangent-space normal 구성
                        float nx = -dX * strength;
                        float ny = -dY * strength;
                        float nz = 1.0f;
                        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                        nx /= len;
                        ny /= len;
                        nz /= len;

                        // [−1,1] → [0,255]
                        row[x] = new Rgba32(
                            (byte)Math.Clamp((int)((nx * 0.5f + 0.5f) * 255f), 0, 255),
                            (byte)Math.Clamp((int)((ny * 0.5f + 0.5f) * 255f), 0, 255),
                            (byte)Math.Clamp((int)((nz * 0.5f + 0.5f) * 255f), 0, 255),
                            255);
                    }
                }
            });

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            result.SaveAsPng(outputPath);
            EditorDebug.Log($"[TextureImporter] Converted displacement → normal map: {outputPath} ({w}x{h})");
        }

        private static float SrgbToLinear(float srgb)
        {
            return srgb <= 0.04045f
                ? srgb / 12.92f
                : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
        }
    }
}
