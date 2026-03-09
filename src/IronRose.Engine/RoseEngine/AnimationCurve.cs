using System;
using System.Collections.Generic;

namespace RoseEngine
{
    /// <summary>
    /// 애니메이션 커브의 단일 키프레임.
    /// </summary>
    public struct Keyframe : IComparable<Keyframe>
    {
        public float time;
        public float value;
        public float inTangent;
        public float outTangent;

        public Keyframe(float time, float value)
        {
            this.time = time;
            this.value = value;
            inTangent = 0f;
            outTangent = 0f;
        }

        public Keyframe(float time, float value, float inTangent, float outTangent)
        {
            this.time = time;
            this.value = value;
            this.inTangent = inTangent;
            this.outTangent = outTangent;
        }

        public int CompareTo(Keyframe other) => time.CompareTo(other.time);
    }

    /// <summary>
    /// 시간 → 값 보간 커브. Hermite cubic spline 기반.
    /// </summary>
    public class AnimationCurve
    {
        private readonly List<Keyframe> _keys = new();

        public int length => _keys.Count;

        public Keyframe this[int index]
        {
            get => _keys[index];
            set => _keys[index] = value;
        }

        public AnimationCurve() { }

        public AnimationCurve(params Keyframe[] keys)
        {
            _keys.AddRange(keys);
            _keys.Sort();
        }

        /// <summary>키프레임 추가 (자동 정렬). 삽입된 인덱스 반환.</summary>
        public int AddKey(float time, float value)
        {
            return AddKey(new Keyframe(time, value));
        }

        public int AddKey(Keyframe key)
        {
            int idx = _keys.BinarySearch(key);
            if (idx < 0) idx = ~idx;
            _keys.Insert(idx, key);
            return idx;
        }

        /// <summary>인덱스로 키프레임 제거.</summary>
        public void RemoveKey(int index)
        {
            if (index >= 0 && index < _keys.Count)
                _keys.RemoveAt(index);
        }

        /// <summary>전체 키프레임 배열 반환 (스냅샷용).</summary>
        public Keyframe[] GetKeys() => _keys.ToArray();

        /// <summary>전체 키프레임을 교체 (Undo 복원용).</summary>
        public void SetKeys(Keyframe[] keys)
        {
            _keys.Clear();
            _keys.AddRange(keys);
            _keys.Sort();
        }

        /// <summary>인덱스의 키프레임을 새 값으로 교체 (자동 재정렬). 새 인덱스 반환.</summary>
        public int MoveKey(int index, Keyframe key)
        {
            if (index < 0 || index >= _keys.Count)
                return -1;

            _keys.RemoveAt(index);
            return AddKey(key);
        }

        /// <summary>
        /// 주어진 시간에서의 보간 값 계산.
        /// Binary search + Hermite cubic interpolation.
        /// </summary>
        public float Evaluate(float time)
        {
            int count = _keys.Count;
            if (count == 0) return 0f;
            if (count == 1) return _keys[0].value;

            // 범위 밖 — 클램프
            if (time <= _keys[0].time) return _keys[0].value;
            if (time >= _keys[count - 1].time) return _keys[count - 1].value;

            // Binary search로 구간 찾기
            int lo = 0, hi = count - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].time <= time)
                    lo = mid;
                else
                    hi = mid;
            }

            var k0 = _keys[lo];
            var k1 = _keys[hi];

            float dt = k1.time - k0.time;
            if (dt <= 0f) return k0.value;

            float t = (time - k0.time) / dt;

            // Hermite cubic spline
            return HermiteInterpolate(k0.value, k0.outTangent * dt, k1.value, k1.inTangent * dt, t);
        }

        /// <summary>Hermite basis: h00, h10, h01, h11.</summary>
        private static float HermiteInterpolate(float p0, float m0, float p1, float m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        // ── Factory Methods ──

        /// <summary>시작 → 끝 직선 보간 커브.</summary>
        public static AnimationCurve Linear(float startTime, float startValue, float endTime, float endValue)
        {
            float dt = endTime - startTime;
            float slope = dt > 0f ? (endValue - startValue) / dt : 0f;

            return new AnimationCurve(
                new Keyframe(startTime, startValue, slope, slope),
                new Keyframe(endTime, endValue, slope, slope)
            );
        }

        /// <summary>0→1 EaseInOut 커브 (tangent = 0).</summary>
        public static AnimationCurve EaseInOut(float startTime, float startValue, float endTime, float endValue)
        {
            return new AnimationCurve(
                new Keyframe(startTime, startValue, 0f, 0f),
                new Keyframe(endTime, endValue, 0f, 0f)
            );
        }

        /// <summary>상수 값 커브.</summary>
        public static AnimationCurve Constant(float startTime, float endTime, float value)
        {
            return new AnimationCurve(
                new Keyframe(startTime, value, 0f, 0f),
                new Keyframe(endTime, value, 0f, 0f)
            );
        }
    }
}
