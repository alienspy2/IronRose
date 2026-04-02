// ------------------------------------------------------------
// @file    RoseCache.cs
// @brief   에셋(텍스처, 메시)을 바이너리 캐시 파일로 저장/로드하여 임포트 속도를 높인다.
//          FormatVersion 9: Material.blendMode 직렬화 추가.
// @deps    IronRose.Engine (GpuTextureCompressor, MeshImportResult, RoseMetadata),
//          RoseEngine (Material, Texture2D, Color, BlendMode, Mesh, Vector2/3/4, BoneWeight)
// @exports
//   class RoseCache
//     SetGpuCompressor(GpuTextureCompressor?): void         — GPU 텍스처 압축기 설정
//     TryLoadTexture(string, RoseMetadata): Texture2D?      — 캐시에서 텍스처 로드
//     StoreTexture(string, Texture2D, RoseMetadata): void   — 텍스처를 캐시에 저장
//     TryLoadMesh(string, RoseMetadata): MeshImportResult?  — 캐시에서 메시 로드
//     StoreMesh(string, MeshImportResult, RoseMetadata): void — 메시를 캐시에 저장
//     HasValidCache(string): bool                           — 유효한 캐시 존재 여부
//     InvalidateCache(string): void                         — 특정 에셋 캐시 무효화
//     ClearAll(): void                                      — 전체 캐시 삭제
// @note    FormatVersion 변경 시 기존 캐시는 자동 무효화됨.
//          Material 직렬화 순서: blendMode(byte) -> color -> emission -> PBR floats -> textures
//          Store 계열 메서드는 temp file + atomic rename 패턴으로 동시 접근 안전성을 보장한다.
//          Load/HasValidCache는 FileShare.ReadWrite|Delete로 열어 쓰기 중 읽기를 허용한다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using IronRose.Engine;
using RoseEngine;
using Veldrid;
using BcPixelFormat = BCnEncoder.Encoder.PixelFormat;
using Debug = RoseEngine.EditorDebug;

namespace IronRose.AssetPipeline
{
    public class RoseCache
    {
        private const uint Magic = 0x45534F52; // "ROSE"
        private const int FormatVersion = 9; // v9: Material.blendMode

        // Custom format ID for BC6H (not in Veldrid 4.9 PixelFormat enum)
        private const int FormatBC6H_UFloat = 1000;

        private readonly string _cacheRoot;

        // GPU texture compressor (set from EngineCore)
        private static GpuTextureCompressor? _gpuCompressor;
        public static void SetGpuCompressor(GpuTextureCompressor? compressor) => _gpuCompressor = compressor;

        public RoseCache(string cacheRoot)
        {
            _cacheRoot = cacheRoot;
            Directory.CreateDirectory(_cacheRoot);
        }

        // ─── Public API ───────────────────────────────────────

        public Texture2D? TryLoadTexture(string assetPath, RoseMetadata meta)
        {
            try
            {
                var cachePath = GetCachePath(assetPath);
                if (!File.Exists(cachePath)) return null;

                using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(fs);

                if (!ValidateHeader(reader, assetPath)) return null;

                byte assetType = reader.ReadByte();
                if (assetType != 2) return null;

                var tex = ReadTexture(reader);
                if (tex != null)
                {
                    tex.name = Path.GetFileNameWithoutExtension(assetPath);
                    Debug.Log($"[RoseCache] Cache hit (texture): {assetPath}");
                }
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] Failed to load cache for '{assetPath}': {ex.Message}");
                TryDeleteCache(assetPath);
                return null;
            }
        }

