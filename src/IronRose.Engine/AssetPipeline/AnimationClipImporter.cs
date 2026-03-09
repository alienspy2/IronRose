using System;
using System.Collections.Generic;
using System.IO;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;

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
                Debug.LogError($"[AnimationClipImporter] File not found: {path}");
                return null;
            }

            try
            {
                var text = File.ReadAllText(path);
                var doc = Toml.ToModel(text);
                return ParseClip(doc, path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AnimationClipImporter] Parse failed: {path} — {ex.Message}");
                return null;
            }
        }

        private static AnimationClip ParseClip(TomlTable doc, string path)
        {
            var clip = new AnimationClip
            {
                name = Path.GetFileNameWithoutExtension(path),
            };

            if (doc.TryGetValue("frame_rate", out var frVal))
                clip.frameRate = ToFloat(frVal);
            if (doc.TryGetValue("wrap_mode", out var wmVal) && wmVal is string wmStr)
            {
                if (Enum.TryParse<WrapMode>(wmStr, true, out var wm))
                    clip.wrapMode = wm;
            }
            if (doc.TryGetValue("length", out var lenVal))
                clip.length = ToFloat(lenVal);

            // Curves
            if (doc.TryGetValue("curves", out var curvesVal) && curvesVal is TomlTableArray curvesArr)
            {
                foreach (TomlTable curveTable in curvesArr)
                {
                    if (!curveTable.TryGetValue("path", out var pathVal) || pathVal is not string curvePath)
                        continue;

                    var curve = new AnimationCurve();

                    if (curveTable.TryGetValue("keys", out var keysVal) && keysVal is TomlTableArray keysArr)
                    {
                        foreach (TomlTable keyTable in keysArr)
                        {
                            float time = keyTable.TryGetValue("time", out var t) ? ToFloat(t) : 0f;
                            float value = keyTable.TryGetValue("value", out var v) ? ToFloat(v) : 0f;
                            float inTan = keyTable.TryGetValue("in_tangent", out var it) ? ToFloat(it) : 0f;
                            float outTan = keyTable.TryGetValue("out_tangent", out var ot) ? ToFloat(ot) : 0f;
                            curve.AddKey(new Keyframe(time, value, inTan, outTan));
                        }
                    }

                    clip.SetCurve(curvePath, curve);
                }
            }

            // Events
            if (doc.TryGetValue("events", out var eventsVal) && eventsVal is TomlTableArray eventsArr)
            {
                foreach (TomlTable evtTable in eventsArr)
                {
                    float time = evtTable.TryGetValue("time", out var t) ? ToFloat(t) : 0f;
                    string func = evtTable.TryGetValue("function", out var f) && f is string fs ? fs : "";
                    float fp = evtTable.TryGetValue("float_param", out var fpv) ? ToFloat(fpv) : 0f;
                    int ip = evtTable.TryGetValue("int_param", out var ipv) && ipv is long ipl ? (int)ipl : 0;
                    string? sp = evtTable.TryGetValue("string_param", out var spv) && spv is string sps ? sps : null;

                    clip.events.Add(new AnimationEvent(time, func)
                    {
                        floatParameter = fp,
                        intParameter = ip,
                        stringParameter = sp,
                    });
                }
            }

            // length가 명시되지 않았으면 자동 계산
            if (clip.length <= 0f)
                clip.RecalculateLength();

            Debug.Log($"[AnimationClipImporter] Loaded: {path} ({clip.curves.Count} curves, {clip.events.Count} events, {clip.length:F2}s)");
            return clip;
        }

        /// <summary>AnimationClip을 .anim TOML 파일로 내보내기.</summary>
        public static void Export(AnimationClip clip, string path)
        {
            var doc = new TomlTable
            {
                ["frame_rate"] = (double)clip.frameRate,
                ["wrap_mode"] = clip.wrapMode.ToString(),
                ["length"] = (double)clip.length,
            };

            // Curves
            var curvesArr = new TomlTableArray();
            foreach (var (curvePath, curve) in clip.curves)
            {
                var curveTable = new TomlTable { ["path"] = curvePath };
                var keysArr = new TomlTableArray();

                for (int i = 0; i < curve.length; i++)
                {
                    var key = curve[i];
                    keysArr.Add(new TomlTable
                    {
                        ["time"] = (double)key.time,
                        ["value"] = (double)key.value,
                        ["in_tangent"] = (double)key.inTangent,
                        ["out_tangent"] = (double)key.outTangent,
                    });
                }

                curveTable["keys"] = keysArr;
                curvesArr.Add(curveTable);
            }
            doc["curves"] = curvesArr;

            // Events
            if (clip.events.Count > 0)
            {
                var eventsArr = new TomlTableArray();
                foreach (var evt in clip.events)
                {
                    var evtTable = new TomlTable
                    {
                        ["time"] = (double)evt.time,
                        ["function"] = evt.functionName,
                        ["float_param"] = (double)evt.floatParameter,
                        ["int_param"] = (long)evt.intParameter,
                    };
                    if (evt.stringParameter != null)
                        evtTable["string_param"] = evt.stringParameter;
                    eventsArr.Add(evtTable);
                }
                doc["events"] = eventsArr;
            }

            var toml = Toml.FromModel(doc);
            File.WriteAllText(path, toml);
            Debug.Log($"[AnimationClipImporter] Exported: {path}");
        }

        private static float ToFloat(object? val)
        {
            return val switch
            {
                double d => (float)d,
                long l => (float)l,
                float f => f,
                _ => 0f,
            };
        }
    }
}
