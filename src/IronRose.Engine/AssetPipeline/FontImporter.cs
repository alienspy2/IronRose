using System;
using System.IO;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    public class FontImporter
    {
        public Font? Import(string fontPath, RoseMetadata? meta)
        {
            if (!File.Exists(fontPath))
            {
                Debug.LogError($"Font not found: {fontPath}");
                return null;
            }

            int fontSize = 32;
            if (meta?.importer.TryGetValue("font_size", out var fsVal) == true)
                fontSize = Convert.ToInt32(fsVal);

            // 캐시 무효화 후 재생성 (Reimport 시 이전 아틀라스 반환 방지)
            Font.InvalidateCache(fontPath);
            var font = Font.CreateFromFile(fontPath, fontSize);

            Debug.Log($"[FontImporter] Loaded: {fontPath} (size={fontSize})");
            return font;
        }
    }
}
