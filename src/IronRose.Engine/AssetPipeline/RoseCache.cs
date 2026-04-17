// ------------------------------------------------------------
// @file    RoseCache.cs
// @brief   에셋(텍스처, 메시)을 바이너리 캐시 파일로 저장/로드하여 임포트 속도를 높인다.
//          FormatVersion 12: sRGB variant 대신 UNorm variant 사용 (엔진 렌더 파이프라인이
//          아직 sRGB variant를 지원하지 않아 압축 텍스처가 어두워지던 버그 수정).
// @deps    IronRose.Engine (GpuTextureCompressor, MeshImportResult, RoseMetadata, ProjectContext,
//                           TextureCompressionFormatResolver),
//          RoseEngine (Material, Texture2D, Color, BlendMode, Mesh, Vector2/3/4, BoneWeight),
//          SixLabors.ImageSharp (PNG 임시 파일 저장용)
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
//          FormatVersion 11+: texture_type + quality → TextureCompressionFormatResolver로 포맷 결정.
//            Color/High,Med=BC7, Color/Low=BC1, ColorWithAlpha/Low=BC3, NormalMap=BC5,
//            Sprite/Low=BC3, HDR/Panoramic=BC6H. NoCompression=R8G8B8A8(LDR)/RGBA16F(HDR).
//          FormatVersion 12: 엔진이 sRGB 파이프라인 미지원이라 Resolver/RoseCache 모두 UNorm
//            variant로만 업로드. BC7_UNorm_SRgb 등을 쓰면 GPU sRGB 디코딩 + 최종 blit의
//            pow(1/2.2) 감마 보정이 이중 적용되어 이미지가 어두워지기 때문.
//          압축 우선순위: Compressonator CLI → GPU Vulkan (BC7/BC5만) → CPU BCnEncoder.NET.
//          Compressonator CLI는 externalTools/compressonatorcli/에서 자동 탐지하며, 없으면 폴백.
//          BC1 폴백 검증: BCnEncoder.NET의 BC1 지원 여부를 static _bc1CpuSupported 플래그로
//          1회 검증하여 미지원 환경에서는 BC3로 폴백한다. CPU 폴백이 BC1→BC3로 전환되면
//          CompressWithCpuFallback의 out actualFormat를 통해 호출 측에 전달되고,
//          Veldrid 포맷(BC3_UNorm(_SRgb))도 동시에 재계산되어 mip 체인 포맷 불일치와 업로드
//          크래시를 방지한다.
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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;
using BcPixelFormat = BCnEncoder.Encoder.PixelFormat;
using Color = RoseEngine.Color;
using Debug = RoseEngine.EditorDebug;

namespace IronRose.AssetPipeline
{
    public class RoseCache
    {
        private const uint Magic = 0x45534F52; // "ROSE"
        private const int FormatVersion = 12; // v12: sRGB variant 대신 UNorm variant 사용 (엔진 렌더 파이프라인이 아직 sRGB variant 미지원)

        // Custom format ID for BC6H (not in Veldrid 4.9 PixelFormat enum)
        private const int FormatBC6H_UFloat = 1000;

        private readonly string _cacheRoot;

        // GPU texture compressor (set from EngineCore)
        private static GpuTextureCompressor? _gpuCompressor;
        public static void SetGpuCompressor(GpuTextureCompressor? compressor) => _gpuCompressor = compressor;

        // Compressonator CLI path cache: null = not yet checked, "" = checked but not found
        private static string? _compressonatorCliPath;
        private static string? _compressonatorLibPath;

        // BC1 CPU 인코더 지원 여부 캐시:
        //   null  = 아직 확인 전 (첫 시도 시 결정)
        //   true  = 정상 동작 확인됨
        //   false = 예외 발생 확인됨 → 이후 호출은 즉시 BC3로 폴백
        // 세션 내 1회 판정. BCnEncoder.NET 버전/환경에 따라 BC1 미지원 가능성이 있기 때문에
        // 런타임 검증으로 안전하게 BC3로 폴백한다.
        private static bool? _bc1CpuSupported;

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
                var textureType = GetMetaString(meta, "texture_type", "Color");
                var quality = GetMetaString(meta, "quality", "High");
                var isSrgb = GetMetaBool(meta, "srgb", false);
                var genMips = GetMetaBool(meta, "generate_mipmaps", false);
                var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);