        public void StoreTexture(string assetPath, Texture2D texture, RoseMetadata meta)
        {
            var cachePath = GetCachePath(assetPath);
            var tempPath = cachePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var sw = Stopwatch.StartNew();
                var compression = GetMetaString(meta, "compression", "BC7");
                var textureType = GetMetaString(meta, "texture_type", "Color");
                var genMips = GetMetaBool(meta, "generate_mipmaps", false);

                Debug.Log($"[RoseCache] Caching texture: {assetPath} ({texture.width}x{texture.height}, {compression}/{textureType}, mips={genMips})");

                using (var fs = File.Create(tempPath))
                using (var writer = new BinaryWriter(fs))
                {
                    WriteValidationHeader(writer, assetPath);
                    writer.Write((byte)2); // Texture
                    WriteTexture(writer, texture, compression, textureType, genMips);
                }

                File.Move(tempPath, cachePath, overwrite: true);

                sw.Stop();
                Debug.Log($"[RoseCache] Cached (texture): {assetPath} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] Failed to cache '{assetPath}': {ex.Message}");
                try { File.Delete(tempPath); } catch { }
            }
        }

        public MeshImportResult? TryLoadMesh(string assetPath, RoseMetadata meta)
        {
            try
            {
                var cachePath = GetCachePath(assetPath);
                if (!File.Exists(cachePath)) return null;

                using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(fs);

                if (!ValidateHeader(reader, assetPath)) return null;

                byte assetType = reader.ReadByte();
                if (assetType != 1) return null;

                // v6: 다중 메시 포맷
                int meshCount = reader.ReadInt32();
                var namedMeshes = new NamedMesh[meshCount];
                var mipMeshes = new MipMesh?[meshCount];

                for (int m = 0; m < meshCount; m++)
                {
                    string meshName = reader.ReadString();
                    int materialIndex = reader.ReadInt32();

                    int vCount = reader.ReadInt32();
                    var verts = new Vertex[vCount];
                    for (int i = 0; i < vCount; i++)
                    {
                        verts[i] = new Vertex(
                            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                            new Vector2(reader.ReadSingle(), reader.ReadSingle())
                        );
                    }

                    int iCount = reader.ReadInt32();
                    var inds = new uint[iCount];
                    for (int i = 0; i < iCount; i++)
                        inds[i] = reader.ReadUInt32();

                    var mesh = new Mesh { name = meshName, vertices = verts, indices = inds };

                    // MipMesh LOD 체인
                    bool hasMipMesh = reader.ReadBoolean();
                    if (hasMipMesh)
                    {
                        int lodCount = reader.ReadInt32();
                        var lodMeshes = new Mesh[lodCount];
                        lodMeshes[0] = mesh;

                        for (int i = 1; i < lodCount; i++)
                        {
                            int lodIndexCount = reader.ReadInt32();
                            var lodIndices = new uint[lodIndexCount];
                            for (int j = 0; j < lodIndexCount; j++)
                                lodIndices[j] = reader.ReadUInt32();

                            lodMeshes[i] = new Mesh
                            {
                                name = $"{meshName}_LOD{i}",
                                vertices = verts,
                                indices = lodIndices,
                            };
                        }

                        mipMeshes[m] = new MipMesh { lodMeshes = lodMeshes };
                    }

                    namedMeshes[m] = new NamedMesh { Name = meshName, Mesh = mesh, MaterialIndex = materialIndex };
                }

                // Materials
                int materialCount = reader.ReadInt32();
                var materials = new Material[materialCount];
                for (int i = 0; i < materialCount; i++)
                {
                    string matName = reader.ReadString();
                    materials[i] = ReadMaterial(reader);
                    materials[i].name = matName;
                }

                Debug.Log($"[RoseCache] Cache hit (mesh): {assetPath} ({meshCount} meshes)");
                return new MeshImportResult { Meshes = namedMeshes, Materials = materials, MipMeshes = mipMeshes };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] Failed to load mesh cache for '{assetPath}': {ex.Message}");
                TryDeleteCache(assetPath);
                return null;
            }
        }

        public void StoreMesh(string assetPath, MeshImportResult result, RoseMetadata meta)
        {
            var cachePath = GetCachePath(assetPath);
            var tempPath = cachePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var sw = Stopwatch.StartNew();
                Debug.Log($"[RoseCache] Caching mesh: {assetPath} ({result.Meshes.Length} meshes, {result.Materials.Length} materials)");

                using (var fs = File.Create(tempPath))
                using (var writer = new BinaryWriter(fs))
                {
                    WriteValidationHeader(writer, assetPath);
                    writer.Write((byte)1); // Mesh

                    // v6: 다중 메시 포맷
                    writer.Write(result.Meshes.Length);
                    for (int m = 0; m < result.Meshes.Length; m++)
                    {
                        var nm = result.Meshes[m];
                        writer.Write(nm.Name ?? $"Mesh_{m}");
                        writer.Write(nm.MaterialIndex);

                        // Vertices
                        writer.Write(nm.Mesh.vertices.Length);
                        foreach (var v in nm.Mesh.vertices)
                        {
                            writer.Write(v.Position.x); writer.Write(v.Position.y); writer.Write(v.Position.z);
                            writer.Write(v.Normal.x); writer.Write(v.Normal.y); writer.Write(v.Normal.z);
                            writer.Write(v.UV.x); writer.Write(v.UV.y);
                        }

                        // Indices
                        writer.Write(nm.Mesh.indices.Length);
                        foreach (var idx in nm.Mesh.indices)
                            writer.Write(idx);

                        // MipMesh LOD 체인
                        var mip = m < result.MipMeshes.Length ? result.MipMeshes[m] : null;
                        bool hasMipMesh = mip != null && mip.LodCount >= 1;
                        writer.Write(hasMipMesh);
                        if (hasMipMesh)
                        {
                            writer.Write(mip!.LodCount);
                            for (int lod = 1; lod < mip.LodCount; lod++)
                            {
                                var lodIndices = mip.lodMeshes[lod].indices;
                                writer.Write(lodIndices.Length);
                                foreach (var idx in lodIndices)
                                    writer.Write(idx);
                            }
                        }
                    }

                    // Materials (이름 포함)
                    writer.Write(result.Materials.Length);
                    for (int i = 0; i < result.Materials.Length; i++)
                    {
                        writer.Write(result.Materials[i].name ?? $"Material_{i}");
                        Debug.Log($"[RoseCache]   Material[{i}] compressing...");
                        var matSw = Stopwatch.StartNew();
                        WriteMaterial(writer, result.Materials[i]);
                        matSw.Stop();
                        Debug.Log($"[RoseCache]   Material[{i}] done ({matSw.ElapsedMilliseconds}ms)");
                    }
                }

                File.Move(tempPath, cachePath, overwrite: true);

                sw.Stop();
                Debug.Log($"[RoseCache] Cached (mesh): {assetPath} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] Failed to cache mesh '{assetPath}': {ex.Message}");
                try { File.Delete(tempPath); } catch { }
            }
        }

        public bool HasValidCache(string assetPath)
        {
            try
            {
                var cachePath = GetCachePath(assetPath);
                if (!File.Exists(cachePath)) return false;

                using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new BinaryReader(fs);
                return ValidateHeader(reader, assetPath);
            }
            catch { return false; }
        }

        public void InvalidateCache(string assetPath)
        {
            TryDeleteCache(assetPath);
        }

        public void ClearAll()
        {
            try
            {
                if (Directory.Exists(_cacheRoot))
                {
                    Directory.Delete(_cacheRoot, recursive: true);
                    Directory.CreateDirectory(_cacheRoot);
                    Debug.Log("[RoseCache] Cache cleared");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] Failed to clear cache: {ex.Message}");
            }
        }

        // ─── Cache Path ──────────────────────────────────────

        private string GetCachePath(string assetPath)
        {
            var fullPath = Path.GetFullPath(assetPath);
            var safeName = fullPath.Replace(Path.DirectorySeparatorChar, '_')
                                   .Replace(Path.AltDirectorySeparatorChar, '_')
                                   .Replace(':', '_')
                                   .Replace(' ', '_');
            return Path.Combine(_cacheRoot, safeName + ".rosecache");
        }

        private void TryDeleteCache(string assetPath)
        {
            try
            {
                var cachePath = GetCachePath(assetPath);
                if (File.Exists(cachePath))
                    File.Delete(cachePath);
            }
            catch { }
        }

        // ─── Validation Header ───────────────────────────────

        private static void WriteValidationHeader(BinaryWriter writer, string assetPath)
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);

            var fileInfo = new FileInfo(assetPath);
            writer.Write(fileInfo.LastWriteTimeUtc.Ticks);
            writer.Write(fileInfo.Length);

            var rosePath = assetPath + ".rose";
            bool roseExists = File.Exists(rosePath);
            writer.Write(roseExists);
            if (roseExists)
                writer.Write(new FileInfo(rosePath).LastWriteTimeUtc.Ticks);
        }

        private static bool ValidateHeader(BinaryReader reader, string assetPath)
        {
            uint magic = reader.ReadUInt32();
            if (magic != Magic) return false;

            int version = reader.ReadInt32();
            if (version != FormatVersion) return false;

            long mtime = reader.ReadInt64();
            long size = reader.ReadInt64();

            var fileInfo = new FileInfo(assetPath);
            if (!fileInfo.Exists) return false;
            if (fileInfo.LastWriteTimeUtc.Ticks != mtime || fileInfo.Length != size)
            {
                Debug.Log($"[RoseCache] Cache stale (asset changed): {assetPath}");
                return false;
            }

            bool roseExists = reader.ReadBoolean();
            if (roseExists)
            {
                long roseMtime = reader.ReadInt64();
                var rosePath = assetPath + ".rose";
                if (!File.Exists(rosePath) || new FileInfo(rosePath).LastWriteTimeUtc.Ticks != roseMtime)
                {
                    Debug.Log($"[RoseCache] Cache stale (.rose changed): {assetPath}");
                    return false;
                }
            }

            return true;
        }

        // ─── BC Compression ──────────────────────────────────

        private static (byte[][] mipData, Veldrid.PixelFormat format) CompressTexture(
            byte[] rgbaData, int width, int height, string compression, string textureType,
            bool generateMipmaps = true)
        {
            if (compression == "none" || string.IsNullOrEmpty(compression) || RoseConfig.DontUseCompressTexture)
                return (new[] { rgbaData }, Veldrid.PixelFormat.R8_G8_B8_A8_UNorm);

            try
            {
                bool isNormalMap = textureType == "NormalMap";
                Veldrid.PixelFormat veldridFormat = isNormalMap
                    ? Veldrid.PixelFormat.BC5_UNorm
                    : Veldrid.PixelFormat.BC7_UNorm;

                Debug.Log($"[RoseCache]     BC compress {textureType} {width}x{height} → {(isNormalMap ? "BC5" : "BC7")}...");
                var sw = Stopwatch.StartNew();

                byte[][] mipData;

                if (_gpuCompressor != null)
                {
                    if (generateMipmaps)
                    {
                        // GPU path: hardware mipmap generation → BC compress each mip
                        var mipChain = _gpuCompressor.GenerateMipmapsGPU(rgbaData, width, height);

                        mipData = new byte[mipChain.Length][];
                        int mw = width, mh = height;
                        for (int i = 0; i < mipChain.Length; i++)
                        {
                            mipData[i] = isNormalMap
                                ? _gpuCompressor.CompressBC5(mipChain[i], mw, mh)
                                : _gpuCompressor.CompressBC7(mipChain[i], mw, mh);
                            mw = Math.Max(1, mw / 2);
                            mh = Math.Max(1, mh / 2);
                        }
                    }
                    else
                    {
                        // GPU path: mip0 only
                        mipData = new byte[1][];
                        mipData[0] = isNormalMap
                            ? _gpuCompressor.CompressBC5(rgbaData, width, height)
                            : _gpuCompressor.CompressBC7(rgbaData, width, height);
                    }
                }
                else
                {
                    // CPU fallback (BCnEncoder.NET) — mip0 only
                    mipData = CompressWithCpuFallback(rgbaData, width, height, isNormalMap);
                }

                sw.Stop();
                Debug.Log($"[RoseCache]     BC compress done: {mipData.Length} mips, {sw.ElapsedMilliseconds}ms" +
                          (_gpuCompressor != null ? " (GPU)" : " (CPU)"));

                return (mipData, veldridFormat);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] BC compression failed, falling back to uncompressed: {ex.Message}");
                return (new[] { rgbaData }, Veldrid.PixelFormat.R8_G8_B8_A8_UNorm);
            }
        }

        private static List<(byte[] data, int width, int height)> GenerateMipChain(
            byte[] rgbaData, int width, int height)
        {
            var mips = new List<(byte[], int, int)>();
            mips.Add((rgbaData, width, height));

            var current = rgbaData;
            int w = width, h = height;

            while (w > 1 || h > 1)
            {
                int nw = Math.Max(1, w / 2);
                int nh = Math.Max(1, h / 2);
                var dst = new byte[nw * nh * 4];

                for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int sx = x * 2, sy = y * 2;
                    int sx1 = Math.Min(sx + 1, w - 1);
                    int sy1 = Math.Min(sy + 1, h - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = current[(sy * w + sx) * 4 + c]
                                + current[(sy * w + sx1) * 4 + c]
                                + current[(sy1 * w + sx) * 4 + c]
                                + current[(sy1 * w + sx1) * 4 + c];
                        dst[(y * nw + x) * 4 + c] = (byte)(sum / 4);
                    }
                }

                mips.Add((dst, nw, nh));
                current = dst;
                w = nw;
                h = nh;
            }

            return mips;
        }

        private static byte[][] CompressWithCpuFallback(byte[] rgbaData, int width, int height, bool isNormalMap)
        {
            var bcFormat = isNormalMap ? CompressionFormat.Bc5 : CompressionFormat.Bc7;
            var encoder = new BcEncoder(bcFormat);
            encoder.OutputOptions.Quality = CompressionQuality.Balanced;
            encoder.OutputOptions.GenerateMipMaps = false;
            encoder.Options.IsParallel = true;
            return encoder.EncodeToRawBytes(rgbaData, width, height, BcPixelFormat.Rgba32);
        }

        // ─── Texture Serialization ───────────────────────────

        private static void WriteTexture(BinaryWriter writer, Texture2D? tex,
            string compression, string textureType, bool generateMipmaps = true)
        {
            if (tex == null)
            {
                writer.Write(0); // width=0 → no texture
                return;
            }

            // HDR textures: BC6H compression or float16 fallback
            if (tex._hdrPixelData != null)
            {
                if (compression == "BC6H" && !RoseConfig.DontUseCompressTexture)
                {
                    var sw = Stopwatch.StartNew();
                    var bc6hData = Bc6hEncoder.Encode(tex._hdrPixelData, tex.width, tex.height);
                    sw.Stop();
                    Debug.Log($"[RoseCache]     BC6H compress {tex.width}x{tex.height} → {bc6hData.Length} bytes ({sw.ElapsedMilliseconds}ms)");

                    writer.Write(tex.width);
                    writer.Write(tex.height);
                    writer.Write(FormatBC6H_UFloat);
                    writer.Write(1); // mipCount = 1
                    writer.Write(bc6hData.Length);
                    writer.Write(bc6hData);
                }
                else
                {
                    var halfBytes = Texture2D.ConvertFloatToHalfBytes(tex._hdrPixelData);
                    writer.Write(tex.width);
                    writer.Write(tex.height);
                    writer.Write((int)Veldrid.PixelFormat.R16_G16_B16_A16_Float);
                    writer.Write(1); // mipCount = 1
                    writer.Write(halfBytes.Length);
                    writer.Write(halfBytes);
                }
                return;
            }

            byte[][] mipData;
            Veldrid.PixelFormat format;

            if (tex._mipData != null)
            {
                // Already compressed
                mipData = tex._mipData;
                format = tex._gpuFormat;
            }
            else if (tex._pixelData != null)
            {
                (mipData, format) = CompressTexture(
                    tex._pixelData, tex.width, tex.height, compression, textureType, generateMipmaps);

                // 압축 결과를 텍스처 객체에도 반영하여, 리임포트 후 GPU 업로드가
                // 캐시 로드와 동일한 경로(BC 압축)를 사용하도록 보장한다.
                if (format != Veldrid.PixelFormat.R8_G8_B8_A8_UNorm)
                {
                    tex._mipData = mipData;
                    tex._gpuFormat = format;
                }
            }
            else
            {
                writer.Write(0);
                return;
            }

            writer.Write(tex.width);
            writer.Write(tex.height);
            writer.Write((int)format);
            writer.Write(mipData.Length);
            foreach (var mip in mipData)
            {
                writer.Write(mip.Length);
                writer.Write(mip);
            }
        }

        private static Texture2D? ReadTexture(BinaryReader reader)
        {
            int width = reader.ReadInt32();
            if (width == 0) return null;

            int height = reader.ReadInt32();
            int formatInt = reader.ReadInt32();
            int mipCount = reader.ReadInt32();

            var mipData = new byte[mipCount][];
            for (int i = 0; i < mipCount; i++)
            {
                int dataLen = reader.ReadInt32();
                mipData[i] = reader.ReadBytes(dataLen);
            }

            // BC6H: decode to float32 HDR data
            if (formatInt == FormatBC6H_UFloat)
            {
                var hdrData = Bc6hEncoder.Decode(mipData[0], width, height);
                return new Texture2D(width, height, hdrData);
            }

            var format = (Veldrid.PixelFormat)formatInt;

            // HDR texture: convert float16 bytes back to float32
            if (format == Veldrid.PixelFormat.R16_G16_B16_A16_Float)
            {
                var hdrData = Texture2D.ConvertHalfBytesToFloat(mipData[0]);
                return new Texture2D(width, height, hdrData);
            }

            if (format == Veldrid.PixelFormat.R8_G8_B8_A8_UNorm)
            {
                return new Texture2D(width, height) { _pixelData = mipData[0] };
            }
            else
            {
                return Texture2D.CreateFromCompressed(width, height, format, mipData);
            }
        }

        // ─── Material Serialization ──────────────────────────

        private static void WriteMaterial(BinaryWriter writer, Material mat)
        {
            writer.Write((byte)mat.blendMode);
            WriteColor(writer, mat.color);
            WriteColor(writer, mat.emission);
            writer.Write(mat.metallic);
            writer.Write(mat.roughness);
            writer.Write(mat.occlusion);
            writer.Write(mat.normalMapStrength);

            WriteTexture(writer, mat.mainTexture, "BC7", "Color");
            WriteTexture(writer, mat.normalMap, "BC5", "NormalMap");
            WriteTexture(writer, mat.MROMap, "BC7", "MRO");
        }

        private static Material ReadMaterial(BinaryReader reader)
        {
            var mat = new Material();
            mat.blendMode = (BlendMode)reader.ReadByte();
            mat.color = ReadColor(reader);
            mat.emission = ReadColor(reader);
            mat.metallic = reader.ReadSingle();
            mat.roughness = reader.ReadSingle();
            mat.occlusion = reader.ReadSingle();
            mat.normalMapStrength = reader.ReadSingle();

            mat.mainTexture = ReadTexture(reader);
            mat.normalMap = ReadTexture(reader) ?? Texture2D.DefaultNormal;
            mat.MROMap = ReadTexture(reader) ?? Texture2D.DefaultMRO;
            return mat;
        }

        // ─── Color Helpers ───────────────────────────────────

        private static void WriteColor(BinaryWriter writer, Color c)
        {
            writer.Write(c.r); writer.Write(c.g); writer.Write(c.b); writer.Write(c.a);
        }

        private static Color ReadColor(BinaryReader reader)
        {
            return new Color(reader.ReadSingle(), reader.ReadSingle(),
                             reader.ReadSingle(), reader.ReadSingle());
        }

        // ─── BC Decode Helper ─────────────────────────────────

        private static byte[]? DecodeBcToRgba(byte[] bcData, int width, int height, Veldrid.PixelFormat format)
        {
            try
            {
                var bcFormat = format switch
                {
                    Veldrid.PixelFormat.BC7_UNorm or Veldrid.PixelFormat.BC7_UNorm_SRgb => CompressionFormat.Bc7,
                    Veldrid.PixelFormat.BC3_UNorm or Veldrid.PixelFormat.BC3_UNorm_SRgb => CompressionFormat.Bc3,
                    Veldrid.PixelFormat.BC1_Rgba_UNorm or Veldrid.PixelFormat.BC1_Rgba_UNorm_SRgb => CompressionFormat.Bc1WithAlpha,
                    Veldrid.PixelFormat.BC1_Rgb_UNorm or Veldrid.PixelFormat.BC1_Rgb_UNorm_SRgb => CompressionFormat.Bc1,
                    _ => (CompressionFormat?)null,
                };
                if (bcFormat == null) return null;

                var decoder = new BcDecoder();
                var pixels = decoder.DecodeRaw(bcData, width, height, bcFormat.Value);

                // ColorRgba32[] → byte[], 투명 픽셀의 RGB 정리
                // BC 압축은 A=0인 픽셀의 RGB를 보존하지 않으므로,
                // 디코딩 후 깨진 RGB가 bilinear 필터링 시 번짐(color fringe)을 유발한다.
                // A=0 픽셀의 RGB를 0으로 초기화하여 방지.
                var rgba = new byte[width * height * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte a = pixels[i].a;
                    if (a == 0)
                    {
                        // rgba는 이미 0으로 초기화됨 — skip
                    }
                    else
                    {
                        rgba[i * 4 + 0] = pixels[i].r;
                        rgba[i * 4 + 1] = pixels[i].g;
                        rgba[i * 4 + 2] = pixels[i].b;
                        rgba[i * 4 + 3] = a;
                    }
                }
                return rgba;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] BC decode failed, using compressed: {ex.Message}");
                return null;
            }
        }

        // ─── Metadata Helpers ────────────────────────────────

        private static string GetMetaString(RoseMetadata meta, string key, string defaultValue)
        {
            return meta.importer.TryGetValue(key, out var val) ? val?.ToString() ?? defaultValue : defaultValue;
        }

        private static bool GetMetaBool(RoseMetadata meta, string key, bool defaultValue)
        {
            if (meta.importer.TryGetValue(key, out var val))
                return val is bool b ? b : defaultValue;
            return defaultValue;
        }
    }
}
