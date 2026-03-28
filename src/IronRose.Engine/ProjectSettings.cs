// ------------------------------------------------------------
// @file    ProjectSettings.cs
// @brief   프로젝트 전역 설정 (rose_projectSettings.toml).
//          활성 렌더러 프로파일, 빌드 시작 씬, 외부 스크립트 에디터 경로, 캐시 설정 등을 관리.
//          [renderer], [build], [editor], [cache] 섹션을 읽고 쓴다.
// @deps    IronRose.Engine/ProjectContext, IronRose.Engine/TomlConfig,
//          IronRose.Engine.Editor/EditorState, RoseEngine/Debug
// @exports
//   static class ProjectSettings
//     ActiveRendererProfileGuid: string?        — 활성 렌더러 프로파일 GUID
//     StartScenePath: string?                   — Standalone 빌드 시작 씬 경로
//     ExternalScriptEditor: string              — 외부 스크립트 에디터 (기본: "code")
//     DontUseCache: bool                        — 캐시 사용 안 함 플래그
//     DontUseCompressTexture: bool              — 텍스처 압축 사용 안 함 플래그
//     ForceClearCache: bool                     — 캐시 강제 삭제 플래그
//     Load(): void                              — 설정 파일 로드 + EditorState 레거시 마이그레이션
//     Save(): void                              — 설정 파일 저장
// @note    FindOrCreatePath()는 ProjectContext.ProjectRoot를 기반으로 파일 경로를 결정한다.
//          Load() 시 EditorState.ActiveRendererProfileGuid 레거시 값을 자동 마이그레이션한다.
//          [cache] 섹션은 기존 rose_config.toml에서 통합되었다.
//          TOML 읽기에 TomlConfig API를 사용한다. Save()는 문자열 직접 조합을 유지한다.
// ------------------------------------------------------------
using System;
using System.IO;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// Project-wide settings (rose_projectSettings.toml).
    /// 프로젝트 루트에 저장되며 버전 관리 가능.
    /// [renderer], [build], [editor], [cache] 섹션을 포함한다.
    /// </summary>
    public static class ProjectSettings
    {
        private const string FileName = "rose_projectSettings.toml";

        /// <summary>활성 렌더러 프로파일 GUID.</summary>
        public static string? ActiveRendererProfileGuid { get; set; }

        /// <summary>Standalone 빌드 시작 씬 경로 (예: Assets/Scenes/a.scene).</summary>
        public static string? StartScenePath { get; set; }

        /// <summary>외부 스크립트 에디터 경로 (기본: "code").</summary>
        public static string ExternalScriptEditor { get; set; } = "code";

        /// <summary>캐시 사용을 비활성화합니다.</summary>
        public static bool DontUseCache { get; set; }

        /// <summary>텍스처 압축을 비활성화합니다.</summary>
        public static bool DontUseCompressTexture { get; set; }

        /// <summary>캐시를 강제로 삭제합니다.</summary>
        public static bool ForceClearCache { get; set; }

        /// <summary>상세 로그 출력 여부 (기본 false).</summary>
        public static bool VerboseLog { get; set; }

        private static string FindOrCreatePath() =>
            Path.Combine(ProjectContext.ProjectRoot, FileName);

        public static void Load()
        {
            // Load() 이전에 프로그래밍 방식으로 설정된 값을 보존한다.
            // (예: Reimport All에서 RoseConfig.EnableForceClearCache() 호출)
            var preForceClear = ForceClearCache;

            var path = FindOrCreatePath();
            if (File.Exists(path))
            {
                var config = TomlConfig.LoadFile(path, "[ProjectSettings]");
                if (config != null)
                {
                    var renderer = config.GetSection("renderer");
                    if (renderer != null)
                    {
                        var sg = renderer.GetString("active_profile_guid", "");
                        if (!string.IsNullOrEmpty(sg))
                            ActiveRendererProfileGuid = sg;
                    }

                    var build = config.GetSection("build");
                    if (build != null)
                    {
                        var ss = build.GetString("start_scene", "");
                        if (!string.IsNullOrEmpty(ss))
                            StartScenePath = ss;
                    }

                    var editor = config.GetSection("editor");
                    if (editor != null)
                    {
                        var se = editor.GetString("external_script_editor", "");
                        if (!string.IsNullOrEmpty(se))
                            ExternalScriptEditor = se;
                    }

                    var cache = config.GetSection("cache");
                    if (cache != null)
                    {
                        DontUseCache = cache.GetBool("dont_use_cache", DontUseCache);
                        DontUseCompressTexture = cache.GetBool("dont_use_compress_texture", DontUseCompressTexture);
                        ForceClearCache = cache.GetBool("force_clear_cache", ForceClearCache);
                    }

                    var log = config.GetSection("log");
                    if (log != null)
                    {
                        VerboseLog = log.GetBool("verbose", VerboseLog);
                    }

                    // Verbose 플래그를 EditorDebug에 즉시 반영
                    EditorDebug.Verbose = VerboseLog;

                    EditorDebug.Log($"[ProjectSettings] Loaded: {path}");
                }
            }

            // 프로그래밍 방식으로 활성화된 ForceClearCache는 파일 값보다 우선한다.
            if (preForceClear)
                ForceClearCache = true;

            // 마이그레이션: EditorState에 저장된 레거시 값 이전
            if (string.IsNullOrEmpty(ActiveRendererProfileGuid))
            {
                var legacyGuid = Editor.EditorState.ActiveRendererProfileGuid;
                if (!string.IsNullOrEmpty(legacyGuid))
                {
                    ActiveRendererProfileGuid = legacyGuid;
                    Save();
                    EditorDebug.Log("[ProjectSettings] Migrated active_renderer_profile_guid from EditorState");
                }
            }
        }

        public static void Save()
        {
            var path = FindOrCreatePath();
            try
            {
                var toml = "[renderer]\n";
                if (!string.IsNullOrEmpty(ActiveRendererProfileGuid))
                    toml += $"active_profile_guid = \"{ActiveRendererProfileGuid}\"\n";

                toml += "\n[build]\n";
                if (!string.IsNullOrEmpty(StartScenePath))
                    toml += $"start_scene = \"{StartScenePath}\"\n";

                toml += "\n[editor]\n";
                toml += $"external_script_editor = \"{ExternalScriptEditor}\"\n";

                toml += "\n[log]\n";
                toml += $"verbose = {VerboseLog.ToString().ToLowerInvariant()}\n";

                toml += "\n[cache]\n";
                toml += $"dont_use_cache = {DontUseCache.ToString().ToLowerInvariant()}\n";
                toml += $"dont_use_compress_texture = {DontUseCompressTexture.ToString().ToLowerInvariant()}\n";
                toml += $"force_clear_cache = {ForceClearCache.ToString().ToLowerInvariant()}\n";

                File.WriteAllText(path, toml);
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[ProjectSettings] Failed to save {path}: {ex.Message}");
            }
        }
    }
}
