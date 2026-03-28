using IronRose.AssetPipeline;
using IronRose.Rendering;

namespace RoseEngine
{
    public enum FsrScaleMode
    {
        NativeAA,        // 1.0x
        Quality,         // 1.5x
        Balanced,        // 1.7x
        Performance,     // 2.0x
        UltraPerformance, // 3.0x
        Custom           // fsrCustomScale 사용
    }

    /// <summary>
    /// Unity-compatible RenderSettings class.
    /// Controls global rendering settings such as skybox material and ambient lighting.
    /// </summary>
    public static class RenderSettings
    {
        /// <summary>
        /// Post-processing effect stack. Set by engine at init time.
        /// Use GetEffect&lt;T&gt;() to access individual effects.
        /// </summary>
        public static PostProcessStack? postProcessing { get; set; }
        /// <summary>
        /// The skybox material. Assign a Material with Shader "Skybox/Panoramic"
        /// and a mainTexture for environment map, or "Skybox/Procedural" for
        /// procedural atmospheric sky.
        /// </summary>
        public static Material? skybox { get; set; }

        /// <summary>
        /// GUID of the skybox texture asset (for serialization/editor).
        /// </summary>
        public static string? skyboxTextureGuid { get; set; }

        /// <summary>
        /// Skybox exposure. Synced to skybox material's exposure property.
        /// </summary>
        public static float skyboxExposure { get; set; } = 1.0f;

        /// <summary>
        /// Skybox rotation in degrees. Synced to skybox material's rotation property.
        /// </summary>
        public static float skyboxRotation { get; set; } = 0.0f;

        /// <summary>
        /// Loads a texture by GUID and creates a Skybox/Panoramic material.
        /// Call after setting skyboxTextureGuid (e.g. on scene load).
        /// </summary>
        public static void ApplySkyboxFromGuid()
        {
            if (string.IsNullOrEmpty(skyboxTextureGuid))
            {
                skybox = null;
                return;
            }

            var db = Resources.GetAssetDatabase();
            var tex = db?.LoadByGuid<Texture2D>(skyboxTextureGuid);
            if (tex == null)
            {
                EditorDebug.LogWarning($"[RenderSettings] Skybox texture not found for GUID: {skyboxTextureGuid}");
                skybox = null;
                return;
            }

            // Read face_size from texture metadata if Panoramic
            int faceSize = 512;
            var texPath = db?.GetPathFromGuid(skyboxTextureGuid);
            if (texPath != null)
            {
                var meta = RoseMetadata.LoadOrCreate(texPath);
                if (meta.importer.TryGetValue("face_size", out var fsVal))
                    faceSize = System.Convert.ToInt32(fsVal);
            }

            var mat = new Material(Shader.Find("Skybox/Panoramic")!);
            mat.mainTexture = tex;
            mat.exposure = skyboxExposure;
            mat.rotation = skyboxRotation;
            mat.cubemapFaceSize = faceSize;
            skybox = mat;
        }

        /// <summary>
        /// Global ambient light color. Used as fallback when no skybox is set.
        /// </summary>
        public static Color ambientLight { get; set; } = new Color(0.2f, 0.2f, 0.2f, 1f);

        /// <summary>
        /// Ambient intensity multiplier for IBL.
        /// </summary>
        public static float ambientIntensity { get; set; } = 1.0f;

        /// <summary>
        /// Procedural sky zenith (top) color.
        /// </summary>
        public static Color skyZenithColor { get; set; } = new Color(0.15f, 0.3f, 0.65f);

        /// <summary>
        /// Procedural sky horizon color.
        /// </summary>
        public static Color skyHorizonColor { get; set; } = new Color(0.6f, 0.7f, 0.85f);

        /// <summary>
        /// Procedural sky zenith intensity.
        /// </summary>
        public static float skyZenithIntensity { get; set; } = 0.8f;

        /// <summary>
        /// Procedural sky horizon intensity.
        /// </summary>
        public static float skyHorizonIntensity { get; set; } = 1.0f;

        /// <summary>
        /// Procedural sky sun intensity. Set to 0 to disable the sun disk entirely.
        /// </summary>
        public static float sunIntensity { get; set; } = 20.0f;

        // --- FSR Upscaler ---
        public static bool fsrEnabled { get; set; } = false;
        public static FsrScaleMode fsrScaleMode { get; set; } = FsrScaleMode.Quality;
        public static float fsrCustomScale { get; set; } = 1.2f;
        public static float fsrSharpness { get; set; } = 0.5f;
        public static float fsrJitterScale { get; set; } = 1.0f;

        // --- SSIL ---
        public static bool ssilEnabled { get; set; } = true;
        public static float ssilRadius { get; set; } = 1.5f;
        public static float ssilFalloffScale { get; set; } = 2.0f;
        public static int ssilSliceCount { get; set; } = 3;
        public static int ssilStepsPerSlice { get; set; } = 3;
        public static bool ssilIndirectEnabled { get; set; } = true;
        public static float ssilIndirectBoost { get; set; } = 0.37f;
        public static float ssilSaturationBoost { get; set; } = 2.00f;
        public static float ssilAoIntensity { get; set; } = 0.5f;

        // --- Active Renderer Profile ---

        /// <summary>현재 활성 렌더러 프로파일 (.renderer 에셋).</summary>
        public static RendererProfile? activeRendererProfile { get; set; }

        /// <summary>활성 프로파일의 에셋 GUID (EditorState 저장용).</summary>
        public static string? activeRendererProfileGuid { get; set; }

        /// <summary>활성 프로파일 이름 (UI 표시용).</summary>
        public static string activeRendererProfileName => activeRendererProfile?.name ?? "None";
    }
}
