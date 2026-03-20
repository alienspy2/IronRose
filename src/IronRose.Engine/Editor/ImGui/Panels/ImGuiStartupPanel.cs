// ------------------------------------------------------------
// @file    ImGuiStartupPanel.cs
// @brief   프로젝트가 로드되지 않은 상태에서 표시되는 시작 화면 패널.
//          "IronRose Engine" 타이틀, New Project / Open Project 버튼을 표시한다.
//          New Project 클릭 시 프로젝트 생성 다이얼로그를 표시하고,
//          Open Project 클릭 시 폴더 선택 다이얼로그를 표시한다.
// @deps    Editor/ProjectContext, Editor/ProjectCreator,
//          Editor/ImGui/NativeFileDialog, Editor/ImGui/Panels/IEditorPanel
// @exports
//   class ImGuiStartupPanel
//     IsOpen: bool                                    — 패널 표시 여부
//     Draw(): void                                    — ImGui 렌더링
//     ShowNewProjectDialog(): void                    — 외부에서 New Project 다이얼로그 표시
//     OpenExistingProject(): void                     — 외부에서 Open Project 실행
// @note    프로젝트 생성/열기 후 프로세스를 재시작한다 (새 CWD로).
//          ProjectContext.IsProjectLoaded == true이면 Draw()에서 아무것도 하지 않는다.
// ------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using ImGuiNET;
using IronRose.Engine.Editor.ImGuiEditor;
using Debug = RoseEngine.Debug;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// 프로젝트가 로드되지 않은 상태에서 표시되는 시작 화면 패널.
    /// </summary>
    public class ImGuiStartupPanel
    {
        private bool _showNewProjectDialog;
        private string _newProjectName = "MyGame";
        private string _newProjectPath = "";
        private string? _errorMessage;

        public void Draw()
        {
            if (ProjectContext.IsProjectLoaded) return;

            // 전체 화면 크기로 중앙 정렬된 시작 패널
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.WorkPos + viewport.WorkSize * 0.5f,
                ImGuiCond.Always,
                new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(420, 320));

            var flags = ImGuiWindowFlags.NoResize
                      | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.NoCollapse
                      | ImGuiWindowFlags.NoTitleBar
                      | ImGuiWindowFlags.NoDocking;

            if (ImGui.Begin("##Startup", flags))
            {
                // 타이틀
                var title = "IronRose Engine";
                var textSize = ImGui.CalcTextSize(title);
                ImGui.SetCursorPosX((420 - textSize.X) * 0.5f);
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.3f, 1.0f), title);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Spacing();

                // 설명 텍스트
                var desc = "Create a new project or open an existing one.";
                var descSize = ImGui.CalcTextSize(desc);
                ImGui.SetCursorPosX((420 - descSize.X) * 0.5f);
                ImGui.TextDisabled(desc);

                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();

                // 버튼
                float buttonWidth = 220;
                float buttonHeight = 40;

                ImGui.SetCursorPosX((420 - buttonWidth) * 0.5f);
                if (ImGui.Button("New Project", new Vector2(buttonWidth, buttonHeight)))
                {
                    ShowNewProjectDialog();
                }

                ImGui.Spacing();
                ImGui.SetCursorPosX((420 - buttonWidth) * 0.5f);
                if (ImGui.Button("Open Project", new Vector2(buttonWidth, buttonHeight)))
                {
                    OpenExistingProject();
                }

                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();

                // 에러 메시지 표시
                if (_errorMessage != null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                    var errSize = ImGui.CalcTextSize(_errorMessage);
                    ImGui.SetCursorPosX((420 - errSize.X) * 0.5f);
                    ImGui.TextWrapped(_errorMessage);
                    ImGui.PopStyleColor();
                }
            }
            ImGui.End();

            if (_showNewProjectDialog)
                DrawNewProjectDialog();
        }

        /// <summary>New Project 다이얼로그를 표시합니다.</summary>
        public void ShowNewProjectDialog()
        {
            _showNewProjectDialog = true;
            _errorMessage = null;
            _newProjectPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "git");
        }

        /// <summary>폴더 선택 다이얼로그를 열어 기존 프로젝트를 엽니다.</summary>
        public void OpenExistingProject()
        {
            _errorMessage = null;
            var folder = NativeFileDialog.PickFolder("Open Project");
            if (string.IsNullOrEmpty(folder)) return;

            // project.toml이 있는지 확인
            if (!File.Exists(Path.Combine(folder, "project.toml")))
            {
                _errorMessage = "Selected folder does not contain a project.toml file.";
                Debug.LogWarning($"[StartupPanel] Not a valid project: {folder}");
                return;
            }

            RestartWithProject(folder);
        }

        private void DrawNewProjectDialog()
        {
            ImGui.SetNextWindowSize(new Vector2(520, 260), ImGuiCond.FirstUseEver);

            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

            if (ImGui.Begin("New Project", ref _showNewProjectDialog,
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse))
            {
                ImGui.Text("Project Name:");
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##name", ref _newProjectName, 256);

                ImGui.Spacing();
                ImGui.Text("Location:");
                ImGui.SetNextItemWidth(-80);
                ImGui.InputText("##path", ref _newProjectPath, 1024);
                ImGui.SameLine();
                if (ImGui.Button("Browse...", new Vector2(70, 0)))
                {
                    var result = NativeFileDialog.PickFolder("Select Location", _newProjectPath);
                    if (result != null)
                        _newProjectPath = result;
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var fullPath = Path.Combine(_newProjectPath, _newProjectName);
                ImGui.TextDisabled($"Project will be created at: {fullPath}");

                // 에러 메시지
                if (_errorMessage != null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                    ImGui.TextWrapped(_errorMessage);
                    ImGui.PopStyleColor();
                }

                ImGui.Spacing();

                // 유효성 검사
                bool canCreate = !string.IsNullOrWhiteSpace(_newProjectName)
                              && !string.IsNullOrWhiteSpace(_newProjectPath)
                              && !Directory.Exists(fullPath);

                if (!canCreate) ImGui.BeginDisabled();

                if (ImGui.Button("Create", new Vector2(120, 0)))
                {
                    if (ProjectCreator.CreateFromTemplate(_newProjectName, _newProjectPath))
                    {
                        _showNewProjectDialog = false;
                        _errorMessage = null;
                        RestartWithProject(fullPath);
                    }
                    else
                    {
                        _errorMessage = "Failed to create project. Check the console for details.";
                    }
                }

                if (!canCreate) ImGui.EndDisabled();

                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(120, 0)))
                {
                    _showNewProjectDialog = false;
                    _errorMessage = null;
                }

                // 디렉토리가 이미 존재하는 경우 경고
                if (!string.IsNullOrWhiteSpace(_newProjectName)
                    && !string.IsNullOrWhiteSpace(_newProjectPath)
                    && Directory.Exists(fullPath))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.6f, 0.2f, 1.0f));
                    ImGui.TextWrapped("Directory already exists.");
                    ImGui.PopStyleColor();
                }
            }
            ImGui.End();
        }

        /// <summary>지정한 프로젝트 디렉토리에서 프로세스를 재시작합니다.</summary>
        private static void RestartWithProject(string projectDir)
        {
            Debug.Log($"[StartupPanel] Restarting with project: {projectDir}");

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Debug.LogError("[StartupPanel] Cannot determine process path for restart.");
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = projectDir,
                    UseShellExecute = false,
                };
                Process.Start(startInfo);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StartupPanel] Restart failed: {ex.Message}");
            }
        }
    }
}
