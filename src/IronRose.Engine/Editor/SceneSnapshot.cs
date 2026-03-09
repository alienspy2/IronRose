using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using IronRose.Rendering;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    public class SceneSnapshot
    {
        public GameObjectSnapshot[] GameObjects { get; init; } = Array.Empty<GameObjectSnapshot>();
        public int TotalGameObjects { get; init; }
        public int FrameCount { get; init; }
        public RenderSettingsSnapshot RenderSettings { get; init; } = new();
        public PostProcessEffectSnapshot[] PostProcessEffects { get; init; } = Array.Empty<PostProcessEffectSnapshot>();

        public static SceneSnapshot Capture()
        {
            var gos = SceneManager.AllGameObjects;
            var snapshots = new GameObjectSnapshot[gos.Count];
            for (int i = 0; i < gos.Count; i++)
                snapshots[i] = GameObjectSnapshot.From(gos[i]);

            return new SceneSnapshot
            {
                GameObjects = snapshots,
                TotalGameObjects = gos.Count,
                FrameCount = Time.frameCount,
                RenderSettings = RenderSettingsSnapshot.Capture(),
                PostProcessEffects = PostProcessEffectSnapshot.CaptureAll(),
            };
        }
    }

    public class PostProcessEffectSnapshot
    {
        public string Name { get; init; } = "";
        public bool Enabled { get; init; }
        public PostProcessParamSnapshot[] Params { get; init; } = Array.Empty<PostProcessParamSnapshot>();

        public static PostProcessEffectSnapshot[] CaptureAll()
        {
            var stack = RoseEngine.RenderSettings.postProcessing;
            if (stack == null) return Array.Empty<PostProcessEffectSnapshot>();

            var effects = stack.Effects;
            var result = new PostProcessEffectSnapshot[effects.Count];
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                var ps = e.GetParameters();
                var paramSnapshots = new PostProcessParamSnapshot[ps.Count];
                for (int j = 0; j < ps.Count; j++)
                {
                    var p = ps[j];
                    string? val = null;
                    try
                    {
                        var v = p.GetValue();
                        val = v is float f
                            ? f.ToString(CultureInfo.InvariantCulture)
                            : v?.ToString();
                    }
                    catch { }

                    paramSnapshots[j] = new PostProcessParamSnapshot
                    {
                        Name = p.Name,
                        TypeName = p.ValueType.Name,
                        Value = val,
                        Min = p.Min,
                        Max = p.Max,
                    };
                }

                result[i] = new PostProcessEffectSnapshot
                {
                    Name = e.Name,
                    Enabled = e.Enabled,
                    Params = paramSnapshots,
                };
            }
            return result;
        }
    }

    public class PostProcessParamSnapshot
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        public string? Value { get; init; }
        public float Min { get; init; }
        public float Max { get; init; }
    }

    public class RenderSettingsSnapshot
    {
        // Skybox
        public string? SkyboxTextureGuid { get; init; }
        public float SkyboxExposure { get; init; }
        public float SkyboxRotation { get; init; }
        // Ambient
        public float AmbientIntensity { get; init; }
        // Sky
        public float SkyZenithIntensity { get; init; }
        public float SkyHorizonIntensity { get; init; }
        public float SunIntensity { get; init; }
        // FSR Upscaler
        public bool FsrEnabled { get; init; }
        public FsrScaleMode FsrScaleMode { get; init; }
        public float FsrCustomScale { get; init; }
        public float FsrSharpness { get; init; }
        public float FsrJitterScale { get; init; }
        // SSIL
        public bool SsilEnabled { get; init; }
        public float SsilRadius { get; init; }
        public float SsilFalloffScale { get; init; }
        public int SsilSliceCount { get; init; }
        public int SsilStepsPerSlice { get; init; }
        public bool SsilIndirectEnabled { get; init; }
        public float SsilIndirectBoost { get; init; }
        public float SsilSaturationBoost { get; init; }
        public float SsilAoIntensity { get; init; }

        public static RenderSettingsSnapshot Capture() => new()
        {
            SkyboxTextureGuid = RoseEngine.RenderSettings.skyboxTextureGuid,
            SkyboxExposure = RoseEngine.RenderSettings.skyboxExposure,
            SkyboxRotation = RoseEngine.RenderSettings.skyboxRotation,
            AmbientIntensity = RoseEngine.RenderSettings.ambientIntensity,
            SkyZenithIntensity = RoseEngine.RenderSettings.skyZenithIntensity,
            SkyHorizonIntensity = RoseEngine.RenderSettings.skyHorizonIntensity,
            SunIntensity = RoseEngine.RenderSettings.sunIntensity,
            FsrEnabled = RoseEngine.RenderSettings.fsrEnabled,
            FsrScaleMode = RoseEngine.RenderSettings.fsrScaleMode,
            FsrCustomScale = RoseEngine.RenderSettings.fsrCustomScale,
            FsrSharpness = RoseEngine.RenderSettings.fsrSharpness,
            FsrJitterScale = RoseEngine.RenderSettings.fsrJitterScale,
            SsilEnabled = RoseEngine.RenderSettings.ssilEnabled,
            SsilRadius = RoseEngine.RenderSettings.ssilRadius,
            SsilFalloffScale = RoseEngine.RenderSettings.ssilFalloffScale,
            SsilSliceCount = RoseEngine.RenderSettings.ssilSliceCount,
            SsilStepsPerSlice = RoseEngine.RenderSettings.ssilStepsPerSlice,
            SsilIndirectEnabled = RoseEngine.RenderSettings.ssilIndirectEnabled,
            SsilIndirectBoost = RoseEngine.RenderSettings.ssilIndirectBoost,
            SsilSaturationBoost = RoseEngine.RenderSettings.ssilSaturationBoost,
            SsilAoIntensity = RoseEngine.RenderSettings.ssilAoIntensity,
        };
    }

    public class GameObjectSnapshot
    {
        public int InstanceId { get; init; }
        public string Name { get; init; } = "";
        public bool ActiveSelf { get; init; }
        public int? ParentId { get; init; }
        public ComponentSnapshot[] Components { get; init; } = Array.Empty<ComponentSnapshot>();

        public static GameObjectSnapshot From(GameObject go)
        {
            var parentId = go.transform.parent?.gameObject.GetInstanceID();

            var comps = go.InternalComponents;
            var compSnapshots = new ComponentSnapshot[comps.Count];
            for (int i = 0; i < comps.Count; i++)
                compSnapshots[i] = ComponentSnapshot.From(comps[i]);

            return new GameObjectSnapshot
            {
                InstanceId = go.GetInstanceID(),
                Name = go.name,
                ActiveSelf = go.activeSelf,
                ParentId = parentId,
                Components = compSnapshots,
            };
        }
    }

    public class ComponentSnapshot
    {
        public string TypeName { get; init; } = "";
        public FieldSnapshot[] Fields { get; init; } = Array.Empty<FieldSnapshot>();

        public static ComponentSnapshot From(Component comp)
        {
            var type = comp.GetType();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var snapshots = new List<FieldSnapshot>();

            foreach (var field in fields)
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                // 엔진 내부 필드 스킵
                if (field.Name.StartsWith("_is") || field.Name == "gameObject") continue;

                bool isPublic = field.IsPublic;
                bool hasSerializeField = field.GetCustomAttribute<SerializeFieldAttribute>() != null;
                bool hasHideInInspector = field.GetCustomAttribute<HideInInspectorAttribute>() != null;

                // public이거나 [SerializeField]가 있어야 표시
                if (!isPublic && !hasSerializeField) continue;

                // [HideInInspector] 면 숨김
                if (hasHideInInspector) continue;

                var header = field.GetCustomAttribute<HeaderAttribute>();
                var range = field.GetCustomAttribute<RangeAttribute>();
                var tooltip = field.GetCustomAttribute<TooltipAttribute>();

                string? valueStr = null;
                try
                {
                    var val = field.GetValue(comp);
                    valueStr = val?.ToString();
                }
                catch { /* skip unreadable fields */ }

                snapshots.Add(new FieldSnapshot
                {
                    Name = field.Name,
                    TypeName = field.FieldType.Name,
                    Value = valueStr,
                    Header = header?.header,
                    RangeMin = range?.min,
                    RangeMax = range?.max,
                    Tooltip = tooltip?.tooltip,
                });
            }

            return new ComponentSnapshot
            {
                TypeName = type.Name,
                Fields = snapshots.ToArray(),
            };
        }
    }

    public class FieldSnapshot
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        public string? Value { get; init; }
        public string? Header { get; init; }
        public float? RangeMin { get; init; }
        public float? RangeMax { get; init; }
        public string? Tooltip { get; init; }
    }
}
