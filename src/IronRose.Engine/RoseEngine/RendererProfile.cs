using System;

namespace RoseEngine
{
    /// <summary>
    /// .renderer 에셋 파일에 저장되는 렌더러 설정 프로파일.
    /// FSR Upscaler + SSIL/AO 14개 프로퍼티를 보유.
    /// RenderSettings static 프로퍼티의 영속화 백엔드.
    /// </summary>
    public class RendererProfile
    {
        public string name { get; set; } = "Default";

        // ── FSR Upscaler (5) ──

        public bool fsrEnabled { get; set; } = false;
        public FsrScaleMode fsrScaleMode { get; set; } = FsrScaleMode.Quality;
        public float fsrCustomScale { get; set; } = 1.2f;
        public float fsrSharpness { get; set; } = 0.5f;
        public float fsrJitterScale { get; set; } = 1.0f;

        // ── SSIL / AO (9) ──

        public bool ssilEnabled { get; set; } = true;
        public float ssilRadius { get; set; } = 1.5f;
        public float ssilFalloffScale { get; set; } = 2.0f;
        public int ssilSliceCount { get; set; } = 3;
        public int ssilStepsPerSlice { get; set; } = 3;
        public float ssilAoIntensity { get; set; } = 0.5f;
        public bool ssilIndirectEnabled { get; set; } = true;
        public float ssilIndirectBoost { get; set; } = 0.37f;
        public float ssilSaturationBoost { get; set; } = 2.0f;

        /// <summary>프로파일 값을 런타임 RenderSettings에 반영.</summary>
        public void ApplyToRenderSettings()
        {
            RenderSettings.fsrEnabled = fsrEnabled;
            RenderSettings.fsrScaleMode = fsrScaleMode;
            RenderSettings.fsrCustomScale = fsrCustomScale;
            RenderSettings.fsrSharpness = fsrSharpness;
            RenderSettings.fsrJitterScale = fsrJitterScale;

            RenderSettings.ssilEnabled = ssilEnabled;
            RenderSettings.ssilRadius = ssilRadius;
            RenderSettings.ssilFalloffScale = ssilFalloffScale;
            RenderSettings.ssilSliceCount = ssilSliceCount;
            RenderSettings.ssilStepsPerSlice = ssilStepsPerSlice;
            RenderSettings.ssilAoIntensity = ssilAoIntensity;
            RenderSettings.ssilIndirectEnabled = ssilIndirectEnabled;
            RenderSettings.ssilIndirectBoost = ssilIndirectBoost;
            RenderSettings.ssilSaturationBoost = ssilSaturationBoost;
        }

        /// <summary>런타임 RenderSettings에서 현재 값을 캡처.</summary>
        public void CaptureFromRenderSettings()
        {
            fsrEnabled = RenderSettings.fsrEnabled;
            fsrScaleMode = RenderSettings.fsrScaleMode;
            fsrCustomScale = RenderSettings.fsrCustomScale;
            fsrSharpness = RenderSettings.fsrSharpness;
            fsrJitterScale = RenderSettings.fsrJitterScale;

            ssilEnabled = RenderSettings.ssilEnabled;
            ssilRadius = RenderSettings.ssilRadius;
            ssilFalloffScale = RenderSettings.ssilFalloffScale;
            ssilSliceCount = RenderSettings.ssilSliceCount;
            ssilStepsPerSlice = RenderSettings.ssilStepsPerSlice;
            ssilAoIntensity = RenderSettings.ssilAoIntensity;
            ssilIndirectEnabled = RenderSettings.ssilIndirectEnabled;
            ssilIndirectBoost = RenderSettings.ssilIndirectBoost;
            ssilSaturationBoost = RenderSettings.ssilSaturationBoost;
        }
    }
}
