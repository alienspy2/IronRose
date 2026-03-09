using System;

namespace RoseEngine
{
    [AttributeUsage(AttributeTargets.Field)]
    public class SerializeFieldAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class HideInInspectorAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Method)]
    public class HeaderAttribute : Attribute
    {
        public string header;
        public HeaderAttribute(string header) { this.header = header; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class RangeAttribute : Attribute
    {
        public float min, max;
        public RangeAttribute(float min, float max) { this.min = min; this.max = max; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class TooltipAttribute : Attribute
    {
        public string tooltip;
        public TooltipAttribute(string tooltip) { this.tooltip = tooltip; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class SpaceAttribute : Attribute
    {
        public float height;
        public SpaceAttribute(float height = 8f) { this.height = height; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class TextAreaAttribute : Attribute
    {
        public int minLines, maxLines;
        public TextAreaAttribute(int minLines = 3, int maxLines = 3)
        {
            this.minLines = minLines;
            this.maxLines = maxLines;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class RequireComponentAttribute : Attribute
    {
        public Type requiredComponent;
        public RequireComponentAttribute(Type requiredComponent) { this.requiredComponent = requiredComponent; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class DisallowMultipleComponentAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public class AddComponentMenuAttribute : Attribute
    {
        public string menuName;
        public AddComponentMenuAttribute(string menuName) { this.menuName = menuName; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class ExecuteInEditModeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public class ExecuteAlwaysAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ReadOnlyInInspectorAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class IntDropdownAttribute : Attribute
    {
        public int[] values;
        public string[] labels;
        public IntDropdownAttribute(params int[] values)
        {
            this.values = values;
            this.labels = Array.ConvertAll(values, v => v.ToString());
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class ContextMenuAttribute : Attribute
    {
        public string menuItem;
        public ContextMenuAttribute(string menuItem) { this.menuItem = menuItem; }
    }
}
