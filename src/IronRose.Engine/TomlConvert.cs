// ------------------------------------------------------------
// @file    TomlConvert.cs
// @brief   TOML 값 타입 변환 유틸리티. Tomlyn 타입과 엔진 타입 간 변환을 담당한다.
//          기존 SceneSerializer, AnimationClipImporter 등에서 중복되던
//          ToFloat, Vec3ToArray, ArrayToColor 등의 변환 로직을 통합한다.
// @deps    Tomlyn.Model, RoseEngine (Vector2/3/4, Quaternion, Color)
// @exports
//   static class TomlConvert
//     ToFloat(object?, float): float
//     ToInt(object?, int): int
//     Vec2ToArray/ArrayToVec2, Vec3ToArray/ArrayToVec3
//     Vec4ToArray/ArrayToVec4, QuatToArray/ArrayToQuat
//     ColorToArray/ArrayToColor
//     GetVec3(TomlTable, string, Vector3?): Vector3
//     GetQuat(TomlTable, string): Quaternion
// ------------------------------------------------------------
using System.Globalization;
using RoseEngine;
using Tomlyn.Model;

namespace IronRose.Engine
{
    /// <summary>
    /// TOML 값 타입 변환 유틸리티. Tomlyn 타입과 엔진 타입 간 변환을 담당한다.
    /// </summary>
    public static class TomlConvert
    {
        // ── 기본 타입 변환 ──

        /// <summary>object를 float로 변환한다. double, long, float, int, string을 처리.</summary>
        public static float ToFloat(object? val, float defaultValue = 0f)
        {
            return val switch
            {
                double d => (float)d,
                long l => (float)l,
                float f => f,
                int i => i,
                string s => float.TryParse(s, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var r) ? r : defaultValue,
                _ => defaultValue,
            };
        }

        /// <summary>object를 int로 변환한다. long, double을 처리.</summary>
        public static int ToInt(object? val, int defaultValue = 0)
        {
            return val switch
            {
                long l => (int)l,
                double d => (int)d,
                int i => i,
                _ => defaultValue,
            };
        }

        // ── Vector2 ──

        public static TomlArray Vec2ToArray(Vector2 v)
        {
            return new TomlArray { (double)v.x, (double)v.y };
        }

        public static Vector2 ArrayToVec2(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 2)
                return Vector2.zero;
            return new Vector2(ToFloat(arr[0]), ToFloat(arr[1]));
        }

        // ── Vector3 ──

        public static TomlArray Vec3ToArray(Vector3 v)
        {
            return new TomlArray { (double)v.x, (double)v.y, (double)v.z };
        }

        public static Vector3 ArrayToVec3(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 3)
                return Vector3.zero;
            return new Vector3(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]));
        }

        // ── Vector4 ──

        public static TomlArray Vec4ToArray(Vector4 v)
        {
            return new TomlArray { (double)v.x, (double)v.y, (double)v.z, (double)v.w };
        }

        public static Vector4 ArrayToVec4(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 4)
                return Vector4.zero;
            return new Vector4(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        // ── Quaternion ──

        public static TomlArray QuatToArray(Quaternion q)
        {
            return new TomlArray { (double)q.x, (double)q.y, (double)q.z, (double)q.w };
        }

        public static Quaternion ArrayToQuat(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 4)
                return Quaternion.identity;
            return new Quaternion(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        // ── Color ──

        public static TomlArray ColorToArray(Color c)
        {
            return new TomlArray { (double)c.r, (double)c.g, (double)c.b, (double)c.a };
        }

        public static Color ArrayToColor(object? val)
        {
            if (val is not TomlArray arr || arr.Count < 4)
                return Color.white;
            return new Color(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
        }

        // ── TomlTable 기반 편의 메서드 ──

        /// <summary>TomlTable에서 키로 Vector3을 읽는다. 키가 없거나 배열이 아니면 기본값.</summary>
        public static Vector3 GetVec3(TomlTable table, string key, Vector3? defaultValue = null)
        {
            if (table.TryGetValue(key, out var val) && val is TomlArray arr && arr.Count >= 3)
                return new Vector3(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]));
            return defaultValue ?? Vector3.zero;
        }

        /// <summary>TomlTable에서 키로 Quaternion을 읽는다. 키가 없으면 identity.</summary>
        public static Quaternion GetQuat(TomlTable table, string key)
        {
            if (table.TryGetValue(key, out var val) && val is TomlArray arr && arr.Count >= 4)
                return new Quaternion(ToFloat(arr[0]), ToFloat(arr[1]), ToFloat(arr[2]), ToFloat(arr[3]));
            return Quaternion.identity;
        }
    }
}
