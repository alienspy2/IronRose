// ------------------------------------------------------------
// @file    ImGuiPreferencesPanel.cs
// @brief   앱-레벨 사용자 Preferences 편집 UI. Edit > Preferences... 메뉴에서 열리며
//          Appearance(Color Theme / UI Scale / Editor Font), Integrations(Enable Claude Usage),
//          AI Asset Generation(토글 + 서버 URL + Health Check + Python Path + Refine Endpoint/Model)
//          섹션을 제공한다.
// @deps    IronRose.Engine/EditorPreferences, IronRose.Engine.Editor.ImGuiEditor/ImGuiTheme,
//          IronRose.Engine.Editor.ImGuiEditor/EditorWidgets,
//          IronRose.Engine.Editor.ImGuiEditor/PanelMaximizer,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/IEditorPanel, ImGuiNET, System.Net.Http
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
using System.Net.Http;
using System.Threading.Tasks;

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

        // Health Check 상태 캐시 (세션 전용, 영속화 없음)
        private string _healthCheckLabel = "";      // 화면에 표시할 결과 라벨 ("OK", "Failed: ..." 등)
        private uint _healthCheckColor = 0xFFFFFFFF; // 라벨 색상 (ImGui ABGR). 0 이면 TextUnformatted 기본색
        private bool _healthCheckRunning = false;    // 중복 클릭 방지
        private static readonly HttpClient _healthHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

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

                ImGui.Spacing();

                if (ImGui.CollapsingHeader("AI Asset Generation", ImGuiTreeNodeFlags.DefaultOpen))
                    DrawAiAssetGeneration();
            }
            ImGui.End();
        }

        private void DrawAppearance()
        {
            // Color Theme
            int themeIdx = (int)EditorPreferences.ColorTheme;
            if (themeIdx < 0 || themeIdx >= ThemeNames.Length) themeIdx = 0;
            string themeLabel = EditorWidgets.BeginPropertyRow("Color Theme");
            if (ImGui.Combo(themeLabel, ref themeIdx, ThemeNames, ThemeNames.Length))
            {
                EditorPreferences.ColorTheme = (EditorColorTheme)themeIdx;
                ImGuiTheme.Apply(EditorPreferences.ColorTheme);
                EditorPreferences.Save();
            }

            // UI Scale
            float scale = EditorPreferences.UiScale;
            if (EditorWidgets.SliderFloatWithInput("preferences", "UI Scale", ref scale, 0.5f, 3.0f))
            {
                scale = Math.Clamp(scale, 0.5f, 3.0f);
                EditorPreferences.UiScale = scale;
                ImGui.GetIO().FontGlobalScale = scale;
                EditorPreferences.Save();
            }

            // Editor Font
            int fontIdx = Array.IndexOf(FontNames, EditorPreferences.EditorFont);
            if (fontIdx < 0) fontIdx = 0;
            string fontLabel = EditorWidgets.BeginPropertyRow("Editor Font");
            if (ImGui.Combo(fontLabel, ref fontIdx, FontNames, FontNames.Length))
            {
                EditorPreferences.EditorFont = FontNames[fontIdx];
                EditorPreferences.Save();
            }
        }

        private void DrawIntegrations()
        {
            // Enable Claude Usage
            bool enabled = EditorPreferences.EnableClaudeUsage;
            string claudeLabel = EditorWidgets.BeginPropertyRow("Enable Claude Usage");
            if (ImGui.Checkbox(claudeLabel, ref enabled))
            {
                EditorPreferences.EnableClaudeUsage = enabled;
                EditorPreferences.Save();
            }
            ImGui.TextDisabled("When enabled, Fix buttons appear in the Feedback panel and invoke claude -p.");
        }

        private void DrawAiAssetGeneration()
        {
            // Enable 토글
            bool enabled = EditorPreferences.EnableAiAssetGeneration;
            string enableLabel = EditorWidgets.BeginPropertyRow("Enable AI Asset Generation");
            if (ImGui.Checkbox(enableLabel, ref enabled))
            {
                EditorPreferences.EnableAiAssetGeneration = enabled;
                EditorPreferences.Save();
            }
            ImGui.TextDisabled("When enabled, \"Generate with AI (Texture)...\" appears in the Asset Browser context menu.");

            // 토글이 꺼지면 하위 위젯은 비활성화 (값 편집은 가능하되 시각적으로 disabled 표시)
            ImGui.BeginDisabled(!enabled);

            // AlienHS Server URL
            {
                string serverLabel = EditorWidgets.BeginPropertyRow("AlienHS Server URL");
                string buf = EditorPreferences.AiAlienhsServerUrl ?? "";
                if (ImGui.InputText(serverLabel, ref buf, 512))
                {
                    EditorPreferences.AiAlienhsServerUrl = buf;
                    EditorPreferences.Save();
                }
            }

            // Health Check 버튼 + 결과 라벨
            {
                string hcLabel = EditorWidgets.BeginPropertyRow("Health Check");
                ImGui.BeginDisabled(_healthCheckRunning);
                if (ImGui.Button(_healthCheckRunning ? "Checking...##healthcheck" : "Check##healthcheck"))
                {
                    RunHealthCheck(EditorPreferences.AiAlienhsServerUrl);
                }
                ImGui.EndDisabled();

                if (!string.IsNullOrEmpty(_healthCheckLabel))
                {
                    ImGui.SameLine();
                    if (_healthCheckColor != 0)
                        ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(_healthCheckColor), _healthCheckLabel);
                    else
                        ImGui.TextUnformatted(_healthCheckLabel);
                }
            }

            // Python Path
            {
                string pyLabel = EditorWidgets.BeginPropertyRow("Python Path");
                string buf = EditorPreferences.AiPythonPath ?? "";
                if (ImGui.InputText(pyLabel, ref buf, 512))
                {
                    EditorPreferences.AiPythonPath = buf;
                    EditorPreferences.Save();
                }
            }

            // Refine Endpoint
            {
                string epLabel = EditorWidgets.BeginPropertyRow("Refine Endpoint");
                string buf = EditorPreferences.AiRefineEndpoint ?? "";
                if (ImGui.InputText(epLabel, ref buf, 256))
                {
                    EditorPreferences.AiRefineEndpoint = buf;
                    EditorPreferences.Save();
                }
                ImGui.TextDisabled("Empty = CLI default.");
            }

            // Refine Model
            {
                string modelLabel = EditorWidgets.BeginPropertyRow("Refine Model");
                string buf = EditorPreferences.AiRefineModel ?? "";
                if (ImGui.InputText(modelLabel, ref buf, 256))
                {
                    EditorPreferences.AiRefineModel = buf;
                    EditorPreferences.Save();
                }
                ImGui.TextDisabled("Empty = CLI default.");
            }

            ImGui.EndDisabled();
        }

        /// <summary>
        /// AlienHS 서버의 루트 경로에 GET 요청을 보내 200이면 OK, 그 외는 Failed로 표시.
        /// 3초 타임아웃. 백그라운드 Task에서 실행하며 결과는 다음 프레임 렌더에 반영된다.
        /// </summary>
        /// <param name="serverUrl">Preferences.AiAlienhsServerUrl 값.</param>
        private void RunHealthCheck(string serverUrl)
        {
            if (_healthCheckRunning) return;
            _healthCheckRunning = true;
            _healthCheckLabel = "Checking...";
            _healthCheckColor = 0xFFAAAAAA; // 회색

            string url = (serverUrl ?? "").TrimEnd('/') + "/";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var resp = await _healthHttp.GetAsync(url);
                    int code = (int)resp.StatusCode;
                    if (code == 200)
                    {
                        _healthCheckLabel = "OK";
                        _healthCheckColor = 0xFF00FF00; // 녹색 (ABGR)
                    }
                    else
                    {
                        _healthCheckLabel = $"Failed: HTTP {code}";
                        _healthCheckColor = 0xFF0000FF; // 빨강
                    }
                }
                catch (TaskCanceledException)
                {
                    _healthCheckLabel = "Failed: timeout";
                    _healthCheckColor = 0xFF0000FF;
                }
                catch (Exception ex)
                {
                    _healthCheckLabel = $"Failed: {ex.Message}";
                    _healthCheckColor = 0xFF0000FF;
                }
                finally
                {
                    _healthCheckRunning = false;
                }
            });
        }
    }
}
