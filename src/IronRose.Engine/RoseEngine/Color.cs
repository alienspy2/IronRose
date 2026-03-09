using System;

namespace RoseEngine
{
    public struct Color : IEquatable<Color>
    {
        public float r, g, b, a;

        public Color(float r, float g, float b, float a = 1f)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static Color white => new(1, 1, 1, 1);
        public static Color black => new(0, 0, 0, 1);
        public static Color red => new(1, 0, 0, 1);
        public static Color green => new(0, 1, 0, 1);
        public static Color blue => new(0, 0, 1, 1);
        public static Color yellow => new(1, 1, 0, 1);
        public static Color cyan => new(0, 1, 1, 1);
        public static Color magenta => new(1, 0, 1, 1);
        public static Color gray => new(0.5f, 0.5f, 0.5f, 1);
        public static Color clear => new(0, 0, 0, 0);

        public static Color HSVToRGB(float h, float s, float v)
        {
            if (s < 1e-5f) return new Color(v, v, v);
            h = h % 1f;
            if (h < 0f) h += 1f;
            h *= 6f;
            int i = (int)h;
            float f = h - i;
            float p = v * (1f - s);
            float q = v * (1f - s * f);
            float t2 = v * (1f - s * (1f - f));
            return i switch
            {
                0 => new Color(v, t2, p),
                1 => new Color(q, v, p),
                2 => new Color(p, v, t2),
                3 => new Color(p, q, v),
                4 => new Color(t2, p, v),
                _ => new Color(v, p, q),
            };
        }

        public static void RGBToHSV(Color color, out float h, out float s, out float v)
        {
            float max = MathF.Max(color.r, MathF.Max(color.g, color.b));
            float min = MathF.Min(color.r, MathF.Min(color.g, color.b));
            float delta = max - min;
            v = max;
            s = max > 1e-5f ? delta / max : 0f;
            if (delta < 1e-5f) { h = 0f; return; }
            if (MathF.Abs(color.r - max) < 1e-5f) h = (color.g - color.b) / delta;
            else if (MathF.Abs(color.g - max) < 1e-5f) h = 2f + (color.b - color.r) / delta;
            else h = 4f + (color.r - color.g) / delta;
            h /= 6f;
            if (h < 0f) h += 1f;
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new(
                a.r + (b.r - a.r) * t,
                a.g + (b.g - a.g) * t,
                a.b + (b.b - a.b) * t,
                a.a + (b.a - a.a) * t
            );
        }

        public static Color operator +(Color a, Color b) => new(a.r + b.r, a.g + b.g, a.b + b.b, a.a + b.a);
        public static Color operator -(Color a, Color b) => new(a.r - b.r, a.g - b.g, a.b - b.b, a.a - b.a);
        public static Color operator *(Color a, float d) => new(a.r * d, a.g * d, a.b * d, a.a * d);
        public static Color operator *(float d, Color a) => new(a.r * d, a.g * d, a.b * d, a.a * d);
        public static bool operator ==(Color a, Color b) =>
            MathF.Abs(a.r - b.r) < 1e-5f && MathF.Abs(a.g - b.g) < 1e-5f &&
            MathF.Abs(a.b - b.b) < 1e-5f && MathF.Abs(a.a - b.a) < 1e-5f;
        public static bool operator !=(Color a, Color b) => !(a == b);

        public bool Equals(Color other) => this == other;
        public override bool Equals(object? obj) => obj is Color c && this == c;
        public override int GetHashCode() => HashCode.Combine(r, g, b, a);
        public override string ToString() => $"RGBA({r:F3}, {g:F3}, {b:F3}, {a:F3})";
    }
}
