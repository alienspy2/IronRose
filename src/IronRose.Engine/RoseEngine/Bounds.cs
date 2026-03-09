using System;

namespace RoseEngine
{
    public struct Bounds : IEquatable<Bounds>
    {
        public Vector3 center;
        public Vector3 size;

        public Bounds(Vector3 center, Vector3 size)
        {
            this.center = center;
            this.size = size;
        }

        public Vector3 extents
        {
            get => size / 2f;
            set => size = value * 2f;
        }

        public Vector3 min
        {
            get => center - extents;
            set => SetMinMax(value, max);
        }

        public Vector3 max
        {
            get => center + extents;
            set => SetMinMax(min, value);
        }

        public void SetMinMax(Vector3 min, Vector3 max)
        {
            extents = (max - min) / 2f;
            center = min + extents;
        }

        public void Encapsulate(Vector3 point)
        {
            SetMinMax(Vector3.Min(min, point), Vector3.Max(max, point));
        }

        public void Encapsulate(Bounds bounds)
        {
            SetMinMax(Vector3.Min(min, bounds.min), Vector3.Max(max, bounds.max));
        }

        public void Expand(float amount)
        {
            size += new Vector3(amount, amount, amount);
        }

        public void Expand(Vector3 amount)
        {
            size += amount;
        }

        public bool Contains(Vector3 point)
        {
            var lo = min;
            var hi = max;
            return point.x >= lo.x && point.x <= hi.x
                && point.y >= lo.y && point.y <= hi.y
                && point.z >= lo.z && point.z <= hi.z;
        }

        public bool Intersects(Bounds bounds)
        {
            var aMin = min; var aMax = max;
            var bMin = bounds.min; var bMax = bounds.max;
            return aMin.x <= bMax.x && aMax.x >= bMin.x
                && aMin.y <= bMax.y && aMax.y >= bMin.y
                && aMin.z <= bMax.z && aMax.z >= bMin.z;
        }

        public Vector3 ClosestPoint(Vector3 point)
        {
            var lo = min;
            var hi = max;
            return new Vector3(
                MathF.Max(lo.x, MathF.Min(hi.x, point.x)),
                MathF.Max(lo.y, MathF.Min(hi.y, point.y)),
                MathF.Max(lo.z, MathF.Min(hi.z, point.z))
            );
        }

        public float SqrDistance(Vector3 point)
        {
            return (ClosestPoint(point) - point).sqrMagnitude;
        }

        public bool Equals(Bounds other) => center == other.center && size == other.size;
        public override bool Equals(object? obj) => obj is Bounds b && Equals(b);
        public override int GetHashCode() => HashCode.Combine(center, size);
        public static bool operator ==(Bounds a, Bounds b) => a.Equals(b);
        public static bool operator !=(Bounds a, Bounds b) => !a.Equals(b);
        public override string ToString() => $"Center: {center}, Extents: {extents}";
    }
}
