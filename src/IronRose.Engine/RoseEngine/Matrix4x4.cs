using System;
using System.Runtime.CompilerServices;
using SN = System.Numerics;

namespace RoseEngine
{
    /// <summary>
    /// 4x4 변환 행렬 — Unity 호환 왼손 좌표계.
    /// 내부 저장/곱셈은 System.Numerics.Matrix4x4 (SIMD 최적화) 위임.
    /// LookAt, Perspective 만 왼손 좌표계로 직접 구현.
    /// </summary>
    public struct Matrix4x4
    {
        internal SN.Matrix4x4 inner;

        public static Matrix4x4 identity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new() { inner = SN.Matrix4x4.Identity };
        }

        /// <summary>Translation * Rotation * Scale (Unity 순서)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix4x4 TRS(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            // System.Numerics: S * R * T = TRS (row-major, post-multiply)
            var s = SN.Matrix4x4.CreateScale(scale.x, scale.y, scale.z);
            var r = SN.Matrix4x4.CreateFromQuaternion(new SN.Quaternion(rot.x, rot.y, rot.z, rot.w));
            var t = SN.Matrix4x4.CreateTranslation(pos.x, pos.y, pos.z);
            return new Matrix4x4 { inner = s * r * t };
        }

        /// <summary>왼손 좌표계 원근 투영 (Unity 호환, depth [0,1])</summary>
        public static Matrix4x4 Perspective(float fovDegrees, float aspect, float near, float far)
        {
            float fovRad = fovDegrees * MathF.PI / 180f;
            float h = 1f / MathF.Tan(fovRad * 0.5f);
            float w = h / aspect;
            float range = far / (far - near);

            // 왼손 좌표계: +Z가 화면 안쪽, depth [0, 1]
            return new Matrix4x4
            {
                inner = new SN.Matrix4x4(
                    w,  0,  0,           0,
                    0,  h,  0,           0,
                    0,  0,  range,       1,  // +1 = 왼손 (오른손은 -1)
                    0,  0, -near * range, 0)
            };
        }

        /// <summary>왼손 좌표계 뷰 행렬 (Unity 호환)</summary>
        public static Matrix4x4 LookAt(Vector3 eye, Vector3 target, Vector3 up)
        {
            // 왼손: zAxis = normalize(target - eye)
            // 오른손은 normalize(eye - target)
            Vector3 zAxis = (target - eye).normalized;
            Vector3 xAxis = Vector3.Cross(up, zAxis).normalized;
            Vector3 yAxis = Vector3.Cross(zAxis, xAxis);

            float tx = -Vector3.Dot(xAxis, eye);
            float ty = -Vector3.Dot(yAxis, eye);
            float tz = -Vector3.Dot(zAxis, eye);

            return new Matrix4x4
            {
                inner = new SN.Matrix4x4(
                    xAxis.x, yAxis.x, zAxis.x, 0,
                    xAxis.y, yAxis.y, zAxis.y, 0,
                    xAxis.z, yAxis.z, zAxis.z, 0,
                    tx,      ty,      tz,      1)
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Matrix4x4 operator *(Matrix4x4 a, Matrix4x4 b)
        {
            // System.Numerics 곱셈 = SIMD 가속
            return new Matrix4x4 { inner = a.inner * b.inner };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SN.Matrix4x4 ToNumerics() => inner;

        public override string ToString() => inner.ToString();
    }
}
