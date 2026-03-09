using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// GLB 파일에서 임베디드 텍스처를 직접 추출합니다.
    /// AssimpNet 4.1.0이 GLB 임베디드 텍스처를 지원하지 않아 필요한 폴백입니다.
    /// </summary>
    public static class GlbTextureExtractor
    {
        private const uint GlbMagic = 0x46546C67; // "glTF"
        private const uint ChunkJson = 0x4E4F534A; // "JSON"
        private const uint ChunkBin = 0x004E4942;  // "BIN\0"

        public class GlbTextures
        {
            public List<byte[]> Images { get; } = new();
            public List<int> MaterialBaseColorImageIndex { get; } = new();
        }

        public static GlbTextures? Extract(string glbPath)
        {
            if (!File.Exists(glbPath) ||
                !glbPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                using var fs = File.OpenRead(glbPath);
                using var reader = new BinaryReader(fs);

                // GLB Header: magic(4) + version(4) + length(4)
                if (fs.Length < 12) return null;
                uint magic = reader.ReadUInt32();
                if (magic != GlbMagic) return null;

                uint version = reader.ReadUInt32();
                uint totalLength = reader.ReadUInt32();

                // Read chunks
                byte[]? jsonBytes = null;
                byte[]? binBytes = null;

                while (fs.Position < totalLength && fs.Position + 8 <= fs.Length)
                {
                    uint chunkLength = reader.ReadUInt32();
                    uint chunkType = reader.ReadUInt32();

                    if (chunkType == ChunkJson)
                        jsonBytes = reader.ReadBytes((int)chunkLength);
                    else if (chunkType == ChunkBin)
                        binBytes = reader.ReadBytes((int)chunkLength);
                    else
                        fs.Seek(chunkLength, SeekOrigin.Current);
                }

                if (jsonBytes == null) return null;

                return ParseGltfJson(jsonBytes, binBytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GlbTextureExtractor] Failed to parse '{glbPath}': {ex.Message}");
                return null;
            }
        }

        private static GlbTextures? ParseGltfJson(byte[] jsonBytes, byte[]? binBytes)
        {
            var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            // images 배열에서 bufferView 기반 이미지 데이터 추출
            var result = new GlbTextures();

            if (!root.TryGetProperty("images", out var imagesEl))
                return null;

            // bufferViews 파싱
            JsonElement bufferViewsEl = default;
            bool hasBufferViews = root.TryGetProperty("bufferViews", out bufferViewsEl);

            foreach (var image in imagesEl.EnumerateArray())
            {
                byte[]? imageData = null;

                if (image.TryGetProperty("bufferView", out var bvIndexEl) && hasBufferViews && binBytes != null)
                {
                    int bvIndex = bvIndexEl.GetInt32();
                    var bufferViews = bufferViewsEl;
                    int idx = 0;
                    foreach (var bv in bufferViews.EnumerateArray())
                    {
                        if (idx == bvIndex)
                        {
                            int byteOffset = bv.TryGetProperty("byteOffset", out var boEl) ? boEl.GetInt32() : 0;
                            int byteLength = bv.GetProperty("byteLength").GetInt32();

                            if (byteOffset + byteLength <= binBytes.Length)
                            {
                                imageData = new byte[byteLength];
                                Array.Copy(binBytes, byteOffset, imageData, 0, byteLength);
                            }
                            break;
                        }
                        idx++;
                    }
                }

                if (imageData != null)
                    result.Images.Add(imageData);
                else
                    result.Images.Add(Array.Empty<byte>());
            }

            // materials → pbrMetallicRoughness.baseColorTexture.index → textures[index].source → images[source]
            if (root.TryGetProperty("materials", out var materialsEl))
            {
                JsonElement texturesEl = default;
                bool hasTextures = root.TryGetProperty("textures", out texturesEl);

                foreach (var mat in materialsEl.EnumerateArray())
                {
                    int imageIndex = -1;

                    if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr)
                        && pbr.TryGetProperty("baseColorTexture", out var bct)
                        && bct.TryGetProperty("index", out var texIdxEl)
                        && hasTextures)
                    {
                        int texIndex = texIdxEl.GetInt32();
                        int ti = 0;
                        foreach (var tex in texturesEl.EnumerateArray())
                        {
                            if (ti == texIndex)
                            {
                                if (tex.TryGetProperty("source", out var srcEl))
                                    imageIndex = srcEl.GetInt32();
                                break;
                            }
                            ti++;
                        }
                    }

                    result.MaterialBaseColorImageIndex.Add(imageIndex);
                }
            }

            return result.Images.Count > 0 ? result : null;
        }
    }
}
