namespace RoseEngine
{
    public struct Rect
    {
        public float x, y, width, height;

        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }

        public float xMin => x;
        public float yMin => y;
        public float xMax => x + width;
        public float yMax => y + height;

        public Vector2 center => new(x + width * 0.5f, y + height * 0.5f);
        public Vector2 size => new(width, height);
        public Vector2 position => new(x, y);

        public bool Contains(Vector2 point)
        {
            return point.x >= xMin && point.x <= xMax &&
                   point.y >= yMin && point.y <= yMax;
        }

        public override string ToString() => $"(x:{x:F2}, y:{y:F2}, w:{width:F2}, h:{height:F2})";
    }
}
