using System;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// Project-wide settings (rose_projectSettings.toml).
    /// 프로젝트 루트(rose_config.toml 옆)에 저장되며 버전 관리 가능.
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

        private static string FindOrCreatePath()
        {
            string[] searchPaths = { ".", "..", "../.." };
            foreach (var dir in searchPaths)
            {
                var full = Path.GetFullPath(dir);
                if (File.Exists(Path.Combine(full, "rose_config.toml")))
                    return Path.Combine(full, FileName);
            }
            return Path.GetFullPath(FileName);
        }

        public static void Load()
        {
            var path = FindOrCreatePath();
            if (File.Exists(path))
            {
                try
                {
                    var table = Toml.ToModel(File.ReadAllText(path));
                    if (table.TryGetValue("renderer", out var rv) && rv is TomlTable renderer)
                    {
                        if (renderer.TryGetValue("active_profile_guid", out var vg)
                            && vg is string sg && !string.IsNullOrEmpty(sg))
                            ActiveRendererProfileGuid = sg;
                    }
                    if (table.TryGetValue("build", out var bv) && bv is TomlTable build)
                    {
                        if (build.TryGetValue("start_scene", out var vs)
                            && vs is string ss && !string.IsNullOrEmpty(ss))
                            StartScenePath = ss;
                    }
                    if (table.TryGetValue("editor", out var ev) && ev is TomlTable editor)
                    {
                        if (editor.TryGetValue("external_script_editor", out var ve)
                            && ve is string se && !string.IsNullOrEmpty(se))
                            ExternalScriptEditor = se;
                    }
                    Debug.Log($"[ProjectSettings] Loaded: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ProjectSettings] Failed to load {path}: {ex.Message}");
                }
            }

            // 마이그레이션: EditorState에 저장된 레거시 값 이전
            if (string.IsNullOrEmpty(ActiveRendererProfileGuid))
            {
                var legacyGuid = Editor.EditorState.ActiveRendererProfileGuid;
                if (!string.IsNullOrEmpty(legacyGuid))
                {
                    ActiveRendererProfileGuid = legacyGuid;
                    Save();
                    Debug.Log("[ProjectSettings] Migrated active_renderer_profile_guid from EditorState");
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

                File.WriteAllText(path, toml);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ProjectSettings] Failed to save {path}: {ex.Message}");
            }
        }
    }
}
