// ------------------------------------------------------------
// @file    Material.cs
// @brief   렌더링에 사용되는 머티리얼 데이터 클래스. PBR 속성, 텍스처, 블렌드 모드, 스카이박스 설정 등을 포함한다.
// @deps    Shader, Color, Texture2D, Cubemap, Vector2
// @exports
//   enum BlendMode                                            — 렌더링 블렌드 모드 (Opaque, AlphaBlend, Additive)
//   class Material
//     name: string                                            — 머티리얼 이름
//     shader: Shader?                                         — 사용할 셰이더
//     color: Color                                            — 기본 색상 (기본값 white)
//     mainTexture: Texture2D?                                 — 메인 텍스처
//     emission: Color                                         — 이미션 색상 (기본값 black)
//     metallic: float                                         — 메탈릭 (0.0)
//     roughness: float                                        — 러프니스 (0.5)
//     occlusion: float                                        — 오클루전 (1.0)
//     normalMap: Texture2D?                                   — 노멀맵
//     normalMapStrength: float                                — 노멀맵 강도 (1.0)
//     MROMap: Texture2D?                                      — MRO(Metallic/Roughness/Occlusion) 맵
//     blendMode: BlendMode                                    — 블렌드 모드 (기본값 Opaque)
//     textureScale: Vector2                                   — 텍스처 스케일 (기본값 one)
//     textureOffset: Vector2                                  — 텍스처 오프셋 (기본값 zero)
//     exposure: float                                         — 스카이박스 노출 (1.0)
//     rotation: float                                         — 스카이박스 회전 (0.0)
//     cubemapFaceSize: int                                    — 큐브맵 페이스 크기 (512)
//     Material()                                              — 기본 생성자
//     Material(Shader)                                        — 셰이더 지정 생성자
//     Material(Color)                                         — 색상 지정 생성자
// @note    BlendMode enum은 Material과 밀접하므로 동일 파일에 정의.
//          _cachedCubemap, _cachedCubemapSource는 RenderSystem이 내부적으로 사용하는 캐시 필드.
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