                Debug.Log($"[RoseCache] Storing texture '{assetPath}' {texture.width}x{texture.height} type={textureType} quality={quality} srgb={isSrgb} mips={genMips} → format={resolution.DisplayLabel}");

                using (var fs = File.Create(tempPath))
                using (var writer = new BinaryWriter(fs))
                {
                    WriteValidationHeader(writer, assetPath);
                    writer.Write((byte)2); // Texture
                    WriteTexture(writer, texture, textureType, quality, isSrgb, genMips);
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
            byte[] rgbaData, int width, int height,
            string textureType, string quality, bool isSrgb,
            bool generateMipmaps = true)
        {
            var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);

            // NoCompression 또는 전역 강제 비압축 플래그.
            // HDR 경로는 여기에 도달하지 않음 (WriteTexture의 _hdrPixelData 블록에서 처리).
            if (resolution.IsUncompressed || RoseConfig.DontUseCompressTexture)
            {
                // RoseConfig.DontUseCompressTexture 플래그 활성 시에도 기존 동작(sRGB 비변종) 유지.
                var uncompressedFormat = RoseConfig.DontUseCompressTexture
                    ? Veldrid.PixelFormat.R8_G8_B8_A8_UNorm
                    : resolution.VeldridFormat;
                return (new[] { rgbaData }, uncompressedFormat);
            }

