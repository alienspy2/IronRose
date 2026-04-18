// ------------------------------------------------------------
// @file    TextureWarmupTypes.cs
// @brief   Warmup/Reimport 텍스처 파이프라인의 2단계 분리 (Plan / Background / Finalize)
//          에서 공유되는 불변 값 타입들. RoseCache, AssetWarmupManager, AssetDatabase가
//          함께 참조한다. Phase 2/3: WarmupHandoff 클래스 추가 — 백그라운드 Task에서
//          메인 스레드로 넘기는 단일 불변 객체.
// @deps    Veldrid (PixelFormat enum), RoseEngine (Texture2D), IronRose.AssetPipeline (RoseMetadata)
// @exports
//   enum TextureCompressionStage
//     Completed      — mipData 완전 (CLI 또는 CPU fallback 성공)
//     NeedsGpu       — GPU 경로 마무리 필요 (BC7/BC5 전용)
//     Uncompressed   — NoCompression 또는 RoseConfig.DontUseCompressTexture
//     Failed         — 예외 — 호출자가 uncompressed fallback 결정
//   record struct TextureCompressionPlan                     — LDR 텍스처 압축 계획 (순수 값)
//     TextureType / Quality / IsSrgb / GenerateMipmaps       — 메타에서 추출된 파라미터
//     CompressonatorFormat: string?                          — CLI -fd 값. null=Uncompressed
//     CompressonatorQuality: double                          — CLI -Quality 값
//     InitialVeldridFormat: Veldrid.PixelFormat              — Resolver가 결정한 GPU 포맷
//     IsUncompressed: bool                                   — NoCompression 여부
//     GpuSupported: bool                                     — BC7/BC5만 true (GPU 폴백 가능)
//   class TextureCompressionResult                           — 백그라운드 압축 결과
//     Stage / MipData / ActualCompressonatorFormat / ActualVeldridFormat
//     SourceTag: string                                      — "CLI"/"CPU"/"UncompressedLDR"
//     DurationMs / BC1FallbackApplied / Error
//   record struct HdrCompressionPlan                         — HDR 텍스처 압축 계획
//   class HdrCompressionResult                               — HDR 압축 결과 (BC6H 또는 Half float)
//   class WarmupHandoff                                      — Phase 2/3: 백그라운드 → 메인 전달용 불변 객체
//     static Skip(path, reason)                              — 처리 대상 아님 (e.g., importerType != TextureImporter)
//     static Failed(path, ex)                                — 백그라운드 예외 발생
//     static ForLdr(path, meta, tex, rgba, plan, result)     — LDR 경로 준비 완료
//     static ForHdr(path, meta, tex, plan, result)           — HDR 경로 준비 완료 (현재 경로에서는 미사용/예비)
//     static Deferred(path, reason)                          — 메인에서 동기 EnsureDiskCached로 폴백
// @note    모든 타입은 백그라운드 스레드에서 생성/전달 가능한 불변 객체로 설계됨.
//          Texture2D 레퍼런스를 들고 있으나 Veldrid 리소스는 메인 업로드 전까지 null 이므로
//          백그라운드 전달 자체는 안전 (메인이 소비할 때까지 해당 객체 수정 금지).
//          RoseMetadata는 참조 타입이지만 Save/OnSaved는 이미 snapshot-then-invoke로 thread-safe.
//          SourceTag는 기존 RoseCache 로그 메시지와 호환되는 문자열("CLI"/"CPU"/"GPU") 유지.
//          WarmupHandoff.IsDeferred 는 "메인에서 기존 동기 EnsureDiskCached 로 폴백" 신호.
//          HDR(BC6H) 경로는 현재 RoseCache 에 StoreTextureHdrPrecompressed 가 없어 Deferred 로 처리한다.
// ------------------------------------------------------------
using System;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// 백그라운드 압축 단계의 결과 상태.
    /// </summary>
    public enum TextureCompressionStage
    {
        /// <summary>mipData가 이미 준비됨 (CLI 또는 CPU fallback 성공).</summary>
        Completed,
        /// <summary>GPU 경로에서 마무리 필요 (BC7/BC5만 해당). MipData는 null.</summary>
        NeedsGpu,
        /// <summary>NoCompression 또는 RoseConfig.DontUseCompressTexture.</summary>
        Uncompressed,
        /// <summary>예외 발생 — 호출자가 uncompressed fallback 결정.</summary>
        Failed,
    }

    /// <summary>
    /// LDR 텍스처 압축 계획. 메타(texture_type/quality/srgb/generate_mipmaps)로부터
    /// TextureCompressionFormatResolver 결과를 결정하여 저장하는 순수 값 타입.
    /// 백그라운드 스레드에서 생성/전달 가능.
    /// </summary>
    public readonly record struct TextureCompressionPlan(
        string TextureType,
        string Quality,
        bool IsSrgb,
        bool GenerateMipmaps,
        string? CompressonatorFormat,
        double CompressonatorQuality,
        Veldrid.PixelFormat InitialVeldridFormat,
        bool IsUncompressed,
        bool GpuSupported);

    /// <summary>
    /// 백그라운드 압축 단계의 결과 객체. Stage에 따라 MipData가 설정되거나 null.
    /// </summary>
    public sealed class TextureCompressionResult
    {
        public TextureCompressionStage Stage { get; init; }

        /// <summary>
        /// Stage == Completed || Uncompressed일 때는 완전 (null 아님).
        /// Stage == NeedsGpu일 때는 null.
        /// Stage == Failed일 때는 uncompressed fallback 데이터(new[] { rgba })가 들어감.
        /// </summary>
        public byte[][]? MipData { get; init; }

        /// <summary>
        /// 실제로 인코딩된 Compressonator 포맷 문자열.
        /// BC1→BC3 런타임 폴백이 발생하면 "BC3"로 세팅됨.
        /// NeedsGpu일 때는 Plan.CompressonatorFormat과 동일.
        /// </summary>
        public string ActualCompressonatorFormat { get; init; } = "";

        /// <summary>
        /// 실제 Veldrid 포맷. BC1→BC3 폴백 시 BC3_UNorm으로 재계산됨.
        /// NeedsGpu일 때는 Plan.InitialVeldridFormat과 동일.
        /// </summary>
        public Veldrid.PixelFormat ActualVeldridFormat { get; init; }

        /// <summary>로그용 태그: "CLI" / "CPU" / "UncompressedLDR" / "NeedsGpu".</summary>
        public string SourceTag { get; init; } = "";

        public long DurationMs { get; init; }

        /// <summary>BC1 요청이 BC3로 전환된 경우 true.</summary>
        public bool BC1FallbackApplied { get; init; }

        /// <summary>Stage == Failed일 때 원인 예외.</summary>
        public Exception? Error { get; init; }
    }

    /// <summary>
    /// HDR(BC6H) 텍스처 압축 계획. 현재 BC6H 경로는 CPU 전용(Bc6hEncoder)이므로
    /// Plan은 실질적으로 "압축할지 / 그대로 Half float로 둘지" 결정만 담는다.
    /// </summary>
    public readonly record struct HdrCompressionPlan(
        bool UseBc6h,
        int Width,
        int Height);

    /// <summary>
    /// HDR 압축 결과. BC6H 경로면 FormatInt == 1000 (FormatBC6H_UFloat),
    /// Half float 경로면 FormatInt == (int)Veldrid.PixelFormat.R16_G16_B16_A16_Float.
    /// </summary>
    public sealed class HdrCompressionResult
    {
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public int FormatInt { get; init; }
        public long DurationMs { get; init; }
    }

    /// <summary>
    /// Warmup 파이프라인에서 백그라운드 Task.Run이 메인 프레임으로 전달하는 단일 불변 객체.
    /// PrepareTextureWarmupBackground가 채워서 반환하고, FinalizeTextureWarmupOnMain이 소비한다.
    ///
    /// 팩토리 메서드만 사용하여 상태 일관성을 보장한다:
    ///   Skip    — 처리 대상이 아닌 에셋 (e.g., importerType 불일치, DontUseCache).
    ///   Failed  — 백그라운드 예외 (메인에서 LogError 후 스킵).
    ///   ForLdr  — LDR 압축 완료 (Plan/Result + rgba 포함).
    ///   ForHdr  — HDR 압축 완료 (현재는 Deferred 로 대체되어 사용 빈도 낮음).
    ///   Deferred — HDR 등 현 파이프라인이 미지원인 경로. 메인에서 기존 동기 EnsureDiskCached 로 폴백.
    /// </summary>
    public sealed class WarmupHandoff
    {
        public string AssetPath { get; init; } = "";
        public RoseMetadata? Meta { get; init; }
        public Texture2D? Texture { get; init; }
        public byte[]? Rgba { get; init; }
        public TextureCompressionPlan Plan { get; init; }
        public TextureCompressionResult? Result { get; init; }
        public HdrCompressionPlan HdrPlan { get; init; }
        public HdrCompressionResult? HdrResult { get; init; }
        public bool IsHdr { get; init; }
        public bool IsSkip { get; init; }
        public bool IsDeferred { get; init; }
        public string? SkipReason { get; init; }
        public Exception? Error { get; init; }

        public static WarmupHandoff Skip(string path, string reason) =>
            new() { AssetPath = path, IsSkip = true, SkipReason = reason };

        public static WarmupHandoff Failed(string path, Exception ex) =>
            new() { AssetPath = path, Error = ex };

        public static WarmupHandoff ForLdr(
            string path, RoseMetadata meta, Texture2D tex, byte[] rgba,
            TextureCompressionPlan plan, TextureCompressionResult result) =>
            new()
            {
                AssetPath = path,
                Meta = meta,
                Texture = tex,
                Rgba = rgba,
                Plan = plan,
                Result = result,
            };

        public static WarmupHandoff ForHdr(
            string path, RoseMetadata meta, Texture2D tex,
            HdrCompressionPlan plan, HdrCompressionResult result) =>
            new()
            {
                AssetPath = path,
                Meta = meta,
                Texture = tex,
                HdrPlan = plan,
                HdrResult = result,
                IsHdr = true,
            };

        public static WarmupHandoff Deferred(string path, string reason) =>
            new() { AssetPath = path, IsDeferred = true, SkipReason = reason };
    }
}
