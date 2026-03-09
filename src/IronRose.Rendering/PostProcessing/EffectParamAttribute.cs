using System;

namespace IronRose.Rendering
{
    [AttributeUsage(AttributeTargets.Property)]
    public class EffectParamAttribute : Attribute
    {
        public string DisplayName { get; }
        public float Min { get; set; } = float.MinValue;
        public float Max { get; set; } = float.MaxValue;

        public EffectParamAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }
}
