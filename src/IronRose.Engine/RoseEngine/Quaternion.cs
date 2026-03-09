using System;

namespace RoseEngine
{
    public struct Quaternion : IEquatable<Quaternion>
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new(0, 0, 0, 1);

        public Vector3 eulerAngles
        {
            get
            {
                float sinr_cosp = 2 * (w * x + y * z);
                float cosr_cosp = 1 - 2 * (x * x + y * y);
                float roll = MathF.Atan2(sinr_cosp, cosr_cosp);

                float sinp = 2 * (w * y - z * x);
                float pitch = MathF.Abs(sinp) >= 1
                    ? MathF.CopySign(MathF.PI / 2, sinp)
                    : MathF.Asin(sinp);

                float siny_cosp = 2 * (w * z + x * y);
                float cosy_cosp = 1 - 2 * (y * y + z * z);
                float yaw = MathF.Atan2(siny_cosp, cosy_cosp);

                const float rad2deg = 180f / MathF.PI;
                return new Vector3(roll * rad2deg, pitch * rad2deg, yaw * rad2deg);
            }
        }

        public Quaternion normalized
        {
            get
            {
                float mag = MathF.Sqrt(x * x + y * y + z * z + w * w);
                if (mag < 1e-10f) return identity;
                float inv = 1f / mag;
                return new Quaternion(x * inv, y * inv, z * inv, w * inv);
            }
        }

        public static Quaternion Euler(float x, float y, float z)
        {
            const float deg2rad = MathF.PI / 180f;
            x *= deg2rad * 0.5f;
            y *= deg2rad * 0.5f;
            z *= deg2rad * 0.5f;

            float cx = MathF.Cos(x), sx = MathF.Sin(x);
            float cy = MathF.Cos(y), sy = MathF.Sin(y);
            float cz = MathF.Cos(z), sz = MathF.Sin(z);

            return new Quaternion(
                sx * cy * cz - cx * sy * sz,
                cx * sy * cz + sx * cy * sz,
                cx * cy * sz - sx * sy * cz,
                cx * cy * cz + sx * sy * sz
            );
        }

        public static Quaternion Euler(Vector3 euler) => Euler(euler.x, euler.y, euler.z);

