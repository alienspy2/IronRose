namespace RoseEngine
{
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
