// ------------------------------------------------------------
// @file    TextureWarmupTypes.cs
// @brief   Warmup/Reimport 텍스처 파이프라인의 2단계 분리 (Plan / Background / Finalize)
//          에서 공유되는 불변 값 타입들. RoseCache와 (향후 Phase 2/3에서) AssetWarmupManager,
//          AssetDatabase가 함께 참조한다.
// @deps    Veldrid (PixelFormat enum)
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
// @note    모든 타입은 백그라운드 스레드에서 생성/전달 가능한 불변 객체로 설계됨.
//          Texture2D, RoseMetadata 등 mutable 엔진 객체 참조는 포함하지 않는다.
//          SourceTag는 기존 RoseCache 로그 메시지와 호환되는 문자열("CLI"/"CPU"/"GPU") 유지.
// ------------------------------------------------------------
using System;

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
}