            try
            {
                var cliFormat = resolution.CompressonatorFormat!; // IsUncompressed=false 이므로 non-null 보장
                var cliQuality = TextureCompressionFormatResolver.GetCompressonatorQuality(cliFormat, quality);
                var veldridFormat = resolution.VeldridFormat;

                // GPU 경로는 현재 BC7/BC5만 지원.
                bool gpuSupported = cliFormat == "BC7" || cliFormat == "BC5";

                // BC1 선제 폴백: CPU BC1이 이미 미지원으로 확인되었고, CLI/GPU 경로가 모두 이 포맷을
                // 처리할 수 없다면 출발점부터 BC3로 전환하여 mip 체인 포맷 불일치를 방지한다.
                // (CLI는 BC1을 지원하므로 CLI가 사용 가능하면 BC1 유지.)
                // Resolver가 현재 UNorm variant만 반환하므로 여기서도 UNorm만 사용한다.
                // 엔진이 sRGB pipeline으로 전환되면 Resolver와 함께 맞춰 갱신할 것.
                bool bc1PreFallback = false;
                if (cliFormat == "BC1" && _bc1CpuSupported == false && _compressonatorCliPath == "")
                {
                    cliFormat = "BC3";
                    veldridFormat = Veldrid.PixelFormat.BC3_UNorm;
                    cliQuality = TextureCompressionFormatResolver.GetCompressonatorQuality(cliFormat, quality);
                    bc1PreFallback = true;
                    Debug.LogWarning("[RoseCache] BC1 pre-fallback: CLI unavailable and CPU BC1 known unsupported → encoding as BC3");
                }

                Debug.Log($"[RoseCache]     BC compress {textureType} {width}x{height} → {resolution.DisplayLabel} (q={quality} cli={cliQuality})...");
                var sw = Stopwatch.StartNew();

                byte[][] mipData;
                bool bc1RuntimeFallback = false;

                // 1순위: Compressonator CLI (Mode 0~7 전체 탐색, 최고 품질)
                var cliResult = CompressWithCompressonator(rgbaData, width, height, cliFormat, cliQuality);
                if (cliResult != null)
                {
                    if (generateMipmaps)
                    {
                        // CLI로 각 mip level 개별 압축
                        var mipChain = GenerateMipChain(rgbaData, width, height);
                        mipData = new byte[mipChain.Count][];
                        mipData[0] = cliResult; // mip0는 이미 압축됨
                        for (int i = 1; i < mipChain.Count; i++)
                        {
                            var (mipRgba, mw, mh) = mipChain[i];
                            var mipResult = CompressWithCompressonator(mipRgba, mw, mh, cliFormat, cliQuality);
                            if (mipResult != null)
                            {
                                mipData[i] = mipResult;
                            }
                            else
                            {
                                // CLI가 중간에 실패하면 나머지는 CPU 폴백.
                                // CPU 폴백이 BC1 → BC3로 전환하면 mip 체인 전체가 포맷 불일치가 되므로,
                                // 해당 시점부터 mip0 포함 전체를 CPU 폴백으로 재생성한다.
                                var fallback = CompressWithCpuFallback(mipRgba, mw, mh, cliFormat, isSrgb, out var mipActualFormat);
                                if (mipActualFormat != cliFormat)
                                {
                                    Debug.LogWarning($"[RoseCache] CPU fallback produced {mipActualFormat} instead of {cliFormat} during CLI mip chain — regenerating full mip chain on CPU to avoid format mismatch");
                                    cliFormat = mipActualFormat;
                                    bc1RuntimeFallback = true;
                                    mipData = CompressWithCpuFallback(rgbaData, width, height, cliFormat, isSrgb, out _);
                                    goto finalizeFormat;
                                }
                                mipData[i] = fallback[0];
                            }
                        }
                    }
                    else
                    {
                        mipData = new byte[][] { cliResult };
                    }
                }
                else if (_gpuCompressor != null && gpuSupported)
                {
                    // 2순위: GPU 경로 (BC7/BC5만 지원, BC1/BC3는 CPU 폴백으로 내려감)
                    if (generateMipmaps)
                    {
                        // GPU path: hardware mipmap generation → BC compress each mip
                        var mipChain = _gpuCompressor.GenerateMipmapsGPU(rgbaData, width, height);

                        mipData = new byte[mipChain.Length][];
                        int mw = width, mh = height;
                        for (int i = 0; i < mipChain.Length; i++)
                        {
                            mipData[i] = cliFormat == "BC5"
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
                        mipData[0] = cliFormat == "BC5"
                            ? _gpuCompressor.CompressBC5(rgbaData, width, height)
                            : _gpuCompressor.CompressBC7(rgbaData, width, height);
                    }
                }
                else
                {
                    // 3순위: CPU 폴백 (BCnEncoder.NET). BC1 미지원 시 내부에서 BC3로 전환.
                    mipData = CompressWithCpuFallback(rgbaData, width, height, cliFormat, isSrgb, out var actualFormat);
                    if (actualFormat != cliFormat)
                    {
                        cliFormat = actualFormat;
                        bc1RuntimeFallback = true;
                    }
                }

            finalizeFormat:
                // CPU 폴백에서 BC1 → BC3 전환이 발생한 경우 Veldrid 포맷도 일치시켜야 한다.
                // 포맷 불일치 상태로 GPU에 업로드하면 크기 계산이 틀려 업로드 크래시/깨진 텍스처.
                // Resolver가 현재 UNorm variant만 반환하므로 여기서도 UNorm만 사용한다.
                if (bc1RuntimeFallback)
                {
                    veldridFormat = cliFormat switch
                    {
                        "BC3" => Veldrid.PixelFormat.BC3_UNorm,
                        "BC1" => Veldrid.PixelFormat.BC1_Rgba_UNorm,
                        "BC5" => Veldrid.PixelFormat.BC5_UNorm,
                        "BC7" => Veldrid.PixelFormat.BC7_UNorm,
                        _     => veldridFormat,
                    };
                    Debug.LogWarning($"[RoseCache] Format fallback applied → Veldrid format = {veldridFormat}");
                }

                sw.Stop();
                string compressSource = cliResult != null
                    ? "CLI"
                    : (_gpuCompressor != null && gpuSupported ? "GPU" : "CPU");
                bool cliAvailable = _compressonatorCliPath != null && _compressonatorCliPath != "";
                Debug.Log($"[RoseCache]     BC compress done via {compressSource} ({cliFormat}, q={cliQuality:0.0##}): {mipData.Length} mips, {sw.ElapsedMilliseconds}ms");
                Debug.Log($"[RoseCache]     Fallback path: CLI={cliAvailable}, GPU={(_gpuCompressor != null && gpuSupported && compressSource == "GPU")}, CPU={(compressSource == "CPU")}, BC1→BC3={(bc1PreFallback || bc1RuntimeFallback)}");

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

        private static byte[]? CompressWithCompressonator(byte[] rgbaData, int width, int height, string format, double quality)
        {
            try
            {
                // Resolve and cache CLI path (platform-specific)
                if (_compressonatorCliPath == null)
                {
                    string platformDir, exeName;
                    if (OperatingSystem.IsWindows())
                    {
                        platformDir = "windows";
                        exeName = "compressonatorcli.exe";
                    }
                    else
                    {
                        platformDir = "linux";
                        exeName = "compressonatorcli-bin";
                    }

                    var cliPath = Path.Combine(ProjectContext.EngineRoot, "externalTools", "compressonatorcli", platformDir, exeName);
                    if (File.Exists(cliPath))
                    {
                        _compressonatorCliPath = cliPath;
                        _compressonatorLibPath = Path.Combine(ProjectContext.EngineRoot, "externalTools", "compressonatorcli", platformDir, "pkglibs");
                        Debug.Log($"[RoseCache] Compressonator CLI found: {cliPath}");
                    }
                    else
                    {
                        _compressonatorCliPath = "";
                        _compressonatorLibPath = "";
                        Debug.Log("[RoseCache] Compressonator CLI not found, will use fallback compressors");
                    }
                }

                if (_compressonatorCliPath == "")
                    return null;

                // Save RGBA data as temporary PNG
                string tempId = Guid.NewGuid().ToString("N");
                string tempInputPath = Path.Combine(Path.GetTempPath(), $"rosecache_{tempId}_input.png");
                string tempOutputPath = Path.Combine(Path.GetTempPath(), $"rosecache_{tempId}_output.dds");

                try
                {
                    using (var image = Image.LoadPixelData<Rgba32>(rgbaData, width, height))
                    {
                        image.SaveAsPng(tempInputPath);
                    }

                    // Run Compressonator CLI
                    int numThreads = Environment.ProcessorCount;
                    // invariant culture 로 직렬화 (locale에 따른 "0,6" 방지)
                    string qualityStr = quality.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _compressonatorCliPath,
                        Arguments = $"-fd {format} -Quality {qualityStr} -NumThreads {numThreads} -silent \"{tempInputPath}\" \"{tempOutputPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    // Linux: set LD_LIBRARY_PATH (matches launcher script)
                    if (!OperatingSystem.IsWindows())
                    {
                        var binDir = Path.GetDirectoryName(_compressonatorCliPath)!;
                        var qtDir = Path.Combine(binDir, "qt");
                        string ldPaths = $"{binDir}:{_compressonatorLibPath}:{qtDir}";
                        string existingLdPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                        startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existingLdPath)
                            ? ldPaths
                            : $"{ldPaths}:{existingLdPath}";
                    }

                    using var process = Process.Start(startInfo);
                    if (process == null)
                        return null;

                    bool exited = process.WaitForExit(60000);
                    if (!exited)
                    {
                        Debug.LogWarning("[RoseCache] Compressonator CLI timed out (60s)");
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    if (process.ExitCode != 0)
                    {
                        string stderr = process.StandardError.ReadToEnd();
                        Debug.LogWarning($"[RoseCache] Compressonator CLI failed (exit {process.ExitCode}): {stderr}");
                        return null;
                    }

                    if (!File.Exists(tempOutputPath))
                    {
                        Debug.LogWarning("[RoseCache] Compressonator CLI did not produce output DDS file");
                        return null;
                    }

                    // Parse DDS file: extract raw BC data
                    byte[] ddsBytes = File.ReadAllBytes(tempOutputPath);
                    if (ddsBytes.Length < 128)
                    {
                        Debug.LogWarning("[RoseCache] Compressonator CLI output DDS too small");
                        return null;
                    }

                    // FourCC is at offset 84: magic(4) + ddsd_size(4) + flags(4) + height(4) + width(4)
                    // + pitch(4) + depth(4) + mipcount(4) + reserved[11](44) + pf_size(4) + pf_flags(4) = 84
                    uint fourcc = BitConverter.ToUInt32(ddsBytes, 84);
                    int dataOffset = 128; // magic(4) + header(124)
                    if (fourcc == 0x30315844) // "DX10" little-endian
                        dataOffset += 20;

                    if (ddsBytes.Length <= dataOffset)
                    {
                        Debug.LogWarning("[RoseCache] Compressonator CLI output DDS has no data after header");
                        return null;
                    }

                    byte[] bcData = new byte[ddsBytes.Length - dataOffset];
                    Array.Copy(ddsBytes, dataOffset, bcData, 0, bcData.Length);
                    return bcData;
                }
                finally
                {
                    // Cleanup temp files
                    try { if (File.Exists(tempInputPath)) File.Delete(tempInputPath); } catch { }
                    try { if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] Compressonator CLI error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// BCnEncoder.NET 기반 CPU 폴백 인코더.
        /// BC1 요청 시 런타임 지원 여부를 static flag(_bc1CpuSupported)로 1회 검증하고,
        /// 미지원으로 판정되면 이후 호출은 즉시 BC3로 폴백한다. `actualFormat`에 실제로
        /// 인코딩된 포맷 문자열을 반환하므로, 호출 측에서 Veldrid 포맷을 그에 맞춰 재계산해야
        /// mip 체인 포맷 불일치 및 업로드 크래시를 방지할 수 있다.
        /// </summary>
        private static byte[][] CompressWithCpuFallback(
            byte[] rgbaData, int width, int height, string format, bool isSrgb,
            out string actualFormat)
        {
            // isSrgb는 현재 BCnEncoder.NET에 전달할 수단이 없음(raw bytes 인코딩).
            // sRGB는 업로드 시 Veldrid 포맷 선택으로만 반영되므로 인코딩 단계에서는 무시.
            // 매개변수는 향후 확장을 위해 받아두되 현재는 사용하지 않는다.
            _ = isSrgb;

            // BC1이 이미 미지원으로 확인된 경우 사전 폴백 — 예외 경로를 반복하지 않음.
            if (format == "BC1" && _bc1CpuSupported == false)
            {
                Debug.LogWarning("[RoseCache] BC1 CPU encoder unavailable (cached), using BC3 fallback");
                format = "BC3";
            }

            var bcFormat = format switch
            {
                "BC5" => CompressionFormat.Bc5,
                "BC3" => CompressionFormat.Bc3,
                "BC1" => CompressionFormat.Bc1WithAlpha, // Color/Low도 알파 1비트 지원 위해 Bc1WithAlpha 사용
                _     => CompressionFormat.Bc7,
            };

            try
            {
                var encoder = new BcEncoder(bcFormat);
                encoder.OutputOptions.Quality = CompressionQuality.Balanced;
                encoder.OutputOptions.GenerateMipMaps = false;
                encoder.Options.IsParallel = true;
                var result = encoder.EncodeToRawBytes(rgbaData, width, height, BcPixelFormat.Rgba32);
                if (format == "BC1") _bc1CpuSupported = true;
                actualFormat = format;
                return result;
            }
            catch (Exception ex) when (format == "BC1")
            {
                _bc1CpuSupported = false;
                Debug.LogWarning($"[RoseCache] BC1 CPU encoder failed ({ex.Message}), falling back to BC3 for this and subsequent calls");
                // BC3로 즉시 재시도.
                var encoder = new BcEncoder(CompressionFormat.Bc3);
                encoder.OutputOptions.Quality = CompressionQuality.Balanced;
                encoder.OutputOptions.GenerateMipMaps = false;
                encoder.Options.IsParallel = true;
                var result = encoder.EncodeToRawBytes(rgbaData, width, height, BcPixelFormat.Rgba32);
                actualFormat = "BC3";
                return result;
            }
        }

        // ─── Texture Serialization ───────────────────────────

        private static void WriteTexture(BinaryWriter writer, Texture2D? tex,
            string textureType, string quality, bool isSrgb, bool generateMipmaps = true)
        {
            if (tex == null)
            {
                writer.Write(0); // width=0 → no texture
                return;
            }

            // HDR textures: BC6H compression or float16 fallback
            if (tex._hdrPixelData != null)
            {
                var hdrResolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);
                if (!hdrResolution.IsUncompressed && !RoseConfig.DontUseCompressTexture)
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
                    tex._pixelData, tex.width, tex.height, textureType, quality, isSrgb, generateMipmaps);

                // 압축 결과를 텍스처 객체에도 반영하여, 리임포트 후 GPU 업로드가
                // 캐시 로드와 동일한 경로(BC 압축)를 사용하도록 보장한다.
                // Resolver 결과로 판정하여 sRGB variant 포맷도 올바르게 처리한다.
                var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);
                if (!resolution.IsUncompressed && !RoseConfig.DontUseCompressTexture)
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

            // Material 내부 텍스처는 메타를 따로 가지지 않으므로 고정값 사용.
            // MRO는 linear color로 처리 (Resolver에 MRO 타입이 없어 Color로 fallback).
            WriteTexture(writer, mat.mainTexture, "Color", "High", true);
            WriteTexture(writer, mat.normalMap, "NormalMap", "High", false);
            WriteTexture(writer, mat.MROMap, "Color", "High", false);
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
