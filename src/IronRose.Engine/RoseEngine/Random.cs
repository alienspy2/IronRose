namespace RoseEngine
{
    public static class Random
    {
        private static System.Random _rng = new();

        public static int seed
        {
            set => _rng = new System.Random(value);
        }

        public static float value => (float)_rng.NextDouble();

        public static float Range(float min, float max) => min + value * (max - min);

        public static int Range(int min, int max) => _rng.Next(min, max);

        public static Vector3 insideUnitSphere
        {
            get
            {
                while (true)
                {
                    var v = new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
                    if (v.sqrMagnitude <= 1f) return v;
                }
            }
        }

        public static Vector3 onUnitSphere => insideUnitSphere.normalized;

        public static Vector2 insideUnitCircle
        {
            get
            {
                while (true)
                {
                    var v = new Vector2(Range(-1f, 1f), Range(-1f, 1f));
                    if (v.sqrMagnitude <= 1f) return v;
                }
            }
        }

        public static Quaternion rotation =>
            Quaternion.Euler(Range(0f, 360f), Range(0f, 360f), Range(0f, 360f));

        public static Quaternion rotationUniform => rotation;

        public static Color ColorHSV(
            float hueMin = 0f, float hueMax = 1f,
            float saturationMin = 0f, float saturationMax = 1f,
            float valueMin = 0f, float valueMax = 1f)
        {
            float h = Range(hueMin, hueMax);
            float s = Range(saturationMin, saturationMax);
            float v = Range(valueMin, valueMax);
            return Color.HSVToRGB(h, s, v);
        }
    }
}
