// ------------------------------------------------------------
// @file    RoseConfig.cs
// @brief   엔진 런타임 설정 래퍼. 캐시 관련 프로퍼티는 ProjectSettings에 위임한다.
//          EnableEditor는 독립적으로 유지 (프로젝트 설정이 아닌 엔진 설정).
//          Load()는 하위 호환을 위해 유지하되, rose_config.toml의 [cache] 값을
//          ProjectSettings로 마이그레이션한 뒤 더 이상 읽지 않는다.
// @deps    IronRose.Engine/ProjectSettings, IronRose.Engine/ProjectContext, RoseEngine/Debug
// @exports
//   static class RoseConfig
//     DontUseCache: bool                  — ProjectSettings.DontUseCache 위임
//     DontUseCompressTexture: bool        — ProjectSettings.DontUseCompressTexture 위임
//     ForceClearCache: bool               — ProjectSettings.ForceClearCache 위임
//     EnableForceClearCache(): void       — ForceClearCache를 true로 설정 (Reimport All)
//     EnableEditor: bool                  — 에디터 활성화 여부
//     Load(): void                        — 레거시 rose_config.toml 마이그레이션
// @note    캐시 프로퍼티는 ProjectSettings에서 관리. RoseConfig는 기존 호출 코드와의
//          호환성을 위한 래퍼로만 유지된다.
//          Load()는 ProjectContext.ProjectRoot 기반으로 우선 탐색하고 CWD로 폴백한다.
// ------------------------------------------------------------
using System;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
using RoseEngine;

namespace IronRose.Engine
{
    public static class RoseConfig
    {
        /// <summary>캐시 사용을 비활성화합니다. ProjectSettings에 위임.</summary>
        public static bool DontUseCache
        {
            get => ProjectSettings.DontUseCache;
            set => ProjectSettings.DontUseCache = value;
        }

        /// <summary>텍스처 압축을 비활성화합니다. ProjectSettings에 위임.</summary>
        public static bool DontUseCompressTexture
        {
            get => ProjectSettings.DontUseCompressTexture;
            set => ProjectSettings.DontUseCompressTexture = value;
        }

        /// <summary>캐시를 강제로 삭제합니다. ProjectSettings에 위임.</summary>
        public static bool ForceClearCache
        {
            get => ProjectSettings.ForceClearCache;
            set => ProjectSettings.ForceClearCache = value;
        }

        /// <summary>프로그래밍 방식으로 ForceClearCache를 활성화합니다 (Reimport All).</summary>
        public static void EnableForceClearCache() => ProjectSettings.ForceClearCache = true;

        // 에디터 설정
        public static bool EnableEditor { get; private set; } = true;

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            // 레거시 rose_config.toml에서 [editor] 섹션만 읽기 (EnableEditor)
            // [cache] 섹션은 ProjectSettings.Load()에서 읽으므로 여기서는 처리하지 않는다.
            // ProjectContext.ProjectRoot 기반 탐색 (우선) + CWD 폴백
            string[] searchPaths;
            if (!string.IsNullOrEmpty(ProjectContext.ProjectRoot))
            {
                searchPaths = new[]
                {
                    Path.Combine(ProjectContext.ProjectRoot, "rose_config.toml"),
                    "rose_config.toml",
                    Path.Combine("..", "rose_config.toml"),
                    Path.Combine("..", "..", "rose_config.toml"),
                };
            }
            else
            {
                searchPaths = new[] { "rose_config.toml", Path.Combine("..", "rose_config.toml"), Path.Combine("..", "..", "rose_config.toml") };
            }

            foreach (var rel in searchPaths)
            {
                var path = Path.GetFullPath(rel);
                if (!File.Exists(path)) continue;

                try
                {
                    var table = Toml.ToModel(File.ReadAllText(path));

                    if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editor)
                    {
                        if (editor.TryGetValue("enable_editor", out var v4) && v4 is bool b4)
                            EnableEditor = b4;
                    }

                    EditorDebug.Log($"[RoseConfig] Loaded: {path} (EnableEditor={EnableEditor})");
                    return;
                }
                catch (Exception ex)
                {
                    EditorDebug.LogWarning($"[RoseConfig] Failed to parse {path}: {ex.Message}");
                }
            }

            EditorDebug.Log("[RoseConfig] No config file found, using defaults");
        }
    }
}
