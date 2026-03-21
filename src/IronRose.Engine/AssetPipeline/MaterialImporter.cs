// ------------------------------------------------------------
// @file    MaterialImporter.cs
// @brief   .mat (TOML) 파일을 Material로 임포트하고, Material을 TOML로 직렬화한다.
//          색상은 [color], [emission] 서브테이블 구조로 저장한다.
// @deps    IronRose.Engine/TomlConfig, RoseEngine (Material, Color, Texture2D, Vector2),
//          AssetPipeline/IAssetDatabase, AssetPipeline/RoseMetadata
// @exports
//   class MaterialImporter
//     Import(string, RoseMetadata, IAssetDatabase?): Material   — .mat 파일에서 Material 로드
//     static WriteDefault(string): void                         — 기본 Material TOML 작성
//     static WriteMaterial(string, Material, ...): void         — Material을 TOML 파일로 직렬화
// @note    ReadColorFromConfig()는 서브테이블(r/g/b/a)에서 Color를 읽는 헬퍼.
//          TomlConvert.ColorToArray()와 다른 형태(배열이 아닌 테이블 구조).
// ------------------------------------------------------------
using System;
using System.IO;
using RoseEngine;
using IronRose.Engine;

namespace IronRose.AssetPipeline
{
    public class MaterialImporter
    {
        public Material Import(string path, RoseMetadata meta, IAssetDatabase? db)
        {
            var config = TomlConfig.LoadString(File.ReadAllText(path), "[MaterialImporter]");
            if (config == null) return new Material { name = Path.GetFileNameWithoutExtension(path) };

            var mat = new Material();
            mat.name = Path.GetFileNameWithoutExtension(path);

            var colorSection = config.GetSection("color");
            if (colorSection != null)
                mat.color = ReadColorFromConfig(colorSection);

            var emissionSection = config.GetSection("emission");
            if (emissionSection != null)
                mat.emission = ReadColorFromConfig(emissionSection);

            mat.metallic = config.GetFloat("metallic", mat.metallic);
            mat.roughness = config.GetFloat("roughness", mat.roughness);
            mat.occlusion = config.GetFloat("occlusion", mat.occlusion);
            mat.normalMapStrength = config.GetFloat("normalMapStrength", mat.normalMapStrength);

            // Texture transform
            float sx = config.GetFloat("textureScaleX", 1f);
            float sy = config.GetFloat("textureScaleY", 1f);
            mat.textureScale = new RoseEngine.Vector2(sx, sy);
            float ox = config.GetFloat("textureOffsetX", 0f);
            float oy = config.GetFloat("textureOffsetY", 0f);
            mat.textureOffset = new RoseEngine.Vector2(ox, oy);

            // Texture references by GUID
            if (db != null)
            {
                var mtg = config.GetString("mainTextureGuid", "");
                if (!string.IsNullOrEmpty(mtg))
                    mat.mainTexture = db.LoadByGuid<Texture2D>(mtg);
                var nmg = config.GetString("normalMapGuid", "");
                if (!string.IsNullOrEmpty(nmg))
                    mat.normalMap = db.LoadByGuid<Texture2D>(nmg);
                var mrog = config.GetString("MROMapGuid", "");
                if (!string.IsNullOrEmpty(mrog))
                    mat.MROMap = db.LoadByGuid<Texture2D>(mrog);
            }

            return mat;
        }

        /// <summary>기본 Material TOML 파일 작성.</summary>
        public static void WriteDefault(string path)
        {
            var config = BuildConfig(Color.white, Color.black, 0f, 0.5f, 1f, 1f,
                RoseEngine.Vector2.one, RoseEngine.Vector2.zero, null, null, null);
            config.SaveToFile(path);
        }

        /// <summary>기존 Material 인스턴스를 TOML 파일로 직렬화 (복제용). 텍스처 GUID는 호출자가 제공.</summary>
        public static void WriteMaterial(string path, Material mat,
            string? mainTexGuid = null, string? normalMapGuid = null, string? mroMapGuid = null)
        {
            var config = BuildConfig(mat.color, mat.emission,
                mat.metallic, mat.roughness, mat.occlusion, mat.normalMapStrength,
                mat.textureScale, mat.textureOffset,
                mainTexGuid, normalMapGuid, mroMapGuid);
            config.SaveToFile(path);
        }

        private static TomlConfig BuildConfig(Color color, Color emission,
            float metallic, float roughness, float occlusion, float normalMapStrength,
            RoseEngine.Vector2 textureScale, RoseEngine.Vector2 textureOffset,
            string? mainTexGuid, string? normalMapGuid, string? mroMapGuid)
        {
            var config = TomlConfig.CreateEmpty();

            var colorSection = TomlConfig.CreateEmpty();
            colorSection.SetValue("r", (double)color.r);
            colorSection.SetValue("g", (double)color.g);
            colorSection.SetValue("b", (double)color.b);
            colorSection.SetValue("a", (double)color.a);
            config.SetSection("color", colorSection);

            var emissionSection = TomlConfig.CreateEmpty();
            emissionSection.SetValue("r", (double)emission.r);
            emissionSection.SetValue("g", (double)emission.g);
            emissionSection.SetValue("b", (double)emission.b);
            emissionSection.SetValue("a", (double)emission.a);
            config.SetSection("emission", emissionSection);

            config.SetValue("metallic", (double)metallic);
            config.SetValue("roughness", (double)roughness);
            config.SetValue("occlusion", (double)occlusion);
            config.SetValue("normalMapStrength", (double)normalMapStrength);

            if (textureScale.x != 1f || textureScale.y != 1f)
            {
                config.SetValue("textureScaleX", (double)textureScale.x);
                config.SetValue("textureScaleY", (double)textureScale.y);
            }
            if (textureOffset.x != 0f || textureOffset.y != 0f)
            {
                config.SetValue("textureOffsetX", (double)textureOffset.x);
                config.SetValue("textureOffsetY", (double)textureOffset.y);
            }

            if (!string.IsNullOrEmpty(mainTexGuid))
                config.SetValue("mainTextureGuid", mainTexGuid);
            if (!string.IsNullOrEmpty(normalMapGuid))
                config.SetValue("normalMapGuid", normalMapGuid);
            if (!string.IsNullOrEmpty(mroMapGuid))
                config.SetValue("MROMapGuid", mroMapGuid);

            return config;
        }

        private static Color ReadColorFromConfig(TomlConfig section)
        {
            float r = section.GetFloat("r", 0f);
            float g = section.GetFloat("g", 0f);
            float b = section.GetFloat("b", 0f);
            float a = section.GetFloat("a", 1f);
            return new Color(r, g, b, a);
        }

    }
}
