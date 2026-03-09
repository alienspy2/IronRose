using System;
using System.IO;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;

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
                Debug.LogError($"[PostProcessProfileImporter] File not found: {path}");
                return null;
            }

            try
            {
                var text = File.ReadAllText(path);
                var doc = Toml.ToModel(text);
                return ParseProfile(doc, path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PostProcessProfileImporter] Parse failed: {path} — {ex.Message}");
                return null;
            }
        }

        private static PostProcessProfile ParseProfile(TomlTable doc, string path)
        {
            var profile = new PostProcessProfile
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            // 각 키는 이펙트 이름 (Bloom, Tonemap 등)
            foreach (var kvp in doc)
            {
                if (kvp.Value is not TomlTable effectTable) continue;

                var effectName = kvp.Key;
                var ov = new EffectOverride { effectName = effectName };

                if (effectTable.TryGetValue("enabled", out var ev) && ev is bool eb)
                    ov.enabled = eb;

                foreach (var param in effectTable)
                {
                    if (param.Key == "enabled") continue;
                    ov.parameters[param.Key] = ToFloat(param.Value);
                }

                profile.effects[effectName] = ov;
            }

            return profile;
        }

        public static void Export(PostProcessProfile profile, string path)
        {
            var doc = new TomlTable();

            foreach (var kvp in profile.effects)
            {
                var ov = kvp.Value;
                var effectTable = new TomlTable
                {
                    ["enabled"] = ov.enabled,
                };

                foreach (var param in ov.parameters)
                {
                    effectTable[param.Key] = (double)param.Value;
                }

                doc[kvp.Key] = effectTable;
            }

            var toml = Toml.FromModel(doc);
            File.WriteAllText(path, toml);
            Debug.Log($"[PostProcessProfileImporter] Exported: {path}");
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

        private static float ToFloat(object? val) => val switch
        {
            double d => (float)d,
            long l => l,
            float f => f,
            _ => 0f,
        };
    }
}
