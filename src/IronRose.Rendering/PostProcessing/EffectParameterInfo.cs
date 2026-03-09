using System;

namespace IronRose.Rendering
{
    public sealed class EffectParameterInfo
    {
        public string Name { get; }
        public Type ValueType { get; }
        public float Min { get; }
        public float Max { get; }
        public Func<object> GetValue { get; }
        public Action<object> SetValue { get; }

        public EffectParameterInfo(string name, Type valueType, float min, float max,
            Func<object> getValue, Action<object> setValue)
        {
            Name = name;
            ValueType = valueType;
            Min = min;
            Max = max;
            GetValue = getValue;
            SetValue = setValue;
        }
    }
}
