// ------------------------------------------------------------
// @file    TextureMetadataMigration.cs
// @brief   .rose TOML 로드 직후 TextureImporter 섹션의 구버전 키를 정리하는
//          마이그레이션 로직. 특히 'compression' 키를 제거하고 'none'인 경우
//          quality = "NoCompression"으로 이관한다.
// @deps    Tomlyn.Model (TomlTable)
// @exports
//   class TextureMetadataMigration (internal static)
//     static Apply(TomlTable importer): bool
//       — 변경이 있었으면 true. 호출 측은 true일 때만 Save를 수행한다.
// @note    TextureImporter가 아닌 섹션은 즉시 false 반환 (no-op).
//          texture_type 누락 처리는 이 함수 범위 밖 (LoadOrCreate/Inferrer 몫).
//          compression="none" + quality="NoCompression"이 이미 있으면 quality는 건드리지 않음.
// ------------------------------------------------------------
using Tomlyn.Model;

namespace IronRose.AssetPipeline
{
    internal static class TextureMetadataMigration
    {
        /// <summary>
        /// TextureImporter 섹션의 구버전 키를 정리한다.
        /// - type != "TextureImporter" → no-op, false 반환.
        /// - compression == "none" → quality = "NoCompression" (기존 quality가 이미 NoCompression이면 스킵).
        /// - compression 기타 값 → 단순 제거. quality는 건드리지 않음.
        /// - 마지막에 compression 키 제거.
        /// 변경이 한 번이라도 발생하면 true를 반환한다.
        /// </summary>
        public static bool Apply(TomlTable importer)
        {
            if (importer == null) return false;

            if (!importer.TryGetValue("type", out var typeVal)
                || typeVal is not string typeStr
                || typeStr != "TextureImporter")
                return false;

            var changed = false;

            if (importer.TryGetValue("compression", out var compVal))
            {
                var compStr = compVal as string;

                if (compStr == "none")
                {
                    // quality = "NoCompression"으로 이관
                    var existingQuality = importer.TryGetValue("quality", out var qVal)
                        ? qVal as string
                        : null;
                    if (existingQuality != "NoCompression")
                    {
                        importer["quality"] = "NoCompression";
                        changed = true;
                    }
                }

                // 어떤 값이든 compression 키는 제거
                importer.Remove("compression");
                changed = true;
            }

            return changed;
        }
    }
}
