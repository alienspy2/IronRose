// ------------------------------------------------------------
// @file    EditorPreferences.cs
// @brief   사용자 전역(앱 레벨) Preferences. ~/.ironrose/settings.toml의
//          [preferences] 섹션을 read-modify-write로 관리한다.
//          프로젝트가 바뀌어도 유지되는 사용자 취향 값을 저장한다.
// @deps    IronRose.Engine/TomlConfig, IronRose.Engine/ProjectContext(동일 파일 공유),
//          RoseEngine/EditorDebug
// @exports
//   enum EditorColorTheme { Rose, Dark, Light }
//   static class EditorPreferences
//     ColorTheme: EditorColorTheme              — 컬러 테마 선택 (기본 Rose)
//     EnableClaudeUsage: bool                   — Claude 연동 사용 여부 (기본 false)
//     UiScale: float                            — UI 스케일 (기본 1.0, 0.5~3.0)
//     EditorFont: string                        — 에디터 폰트명 (기본 "Roboto")
//     EnableAiAssetGeneration: bool             — AI 이미지 생성 기능 전체 on/off (기본 true, 메뉴/다이얼로그 노출만 제어)
//     AiAlienhsServerUrl: string                — AlienHS 서버 URL (기본 "http://localhost:25000")
//     AiComfyUrl: string                        — AlienHS 서버가 사용할 ComfyUI URL 오버라이드 (기본 "")
//     AiPythonPath: string                      — CLI 실행 파이썬 경로 (기본 "python")
//     AiGenerationModel: string                 — ComfyUI 이미지 생성 모델 파일명 (기본 "z_image_turbo_nvfp4.safetensors")
//     FocusGameViewOnPlay: bool                 — Play 모드 진입 시 Game View를 자동 포커스 (기본 true)
//     Load(): void                              — settings.toml [preferences] 로드
//     Save(): void                              — settings.toml [preferences] 저장 (read-modify-write)
//     MigrateFromEditorState(float?, string?): void — 레거시 EditorState 값 1회성 이전 훅
// @note    Save()는 ProjectContext.SaveLastProjectPath()와 동일한 read-modify-write 패턴으로
//          기존 [editor] last_project 등 다른 섹션을 절대 덮어쓰지 않는다.
//          color_theme은 TOML에 문자열("rose"|"dark"|"light")로 저장된다.
//          Load 시 UiScale은 [0.5, 3.0] 범위로 클램프된다.
//          파일이 없거나 파싱 실패 시 기본값을 유지한다.
//
//          === 새 preference 항목 추가 가이드 ===
//          1. 정적 속성 추가 (기본값 포함)
//          2. Load()에서 pref 섹션 파싱 추가
//          3. Save()에서 pref.SetValue(key, ...) 추가
//          4. ImGuiPreferencesPanel에 UI 위젯 추가 (필요 시 새 CollapsingHeader 섹션)
//          5. 값 변경 시 Save() 호출
// ------------------------------------------------------------
using System;
using System.IO;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// 에디터 컬러 테마. 값은 TOML에 소문자 문자열로 저장된다.
    /// 확장 시 enum 값 추가 + ColorThemeToString/ParseColorTheme 분기 추가 + ImGuiTheme.Apply() 분기 추가.
    /// </summary>
    public enum EditorColorTheme
    {
        Rose,
        Dark,
        Light,
    }

    /// <summary>
    /// 사용자 전역(앱 레벨) 선호 설정.
    /// ~/.ironrose/settings.toml의 [preferences] 섹션에 영속화된다.
    /// 프로젝트가 바뀌어도 유지되는 사용자 취향 값을 담는다.
    /// </summary>
    public static class EditorPreferences
    {
        /// <summary>컬러 테마 선택.</summary>
        public static EditorColorTheme ColorTheme { get; set; } = EditorColorTheme.Rose;

        /// <summary>Claude 연동 사용 여부.</summary>
        public static bool EnableClaudeUsage { get; set; } = false;

        /// <summary>UI 스케일 (0.5 ~ 3.0).</summary>
        public static float UiScale { get; set; } = 1.0f;

        /// <summary>에디터에서 사용할 폰트 이름.</summary>
        public static string EditorFont { get; set; } = "Roboto";

        /// <summary>AI 이미지 생성 기능 on/off 토글. 끄면 Asset Browser 메뉴/다이얼로그가 숨겨진다.</summary>
        public static bool EnableAiAssetGeneration { get; set; } = true;

        /// <summary>AlienHS invoke-comfyui 서버 URL.</summary>
        public static string AiAlienhsServerUrl { get; set; } = "http://localhost:25000";

        /// <summary>
        /// AlienHS 서버가 사용할 ComfyUI URL 오버라이드. 빈 문자열이면 서버 기본값을 사용한다.
        /// CLI의 --comfy-url 인자로 전달된다.
        /// </summary>
        public static string AiComfyUrl { get; set; } = "";

        /// <summary>CLI 실행에 사용할 Python 인터프리터 경로.</summary>
        public static string AiPythonPath { get; set; } = "python";

        /// <summary>ComfyUI 이미지 생성 모델 파일명. CLI의 --model 인자로 전달된다. 빈 문자열이면 CLI 기본값을 사용한다.</summary>
        public static string AiGenerationModel { get; set; } = "z_image_turbo_nvfp4.safetensors";

        /// <summary>
        /// Play 모드 진입 시 Game View 패널을 자동으로 포커스. Play 종료 시 이전 포커스 패널로 복귀.
        /// Game View 탭 우클릭 컨텍스트 메뉴에서 토글한다.
        /// </summary>
        public static bool FocusGameViewOnPlay { get; set; } = true;

        /// <summary>글로벌 설정 디렉토리 (~/.ironrose/).</summary>
        private static string GlobalSettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ironrose");

        /// <summary>글로벌 설정 파일 경로 (~/.ironrose/settings.toml).</summary>
        private static string GlobalSettingsPath =>
            Path.Combine(GlobalSettingsDir, "settings.toml");

        /// <summary>
        /// ~/.ironrose/settings.toml의 [preferences] 섹션을 읽어 정적 속성에 반영한다.
        /// 파일이 없거나 섹션이 없으면 기본값을 유지한다.
        /// </summary>
        public static void Load()
        {
            try
            {
                var config = TomlConfig.LoadFile(GlobalSettingsPath, "[EditorPreferences]");
                if (config == null)
                    return;

                var pref = config.GetSection("preferences");
                if (pref == null)
                    return;

                // color_theme (문자열, 대소문자 무시)
                var themeStr = pref.GetString("color_theme", "");
                if (!string.IsNullOrEmpty(themeStr))
                    ColorTheme = ParseColorTheme(themeStr);

                // enable_claude_usage
                EnableClaudeUsage = pref.GetBool("enable_claude_usage", EnableClaudeUsage);

                // ui_scale (0.5 ~ 3.0 클램프)
                var scale = pref.GetFloat("ui_scale", UiScale);
                UiScale = Math.Clamp(scale, 0.5f, 3.0f);

                // editor_font
                var fontStr = pref.GetString("editor_font", "");
                if (!string.IsNullOrEmpty(fontStr))
                    EditorFont = fontStr;

                // enable_ai_asset_generation
                EnableAiAssetGeneration = pref.GetBool("enable_ai_asset_generation", EnableAiAssetGeneration);

                // ai_alienhs_server_url
                var aiServerStr = pref.GetString("ai_alienhs_server_url", "");
                if (!string.IsNullOrEmpty(aiServerStr))
                    AiAlienhsServerUrl = aiServerStr;

                // ai_comfy_url (빈 문자열도 유효 — 그대로 복원)
                AiComfyUrl = pref.GetString("ai_comfy_url", AiComfyUrl) ?? "";

                // ai_python_path
                var aiPyStr = pref.GetString("ai_python_path", "");
                if (!string.IsNullOrEmpty(aiPyStr))
                    AiPythonPath = aiPyStr;

                // ai_generation_model (ComfyUI 이미지 생성 모델 파일명)
                AiGenerationModel = pref.GetString("ai_generation_model", AiGenerationModel) ?? "";

                // focus_game_view_on_play
                FocusGameViewOnPlay = pref.GetBool("focus_game_view_on_play", FocusGameViewOnPlay);

                EditorDebug.Log($"[EditorPreferences] Loaded: {GlobalSettingsPath}");
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[EditorPreferences] Failed to load: {ex.Message}");
            }
        }

        /// <summary>
        /// ~/.ironrose/settings.toml의 [preferences] 섹션에 현재 값을 저장한다.
        /// read-modify-write 패턴: 기존 [editor] 등 다른 섹션을 보존한다.
        /// </summary>
        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(GlobalSettingsDir);

                // 기존 settings.toml 로드 또는 빈 생성
                var config = TomlConfig.LoadFile(GlobalSettingsPath) ?? TomlConfig.CreateEmpty();

                // [preferences] 섹션 가져오기 또는 생성
                var pref = config.GetSection("preferences");
                if (pref == null)
                {
                    pref = TomlConfig.CreateEmpty();
                    config.SetSection("preferences", pref);
                }

                pref.SetValue("color_theme", ColorThemeToString(ColorTheme));
                pref.SetValue("enable_claude_usage", EnableClaudeUsage);
                pref.SetValue("ui_scale", (double)UiScale);
                pref.SetValue("editor_font", EditorFont);
                pref.SetValue("enable_ai_asset_generation", EnableAiAssetGeneration);
                pref.SetValue("ai_alienhs_server_url", AiAlienhsServerUrl);
                pref.SetValue("ai_comfy_url", AiComfyUrl);
                pref.SetValue("ai_python_path", AiPythonPath);
                pref.SetValue("ai_generation_model", AiGenerationModel);
                pref.SetValue("focus_game_view_on_play", FocusGameViewOnPlay);

                config.SaveToFile(GlobalSettingsPath, "[EditorPreferences]");
                EditorDebug.Log($"[EditorPreferences] Saved: {GlobalSettingsPath}");
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[EditorPreferences] Failed to save: {ex.Message}");
            }
        }

        /// <summary>
        /// 레거시 EditorState의 UiScale/EditorFont 값을 Preferences로 이전하는 1회성 훅.
        /// null이 아닌 인자만 해당 속성에 덮어쓰고, 하나 이상 적용되었으면 Save()를 호출한다.
        /// Phase B에서 호출된다. Phase A에서는 시그니처와 동작만 정의.
        /// </summary>
        /// <param name="legacyUiScale">레거시 UiScale 값 (null이면 무시).</param>
        /// <param name="legacyEditorFont">레거시 EditorFont 값 (null이면 무시).</param>
        public static void MigrateFromEditorState(float? legacyUiScale, string? legacyEditorFont)
        {
            bool changed = false;

            if (legacyUiScale.HasValue)
            {
                UiScale = Math.Clamp(legacyUiScale.Value, 0.5f, 3.0f);
                changed = true;
            }

            if (legacyEditorFont != null)
            {
                EditorFont = legacyEditorFont;
                changed = true;
            }

            if (changed)
            {
                Save();
                EditorDebug.Log("[EditorPreferences] Migrated legacy values from EditorState");
            }
        }

        /// <summary>EditorColorTheme → TOML 저장용 소문자 문자열.</summary>
        private static string ColorThemeToString(EditorColorTheme t) => t switch
        {
            EditorColorTheme.Rose => "rose",
            EditorColorTheme.Dark => "dark",
            EditorColorTheme.Light => "light",
            _ => "rose",
        };

        /// <summary>TOML 문자열 → EditorColorTheme. 대소문자 무시, 실패 시 Rose.</summary>
        private static EditorColorTheme ParseColorTheme(string s)
        {
            var lower = (s ?? "").Trim().ToLowerInvariant();
            return lower switch
            {
                "rose" => EditorColorTheme.Rose,
                "dark" => EditorColorTheme.Dark,
                "light" => EditorColorTheme.Light,
                _ => EditorColorTheme.Rose,
            };
        }
    }
}
