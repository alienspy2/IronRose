// ------------------------------------------------------------
// @file    Material.cs
// @brief   PBR Material 데이터 클래스와 BlendMode enum 정의. 색상, 텍스처, PBR 파라미터,
//          블렌드 모드, 텍스처 트랜스폼, 스카이박스 프로퍼티 등을 포함한다.
// @deps    RoseEngine (Color, Texture2D, Shader, Vector2, Cubemap)
// @exports
//   enum BlendMode                                          — Opaque(0), AlphaBlend(1), Additive(2)
//   class Material
//     name, shader, color, mainTexture, emission            — 기본 속성
//     metallic, roughness, occlusion, normalMap,
//       normalMapStrength, MROMap                           — PBR 속성
//     blendMode: BlendMode                                  — 렌더 파이프라인 블렌드 모드 (기본 Opaque)
//     textureScale, textureOffset                           — 텍스처 트랜스폼
//     exposure, rotation, cubemapFaceSize                   — 스카이박스 속성
// @note    blendMode 기본값 Opaque로 기존 동작과 호환.
// ------------------------------------------------------------
namespace RoseEngine
{
    public enum BlendMode
    {
        Opaque = 0,
        AlphaBlend = 1,
        Additive = 2,
    }

    public class Material
    {
        public string name { get; set; } = "";
        public Shader? shader { get; set; }
        public Color color { get; set; } = Color.white;
        public Texture2D? mainTexture { get; set; }
        public Color emission { get; set; } = Color.black;

        // PBR properties
        public float metallic { get; set; } = 0.0f;
        public float roughness { get; set; } = 0.5f;
        public float occlusion { get; set; } = 1.0f;
        public Texture2D? normalMap { get; set; }
        public float normalMapStrength { get; set; } = 1.0f;
        public Texture2D? MROMap { get; set; }

        // Blend mode for rendering pipeline selection
        public BlendMode blendMode { get; set; } = BlendMode.Opaque;

        // Texture transform (Unity-style Tiling & Offset)
        public Vector2 textureScale { get; set; } = Vector2.one;
        public Vector2 textureOffset { get; set; } = Vector2.zero;

        // Skybox properties (used when shader is Skybox/Panoramic or Skybox/Procedural)
        public float exposure { get; set; } = 1.0f;
        public float rotation { get; set; } = 0.0f;
        public int cubemapFaceSize { get; set; } = 512;

        // RenderSystem caches the lazy-converted cubemap here
        internal Cubemap? _cachedCubemap;
        internal Texture2D? _cachedCubemapSource; // change detection

        public Material() { }

        public Material(Shader shader)
        {
            this.shader = shader;
        }

        public Material(Color color)
        {
            this.color = color;
        }
    }
}
