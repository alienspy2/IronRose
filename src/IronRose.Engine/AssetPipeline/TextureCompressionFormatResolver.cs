// ------------------------------------------------------------
// @file    TextureCompressionFormatResolver.cs
// @brief   texture_type + quality + (HDR/sRGB) 조합을 받아 GPU 업로드 포맷,
//          Compressonator CLI 포맷 문자열, 프리뷰 라벨, bits-per-pixel을
//          결정하는 순수 함수 Resolver.
// @deps    Veldrid (PixelFormat enum)
// @exports
//   struct TextureFormatResolution
//     VeldridFormat: Veldrid.PixelFormat                 — GPU 업로드용 픽셀 포맷
//     BC6HVirtualId: int                                 — BC6H 경로에서만 1000, 그 외 0
//     CompressonatorFormat: string?                      — CLI -fd 값. null이면 NoCompression
//     DisplayLabel: string                               — UI 프리뷰 라벨 (예: "BC7 (8 bpp)")
//     BitsPerPixel: int                                  — 1픽셀당 비트 수 (참고값)
//     IsUncompressed: bool                               — NoCompression 여부
//     IsHdr: bool                                        — HDR 경로 여부 (HDR/Panoramic)
//   class TextureCompressionFormatResolver
//     const BC6HVirtualId: int = 1000                    — RoseCache.FormatBC6H_UFloat과 일치
//     static AllTextureTypes: IReadOnlyList<string>      — 지원되는 texture_type 목록
//     static AllQualities: IReadOnlyList<string>         — 지원되는 quality 목록
//     static Resolve(string, string, bool): TextureFormatResolution
//     static IsHdrType(string): bool                     — HDR 또는 Panoramic
//     static GetCompressonatorQuality(string, string): double
//                                                        — Compressonator CLI -Quality 인자 값
// @note    알 수 없는 textureType은 "Color", 알 수 없는 quality는 "High"로 fallback.
//          NormalMap/HDR/Panoramic은 sRGB 파라미터를 무시한다 (항상 linear).
//          현재 엔진 렌더 파이프라인은 sRGB variant 텍스처 포맷을 지원하지 않는다:
//          셰이더는 텍스처 raw bytes를 직접 사용하고, blit.frag의 pow(color, 1/2.2)로
//          최종 감마 보정을 한다. BC7_UNorm_SRgb 등 sRGB variant를 사용하면 GPU가
//          자동 sRGB→linear 변환을 하여 감마 보정이 이중으로 걸려 이미지가 어두워진다.
//          따라서 `isSrgb` 매개변수는 받아두되 VeldridFormat은 항상 UNorm variant를
//          반환한다. 엔진이 proper sRGB 파이프라인으로 전환되면 이 제한을 해제하고
//          sRGB variant를 돌려주면 된다. (버그 리포트 2026-04-17)
//          Phase 1 단독 적용 시점에는 파이프라인이 아직 이 Resolver를 사용하지 않는다
//          (Phase 3에서 RoseCache 통합). 그 전까지는 순수 함수 유닛으로만 존재.
// ------------------------------------------------------------
using System.Collections.Generic;

namespace IronRose.AssetPipeline
{
    public readonly record struct TextureFormatResolution(
        Veldrid.PixelFormat VeldridFormat,
        int BC6HVirtualId,
        string? CompressonatorFormat,
        string DisplayLabel,
        int BitsPerPixel,
        bool IsUncompressed,
        bool IsHdr);

    public static class TextureCompressionFormatResolver
    {
        /// <summary>
        /// BC6H 가상 포맷 ID. Veldrid.PixelFormat에는 BC6H가 없어서 캐시 헤더에 쓰는 sentinel.
        /// RoseCache.cs의 FormatBC6H_UFloat (= 1000)과 반드시 일치해야 한다.
        /// </summary>
        public const int BC6HVirtualId = 1000;

        private static readonly string[] _allTextureTypes =
        {
            "Color", "ColorWithAlpha", "NormalMap", "Sprite", "HDR", "Panoramic",
        };

        private static readonly string[] _allQualities =
        {
            "High", "Medium", "Low", "NoCompression",
        };

        public static IReadOnlyList<string> AllTextureTypes => _allTextureTypes;
        public static IReadOnlyList<string> AllQualities => _allQualities;

        /// <summary>
        /// textureType이 HDR 경로(HDR/Panoramic)에 속하는지 여부.
        /// </summary>
        public static bool IsHdrType(string textureType)
            => textureType == "HDR" || textureType == "Panoramic";

