// ------------------------------------------------------------
// @file    PostProcessProfileImporter.cs
// @brief   .ppprofile (TOML) 파일을 PostProcessProfile로 임포트/익스포트한다.
//          각 TOML 섹션이 이펙트(Bloom, Tonemap 등)에 대응하며
//          enabled + 파라미터(float) 쌍으로 구성된다.
// @deps    IronRose.Engine/TomlConfig, RoseEngine (EditorDebug, PostProcessProfile, EffectOverride),
//          AssetPipeline/RoseMetadata
// @exports
//   class PostProcessProfileImporter
//     Import(string, RoseMetadata?): PostProcessProfile?        — .ppprofile 파일에서 로드
//     static Export(PostProcessProfile, string): void           — PostProcessProfile을 TOML로 저장
//     static WriteDefault(string): void                         — 기본 프로파일 생성
// @note    기존 로컬 ToFloat 메서드를 제거하고 TomlConfig.GetFloat()로 대체.
// ------------------------------------------------------------
using System;
using System.IO;
using RoseEngine;
using IronRose.Engine;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// .ppprofile (TOML) 파일을 PostProcessProfile로 임포트/익스포트.
    ///
    /// TOML 구조:
    /// [Bloom]
    /// enabled = true
    /// Threshold = 0.8
    /// "Soft Knee" = 0.5
    /// Intensity = 0.5
    ///
    /// [Tonemap]
    /// enabled = true
    /// Exposure = 1.5
    /// Saturation = 1.6
    /// Contrast = 1.0
    /// "White Point" = 10.0
    /// Gamma = 1.2
    /// </summary>
    public class PostProcessProfileImporter
    {
        public PostProcessProfile? Import(string path, RoseMetadata? meta = null)
        {
            if (!File.Exists(path))
            {
                EditorDebug.LogError($"[PostProcessProfileImporter] File not found: {path}");
                return null;
            }

            var config = TomlConfig.LoadFile(path, "[PostProcessProfileImporter]");
            if (config == null) return null;

            return ParseProfile(config, path);
        }

        private static PostProcessProfile ParseProfile(TomlConfig config, string path)
        {
            var profile = new PostProcessProfile
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            foreach (var key in config.Keys)
            {
                var effectSection = config.GetSection(key);
                if (effectSection == null) continue;

                var ov = new EffectOverride { effectName = key };
                ov.enabled = effectSection.GetBool("enabled", false);

                foreach (var paramKey in effectSection.Keys)
                {
                    if (paramKey == "enabled") continue;
                    ov.parameters[paramKey] = effectSection.GetFloat(paramKey, 0f);
                }

                profile.effects[key] = ov;
            }

            return profile;
        }

        public static void Export(PostProcessProfile profile, string path)
        {
            var config = TomlConfig.CreateEmpty();

            foreach (var kvp in profile.effects)
            {
                var ov = kvp.Value;
                var effectSection = TomlConfig.CreateEmpty();
                effectSection.SetValue("enabled", ov.enabled);

                foreach (var param in ov.parameters)
                    effectSection.SetValue(param.Key, (double)param.Value);

                config.SetSection(kvp.Key, effectSection);
            }

            config.SaveToFile(path);
            EditorDebug.Log($"[PostProcessProfileImporter] Exported: {path}");
        }

        /// <summary>기본값 프로파일 생성 (빈 프로파일).</summary>
        public static void WriteDefault(string path)
        {
            var profile = new PostProcessProfile
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            // Bloom 기본값
            var bloom = new EffectOverride { effectName = "Bloom", enabled = true };
            bloom.parameters["Threshold"] = 0.8f;
            bloom.parameters["Soft Knee"] = 0.5f;
            bloom.parameters["Intensity"] = 0.5f;
            profile.effects["Bloom"] = bloom;

            // Tonemap 기본값
            var tonemap = new EffectOverride { effectName = "Tonemap", enabled = true };
            tonemap.parameters["Exposure"] = 1.5f;
            tonemap.parameters["Saturation"] = 1.6f;
            tonemap.parameters["Contrast"] = 1.0f;
            tonemap.parameters["White Point"] = 10.0f;
            tonemap.parameters["Gamma"] = 1.2f;
            profile.effects["Tonemap"] = tonemap;

            Export(profile, path);
        }
    }
}
