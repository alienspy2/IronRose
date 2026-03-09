using System;
using System.Collections.Generic;

namespace RoseEngine
{
    /// <summary>
    /// 애니메이션 이벤트 — 특정 시간에 MonoBehaviour 메서드를 호출.
    /// </summary>
    public struct AnimationEvent
    {
        public float time;
        public string functionName;
        public float floatParameter;
        public int intParameter;
        public string? stringParameter;

        public AnimationEvent(float time, string functionName)
        {
            this.time = time;
            this.functionName = functionName;
            floatParameter = 0f;
            intParameter = 0;
            stringParameter = null;
        }
    }

    /// <summary>
    /// 애니메이션 클립 에셋 — propertyPath별 커브 + 이벤트 묶음.
    /// propertyPath 규칙: "localPosition.x", "SpriteRenderer.color.r" 등.
    /// </summary>
    public class AnimationClip : Object
    {
        /// <summary>propertyPath → AnimationCurve 매핑.</summary>
        public Dictionary<string, AnimationCurve> curves { get; } = new();

        /// <summary>클립 총 길이 (초).</summary>
        public float length { get; set; }

        /// <summary>에디터 표시용 프레임 레이트.</summary>
        public float frameRate { get; set; } = 60f;

        /// <summary>재생 종료 시 동작.</summary>
        public WrapMode wrapMode { get; set; } = WrapMode.Once;

        /// <summary>시간순 정렬된 이벤트 목록.</summary>
        public List<AnimationEvent> events { get; } = new();

        /// <summary>
        /// 커브를 추가하거나 교체.
        /// </summary>
        public void SetCurve(string propertyPath, AnimationCurve curve)
        {
            curves[propertyPath] = curve;
        }

        /// <summary>
        /// 모든 커브 중 최대 시간을 계산하여 length를 자동 갱신.
        /// </summary>
        public void RecalculateLength()
        {
            float max = 0f;
            foreach (var curve in curves.Values)
            {
                if (curve.length > 0)
                {
                    float last = curve[curve.length - 1].time;
                    if (last > max) max = last;
                }
            }
            length = max;
        }
    }
}