        public static Quaternion AngleAxis(float angle, Vector3 axis)
        {
            float rad = angle * MathF.PI / 180f * 0.5f;
            axis = axis.normalized;
            float s = MathF.Sin(rad);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, MathF.Cos(rad));
        }

        public static Quaternion Inverse(Quaternion q)
        {
            float dot = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (dot < 1e-10f) return identity;
            float inv = 1f / dot;
            return new Quaternion(-q.x * inv, -q.y * inv, -q.z * inv, q.w * inv);
        }

        public static Quaternion Normalize(Quaternion q) => q.normalized;

        public static Quaternion Lerp(Quaternion a, Quaternion b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return LerpUnclamped(a, b, t);
        }

        public static Quaternion LerpUnclamped(Quaternion a, Quaternion b, float t)
        {
            float dot = Dot(a, b);
            if (dot < 0f)
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);

            return new Quaternion(
                a.x + (b.x - a.x) * t,
                a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t,
                a.w + (b.w - a.w) * t
            ).normalized;
        }

        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return SlerpUnclamped(a, b, t);
        }

        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t)
        {
            float dot = Dot(a, b);
            if (dot < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                dot = -dot;
            }

            if (dot > 0.9995f)
            {
                // Very close — use linear interpolation
                return LerpUnclamped(a, b, t);
            }

            float theta = MathF.Acos(dot);
            float sinTheta = MathF.Sin(theta);
            float wa = MathF.Sin((1f - t) * theta) / sinTheta;
            float wb = MathF.Sin(t * theta) / sinTheta;

            return new Quaternion(
                wa * a.x + wb * b.x,
                wa * a.y + wb * b.y,
                wa * a.z + wb * b.z,
                wa * a.w + wb * b.w
            );
        }

        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
            float angle = Angle(from, to);
            if (angle == 0f) return to;
            return SlerpUnclamped(from, to, MathF.Min(1f, maxDegreesDelta / angle));
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = MathF.Min(MathF.Abs(Dot(a, b)), 1f);
            return 2f * MathF.Acos(dot) * (180f / MathF.PI);
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards = default)
        {
            if (upwards == Vector3.zero) upwards = Vector3.up;

            forward = forward.normalized;
            if (forward.sqrMagnitude < 1e-10f) return identity;

            Vector3 right = Vector3.Cross(upwards, forward).normalized;
            if (right.sqrMagnitude < 1e-10f)
            {
                // forward is parallel to upwards, pick arbitrary up
                right = Vector3.Cross(Vector3.right, forward).normalized;
                if (right.sqrMagnitude < 1e-10f)
                    right = Vector3.Cross(Vector3.forward, forward).normalized;
            }
            upwards = Vector3.Cross(forward, right);

            float m00 = right.x, m01 = upwards.x, m02 = forward.x;
            float m10 = right.y, m11 = upwards.y, m12 = forward.y;
            float m20 = right.z, m21 = upwards.z, m22 = forward.z;

            float trace = m00 + m11 + m22;
            Quaternion q;

            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                q = new Quaternion(
                    (m21 - m12) / s,
                    (m02 - m20) / s,
                    (m10 - m01) / s,
                    0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(
                    0.25f * s,
                    (m01 + m10) / s,
                    (m02 + m20) / s,
                    (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion(
                    (m01 + m10) / s,
                    0.25f * s,
                    (m12 + m21) / s,
                    (m02 - m20) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion(
                    (m02 + m20) / s,
                    (m12 + m21) / s,
                    0.25f * s,
                    (m10 - m01) / s);
            }

            return q.normalized;
        }

        public static Quaternion FromToRotation(Vector3 fromDirection, Vector3 toDirection)
        {
            float dot = Vector3.Dot(fromDirection.normalized, toDirection.normalized);
            if (dot >= 1f) return identity;
            if (dot <= -1f)
            {
                // 180-degree rotation
                var axis = Vector3.Cross(Vector3.right, fromDirection);
                if (axis.sqrMagnitude < 1e-6f)
                    axis = Vector3.Cross(Vector3.up, fromDirection);
                return AngleAxis(180f, axis.normalized);
            }

            var cross = Vector3.Cross(fromDirection, toDirection);
            return new Quaternion(cross.x, cross.y, cross.z, 1f + dot).normalized;
        }

        // --- Operators ---
        public static Quaternion operator *(Quaternion a, Quaternion b) => new(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
        );

        public static Vector3 operator *(Quaternion q, Vector3 v)
        {
            float qx2 = q.x * 2f, qy2 = q.y * 2f, qz2 = q.z * 2f;
            float qxx = q.x * qx2, qyy = q.y * qy2, qzz = q.z * qz2;
            float qxy = q.x * qy2, qxz = q.x * qz2, qyz = q.y * qz2;
            float qwx = q.w * qx2, qwy = q.w * qy2, qwz = q.w * qz2;

            return new Vector3(
                (1f - (qyy + qzz)) * v.x + (qxy - qwz) * v.y + (qxz + qwy) * v.z,
                (qxy + qwz) * v.x + (1f - (qxx + qzz)) * v.y + (qyz - qwx) * v.z,
                (qxz - qwy) * v.x + (qyz + qwx) * v.y + (1f - (qxx + qyy)) * v.z
            );
        }

        public static bool operator ==(Quaternion a, Quaternion b) =>
            MathF.Abs(Dot(a, b)) > 1f - 1e-6f;
        public static bool operator !=(Quaternion a, Quaternion b) => !(a == b);

        public static float Dot(Quaternion a, Quaternion b) =>
            a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        public bool Equals(Quaternion other) => this == other;
        public override bool Equals(object? obj) => obj is Quaternion q && this == q;
        public override int GetHashCode() => HashCode.Combine(x, y, z, w);
        public override string ToString() => $"({x:F4}, {y:F4}, {z:F4}, {w:F4})";
    }
}
