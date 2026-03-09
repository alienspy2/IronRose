using System.Collections.Generic;

namespace RoseEngine
{
    public class SpriteRenderer : Component
    {
        public Sprite? sprite;
        public Color color = Color.white;
        public bool flipX;
        public bool flipY;
        public int sortingOrder;
        public bool enabled = true;

        internal Mesh? _cachedMesh;
        private Sprite? _cachedSprite;
        private bool _cachedFlipX;
        private bool _cachedFlipY;

        internal static readonly List<SpriteRenderer> _allSpriteRenderers = new();

        internal override void OnAddedToGameObject() => _allSpriteRenderers.Add(this);
        internal override void OnComponentDestroy() => _allSpriteRenderers.Remove(this);
        internal static void ClearAll() => _allSpriteRenderers.Clear();

        internal void EnsureMesh()
        {
            if (sprite == null)
            {
                _cachedMesh = null;
                _cachedSprite = null;
                return;
            }

            if (_cachedMesh != null && _cachedSprite == sprite &&
                _cachedFlipX == flipX && _cachedFlipY == flipY)
                return;

            _cachedMesh = BuildSpriteMesh(sprite, flipX, flipY);
            _cachedSprite = sprite;
            _cachedFlipX = flipX;
            _cachedFlipY = flipY;
        }

        private static Mesh BuildSpriteMesh(Sprite sprite, bool flipX, bool flipY)
        {
            var mesh = new Mesh();

            var bounds = sprite.bounds;
            float w = bounds.x;
            float h = bounds.y;

            // Pivot offset (pivot is [0..1] normalized)
            float left = -sprite.pivot.x * w;
            float right = left + w;
            float bottom = -sprite.pivot.y * h;
            float top = bottom + h;

            // UV coordinates
            float uMin = flipX ? sprite.uvMax.x : sprite.uvMin.x;
            float uMax = flipX ? sprite.uvMin.x : sprite.uvMax.x;
            float vMin = flipY ? sprite.uvMin.y : sprite.uvMax.y;
            float vMax = flipY ? sprite.uvMax.y : sprite.uvMin.y;

            // Quad facing Z+ (same orientation as PrimitiveGenerator.CreateQuad)
            mesh.vertices = new Vertex[]
            {
                new(new Vector3(left,  bottom, 0f), Vector3.forward, new Vector2(uMin, vMin)),
                new(new Vector3(right, bottom, 0f), Vector3.forward, new Vector2(uMax, vMin)),
                new(new Vector3(right, top,    0f), Vector3.forward, new Vector2(uMax, vMax)),
                new(new Vector3(left,  top,    0f), Vector3.forward, new Vector2(uMin, vMax)),
            };

            mesh.indices = new uint[] { 0, 1, 2, 0, 2, 3 };
            return mesh;
        }
    }
}
