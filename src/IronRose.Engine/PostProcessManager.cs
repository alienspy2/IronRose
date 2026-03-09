using System;
using System.Collections.Generic;
using RoseEngine;
using IronRose.Rendering;

namespace IronRose.Engine
{
    /// <summary>
    /// Volume 기반 Post Processing 관리자.
    /// 카메라 위치에 따라 PostProcessVolume들의 가중 평균을 계산하고
    /// PostProcessStack의 이펙트에 블렌딩된 값을 적용.
    /// </summary>
    public class PostProcessManager : IDisposable
    {
        public static PostProcessManager? Instance { get; private set; }

        /// <summary>현재 PP가 활성 상태인지. false면 RenderSystem이 PP를 건너뛴다.</summary>
        public bool IsPostProcessActive { get; private set; }

        public void Initialize()
        {
            Instance = this;
            Debug.Log("[PostProcessManager] Initialized");
        }

        /// <summary>매 프레임 호출. 카메라 위치 기반으로 Volume 블렌딩 수행.</summary>
        /// <param name="cameraPos">카메라 월드 위치.</param>
        /// <param name="targetStack">블렌딩 결과를 적용할 PostProcessStack. null이면 RenderSettings.postProcessing 사용.</param>
        public void Update(Vector3 cameraPos, PostProcessStack? targetStack = null)
        {
            var stack = targetStack ?? RenderSettings.postProcessing;
            if (stack == null || stack.Effects.Count == 0)
            {
                IsPostProcessActive = false;
                return;
            }

            // 1. 각 Volume의 effectiveWeight 계산
            float totalWeight = 0f;
            var activeVolumes = new List<(PostProcessVolume vol, float effectiveWeight)>();

            foreach (var vol in PostProcessVolume._allVolumes)
            {
                if (vol.profile == null || vol.weight <= 0f)
                    continue;
                if (vol.gameObject == null || !vol.gameObject.activeSelf)
                    continue;
                if (!vol.enabled)
                    continue;

                var box = vol.gameObject.GetComponent<BoxCollider>();
                if (box == null)
                    continue;

                float distFactor = ComputeDistanceFactor(cameraPos, vol);
                if (distFactor <= 0f) continue;

                float ew = vol.weight * distFactor;
                activeVolumes.Add((vol, ew));
                totalWeight += ew;
            }

            if (activeVolumes.Count == 0 || totalWeight <= 0f)
            {
                IsPostProcessActive = false;
                return;
            }

            IsPostProcessActive = true;

            // 2. 이펙트별 weighted average 계산 + 적용
            foreach (var effect in stack.Effects)
            {
                var neutralValues = effect.GetNeutralValues();
                var blendedValues = new Dictionary<string, float>();

                // 초기화: neutral로 시작
                foreach (var kvp in neutralValues)
                    blendedValues[kvp.Key] = 0f;

                foreach (var (vol, ew) in activeVolumes)
                {
                    var ov = vol.profile!.TryGetEffect(effect.Name);
                    if (ov != null && ov.enabled)
                    {
                        // Volume에 이펙트 오버라이드가 있음
                        foreach (var paramName in blendedValues.Keys)
                        {
                            float value;
                            if (ov.TryGetParam(paramName, out var ovVal))
                                value = ovVal;
                            else
                                neutralValues.TryGetValue(paramName, out value);

                            blendedValues[paramName] += value * ew;
                        }
                    }
                    else
                    {
                        // Volume에 이펙트 없음 → neutral 값으로 참여
                        foreach (var paramName in blendedValues.Keys)
                        {
                            neutralValues.TryGetValue(paramName, out var neutralVal);
                            blendedValues[paramName] += neutralVal * ew;
                        }
                    }
                }

                // weighted average (totalWeight는 이미 계산됨)
                foreach (var paramName in new List<string>(blendedValues.Keys))
                    blendedValues[paramName] /= totalWeight;

                // 블렌딩된 값을 실제 이펙트에 적용
                foreach (var param in effect.GetParameters())
                {
                    if (blendedValues.TryGetValue(param.Name, out var blendedVal))
                    {
                        if (param.ValueType == typeof(float))
                            param.SetValue(blendedVal);
                        else if (param.ValueType == typeof(int))
                            param.SetValue((int)MathF.Round(blendedVal));
                        else if (param.ValueType == typeof(bool))
                            param.SetValue(blendedVal >= 0.5f);
                    }
                }

                // 이펙트 활성 상태 설정: 하나라도 enabled override가 있으면 활성
                bool anyEnabled = false;
                foreach (var (vol, _) in activeVolumes)
                {
                    var ov = vol.profile!.TryGetEffect(effect.Name);
                    if (ov != null && ov.enabled)
                    {
                        anyEnabled = true;
                        break;
                    }
                }
                effect.Enabled = anyEnabled;
            }
        }

        /// <summary>
        /// 카메라와 Volume 사이의 distance factor (0~1).
        /// inner bounds 내부 → 1.0
        /// blendDistance == 0 && 외부 → 0.0
        /// blendDistance > 0 → 1.0 - (inner surface 까지 거리 / blendDistance), clamp 0~1
        /// </summary>
        private static float ComputeDistanceFactor(Vector3 cameraPos, PostProcessVolume vol)
        {
            var innerBounds = vol.GetInnerBounds();

            if (innerBounds.Contains(cameraPos))
                return 1f;

            if (vol.blendDistance <= 0f)
                return 0f;

            // inner surface 까지의 거리
            float sqrDist = innerBounds.SqrDistance(cameraPos);
            float dist = MathF.Sqrt(sqrDist);

            if (dist >= vol.blendDistance)
                return 0f;

            return 1f - (dist / vol.blendDistance);
        }

        public void Reset()
        {
            IsPostProcessActive = false;
        }

        public void Dispose()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
