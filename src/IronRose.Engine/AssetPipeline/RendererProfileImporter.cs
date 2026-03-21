// ------------------------------------------------------------
// @file    RendererProfileImporter.cs
// @brief   .renderer (TOML) 파일을 RendererProfile로 임포트/익스포트한다.
//          [fsr]과 [ssil] 두 섹션으로 구성되며 FSR 업스케일링과 SSIL AO 설정을 담당한다.
// @deps    IronRose.Engine/TomlConfig, RoseEngine (EditorDebug, RendererProfile, FsrScaleMode),
//          AssetPipeline/RoseMetadata
// @exports
//   class RendererProfileImporter
//     Import(string, RoseMetadata?): RendererProfile?           — .renderer 파일에서 로드
//     static Export(RendererProfile, string): void              — RendererProfile을 TOML로 저장
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
                EditorDebug.LogError($"[RendererProfileImporter] File not found: {path}");
                return null;
            }

            var config = TomlConfig.LoadFile(path, "[RendererProfileImporter]");
            if (config == null) return null;

            return ParseProfile(config, path);
        }

        private static RendererProfile ParseProfile(TomlConfig config, string path)
        {
            var profile = new RendererProfile
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            var fsr = config.GetSection("fsr");
            if (fsr != null)
            {
                profile.fsrEnabled = fsr.GetBool("enabled", profile.fsrEnabled);
                var scaleMode = fsr.GetString("scale_mode", "");
                if (!string.IsNullOrEmpty(scaleMode) && Enum.TryParse<FsrScaleMode>(scaleMode, true, out var mode))
                    profile.fsrScaleMode = mode;
                profile.fsrCustomScale = fsr.GetFloat("custom_scale", profile.fsrCustomScale);
                profile.fsrSharpness = fsr.GetFloat("sharpness", profile.fsrSharpness);
                profile.fsrJitterScale = fsr.GetFloat("jitter_scale", profile.fsrJitterScale);
            }

            var ssil = config.GetSection("ssil");
            if (ssil != null)
            {
                profile.ssilEnabled = ssil.GetBool("enabled", profile.ssilEnabled);
                profile.ssilRadius = ssil.GetFloat("radius", profile.ssilRadius);
                profile.ssilFalloffScale = ssil.GetFloat("falloff_scale", profile.ssilFalloffScale);
                profile.ssilSliceCount = ssil.GetInt("slice_count", profile.ssilSliceCount);
                profile.ssilStepsPerSlice = ssil.GetInt("steps_per_slice", profile.ssilStepsPerSlice);
                profile.ssilAoIntensity = ssil.GetFloat("ao_intensity", profile.ssilAoIntensity);
                profile.ssilIndirectEnabled = ssil.GetBool("indirect_enabled", profile.ssilIndirectEnabled);
                profile.ssilIndirectBoost = ssil.GetFloat("indirect_boost", profile.ssilIndirectBoost);
                profile.ssilSaturationBoost = ssil.GetFloat("saturation_boost", profile.ssilSaturationBoost);
            }

            return profile;
        }

        public static void Export(RendererProfile profile, string path)
        {
            var config = TomlConfig.CreateEmpty();

            var fsr = TomlConfig.CreateEmpty();
            fsr.SetValue("enabled", profile.fsrEnabled);
            fsr.SetValue("scale_mode", profile.fsrScaleMode.ToString());
            fsr.SetValue("custom_scale", (double)profile.fsrCustomScale);
            fsr.SetValue("sharpness", (double)profile.fsrSharpness);
            fsr.SetValue("jitter_scale", (double)profile.fsrJitterScale);
            config.SetSection("fsr", fsr);

            var ssil = TomlConfig.CreateEmpty();
            ssil.SetValue("enabled", profile.ssilEnabled);
            ssil.SetValue("radius", (double)profile.ssilRadius);
            ssil.SetValue("falloff_scale", (double)profile.ssilFalloffScale);
            ssil.SetValue("slice_count", (long)profile.ssilSliceCount);
            ssil.SetValue("steps_per_slice", (long)profile.ssilStepsPerSlice);
            ssil.SetValue("ao_intensity", (double)profile.ssilAoIntensity);
            ssil.SetValue("indirect_enabled", profile.ssilIndirectEnabled);
            ssil.SetValue("indirect_boost", (double)profile.ssilIndirectBoost);
            ssil.SetValue("saturation_boost", (double)profile.ssilSaturationBoost);
            config.SetSection("ssil", ssil);

            config.SaveToFile(path);
            EditorDebug.Log($"[RendererProfileImporter] Exported: {path}");
        }

        /// <summary>기본값 프로파일 생성.</summary>
        public static void WriteDefault(string path)
        {
            Export(new RendererProfile(), path);
        }
    }
}
