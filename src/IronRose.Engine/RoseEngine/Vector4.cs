using System;

namespace RoseEngine
{
    public struct Vector4 : IEquatable<Vector4>
    {
        public float x, y, z, w;

        public Vector4(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Vector4 zero => new(0, 0, 0, 0);
        public static Vector4 one => new(1, 1, 1, 1);

        public static Vector4 operator +(Vector4 a, Vector4 b) => new(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);
        public static Vector4 operator -(Vector4 a, Vector4 b) => new(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w);
        public static Vector4 operator *(Vector4 a, float d) => new(a.x * d, a.y * d, a.z * d, a.w * d);
        public static Vector4 operator /(Vector4 a, float d) => new(a.x / d, a.y / d, a.z / d, a.w / d);
        public static bool operator ==(Vector4 a, Vector4 b) =>
            MathF.Abs(a.x - b.x) < 1e-5f && MathF.Abs(a.y - b.y) < 1e-5f &&
            MathF.Abs(a.z - b.z) < 1e-5f && MathF.Abs(a.w - b.w) < 1e-5f;
        public static bool operator !=(Vector4 a, Vector4 b) => !(a == b);

        public bool Equals(Vector4 other) => this == other;
        public override bool Equals(object? obj) => obj is Vector4 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2}, {w:F2})";
    }
}
