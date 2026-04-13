// ------------------------------------------------------------
// @file    ImGuiPreferencesPanel.cs
// @brief   앱-레벨 사용자 Preferences 편집 UI. Edit > Preferences... 메뉴에서 열리며
//          Appearance(Color Theme / UI Scale / Editor Font)와 Integrations(Enable Claude Usage)
//          섹션을 제공한다.
// @deps    IronRose.Engine/EditorPreferences, IronRose.Engine.Editor.ImGuiEditor/ImGuiTheme,
//          IronRose.Engine.Editor.ImGuiEditor/PanelMaximizer,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/IEditorPanel, ImGuiNET
// @exports
//   class ImGuiPreferencesPanel : IEditorPanel
//     IsOpen: bool                      — 창 표시 여부 (런타임 전용, 세션 영속화 없음)
//     Draw(): void                      — Preferences 창 렌더
// @note    값 변경 시 즉시 EditorPreferences.Save()가 호출된다.
//          Color Theme 변경 시 ImGuiTheme.Apply()가 즉시 재호출되어 팔레트가 런타임에 전환된다.
//          UiScale/EditorFont 변경 시 EditorPreferences 값만 갱신하며, 실제 반영(_uiScale, _currentFont
//          동기화)은 ImGuiOverlay.Update() 시작부의 역동기화 블록에서 다음 프레임에 수행된다.
//          창 가시성은 세션 간 영속화하지 않는다 (EditorState에 PanelPreferences 필드 없음).
// ------------------------------------------------------------
using System;
using ImGuiNET;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// 앱-레벨 사용자 Preferences 편집 패널.
    /// Appearance(Color Theme / UI Scale / Editor Font) + Integrations(Enable Claude Usage) 섹션 제공.
    /// </summary>
    public class ImGuiPreferencesPanel : IEditorPanel
    {
        private bool _isOpen = false;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        // Preferences 패널이 알고 있는 폰트 목록. ImGuiOverlay.FontNames와 동기.
        // (Overlay 쪽 배열이 변경되면 함께 갱신할 것.)
        private static readonly string[] FontNames =
        {
            "Roboto",
            "ArchivoBlack",
            "NotoSans",
            "NotoSansKR",
        };

        private static readonly string[] ThemeNames = { "Rose", "Dark", "Light" };

        public void Draw()
        {
            if (!IsOpen) return;

            var visible = ImGui.Begin("Preferences", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Preferences");
            if (visible)
            {
                if (ImGui.CollapsingHeader("Appearance", ImGuiTreeNodeFlags.DefaultOpen))
                    DrawAppearance();

                ImGui.Spacing();

                if (ImGui.CollapsingHeader("Integrations", ImGuiTreeNodeFlags.DefaultOpen))
                    DrawIntegrations();
            }
            ImGui.End();
        }

        private void DrawAppearance()
        {
            // Color Theme
            int themeIdx = (int)EditorPreferences.ColorTheme;
            if (themeIdx < 0 || themeIdx >= ThemeNames.Length) themeIdx = 0;
            if (ImGui.Combo("Color Theme", ref themeIdx, ThemeNames, ThemeNames.Length))
            {
                EditorPreferences.ColorTheme = (EditorColorTheme)themeIdx;
                ImGuiTheme.Apply(EditorPreferences.ColorTheme);
                EditorPreferences.Save();
            }

            // UI Scale
            float scale = EditorPreferences.UiScale;
            if (ImGui.SliderFloat("UI Scale", ref scale, 0.5f, 3.0f, "%.2f"))
            {
                scale = Math.Clamp(scale, 0.5f, 3.0f);
                EditorPreferences.UiScale = scale;
                ImGui.GetIO().FontGlobalScale = scale;
                EditorPreferences.Save();
            }

            // Editor Font
            int fontIdx = Array.IndexOf(FontNames, EditorPreferences.EditorFont);
            if (fontIdx < 0) fontIdx = 0;
            if (ImGui.Combo("Editor Font", ref fontIdx, FontNames, FontNames.Length))
            {
                EditorPreferences.EditorFont = FontNames[fontIdx];
                EditorPreferences.Save();
            }
        }

        private void DrawIntegrations()
        {
            // Enable Claude Usage
            bool enabled = EditorPreferences.EnableClaudeUsage;
            if (ImGui.Checkbox("Enable Claude Usage", ref enabled))
            {
                EditorPreferences.EnableClaudeUsage = enabled;
                EditorPreferences.Save();
            }
            ImGui.TextDisabled("When enabled, Fix buttons appear in the Feedback panel and invoke claude -p.");
        }
    }
}
