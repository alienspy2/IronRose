namespace RoseEngine
{
    public class Sprite
    {
        public Texture2D texture { get; private set; }
        public Rect rect { get; private set; }
        public Vector2 pivot { get; }
        public float pixelsPerUnit { get; private set; }

        /// <summary>9-slice 경계 (left, bottom, right, top) 픽셀 단위.</summary>
        public Vector4 border { get; internal set; }

        /// <summary>슬라이스 이름 (Multiple 모드에서 사용).</summary>
        public string spriteName { get; internal set; } = "";

        /// <summary>Sub-asset GUID (개별 슬라이스 식별자).</summary>
        public string guid { get; internal set; } = "";

        internal Vector2 uvMin;
        internal Vector2 uvMax;

        public Vector2 bounds => new(rect.width / pixelsPerUnit, rect.height / pixelsPerUnit);

        private Sprite(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit)
        {
            this.texture = texture;
            this.rect = rect;
            this.pivot = pivot;
            this.pixelsPerUnit = pixelsPerUnit;

            // Compute normalized UV coordinates
            uvMin = new Vector2(rect.x / texture.width, rect.y / texture.height);
            uvMax = new Vector2(rect.xMax / texture.width, rect.yMax / texture.height);
        }

        /// <summary>9-slice 내부 영역의 UV 좌표 계산.</summary>
        public (Vector2 innerMin, Vector2 innerMax) GetBorderUVs()
        {
            float texW = texture.width;
            float texH = texture.height;
            return (
                new Vector2(uvMin.x + border.x / texW, uvMin.y + border.y / texH),
                new Vector2(uvMax.x - border.z / texW, uvMax.y - border.w / texH)
            );
        }

        /// <summary>텍스처 교체 + rect/ppu 비례 조정 (시각적 크기 유지).</summary>
        internal void ReplaceTexture(Texture2D newTex)
        {
            float scaleX = (float)newTex.width / texture.width;
            float scaleY = (float)newTex.height / texture.height;

            texture = newTex;
            rect = new Rect(rect.x * scaleX, rect.y * scaleY, rect.width * scaleX, rect.height * scaleY);
            pixelsPerUnit *= scaleX;

            uvMin = new Vector2(rect.x / texture.width, rect.y / texture.height);
            uvMax = new Vector2(rect.xMax / texture.width, rect.yMax / texture.height);
        }

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit = 100f)
        {
            return new Sprite(texture, rect, pivot, pixelsPerUnit);
        }

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, Vector4 border)
        {
            var sprite = new Sprite(texture, rect, pivot, pixelsPerUnit);
            sprite.border = border;
            return sprite;
        }
    }
}
