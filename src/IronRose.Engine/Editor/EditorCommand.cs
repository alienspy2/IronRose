using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using IronRose.Rendering;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    public abstract class EditorCommand
    {
        public abstract void Execute();

        protected static object? ParseValue(Type type, string raw)
        {
            try
            {
                if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return bool.Parse(raw);
                if (type == typeof(string)) return raw;
                if (type.IsEnum) return Enum.Parse(type, raw);
            }
            catch { }
            return null;
        }
    }

    public class SetFieldCommand : EditorCommand
    {
        public int GameObjectId { get; init; }
        public string ComponentType { get; init; } = "";
        public string FieldName { get; init; } = "";
        public string NewValue { get; init; } = "";

        public override void Execute()
        {
            var go = SceneManager.AllGameObjects
                .FirstOrDefault(g => g.GetInstanceID() == GameObjectId);
            if (go == null) return;

            var comp = go.InternalComponents
                .FirstOrDefault(c => c.GetType().Name == ComponentType);
            if (comp == null) return;

            var field = comp.GetType().GetField(FieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return;

            var value = ParseValue(field.FieldType, NewValue);
            if (value != null)
                field.SetValue(comp, value);
        }

        private new static object? ParseValue(Type type, string raw)
        {
            try
            {
                if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
                if (type == typeof(bool)) return bool.Parse(raw);
                if (type == typeof(string)) return raw;
                if (type == typeof(Vector3)) return ParseVector3(raw);
                if (type == typeof(Color)) return ParseColor(raw);
                if (type.IsEnum) return Enum.Parse(type, raw);
            }
            catch { /* parse failure → ignore */ }
            return null;
        }

        private static Vector3 ParseVector3(string raw)
        {
            // Format: "X, Y, Z" or "(X, Y, Z)"
            var cleaned = raw.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            return new Vector3(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
        }

        private static Color ParseColor(string raw)
        {
            var cleaned = raw.Trim('(', ')', ' ');
            var parts = cleaned.Split(',');
            return new Color(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
                parts.Length > 3 ? float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) : 1f);
        }
    }

    public class SetActiveCommand : EditorCommand
    {
        public int GameObjectId { get; init; }
        public bool Active { get; init; }

        public override void Execute()
        {
            var go = SceneManager.AllGameObjects
                .FirstOrDefault(g => g.GetInstanceID() == GameObjectId);
            go?.SetActive(Active);
        }
    }

    public class SetRenderSettingsCommand : EditorCommand
    {
        public string PropertyName { get; init; } = "";
        public string NewValue { get; init; } = "";

        public override void Execute()
        {
            var prop = typeof(RoseEngine.RenderSettings).GetProperty(PropertyName,
                BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return;

            var value = ParseValue(prop.PropertyType, NewValue);
            if (value != null)
                prop.SetValue(null, value);
        }
    }

    public class SetPostProcessCommand : EditorCommand
    {
        public string EffectName { get; init; } = "";
        public string ParamName { get; init; } = "";
        public string NewValue { get; init; } = "";

        public override void Execute()
        {
            var stack = RoseEngine.RenderSettings.postProcessing;
            if (stack == null) return;

            var effect = stack.Effects.FirstOrDefault(e => e.Name == EffectName);
            if (effect == null) return;

            if (ParamName == "Enabled")
            {
                if (bool.TryParse(NewValue, out var enabled))
                    effect.Enabled = enabled;
                return;
            }

            var param = effect.GetParameters().FirstOrDefault(p => p.Name == ParamName);
            if (param == null) return;

            var value = ParseValue(param.ValueType, NewValue);
            if (value != null)
                param.SetValue(value);
        }
    }

    public class PauseCommand : EditorCommand
    {
        public bool Pause { get; init; }

        public override void Execute()
        {
            if (Pause)
                Application.Pause();
            else
                Application.Resume();
        }
    }
}