        /// <summary>
        /// texture_type + quality + sRGB 조합으로 최종 포맷을 결정한다.
        /// - HDR 경로: High/Medium/Low → BC6H, NoCompression → RGBA16F.
        /// - LDR 경로: 매핑표에 따라 BC7/BC3/BC1/BC5 선택 또는 NoCompression→RGBA8.
        /// - sRGB 파라미터는 현재 VeldridFormat에 반영하지 않는다 (파일 상단 @note 참조).
        ///   엔진이 proper sRGB 파이프라인으로 전환되면 UNorm↔UNorm_SRgb variant를
        ///   여기서 선택하도록 확장한다.
        /// 알 수 없는 textureType은 "Color", 알 수 없는 quality는 "High"로 fallback.
        /// </summary>
        public static TextureFormatResolution Resolve(string textureType, string quality, bool isSrgb)
        {
            // 알 수 없는 값 fallback
            var type = textureType;
            if (System.Array.IndexOf(_allTextureTypes, type) < 0)
                type = "Color";

            var q = quality;
            if (System.Array.IndexOf(_allQualities, q) < 0)
                q = "High";

            // HDR 경로
            if (IsHdrType(type))
            {
                if (q == "NoCompression")
                {
                    return new TextureFormatResolution(
                        VeldridFormat: Veldrid.PixelFormat.R16_G16_B16_A16_Float,
                        BC6HVirtualId: 0,
                        CompressonatorFormat: null,
                        DisplayLabel: "RGBA16F (64 bpp, HDR Uncompressed)",
                        BitsPerPixel: 64,
                        IsUncompressed: true,
                        IsHdr: true);
                }

                // High/Medium/Low → BC6H
                // Veldrid.PixelFormat에는 BC6H가 없으므로, 실제 포맷 필드는 의미가 없고
                // BC6HVirtualId로 구분한다. 안전하게 R16_G16_B16_A16_Float를 채워 둔다.
                return new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.R16_G16_B16_A16_Float,
                    BC6HVirtualId: BC6HVirtualId,
                    CompressonatorFormat: "BC6H",
                    DisplayLabel: "BC6H (8 bpp, HDR)",
                    BitsPerPixel: 8,
                    IsUncompressed: false,
                    IsHdr: true);
            }

            // LDR 경로
            // 현재 렌더 파이프라인 제약으로 sRGB variant는 사용하지 않는다.
            // isSrgb는 매개변수로는 받되 VeldridFormat에는 반영하지 않는다. 파일 상단 @note 참조.
            _ = isSrgb;

            // NoCompression: RGBA8 직업로드
            if (q == "NoCompression")
            {
                return new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.R8_G8_B8_A8_UNorm,
                    BC6HVirtualId: 0,
                    CompressonatorFormat: null,
                    DisplayLabel: "R8G8B8A8 (32 bpp, Uncompressed)",
                    BitsPerPixel: 32,
                    IsUncompressed: true,
                    IsHdr: false);
            }

            // 압축 경로
            // (textureType, quality) → Compressonator format 문자열
            var compFormat = type switch
            {
                "Color" => q switch
                {
                    "Low" => "BC1",
                    _ => "BC7", // High, Medium
                },
                "ColorWithAlpha" => q switch
                {
                    "Low" => "BC3",
                    _ => "BC7", // High, Medium
                },
                "NormalMap" => "BC5", // 모든 quality
                "Sprite" => q switch
                {
                    "Low" => "BC3",
                    _ => "BC7", // High, Medium
                },
                _ => "BC7",
            };

            return compFormat switch
            {
                "BC7" => new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.BC7_UNorm,
                    BC6HVirtualId: 0,
                    CompressonatorFormat: "BC7",
                    DisplayLabel: "BC7 (8 bpp)",
                    BitsPerPixel: 8,
                    IsUncompressed: false,
                    IsHdr: false),
                "BC3" => new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.BC3_UNorm,
                    BC6HVirtualId: 0,
                    CompressonatorFormat: "BC3",
                    DisplayLabel: "BC3 (8 bpp)",
                    BitsPerPixel: 8,
                    IsUncompressed: false,
                    IsHdr: false),
                "BC1" => new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.BC1_Rgba_UNorm,
                    BC6HVirtualId: 0,
                    CompressonatorFormat: "BC1",
                    DisplayLabel: "BC1 (4 bpp)",
                    BitsPerPixel: 4,
                    IsUncompressed: false,
                    IsHdr: false),
                "BC5" => new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.BC5_UNorm,
                    BC6HVirtualId: 0,
                    CompressonatorFormat: "BC5",
                    DisplayLabel: "BC5 (8 bpp)",
                    BitsPerPixel: 8,
                    IsUncompressed: false,
                    IsHdr: false),
                _ => new TextureFormatResolution(
                    VeldridFormat: Veldrid.PixelFormat.BC7_UNorm,
                    BC6HVirtualId: 0,
                    CompressonatorFormat: "BC7",
                    DisplayLabel: "BC7 (8 bpp)",
                    BitsPerPixel: 8,
                    IsUncompressed: false,
                    IsHdr: false),
            };
        }

        /// <summary>
        /// Compressonator CLI의 -Quality 인자값을 반환한다.
        /// BC7만 품질에 민감하며(High=1.0/Medium=0.6), 그 외 포맷은 1.0 고정.
        /// </summary>
        public static double GetCompressonatorQuality(string compressonatorFormat, string quality)
        {
            if (compressonatorFormat == "BC7")
            {
                return quality switch
                {
                    "Medium" => 0.6,
                    _ => 1.0, // High (Low는 BC7 매핑되지 않음)
                };
            }
            return 1.0;
        }
    }
}
