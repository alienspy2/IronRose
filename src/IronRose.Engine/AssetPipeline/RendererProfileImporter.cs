using System;
using System.IO;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// .renderer (TOML) 파일을 RendererProfile로 임포트/익스포트.
    ///
    /// TOML 구조:
    /// [fsr]
    /// enabled = false
    /// scale_mode = "Quality"
    /// custom_scale = 1.2
    /// sharpness = 0.5
    /// jitter_scale = 1.0
    ///
    /// [ssil]
    /// enabled = true
    /// radius = 1.5
    /// falloff_scale = 2.0
    /// slice_count = 3
    /// steps_per_slice = 3
    /// ao_intensity = 0.5
    /// indirect_enabled = true
    /// indirect_boost = 0.37
    /// saturation_boost = 2.0
    /// </summary>
    public class RendererProfileImporter
    {
        public RendererProfile? Import(string path, RoseMetadata? meta = null)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"[RendererProfileImporter] File not found: {path}");
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
                Debug.LogError($"[RendererProfileImporter] Parse failed: {path} — {ex.Message}");
                return null;
            }
        }

        private static RendererProfile ParseProfile(TomlTable doc, string path)
        {
            var profile = new RendererProfile
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            // [fsr]
            if (doc.TryGetValue("fsr", out var fsrVal) && fsrVal is TomlTable fsr)
            {
                if (fsr.TryGetValue("enabled", out var v) && v is bool b) profile.fsrEnabled = b;
                if (fsr.TryGetValue("scale_mode", out var sm) && sm is string sms)
                {
                    if (Enum.TryParse<FsrScaleMode>(sms, true, out var mode))
                        profile.fsrScaleMode = mode;
                }
                if (fsr.TryGetValue("custom_scale", out var cs)) profile.fsrCustomScale = ToFloat(cs);
                if (fsr.TryGetValue("sharpness", out var sh)) profile.fsrSharpness = ToFloat(sh);
                if (fsr.TryGetValue("jitter_scale", out var js)) profile.fsrJitterScale = ToFloat(js);
            }

            // [ssil]
            if (doc.TryGetValue("ssil", out var ssilVal) && ssilVal is TomlTable ssil)
            {
                if (ssil.TryGetValue("enabled", out var v) && v is bool b) profile.ssilEnabled = b;
                if (ssil.TryGetValue("radius", out var r)) profile.ssilRadius = ToFloat(r);
                if (ssil.TryGetValue("falloff_scale", out var fs)) profile.ssilFalloffScale = ToFloat(fs);
                if (ssil.TryGetValue("slice_count", out var sc) && sc is long lsc) profile.ssilSliceCount = (int)lsc;
                if (ssil.TryGetValue("steps_per_slice", out var sps) && sps is long lsps) profile.ssilStepsPerSlice = (int)lsps;
                if (ssil.TryGetValue("ao_intensity", out var ai)) profile.ssilAoIntensity = ToFloat(ai);
                if (ssil.TryGetValue("indirect_enabled", out var ie) && ie is bool bie) profile.ssilIndirectEnabled = bie;
                if (ssil.TryGetValue("indirect_boost", out var ib)) profile.ssilIndirectBoost = ToFloat(ib);
                if (ssil.TryGetValue("saturation_boost", out var sb)) profile.ssilSaturationBoost = ToFloat(sb);
            }

            return profile;
        }

        public static void Export(RendererProfile profile, string path)
        {
            var doc = new TomlTable();

            // [fsr]
            var fsr = new TomlTable
            {
                ["enabled"] = profile.fsrEnabled,
                ["scale_mode"] = profile.fsrScaleMode.ToString(),
                ["custom_scale"] = (double)profile.fsrCustomScale,
                ["sharpness"] = (double)profile.fsrSharpness,
                ["jitter_scale"] = (double)profile.fsrJitterScale,
            };
            doc["fsr"] = fsr;

            // [ssil]
            var ssil = new TomlTable
            {
                ["enabled"] = profile.ssilEnabled,
                ["radius"] = (double)profile.ssilRadius,
                ["falloff_scale"] = (double)profile.ssilFalloffScale,
                ["slice_count"] = (long)profile.ssilSliceCount,
                ["steps_per_slice"] = (long)profile.ssilStepsPerSlice,
                ["ao_intensity"] = (double)profile.ssilAoIntensity,
                ["indirect_enabled"] = profile.ssilIndirectEnabled,
                ["indirect_boost"] = (double)profile.ssilIndirectBoost,
                ["saturation_boost"] = (double)profile.ssilSaturationBoost,
            };
            doc["ssil"] = ssil;

            var toml = Toml.FromModel(doc);
            File.WriteAllText(path, toml);
            Debug.Log($"[RendererProfileImporter] Exported: {path}");
        }

        /// <summary>기본값 프로파일 생성.</summary>
        public static void WriteDefault(string path)
        {
            Export(new RendererProfile(), path);
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
