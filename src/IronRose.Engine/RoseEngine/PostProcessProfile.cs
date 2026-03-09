using System.Collections.Generic;

namespace RoseEngine
{
    /// <summary>
    /// Post Processing 프로파일 — 이펙트별 파라미터 오버라이드를 저장.
    /// .ppprofile TOML 에셋 파일에 직렬화됨.
    ///
    /// 구조: effectName → paramName → float value
    /// (bool은 0/1, int는 float 캐스트)
    /// </summary>
    public class PostProcessProfile
    {
        public string name { get; set; } = "Default PP";

        /// <summary>이펙트별 오버라이드. key: effect.Name, value: param overrides.</summary>
        public Dictionary<string, EffectOverride> effects { get; set; } = new();

        /// <summary>프로파일에 이펙트 오버라이드가 있는지 확인.</summary>
        public bool HasEffect(string effectName) => effects.ContainsKey(effectName);

        /// <summary>이펙트 오버라이드를 가져온다. 없으면 null.</summary>
        public EffectOverride? TryGetEffect(string effectName)
        {
            return effects.TryGetValue(effectName, out var ov) ? ov : null;
        }

        /// <summary>이펙트 오버라이드를 가져오거나 새로 생성한다.</summary>
        public EffectOverride GetOrAddEffect(string effectName)
        {
            if (!effects.TryGetValue(effectName, out var ov))
            {
                ov = new EffectOverride { effectName = effectName };
                effects[effectName] = ov;
            }
            return ov;
        }
    }

    /// <summary>
    /// 개별 이펙트에 대한 파라미터 오버라이드.
    /// </summary>
    public class EffectOverride
    {
        public string effectName { get; set; } = "";
        public bool enabled { get; set; } = true;

        /// <summary>파라미터 오버라이드. key: param DisplayName, value: float value.</summary>
        public Dictionary<string, float> parameters { get; set; } = new();

        public bool TryGetParam(string paramName, out float value)
        {
            return parameters.TryGetValue(paramName, out value);
        }

        public void SetParam(string paramName, float value)
        {
            parameters[paramName] = value;
        }
    }
}
