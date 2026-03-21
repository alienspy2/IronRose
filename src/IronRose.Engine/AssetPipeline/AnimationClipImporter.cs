// ------------------------------------------------------------
// @file    AnimationClipImporter.cs
// @brief   .anim (TOML) 파일을 AnimationClip으로 임포트/익스포트한다.
//          커브([[curves]])와 이벤트([[events]])를 TomlConfigArray로 처리한다.
// @deps    IronRose.Engine/TomlConfig, IronRose.Engine/TomlConfigArray,
//          RoseEngine (EditorDebug, AnimationClip, AnimationCurve, Keyframe,
//          AnimationEvent, WrapMode), AssetPipeline/RoseMetadata
// @exports
//   class AnimationClipImporter
//     Import(string, RoseMetadata?): AnimationClip?             — .anim 파일에서 로드
//     static Export(AnimationClip, string): void                — AnimationClip을 TOML로 저장
// @note    기존 로컬 ToFloat 메서드를 제거하고 TomlConfig.GetFloat()로 대체.
//          Export 시 string_param이 null인 경우 SetValue를 호출하지 않는다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using RoseEngine;
using IronRose.Engine;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// .anim (TOML) 파일을 AnimationClip으로 임포트/익스포트.
    ///
    /// TOML 구조:
    /// frame_rate = 60
    /// wrap_mode = "Loop"
    /// length = 2.0
    ///
    /// [[curves]]
    /// path = "localPosition.x"
    /// [[curves.keys]]
    /// time = 0.0
    /// value = 0.0
    /// in_tangent = 0.0
    /// out_tangent = 1.0
    ///
    /// [[events]]
    /// time = 1.0
    /// function = "OnHit"
    /// float_param = 0.0
    /// int_param = 0
    /// string_param = ""
    /// </summary>
    public class AnimationClipImporter
    {
        public AnimationClip? Import(string path, RoseMetadata? meta = null)
        {
            if (!File.Exists(path))
            {
                EditorDebug.LogError($"[AnimationClipImporter] File not found: {path}");
                return null;
            }

            var config = TomlConfig.LoadFile(path, "[AnimationClipImporter]");
            if (config == null) return null;

            return ParseClip(config, path);
        }

        private static AnimationClip ParseClip(TomlConfig config, string path)
        {
            var clip = new AnimationClip
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            clip.frameRate = config.GetFloat("frame_rate", clip.frameRate);
            var wmStr = config.GetString("wrap_mode", "");
            if (!string.IsNullOrEmpty(wmStr) && Enum.TryParse<WrapMode>(wmStr, true, out var wm))
                clip.wrapMode = wm;
            clip.length = config.GetFloat("length", clip.length);

            // Curves
            var curvesArr = config.GetArray("curves");
            if (curvesArr != null)
            {
                foreach (var curveConfig in curvesArr)
                {
                    var curvePath = curveConfig.GetString("path", "");
                    if (string.IsNullOrEmpty(curvePath)) continue;

                    var curve = new AnimationCurve();
                    var keysArr = curveConfig.GetArray("keys");
                    if (keysArr != null)
                    {
                        foreach (var keyConfig in keysArr)
                        {
                            float time = keyConfig.GetFloat("time", 0f);
                            float value = keyConfig.GetFloat("value", 0f);
                            float inTan = keyConfig.GetFloat("in_tangent", 0f);
                            float outTan = keyConfig.GetFloat("out_tangent", 0f);
                            curve.AddKey(new Keyframe(time, value, inTan, outTan));
                        }
                    }
                    clip.SetCurve(curvePath, curve);
                }
            }

            // Events
            var eventsArr = config.GetArray("events");
            if (eventsArr != null)
            {
                foreach (var evtConfig in eventsArr)
                {
                    float time = evtConfig.GetFloat("time", 0f);
                    string func = evtConfig.GetString("function", "");
                    float fp = evtConfig.GetFloat("float_param", 0f);
                    int ip = evtConfig.GetInt("int_param", 0);
                    string? sp = evtConfig.GetString("string_param", "");
                    if (string.IsNullOrEmpty(sp)) sp = null;

                    clip.events.Add(new AnimationEvent(time, func)
                    {
                        floatParameter = fp,
                        intParameter = ip,
                        stringParameter = sp,
                    });
                }
            }

            if (clip.length <= 0f)
                clip.RecalculateLength();

            EditorDebug.Log($"[AnimationClipImporter] Loaded: {path} ({clip.curves.Count} curves, {clip.events.Count} events, {clip.length:F2}s)");
            return clip;
        }

        /// <summary>AnimationClip을 .anim TOML 파일로 내보내기.</summary>
        public static void Export(AnimationClip clip, string path)
        {
            var config = TomlConfig.CreateEmpty();
            config.SetValue("frame_rate", (double)clip.frameRate);
            config.SetValue("wrap_mode", clip.wrapMode.ToString());
            config.SetValue("length", (double)clip.length);

            // Curves
            var curvesArr = new TomlConfigArray();
            foreach (var (curvePath, curve) in clip.curves)
            {
                var curveConfig = TomlConfig.CreateEmpty();
                curveConfig.SetValue("path", curvePath);

                var keysArr = new TomlConfigArray();
                for (int i = 0; i < curve.length; i++)
                {
                    var key = curve[i];
                    var keyConfig = TomlConfig.CreateEmpty();
                    keyConfig.SetValue("time", (double)key.time);
                    keyConfig.SetValue("value", (double)key.value);
                    keyConfig.SetValue("in_tangent", (double)key.inTangent);
                    keyConfig.SetValue("out_tangent", (double)key.outTangent);
                    keysArr.Add(keyConfig);
                }
                curveConfig.SetArray("keys", keysArr);
                curvesArr.Add(curveConfig);
            }
            config.SetArray("curves", curvesArr);

            // Events
            if (clip.events.Count > 0)
            {
                var eventsArr = new TomlConfigArray();
                foreach (var evt in clip.events)
                {
                    var evtConfig = TomlConfig.CreateEmpty();
                    evtConfig.SetValue("time", (double)evt.time);
                    evtConfig.SetValue("function", evt.functionName);
                    evtConfig.SetValue("float_param", (double)evt.floatParameter);
                    evtConfig.SetValue("int_param", (long)evt.intParameter);
                    if (evt.stringParameter != null)
                        evtConfig.SetValue("string_param", evt.stringParameter);
                    eventsArr.Add(evtConfig);
                }
                config.SetArray("events", eventsArr);
            }

            config.SaveToFile(path);
            EditorDebug.Log($"[AnimationClipImporter] Exported: {path}");
        }
    }
}
