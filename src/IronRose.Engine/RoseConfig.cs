using System;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
using RoseEngine;

namespace IronRose.Engine
{
    public static class RoseConfig
    {
        public static bool DontUseCache { get; private set; }
        public static bool DontUseCompressTexture { get; private set; }
        public static bool ForceClearCache { get; private set; }

        /// <summary>프로그래밍 방식으로 ForceClearCache를 활성화합니다 (Reimport All).</summary>
        public static void EnableForceClearCache() => ForceClearCache = true;

        // 에디터 설정
        public static bool EnableEditor { get; private set; } = true;

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            string[] searchPaths = { "rose_config.toml", "../rose_config.toml", "../../rose_config.toml" };

            foreach (var rel in searchPaths)
            {
                var path = Path.GetFullPath(rel);
                if (!File.Exists(path)) continue;

                try
                {
                    var table = Toml.ToModel(File.ReadAllText(path));

                    if (table.TryGetValue("cache", out var cacheVal) && cacheVal is TomlTable cache)
                    {
                        if (cache.TryGetValue("dont_use_cache", out var v1) && v1 is bool b1)
                            DontUseCache = b1;
                        if (cache.TryGetValue("dont_use_compress_texture", out var v2) && v2 is bool b2)
                            DontUseCompressTexture = b2;
                        if (cache.TryGetValue("force_clear_cache", out var v3) && v3 is bool b3)
                            ForceClearCache = b3;
                    }

                    if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
                    {
                        if (editor.TryGetValue("enable_editor", out var v4) && v4 is bool b4)
                            EnableEditor = b4;
                    }

                    Debug.Log($"[RoseConfig] Loaded: {path} (DontUseCache={DontUseCache}, DontUseCompressTexture={DontUseCompressTexture}, ForceClearCache={ForceClearCache}, EnableEditor={EnableEditor})");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RoseConfig] Failed to parse {path}: {ex.Message}");
                }
            }

            Debug.Log("[RoseConfig] No config file found, using defaults");
        }
    }
}
