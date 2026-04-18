// ------------------------------------------------------------
// @file    RoseCache.cs
// @brief   에셋(텍스처, 메시)을 바이너리 캐시 파일로 저장/로드하여 임포트 속도를 높인다.
//          FormatVersion 12: sRGB variant 대신 UNorm variant 사용 (엔진 렌더 파이프라인이
//          아직 sRGB variant를 지원하지 않아 압축 텍스처가 어두워지던 버그 수정).
//          Phase 1: 텍스처 압축을 Plan(순수)/Background(CLI·CPU)/FinalizeOnMain(GPU) 3단계로
//          분리. 기존 CompressTexture는 3단계를 동기로 합성한 내부 래퍼로 유지한다.
// @deps    IronRose.Engine (GpuTextureCompressor, MeshImportResult, RoseMetadata, ProjectContext,
//                           TextureCompressionFormatResolver, TextureWarmupTypes),
//          IronRose.Contracts (ThreadGuard),
//          RoseEngine (Material, Texture2D, Color, BlendMode, Mesh, Vector2/3/4, BoneWeight),
//          SixLabors.ImageSharp (PNG 임시 파일 저장용)
// @exports
//   class RoseCache
//     SetGpuCompressor(GpuTextureCompressor?): void         — GPU 텍스처 압축기 설정
//     TryLoadTexture(string, RoseMetadata): Texture2D?      — 캐시에서 텍스처 로드
//     StoreTexture(string, Texture2D, RoseMetadata): void   — 텍스처를 캐시에 저장 (동기 합성 경로)
//     StoreTexturePrecompressed(string, Texture2D, RoseMetadata, byte[][], Veldrid.PixelFormat): void
//                                                           — 이미 압축된 mipData를 받아 직렬화만
//     static PlanTextureCompression(RoseMetadata, int, int): TextureCompressionPlan
//                                                           — 순수 함수(백그라운드 안전)
//     static CompressTextureBackground(TextureCompressionPlan, byte[], int, int): TextureCompressionResult
//                                                           — CLI/CPU 경로 (백그라운드 안전)
//     static FinalizeTextureOnMain(TextureCompressionPlan, TextureCompressionResult, byte[], int, int)
//                        : (byte[][], Veldrid.PixelFormat)  — GPU 경로 마무리 (메인 전용)
//     static PlanHdrCompression(RoseMetadata, Texture2D): HdrCompressionPlan
//     static EncodeHdrBackground(HdrCompressionPlan, float[], int, int): HdrCompressionResult
//     static FinalizeHdrOnMain(HdrCompressionPlan, HdrCompressionResult): HdrCompressionResult
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
//          ReadTexture는 BC6H 경로에서 Texture2D._storedAsBC6H = true로 설정한다. 이는 로드 후
//          HDR float 포맷으로 디코드되어 _gpuFormat이 R16_G16_B16_A16_Float이 되는 상황에서도
//          Inspector 프리뷰 라벨이 실제 저장 포맷(BC6H)을 표시할 수 있도록 하는 보조 플래그.
//          스레드 안전: Plan/CompressTextureBackground/EncodeHdrBackground 는 순수 계산+파일 I/O 이며
//          _gpuCompressor 등 메인 전용 리소스에 절대 접근하지 않는다. FinalizeTextureOnMain /
//          FinalizeHdrOnMain / StoreTexturePrecompressed / StoreTexture 는 메인 전용이며
//          진입부에 ThreadGuard.CheckMainThread 를 둔다. 정적 필드 캐시(_compressonatorCliPath,
//          _compressonatorLibPath, _bc1CpuSupported)는 Interlocked로 1회-승자 방식으로 설정한다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
        // 다중 스레드가 동시에 CompressTextureBackground 를 호출할 수 있으므로
        // Interlocked.CompareExchange 로 첫 할당 승자만 값을 세팅한다.
        private static string? _compressonatorCliPath;
        private static string? _compressonatorLibPath;

        // BC1 CPU 인코더 지원 여부 3-state 캐시 (Interlocked용):
        //   0 = unknown (아직 확인 전)
        //   1 = supported (정상 동작 확인됨)
        //   2 = unsupported (예외 발생 확인됨 → 이후 호출은 즉시 BC3로 폴백)
        // 백그라운드 CPU 폴백에서 공유되므로 Volatile.Read + Interlocked.CompareExchange 조합.
        // BCnEncoder.NET 버전/환경에 따라 BC1 미지원 가능성이 있기 때문에 런타임 검증으로 폴백.
        private const int Bc1StateUnknown = 0;
        private const int Bc1StateSupported = 1;
        private const int Bc1StateUnsupported = 2;
        private static int _bc1CpuSupportedState = Bc1StateUnknown;

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

        /// <summary>
        /// 텍스처를 캐시에 저장한다. 기존 동기 경로 호환: 내부적으로 Plan → CompressTextureBackground
        /// → FinalizeTextureOnMain → StoreTexturePrecompressed를 순차 호출한다.
        /// HDR 텍스처는 별도 HDR 파이프라인을 따른다 (PlanHdrCompression / EncodeHdrBackground
        /// / FinalizeHdrOnMain). 외부 계약은 기존과 동일.
        /// </summary>
        public void StoreTexture(string assetPath, Texture2D texture, RoseMetadata meta)
        {
            if (!ThreadGuard.CheckMainThread("RoseCache.StoreTexture"))
            {
                Debug.LogWarning($"[RoseCache] StoreTexture called off main thread for '{assetPath}' — skipped");
                return;
            }

            var textureType = GetMetaString(meta, "texture_type", "Color");
            var quality = GetMetaString(meta, "quality", "High");
            var isSrgb = GetMetaBool(meta, "srgb", false);
            var genMips = GetMetaBool(meta, "generate_mipmaps", false);
            var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);

            Debug.Log($"[RoseCache] Storing texture '{assetPath}' {texture.width}x{texture.height} type={textureType} quality={quality} srgb={isSrgb} mips={genMips} → format={resolution.DisplayLabel}");

            // HDR 경로: _hdrPixelData가 있으면 HDR 파이프라인으로 분기.
            // 이 경로는 _mipData를 설정하지 않고 WriteTexture 내부의 HDR 블록에서 직렬화한다.
            if (texture._hdrPixelData != null)
            {
                // HDR은 현재 WriteTexture의 HDR 블록이 Bc6hEncoder.Encode를 직접 호출하므로,
                // Plan/Background/Finalize 분리를 외부에 노출만 하고 여기서는 기존 경로 유지.
                // (파일 쓰기는 아래 공통 블록에서 수행.)
                WriteCacheFile(assetPath, writer =>
                    WriteTexture(writer, texture, textureType, quality, isSrgb, genMips));
                return;
            }

            // LDR 경로: Plan → Background → FinalizeOnMain → Precompressed store
            if (texture._mipData != null)
            {
                // 이미 어떤 경로에서 압축이 끝난 텍스처 → 재압축하지 않고 그대로 저장.
                StoreTexturePrecompressed(assetPath, texture, meta, texture._mipData, texture._gpuFormat);
                return;
            }

            if (texture._pixelData == null)
            {
                // 픽셀 데이터도 mip 데이터도 없는 경우: 빈 텍스처로 기록.
                WriteCacheFile(assetPath, writer =>
                    WriteTexture(writer, texture, textureType, quality, isSrgb, genMips));
                return;
            }

            var plan = PlanTextureCompression(meta, texture.width, texture.height);
            var result = CompressTextureBackground(plan, texture._pixelData, texture.width, texture.height);
            var (mipData, format) = FinalizeTextureOnMain(
                plan, result, texture._pixelData, texture.width, texture.height);
            StoreTexturePrecompressed(assetPath, texture, meta, mipData, format);
        }

        /// <summary>
        /// 이미 압축된 mipData/format을 받아 디스크 직렬화만 수행한다. 메인 전용.
        /// 호출자는 백그라운드 파이프라인(PlanTextureCompression → CompressTextureBackground →
        /// FinalizeTextureOnMain)을 거쳐 받은 결과를 그대로 넘겨야 한다.
        /// Precompressed 경로에서는 texture._mipData / _gpuFormat을 최종 반영한다.
        /// </summary>
        public void StoreTexturePrecompressed(
            string assetPath, Texture2D texture, RoseMetadata meta,
            byte[][] mipData, Veldrid.PixelFormat format)
        {
            if (!ThreadGuard.CheckMainThread("RoseCache.StoreTexturePrecompressed"))
            {
                Debug.LogWarning($"[RoseCache] StoreTexturePrecompressed called off main thread for '{assetPath}' — skipped");
                return;
            }

            var textureType = GetMetaString(meta, "texture_type", "Color");
            var quality = GetMetaString(meta, "quality", "High");
            var isSrgb = GetMetaBool(meta, "srgb", false);
            var genMips = GetMetaBool(meta, "generate_mipmaps", false);

            // 압축 결과를 텍스처 객체에도 반영하여, 리임포트 후 GPU 업로드가
            // 캐시 로드와 동일한 경로(BC 압축)를 사용하도록 보장한다.
            // Resolver 결과로 판정하여 UNorm/uncompressed 모두 올바르게 처리한다.
            var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);
            if (!resolution.IsUncompressed && !RoseConfig.DontUseCompressTexture)
            {
                texture._mipData = mipData;
                texture._gpuFormat = format;
            }

            WriteCacheFile(assetPath, writer =>
                WriteTexture(writer, texture, textureType, quality, isSrgb, genMips));
        }

        /// <summary>
        /// 텍스처 캐시 파일 쓰기 공통 블록. temp + atomic rename.
        /// </summary>
        private void WriteCacheFile(string assetPath, Action<BinaryWriter> writeBody)
        {
            var cachePath = GetCachePath(assetPath);
            var tempPath = cachePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var sw = Stopwatch.StartNew();

                using (var fs = File.Create(tempPath))
                using (var writer = new BinaryWriter(fs))
                {
                    WriteValidationHeader(writer, assetPath);
                    writer.Write((byte)2); // Texture
                    writeBody(writer);
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

        /// <summary>
        /// 순수 함수. RoseMetadata와 텍스처 크기를 받아 압축 플랜을 계산한다.
        /// Static mutable 상태에 접근하지 않으며 백그라운드 호출에 안전하다.
        /// </summary>
        public static TextureCompressionPlan PlanTextureCompression(RoseMetadata meta, int width, int height)
        {
            var textureType = GetMetaString(meta, "texture_type", "Color");
            var quality = GetMetaString(meta, "quality", "High");
            var isSrgb = GetMetaBool(meta, "srgb", false);
            var genMips = GetMetaBool(meta, "generate_mipmaps", false);
            return BuildPlan(textureType, quality, isSrgb, genMips, width, height);
        }

        /// <summary>
        /// PlanTextureCompression의 파라미터 버전. RoseMetadata 없이 호출해야 하는 경로
        /// (예: Material 내부 텍스처 직렬화)에서 사용한다. 순수 함수.
        /// </summary>
        private static TextureCompressionPlan BuildPlan(
            string textureType, string quality, bool isSrgb, bool generateMipmaps,
            int width, int height)
        {
            _ = width; _ = height; // plan 계산에는 현재 크기 의존성 없음 — 미래 확장 대비 파라미터만 유지
            var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);

            string? cliFormat = resolution.IsUncompressed ? null : resolution.CompressonatorFormat;
            double cliQuality = cliFormat != null
                ? TextureCompressionFormatResolver.GetCompressonatorQuality(cliFormat, quality)
                : 0.0;
            bool gpuSupported = cliFormat == "BC7" || cliFormat == "BC5";

            return new TextureCompressionPlan(
                TextureType: textureType,
                Quality: quality,
                IsSrgb: isSrgb,
                GenerateMipmaps: generateMipmaps,
                CompressonatorFormat: cliFormat,
                CompressonatorQuality: cliQuality,
                InitialVeldridFormat: resolution.VeldridFormat,
                IsUncompressed: resolution.IsUncompressed,
                GpuSupported: gpuSupported);
        }

        /// <summary>
        /// 동기 합성 래퍼. Background → FinalizeOnMain을 순차 호출하여 기존 CompressTexture와
        /// 동일한 동작을 재현한다. WriteTexture에서 Material 내부 텍스처처럼 RoseMetadata 없이
        /// 즉시 압축이 필요한 경로에서 사용된다.
        /// 메인 스레드에서만 호출해야 한다 (FinalizeTextureOnMain이 메인 전용이므로).
        /// </summary>
        private static (byte[][] mipData, Veldrid.PixelFormat format) CompressTextureSync(
            byte[] rgbaData, int width, int height,
            string textureType, string quality, bool isSrgb, bool generateMipmaps)
        {
            var plan = BuildPlan(textureType, quality, isSrgb, generateMipmaps, width, height);
            var result = CompressTextureBackground(plan, rgbaData, width, height);
            return FinalizeTextureOnMain(plan, result, rgbaData, width, height);
        }

        /// <summary>
        /// 백그라운드 호출 가능. CLI 및 CPU 폴백 경로만 실행하며 _gpuCompressor에 절대 접근하지 않는다.
        /// GPU가 필요한 경우 Stage=NeedsGpu로 반환하고 FinalizeTextureOnMain이 GPU 경로를 수행한다.
        /// </summary>
        public static TextureCompressionResult CompressTextureBackground(
            TextureCompressionPlan plan, byte[] rgbaData, int width, int height)
        {
            // NoCompression 또는 전역 강제 비압축 플래그.
            if (plan.IsUncompressed || RoseConfig.DontUseCompressTexture)
            {
                var uncompressedFormat = RoseConfig.DontUseCompressTexture
                    ? Veldrid.PixelFormat.R8_G8_B8_A8_UNorm
                    : plan.InitialVeldridFormat;
                return new TextureCompressionResult
                {
                    Stage = TextureCompressionStage.Uncompressed,
                    MipData = new[] { rgbaData },
                    ActualCompressonatorFormat = "",
                    ActualVeldridFormat = uncompressedFormat,
                    SourceTag = "UncompressedLDR",
                };
            }

            try
            {
                var cliFormat = plan.CompressonatorFormat!; // IsUncompressed=false 이므로 non-null
                var cliQuality = plan.CompressonatorQuality;
                var veldridFormat = plan.InitialVeldridFormat;

                // BC1 선제 폴백: CPU BC1이 이미 미지원으로 확인되었고, CLI 경로도 사용 불가이면
                // 출발점부터 BC3로 전환하여 mip 체인 포맷 불일치를 방지한다.
                bool bc1PreFallback = false;
                bool bc1CpuUnsupported = Volatile.Read(ref _bc1CpuSupportedState) == Bc1StateUnsupported;
                bool cliKnownUnavailable = _compressonatorCliPath == "";
                if (cliFormat == "BC1" && bc1CpuUnsupported && cliKnownUnavailable)
                {
                    cliFormat = "BC3";
                    veldridFormat = Veldrid.PixelFormat.BC3_UNorm;
                    cliQuality = TextureCompressionFormatResolver.GetCompressonatorQuality(cliFormat, plan.Quality);
                    bc1PreFallback = true;
                    Debug.LogWarning("[RoseCache] BC1 pre-fallback: CLI unavailable and CPU BC1 known unsupported → encoding as BC3");
                }

                var displayLabel = TextureCompressionFormatResolver.Resolve(plan.TextureType, plan.Quality, plan.IsSrgb).DisplayLabel;
                Debug.Log($"[RoseCache]     BC compress {plan.TextureType} {width}x{height} → {displayLabel} (q={plan.Quality} cli={cliQuality})...");
                var sw = Stopwatch.StartNew();

                byte[][]? mipData = null;
                bool bc1RuntimeFallback = false;
                string sourceTag;

                // 1순위: Compressonator CLI (Mode 0~7 전체 탐색, 최고 품질)
                var cliResult = CompressWithCompressonator(rgbaData, width, height, cliFormat, cliQuality);
                if (cliResult != null)
                {
                    if (plan.GenerateMipmaps)
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
                                var fallback = CompressWithCpuFallback(mipRgba, mw, mh, cliFormat, plan.IsSrgb, out var mipActualFormat);
                                if (mipActualFormat != cliFormat)
                                {
                                    Debug.LogWarning($"[RoseCache] CPU fallback produced {mipActualFormat} instead of {cliFormat} during CLI mip chain — regenerating full mip chain on CPU to avoid format mismatch");
                                    cliFormat = mipActualFormat;
                                    bc1RuntimeFallback = true;
                                    mipData = CompressWithCpuFallback(rgbaData, width, height, cliFormat, plan.IsSrgb, out _);
                                    break;
                                }
                                mipData[i] = fallback[0];
                            }
                        }
                    }
                    else
                    {
                        mipData = new byte[][] { cliResult };
                    }
                    sourceTag = "CLI";
                }
                else if (plan.GpuSupported)
                {
                    // 2순위: GPU는 메인에서만 가능 → NeedsGpu 반환. CLI 실패 로그 구분.
                    sw.Stop();
                    bool cliAvailable = _compressonatorCliPath != null && _compressonatorCliPath != "";
                    Debug.Log($"[RoseCache]     BC compress background → needs GPU ({cliFormat}): CLI failed/unavailable, deferring to main thread ({sw.ElapsedMilliseconds}ms)");
                    Debug.Log($"[RoseCache]     Fallback path: CLI={cliAvailable}, GPU=pending, CPU=false, BC1→BC3={bc1PreFallback}");
                    return new TextureCompressionResult
                    {
                        Stage = TextureCompressionStage.NeedsGpu,
                        MipData = null,
                        ActualCompressonatorFormat = cliFormat,
                        ActualVeldridFormat = veldridFormat,
                        SourceTag = "NeedsGpu",
                        DurationMs = sw.ElapsedMilliseconds,
                        BC1FallbackApplied = bc1PreFallback,
                    };
                }
                else
                {
                    // 3순위: CPU 폴백 (BCnEncoder.NET). BC1 미지원 시 내부에서 BC3로 전환.
                    if (plan.GenerateMipmaps)
                    {
                        var mipChain = GenerateMipChain(rgbaData, width, height);
                        mipData = new byte[mipChain.Count][];
                        string mipFormat = cliFormat;
                        for (int i = 0; i < mipChain.Count; i++)
                        {
                            var (mipRgba, mw, mh) = mipChain[i];
                            var cpuResult = CompressWithCpuFallback(mipRgba, mw, mh, mipFormat, plan.IsSrgb, out var actualFormat);
                            if (actualFormat != mipFormat)
                            {
                                // BC1→BC3 전환이 중간에 발생하면 이후 모든 mip을 동일 포맷으로.
                                mipFormat = actualFormat;
                                bc1RuntimeFallback = true;
                            }
                            mipData[i] = cpuResult[0];
                        }
                        cliFormat = mipFormat;
                    }
                    else
                    {
                        mipData = CompressWithCpuFallback(rgbaData, width, height, cliFormat, plan.IsSrgb, out var actualFormat);
                        if (actualFormat != cliFormat)
                        {
                            cliFormat = actualFormat;
                            bc1RuntimeFallback = true;
                        }
                    }
                    sourceTag = "CPU";
                }

                // CPU 폴백에서 BC1 → BC3 전환이 발생한 경우 Veldrid 포맷도 일치시켜야 한다.
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
                bool cliAvailableFinal = _compressonatorCliPath != null && _compressonatorCliPath != "";
                Debug.Log($"[RoseCache]     BC compress done via {sourceTag} ({cliFormat}, q={cliQuality:0.0##}): {mipData!.Length} mips, {sw.ElapsedMilliseconds}ms");
                Debug.Log($"[RoseCache]     Fallback path: CLI={cliAvailableFinal}, GPU=false, CPU={(sourceTag == "CPU")}, BC1→BC3={(bc1PreFallback || bc1RuntimeFallback)}");

                return new TextureCompressionResult
                {
                    Stage = TextureCompressionStage.Completed,
                    MipData = mipData,
                    ActualCompressonatorFormat = cliFormat,
                    ActualVeldridFormat = veldridFormat,
                    SourceTag = sourceTag,
                    DurationMs = sw.ElapsedMilliseconds,
                    BC1FallbackApplied = bc1PreFallback || bc1RuntimeFallback,
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] BC compression failed, falling back to uncompressed: {ex.Message}");
                return new TextureCompressionResult
                {
                    Stage = TextureCompressionStage.Failed,
                    MipData = new[] { rgbaData },
                    ActualCompressonatorFormat = "",
                    ActualVeldridFormat = Veldrid.PixelFormat.R8_G8_B8_A8_UNorm,
                    SourceTag = "Failed",
                    Error = ex,
                };
            }
        }

        /// <summary>
        /// 메인 전용. 백그라운드 Stage == NeedsGpu 일 때 GPU 경로를 실행하여 mipData/format을
        /// 마무리한다. Completed/Uncompressed/Failed는 그대로 통과시킨다.
        /// ThreadGuard 위반 또는 GPU 실패 시 CPU 폴백으로 재시도하여 어떤 경우에도 유효한 결과를 반환.
        /// </summary>
        public static (byte[][] mipData, Veldrid.PixelFormat format) FinalizeTextureOnMain(
            TextureCompressionPlan plan, TextureCompressionResult result,
            byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("RoseCache.FinalizeTextureOnMain"))
            {
                Debug.LogWarning("[RoseCache] FinalizeTextureOnMain called off main thread — falling back to uncompressed");
                return (new[] { rgbaData }, Veldrid.PixelFormat.R8_G8_B8_A8_UNorm);
            }

            switch (result.Stage)
            {
                case TextureCompressionStage.Completed:
                    // CompressTextureBackground가 모든 Completed 경로에서 ActualVeldridFormat을 설정한다.
                    return (result.MipData!, result.ActualVeldridFormat);

                case TextureCompressionStage.Uncompressed:
                    // Background에서 이미 DontUseCompressTexture를 반영하여 ActualVeldridFormat을 확정했다.
                    return (result.MipData!, result.ActualVeldridFormat);

                case TextureCompressionStage.Failed:
                    // Background 단계에서 이미 폴백 데이터를 준비해 둠.
                    return (result.MipData ?? new[] { rgbaData }, Veldrid.PixelFormat.R8_G8_B8_A8_UNorm);

                case TextureCompressionStage.NeedsGpu:
                {
                    // GPU 경로 실행.
                    var cliFormat = string.IsNullOrEmpty(result.ActualCompressonatorFormat)
                        ? plan.CompressonatorFormat!
                        : result.ActualCompressonatorFormat;

                    if (_gpuCompressor == null)
                    {
                        // GPU 리소스 없음 → CPU 폴백으로 전환.
                        Debug.LogWarning("[RoseCache] NeedsGpu stage but _gpuCompressor is null — falling back to CPU");
                        return FinalizeViaCpuFallback(plan, rgbaData, width, height, cliFormat);
                    }

                    var sw = Stopwatch.StartNew();
                    byte[][] mipData;
                    if (plan.GenerateMipmaps)
                    {
                        var mipChain = _gpuCompressor.GenerateMipmapsGPU(rgbaData, width, height);
                        if (mipChain.Length == 0)
                        {
                            Debug.LogWarning("[RoseCache] GenerateMipmapsGPU returned empty — falling back to CPU");
                            return FinalizeViaCpuFallback(plan, rgbaData, width, height, cliFormat);
                        }
                        mipData = new byte[mipChain.Length][];
                        int mw = width, mh = height;
                        for (int i = 0; i < mipChain.Length; i++)
                        {
                            mipData[i] = cliFormat == "BC5"
                                ? _gpuCompressor.CompressBC5(mipChain[i], mw, mh)
                                : _gpuCompressor.CompressBC7(mipChain[i], mw, mh);
                            if (mipData[i].Length == 0)
                            {
                                Debug.LogWarning($"[RoseCache] GPU compress mip {i} returned empty — falling back to CPU");
                                return FinalizeViaCpuFallback(plan, rgbaData, width, height, cliFormat);
                            }
                            mw = Math.Max(1, mw / 2);
                            mh = Math.Max(1, mh / 2);
                        }
                    }
                    else
                    {
                        mipData = new byte[1][];
                        mipData[0] = cliFormat == "BC5"
                            ? _gpuCompressor.CompressBC5(rgbaData, width, height)
                            : _gpuCompressor.CompressBC7(rgbaData, width, height);
                        if (mipData[0].Length == 0)
                        {
                            Debug.LogWarning("[RoseCache] GPU compress returned empty — falling back to CPU");
                            return FinalizeViaCpuFallback(plan, rgbaData, width, height, cliFormat);
                        }
                    }

                    sw.Stop();
                    var veldridFormat = cliFormat switch
                    {
                        "BC5" => Veldrid.PixelFormat.BC5_UNorm,
                        "BC7" => Veldrid.PixelFormat.BC7_UNorm,
                        _     => plan.InitialVeldridFormat,
                    };
                    bool cliAvailable = _compressonatorCliPath != null && _compressonatorCliPath != "";
                    Debug.Log($"[RoseCache]     BC compress done via GPU ({cliFormat}, q={plan.CompressonatorQuality:0.0##}): {mipData.Length} mips, {sw.ElapsedMilliseconds}ms");
                    Debug.Log($"[RoseCache]     Fallback path: CLI={cliAvailable}, GPU=true, CPU=false, BC1→BC3={result.BC1FallbackApplied}");
                    return (mipData, veldridFormat);
                }

                default:
                    return (new[] { rgbaData }, Veldrid.PixelFormat.R8_G8_B8_A8_UNorm);
            }
        }

        /// <summary>
        /// Finalize 단계에서 GPU 경로가 실패했을 때 CPU BCnEncoder로 재시도한다. 메인 전용.
        /// </summary>
        private static (byte[][] mipData, Veldrid.PixelFormat format) FinalizeViaCpuFallback(
            TextureCompressionPlan plan, byte[] rgbaData, int width, int height, string cliFormat)
        {
            try
            {
                byte[][] mipData;
                string mipFormat = cliFormat;
                bool bc1RuntimeFallback = false;

                if (plan.GenerateMipmaps)
                {
                    var mipChain = GenerateMipChain(rgbaData, width, height);
                    mipData = new byte[mipChain.Count][];
                    for (int i = 0; i < mipChain.Count; i++)
                    {
                        var (mipRgba, mw, mh) = mipChain[i];
                        var cpuResult = CompressWithCpuFallback(mipRgba, mw, mh, mipFormat, plan.IsSrgb, out var actualFormat);
                        if (actualFormat != mipFormat)
                        {
                            mipFormat = actualFormat;
                            bc1RuntimeFallback = true;
                        }
                        mipData[i] = cpuResult[0];
                    }
                }
                else
                {
                    mipData = CompressWithCpuFallback(rgbaData, width, height, mipFormat, plan.IsSrgb, out var actualFormat);
                    if (actualFormat != mipFormat)
                    {
                        mipFormat = actualFormat;
                        bc1RuntimeFallback = true;
                    }
                }

                var veldridFormat = mipFormat switch
                {
                    "BC3" => Veldrid.PixelFormat.BC3_UNorm,
                    "BC1" => Veldrid.PixelFormat.BC1_Rgba_UNorm,
                    "BC5" => Veldrid.PixelFormat.BC5_UNorm,
                    "BC7" => Veldrid.PixelFormat.BC7_UNorm,
                    _     => plan.InitialVeldridFormat,
                };
                if (bc1RuntimeFallback)
                    Debug.LogWarning($"[RoseCache] Format fallback applied → Veldrid format = {veldridFormat}");

                bool cliAvailable = _compressonatorCliPath != null && _compressonatorCliPath != "";
                // 로그 형식 호환성 유지: "BC compress done via CPU (<fmt>, q=<q>): <n> mips, <ms>ms"
                // 기존 grep과 호환되도록 duration은 0으로 표기 (GPU→CPU 전환 분기의 별도 측정값 없음).
                Debug.Log($"[RoseCache]     BC compress done via CPU ({mipFormat}, q={plan.CompressonatorQuality:0.0##}): {mipData.Length} mips, 0ms");
                Debug.Log($"[RoseCache]     Fallback path: CLI={cliAvailable}, GPU=false, CPU=true, BC1→BC3={bc1RuntimeFallback}");
                return (mipData, veldridFormat);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoseCache] CPU fallback also failed, using uncompressed: {ex.Message}");
                return (new[] { rgbaData }, Veldrid.PixelFormat.R8_G8_B8_A8_UNorm);
            }
        }

        // ─── HDR (BC6H) Compression ─────────────────────────────

        /// <summary>
        /// 순수 함수. HDR 텍스처의 압축 플랜을 계산한다.
        /// 백그라운드 호출 안전.
        /// </summary>
        public static HdrCompressionPlan PlanHdrCompression(RoseMetadata meta, Texture2D texture)
        {
            var textureType = GetMetaString(meta, "texture_type", "HDR");
            var quality = GetMetaString(meta, "quality", "High");
            var isSrgb = GetMetaBool(meta, "srgb", false);
            var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);

            bool useBc6h = !resolution.IsUncompressed && !RoseConfig.DontUseCompressTexture;
            return new HdrCompressionPlan(
                UseBc6h: useBc6h,
                Width: texture.width,
                Height: texture.height);
        }

        /// <summary>
        /// 백그라운드 호출 가능. BC6H CPU 인코딩 또는 Half float 변환을 수행한다.
        /// _gpuCompressor 등 메인 전용 리소스에 접근하지 않는다.
        /// </summary>
        public static HdrCompressionResult EncodeHdrBackground(
            HdrCompressionPlan plan, float[] hdrPixelData, int width, int height)
        {
            _ = width; _ = height; // plan에 이미 포함됨 — 시그니처 일관성을 위해 파라미터만 유지
            if (plan.UseBc6h)
            {
                var sw = Stopwatch.StartNew();
                var bc6hData = Bc6hEncoder.Encode(hdrPixelData, plan.Width, plan.Height);
                sw.Stop();
                Debug.Log($"[RoseCache]     BC6H compress {plan.Width}x{plan.Height} → {bc6hData.Length} bytes ({sw.ElapsedMilliseconds}ms)");
                return new HdrCompressionResult
                {
                    Data = bc6hData,
                    FormatInt = FormatBC6H_UFloat,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }
            else
            {
                var swSw = Stopwatch.StartNew();
                var halfBytes = Texture2D.ConvertFloatToHalfBytes(hdrPixelData);
                swSw.Stop();
                return new HdrCompressionResult
                {
                    Data = halfBytes,
                    FormatInt = (int)Veldrid.PixelFormat.R16_G16_B16_A16_Float,
                    DurationMs = swSw.ElapsedMilliseconds,
                };
            }
        }

        /// <summary>
        /// 메인 전용. HDR 경로는 모두 CPU 연산이라 실질적으로 passthrough.
        /// Phase 3에서 warmup handoff가 메인 스레드 진입 후 이 메서드를 호출하여 일관된
        /// API 경계를 유지할 수 있도록 준비해 둔 메서드.
        /// </summary>
        public static HdrCompressionResult FinalizeHdrOnMain(HdrCompressionPlan plan, HdrCompressionResult result)
        {
            _ = plan;
            if (!ThreadGuard.CheckMainThread("RoseCache.FinalizeHdrOnMain"))
            {
                Debug.LogWarning("[RoseCache] FinalizeHdrOnMain called off main thread — passing through result");
            }
            return result;
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
                // Resolve and cache CLI path (platform-specific).
                // 다중 스레드가 경합할 수 있으므로 Interlocked.CompareExchange로 첫 할당 승자 선택.
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
                    string resolvedCli;
                    string resolvedLib;
                    bool found;
                    if (File.Exists(cliPath))
                    {
                        resolvedCli = cliPath;
                        resolvedLib = Path.Combine(ProjectContext.EngineRoot, "externalTools", "compressonatorcli", platformDir, "pkglibs");
                        found = true;
                    }
                    else
                    {
                        resolvedCli = "";
                        resolvedLib = "";
                        found = false;
                    }

                    // 승자만 로그를 남긴다. 이미 다른 스레드가 세팅했으면 그대로 사용.
                    var prevCli = Interlocked.CompareExchange(ref _compressonatorCliPath, resolvedCli, null);
                    if (prevCli == null)
                    {
                        // _compressonatorLibPath는 _compressonatorCliPath 승자만 설정하도록 순서 보장.
                        Interlocked.CompareExchange(ref _compressonatorLibPath, resolvedLib, null);
                        if (found)
                            Debug.Log($"[RoseCache] Compressonator CLI found: {resolvedCli}");
                        else
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
            // 백그라운드 스레드도 호출할 수 있으므로 Volatile.Read로 상태 판독.
            int bc1State = Volatile.Read(ref _bc1CpuSupportedState);
            if (format == "BC1" && bc1State == Bc1StateUnsupported)
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
                if (format == "BC1")
                {
                    // BC1 정상 동작 확인: Unknown → Supported만 승자가 세팅. 이미 Unsupported면 덮지 않음.
                    Interlocked.CompareExchange(ref _bc1CpuSupportedState, Bc1StateSupported, Bc1StateUnknown);
                }
                actualFormat = format;
                return result;
            }
            catch (Exception ex) when (format == "BC1")
            {
                // BC1 예외 확인: Unknown → Unsupported 1회-승자. 이미 Supported/Unsupported 상태면 그대로.
                Interlocked.CompareExchange(ref _bc1CpuSupportedState, Bc1StateUnsupported, Bc1StateUnknown);
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

            // Phase 1 이후 WriteTexture 불변식:
            //   - tex._mipData != null: 이미 압축된 상태 → 그대로 직렬화 (StoreTexture/StoreTexturePrecompressed 경로).
            //   - tex._mipData == null && tex._pixelData != null: Material 내부 텍스처가 이 경로로 진입한다
            //     (WriteMaterial → WriteTexture). Material 내부 텍스처는 RoseMetadata를 가지지 않으므로
            //     호출자가 제공한 (textureType, quality, isSrgb, generateMipmaps)로 Plan을 구성한 뒤
            //     Background + FinalizeOnMain 합성 래퍼로 압축하여 기존 동작과 바이트 단위 동일성을 유지한다.
            //   - 둘 다 null: 빈 텍스처로 기록.
            byte[][] mipData;
            Veldrid.PixelFormat format;

            if (tex._mipData != null)
            {
                mipData = tex._mipData;
                format = tex._gpuFormat;
            }
            else if (tex._pixelData != null)
            {
                (mipData, format) = CompressTextureSync(
                    tex._pixelData, tex.width, tex.height, textureType, quality, isSrgb, generateMipmaps);

                // 압축 결과를 텍스처 객체에도 반영하여, 리임포트 후 GPU 업로드가
                // 캐시 로드와 동일한 경로(BC 압축)를 사용하도록 보장한다.
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
                var bc6hTex = new Texture2D(width, height, hdrData);
                // 원본 저장 포맷이 BC6H였음을 Inspector 프리뷰 라벨이 알 수 있도록 플래그 설정.
                bc6hTex._storedAsBC6H = true;
                return bc6hTex;
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
