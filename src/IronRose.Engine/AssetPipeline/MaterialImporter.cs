using System;
using System.IO;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;

namespace IronRose.AssetPipeline
{
    public class MaterialImporter
    {
        public Material Import(string path, RoseMetadata meta, IAssetDatabase? db)
        {
            var toml = Toml.ToModel(File.ReadAllText(path));
            var mat = new Material();

            mat.name = Path.GetFileNameWithoutExtension(path);

            if (toml.TryGetValue("color", out var colorVal) && colorVal is TomlTable ct)
                mat.color = ReadColor(ct);

            if (toml.TryGetValue("emission", out var emVal) && emVal is TomlTable et)
                mat.emission = ReadColor(et);

            if (toml.TryGetValue("metallic", out var metalVal))
                mat.metallic = Convert.ToSingle(metalVal);

            if (toml.TryGetValue("roughness", out var roughVal))
                mat.roughness = Convert.ToSingle(roughVal);

            if (toml.TryGetValue("occlusion", out var occVal))
                mat.occlusion = Convert.ToSingle(occVal);

            if (toml.TryGetValue("normalMapStrength", out var nmsVal))
                mat.normalMapStrength = Convert.ToSingle(nmsVal);

            // Texture transform
            {
                float sx = toml.TryGetValue("textureScaleX", out var sxv) ? Convert.ToSingle(sxv) : 1f;
                float sy = toml.TryGetValue("textureScaleY", out var syv) ? Convert.ToSingle(syv) : 1f;
                mat.textureScale = new RoseEngine.Vector2(sx, sy);

                float ox = toml.TryGetValue("textureOffsetX", out var oxv) ? Convert.ToSingle(oxv) : 0f;
                float oy = toml.TryGetValue("textureOffsetY", out var oyv) ? Convert.ToSingle(oyv) : 0f;
                mat.textureOffset = new RoseEngine.Vector2(ox, oy);
            }

            // Texture references by GUID
            if (db != null)
            {
                if (toml.TryGetValue("mainTextureGuid", out var mtGuid) && mtGuid is string mtg && !string.IsNullOrEmpty(mtg))
                    mat.mainTexture = db.LoadByGuid<Texture2D>(mtg);

                if (toml.TryGetValue("normalMapGuid", out var nmGuid) && nmGuid is string nmg && !string.IsNullOrEmpty(nmg))
                    mat.normalMap = db.LoadByGuid<Texture2D>(nmg);

                if (toml.TryGetValue("MROMapGuid", out var mroGuid) && mroGuid is string mrog && !string.IsNullOrEmpty(mrog))
                    mat.MROMap = db.LoadByGuid<Texture2D>(mrog);
            }

            return mat;
        }

        /// <summary>기본 Material TOML 파일 작성.</summary>
        public static void WriteDefault(string path)
        {
            var table = BuildTomlTable(Color.white, Color.black, 0f, 0.5f, 1f, 1f,
                RoseEngine.Vector2.one, RoseEngine.Vector2.zero, null, null, null);
            File.WriteAllText(path, Toml.FromModel(table));
        }

        /// <summary>기존 Material 인스턴스를 TOML 파일로 직렬화 (복제용). 텍스처 GUID는 호출자가 제공.</summary>
        public static void WriteMaterial(string path, Material mat,
            string? mainTexGuid = null, string? normalMapGuid = null, string? mroMapGuid = null)
        {
            var table = BuildTomlTable(mat.color, mat.emission,
                mat.metallic, mat.roughness, mat.occlusion, mat.normalMapStrength,
                mat.textureScale, mat.textureOffset,
                mainTexGuid, normalMapGuid, mroMapGuid);
            File.WriteAllText(path, Toml.FromModel(table));
        }

        private static TomlTable BuildTomlTable(Color color, Color emission,
            float metallic, float roughness, float occlusion, float normalMapStrength,
            RoseEngine.Vector2 textureScale, RoseEngine.Vector2 textureOffset,
            string? mainTexGuid, string? normalMapGuid, string? mroMapGuid)
        {
            var table = new TomlTable
            {
                ["color"] = new TomlTable
                {
                    ["r"] = (double)color.r,
                    ["g"] = (double)color.g,
                    ["b"] = (double)color.b,
                    ["a"] = (double)color.a,
                },
                ["emission"] = new TomlTable
                {
                    ["r"] = (double)emission.r,
                    ["g"] = (double)emission.g,
                    ["b"] = (double)emission.b,
                    ["a"] = (double)emission.a,
                },
                ["metallic"] = (double)metallic,
                ["roughness"] = (double)roughness,
                ["occlusion"] = (double)occlusion,
                ["normalMapStrength"] = (double)normalMapStrength,
            };

            // Texture transform (only write non-default values)
            if (textureScale.x != 1f || textureScale.y != 1f)
            {
                table["textureScaleX"] = (double)textureScale.x;
                table["textureScaleY"] = (double)textureScale.y;
            }
            if (textureOffset.x != 0f || textureOffset.y != 0f)
            {
                table["textureOffsetX"] = (double)textureOffset.x;
                table["textureOffsetY"] = (double)textureOffset.y;
            }

            if (!string.IsNullOrEmpty(mainTexGuid))
                table["mainTextureGuid"] = mainTexGuid;
            if (!string.IsNullOrEmpty(normalMapGuid))
                table["normalMapGuid"] = normalMapGuid;
            if (!string.IsNullOrEmpty(mroMapGuid))
                table["MROMapGuid"] = mroMapGuid;

            return table;
        }

        private static Color ReadColor(TomlTable ct)
        {
            float r = ct.TryGetValue("r", out var rv) ? Convert.ToSingle(rv) : 0f;
            float g = ct.TryGetValue("g", out var gv) ? Convert.ToSingle(gv) : 0f;
            float b = ct.TryGetValue("b", out var bv) ? Convert.ToSingle(bv) : 0f;
            float a = ct.TryGetValue("a", out var av) ? Convert.ToSingle(av) : 1f;
            return new Color(r, g, b, a);
        }

    }
}
