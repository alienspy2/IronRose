using System.Collections.Generic;

namespace RoseEngine
{
    public class TextRenderer : Component
    {
        public Font? font;
        public string text = "";
        public Color color = Color.white;
        public TextAlignment alignment = TextAlignment.Left;
        public float fontSize = 32f;
        public float pixelsPerUnit = 100f;
        public int sortingOrder = 0;
        public bool enabled = true;

        internal Mesh? _cachedMesh;
        private string? _cachedText;
        private Font? _cachedFont;
        private TextAlignment _cachedAlignment;
        private float _cachedFontSize;
        private float _cachedPixelsPerUnit;

        internal static readonly ComponentRegistry<TextRenderer> _allTextRenderers = new();

        internal override void OnAddedToGameObject()
        {
            ThreadGuard.DebugCheckMainThread("TextRenderer.Register");
            _allTextRenderers.Register(this);
        }

        internal override void OnComponentDestroy()
        {
            ThreadGuard.DebugCheckMainThread("TextRenderer.Unregister");
            _allTextRenderers.Unregister(this);
        }

        internal static void ClearAll() => _allTextRenderers.Clear();

        /// <summary>Detect text/font/alignment changes and rebuild mesh</summary>
        internal void EnsureMesh()
        {
            if (font == null || string.IsNullOrEmpty(text))
            {
                _cachedMesh = null;
                _cachedText = null;
                return;
            }

            if (_cachedMesh != null && _cachedText == text &&
                _cachedFont == font && _cachedAlignment == alignment &&
                _cachedFontSize == fontSize && _cachedPixelsPerUnit == pixelsPerUnit)
                return;

            _cachedMesh = BuildTextMesh(font, text, alignment, fontSize, pixelsPerUnit);
            _cachedText = text;
            _cachedFont = font;
            _cachedAlignment = alignment;
            _cachedFontSize = fontSize;
            _cachedPixelsPerUnit = pixelsPerUnit;
        }

        private static Mesh BuildTextMesh(Font font, string text, TextAlignment alignment, float fontSize, float ppu)
        {
            // Normalize: font atlas size doesn't affect display size
            ppu = ppu * font.fontSize / fontSize;

            var mesh = new Mesh();
            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // Split into lines
            string[] lines = text.Split('\n');

            float cursorY = 0f;

            foreach (string line in lines)
            {
                // Calculate line width (for alignment)
                float lineWidth = 0f;
                foreach (char ch in line)
                {
                    if (font.glyphs.TryGetValue(ch, out var g))
                        lineWidth += g.advance / ppu;
                }

                // Alignment offset
                float offsetX = alignment switch
                {
                    TextAlignment.Center => -lineWidth / 2f,
                    TextAlignment.Right => -lineWidth,
                    _ => 0f,
                };

                float cursorX = offsetX;

                foreach (char ch in line)
                {
                    if (!font.glyphs.TryGetValue(ch, out var glyph))
                    {
                        cursorX += font.fontSize * 0.5f / ppu; // unknown char: half-width space
                        continue;
                    }

                    float w = glyph.width / ppu;
                    float h = glyph.height / ppu;

                    // Baseline-relative placement
                    float x0 = cursorX;
                    float x1 = cursorX + w;
                    float y0 = cursorY;              // bottom
                    float y1 = cursorY + h;           // top

                    uint baseIndex = (uint)verts.Count;

                    // Z+ facing quad (same orientation as SpriteRenderer)
                    verts.Add(new Vertex(new Vector3(x0, y0, 0f), Vector3.forward, new Vector2(glyph.uvMin.x, glyph.uvMax.y)));
                    verts.Add(new Vertex(new Vector3(x1, y0, 0f), Vector3.forward, new Vector2(glyph.uvMax.x, glyph.uvMax.y)));
                    verts.Add(new Vertex(new Vector3(x1, y1, 0f), Vector3.forward, new Vector2(glyph.uvMax.x, glyph.uvMin.y)));
                    verts.Add(new Vertex(new Vector3(x0, y1, 0f), Vector3.forward, new Vector2(glyph.uvMin.x, glyph.uvMin.y)));

                    indices.Add(baseIndex);
                    indices.Add(baseIndex + 1);
                    indices.Add(baseIndex + 2);
                    indices.Add(baseIndex);
                    indices.Add(baseIndex + 2);
                    indices.Add(baseIndex + 3);

                    cursorX += glyph.advance / ppu;
                }

                cursorY -= font.lineHeight / ppu;  // next line (downward)
            }

            mesh.vertices = verts.ToArray();
            mesh.indices = indices.ToArray();
            return mesh;
        }
    }
}
