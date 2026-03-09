namespace RoseEngine
{
    public struct Resolution
    {
        public int width;
        public int height;
        public int refreshRate;

        public override string ToString() => $"{width}x{height} @ {refreshRate}Hz";
    }

    public static class Screen
    {
        internal static int _width = 1280;
        internal static int _height = 720;
        internal static float _dpi = 96f;

        public static int width => _width;
        public static int height => _height;
        public static float dpi => _dpi;

        public static Resolution currentResolution => new Resolution
        {
            width = _width,
            height = _height,
            refreshRate = 60,
        };

        internal static void SetSize(int w, int h)
        {
            _width = w;
            _height = h;
        }
    }
}
