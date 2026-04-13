// ------------------------------------------------------------
// @file    ImGuiPreferencesPanel.cs
// @brief   앱-레벨 사용자 Preferences 편집 UI. Edit > Preferences... 메뉴에서 열리며
//          Appearance(Color Theme / UI Scale / Editor Font), Integrations(Enable Claude Usage),
//          AI Asset Generation(토글 + 서버 URL + Server Health Check + Comfy URL 오버라이드 +
//          Python Path + Python Health Check + Generation Model) 섹션을 제공한다.
// @deps    IronRose.Engine/EditorPreferences, IronRose.Engine.Editor.ImGuiEditor/ImGuiTheme,
//          IronRose.Engine.Editor.ImGuiEditor/EditorWidgets,
//          IronRose.Engine.Editor.ImGuiEditor/PanelMaximizer,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/IEditorPanel, ImGuiNET, System.Net.Http,
//          System.Diagnostics.Process, System.ComponentModel.Win32Exception
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
using System.ComponentModel;
using System.Diagnostics;
using ImGuiNET;
using System.Net.Http;
using System.Text.RegularExpressions;
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

        // Server URL Health Check 상태 캐시 (세션 전용, 영속화 없음)
        private string _healthCheckLabel = "";      // 화면에 표시할 결과 라벨 ("OK", "Failed: ..." 등)
        private uint _healthCheckColor = 0xFFFFFFFF; // 라벨 색상 (ImGui ABGR). 0 이면 TextUnformatted 기본색
        private bool _healthCheckRunning = false;    // 중복 클릭 방지
        private static readonly HttpClient _healthHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

        // Python Path Health Check 상태 캐시 (세션 전용, 영속화 없음)
        private string _pythonCheckLabel = "";
        private uint _pythonCheckColor = 0xFFFFFFFF;
        private bool _pythonCheckRunning = false;

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

            // Comfy URL (AlienHS 서버가 사용할 ComfyUI URL 오버라이드)
            {
                string comfyLabel = EditorWidgets.BeginPropertyRow("Comfy URL");
                string buf = EditorPreferences.AiComfyUrl ?? "";
                if (ImGui.InputText(comfyLabel, ref buf, 512))
                {
                    EditorPreferences.AiComfyUrl = buf;
                    EditorPreferences.Save();
                }
                ImGui.TextDisabled("Override AlienHS default (leave empty to use server's default).");
            }

            // Server Health Check 버튼 + 결과 라벨
            {
                string hcLabel = EditorWidgets.BeginPropertyRow("Health Check");
                ImGui.BeginDisabled(_healthCheckRunning);
                if (ImGui.Button(_healthCheckRunning ? "Checking...##healthcheck" : "Check##healthcheck"))
                {
                    RunHealthCheck(EditorPreferences.AiAlienhsServerUrl ?? "");
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

            // Python Health Check 버튼 + 결과 라벨
            {
                string hcLabel = EditorWidgets.BeginPropertyRow("Python Check");
                ImGui.BeginDisabled(_pythonCheckRunning);
                if (ImGui.Button(_pythonCheckRunning ? "Checking...##pythoncheck" : "Check##pythoncheck"))
                {
                    RunPythonHealthCheck(EditorPreferences.AiPythonPath ?? "");
                }
                ImGui.EndDisabled();

                if (!string.IsNullOrEmpty(_pythonCheckLabel))
                {
                    ImGui.SameLine();
                    if (_pythonCheckColor != 0)
                        ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(_pythonCheckColor), _pythonCheckLabel);
                    else
                        ImGui.TextUnformatted(_pythonCheckLabel);
                }
            }

            // Generation Model (ComfyUI 이미지 생성 모델 파일명. CLI의 --model 인자)
            {
                string modelLabel = EditorWidgets.BeginPropertyRow("Generation Model");
                string buf = EditorPreferences.AiGenerationModel ?? "";
                if (ImGui.InputText(modelLabel, ref buf, 256))
                {
                    EditorPreferences.AiGenerationModel = buf;
                    EditorPreferences.Save();
                }
                ImGui.TextDisabled("ComfyUI model file name (e.g., z_image_turbo_nvfp4.safetensors). Empty = CLI default.");
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

        /// <summary>
        /// AiPythonPath 값을 그대로 Process.Start로 호출하여 `--version`을 수행한다.
        /// 정상 종료 + stdout/stderr에 "Python" 포함 시 OK. Win32Exception(파일 없음),
        /// 타임아웃(3초), exit code != 0, 기타 예외는 Failed로 표시한다.
        /// 백그라운드 Task에서 실행하며 UI를 블로킹하지 않는다.
        /// </summary>
        /// <param name="pythonPath">Preferences.AiPythonPath 값.</param>
        private void RunPythonHealthCheck(string pythonPath)
        {
            if (_pythonCheckRunning) return;
            _pythonCheckRunning = true;
            _pythonCheckLabel = "Checking...";
            _pythonCheckColor = 0xFFAAAAAA; // 회색

            string path = (pythonPath ?? "").Trim();
            if (string.IsNullOrEmpty(path))
            {
                _pythonCheckLabel = "Failed: path is empty";
                _pythonCheckColor = 0xFF0000FF;
                _pythonCheckRunning = false;
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = path,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("--version");

                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        _pythonCheckLabel = "Failed: could not start process";
                        _pythonCheckColor = 0xFF0000FF;
                        return;
                    }

                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    bool exited = proc.WaitForExit(3000);
                    if (!exited)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        _pythonCheckLabel = "Failed: timeout";
                        _pythonCheckColor = 0xFF0000FF;
                        return;
                    }

                    int exitCode = proc.ExitCode;
                    string combined = (stdout + "\n" + stderr);

                    if (exitCode == 0 && combined.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var match = Regex.Match(combined, @"Python\s+[\w\.\-\+]+", RegexOptions.IgnoreCase);
                        string version = match.Success ? match.Value.Trim() : "Python";
                        _pythonCheckLabel = $"OK ({version})";
                        _pythonCheckColor = 0xFF00FF00; // 녹색
                    }
                    else if (exitCode != 0)
                    {
                        _pythonCheckLabel = $"Failed: exit code {exitCode}";
                        _pythonCheckColor = 0xFF0000FF;
                    }
                    else
                    {
                        _pythonCheckLabel = "Failed: no \"Python\" in output";
                        _pythonCheckColor = 0xFF0000FF;
                    }
                }
                catch (Win32Exception ex)
                {
                    // 파일 없음, 권한 없음 등
                    _pythonCheckLabel = $"Failed: {ex.Message}";
                    _pythonCheckColor = 0xFF0000FF;
                }
                catch (Exception ex)
                {
                    _pythonCheckLabel = $"Failed: {ex.Message}";
                    _pythonCheckColor = 0xFF0000FF;
                }
                finally
                {
                    _pythonCheckRunning = false;
                }
            });
        }
    }
}
