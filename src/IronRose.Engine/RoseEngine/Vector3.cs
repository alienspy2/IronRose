using System;

namespace RoseEngine
{
    public struct Vector3 : IEquatable<Vector3>
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 up => new(0, 1, 0);
        public static Vector3 down => new(0, -1, 0);
        public static Vector3 left => new(-1, 0, 0);
        public static Vector3 right => new(1, 0, 0);
        public static Vector3 forward => new(0, 0, 1);
        public static Vector3 back => new(0, 0, -1);
        public static Vector3 positiveInfinity => new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        public static Vector3 negativeInfinity => new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
        public float sqrMagnitude => x * x + y * y + z * z;

        public Vector3 normalized
        {
            get
            {
                float mag = magnitude;
                return mag > MathConstants.NormalizeEpsilon ? this / mag : zero;
            }
        }

        public void Normalize()
        {
            float mag = magnitude;
            if (mag > MathConstants.NormalizeEpsilon) { x /= mag; y /= mag; z /= mag; }
            else { x = 0; y = 0; z = 0; }
        }

        public void Set(float newX, float newY, float newZ) { x = newX; y = newY; z = newZ; }

        public float this[int index]
        {
            get => index switch { 0 => x, 1 => y, 2 => z, _ => throw new IndexOutOfRangeException() };
            set { switch (index) { case 0: x = value; break; case 1: y = value; break; case 2: z = value; break; default: throw new IndexOutOfRangeException(); } }
        }

        // --- Static methods ---

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 Cross(Vector3 a, Vector3 b) => new(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x
        );

        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t)
        {
            return new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
        {
            Vector3 diff = target - current;
            float dist = diff.magnitude;
            if (dist <= maxDistanceDelta || dist < 1e-10f) return target;
            return current + diff / dist * maxDistanceDelta;
        }

        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity,
            float smoothTime, float maxSpeed = Mathf.Infinity, float deltaTime = -1f)
        {
            if (deltaTime < 0f) deltaTime = Time.deltaTime;
            smoothTime = MathF.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * deltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            Vector3 change = current - target;
            float maxChange = maxSpeed * smoothTime;
            float sqrMag = change.sqrMagnitude;
            if (sqrMag > maxChange * maxChange)
                change = change.normalized * maxChange;

            Vector3 temp = (currentVelocity + change * omega) * deltaTime;
            currentVelocity = (currentVelocity - temp * omega) * exp;
            Vector3 output = (current - change) + (change + temp) * exp;

            if (Dot(target - current, output - target) > 0f)
            {
                output = target;
                currentVelocity = (output - target) / deltaTime;
            }
            return output;
        }

        public static float Angle(Vector3 from, Vector3 to)
        {
            float denom = MathF.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denom < 1e-15f) return 0f;
            float dot = Math.Clamp(Dot(from, to) / denom, -1f, 1f);
            return MathF.Acos(dot) * (180f / MathF.PI);
        }

        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
        {
            float angle = Angle(from, to);
            float sign = MathF.Sign(Dot(axis, Cross(from, to)));
            return angle * sign;
        }

        public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);

        public void Scale(Vector3 scale) { x *= scale.x; y *= scale.y; z *= scale.z; }

        public static Vector3 Project(Vector3 vector, Vector3 onNormal)
        {
            float sqrMag = onNormal.sqrMagnitude;
            if (sqrMag < 1e-15f) return zero;
            return onNormal * (Dot(vector, onNormal) / sqrMag);
        }

        public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            return vector - Project(vector, planeNormal);
        }

        public static Vector3 Reflect(Vector3 inDirection, Vector3 inNormal)
        {
            return inDirection - 2f * Dot(inDirection, inNormal) * inNormal;
        }

        public static Vector3 ClampMagnitude(Vector3 vector, float maxLength)
        {
            if (vector.sqrMagnitude > maxLength * maxLength)
                return vector.normalized * maxLength;
            return vector;
        }

        public static Vector3 Min(Vector3 a, Vector3 b) =>
            new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

        public static Vector3 Max(Vector3 a, Vector3 b) =>
            new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));

        public static Vector3 RotateTowards(Vector3 current, Vector3 target, float maxRadiansDelta, float maxMagnitudeDelta)
        {
            float angle = Angle(current, target) * (MathF.PI / 180f);
            if (angle == 0f) return target;

            float t = MathF.Min(1f, maxRadiansDelta / angle);
            var result = Vector3.Lerp(current, target, t);

            float currentMag = current.magnitude;
            float targetMag = target.magnitude;
            float newMag = currentMag + (targetMag - currentMag) * t;
            if (MathF.Abs(targetMag - currentMag) > maxMagnitudeDelta)
                newMag = currentMag + MathF.Sign(targetMag - currentMag) * maxMagnitudeDelta;

            return result.normalized * newMag;
        }

        // --- Operators ---
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 a) => new(-a.x, -a.y, -a.z);
        public static Vector3 operator *(Vector3 a, float d) => new(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator *(float d, Vector3 a) => new(a.x * d, a.y * d, a.z * d);
        public static Vector3 operator /(Vector3 a, float d) => new(a.x / d, a.y / d, a.z / d);
        public static bool operator ==(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-10f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);

        public bool Equals(Vector3 other) => this == other;
        public override bool Equals(object? obj) => obj is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }
}
