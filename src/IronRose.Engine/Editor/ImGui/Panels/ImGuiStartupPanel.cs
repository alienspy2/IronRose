// ------------------------------------------------------------
// @file    ImGuiStartupPanel.cs
// @brief   프로젝트가 로드되지 않은 상태에서 표시되는 시작 화면 패널.
//          "IronRose Engine" 타이틀, New Project / Open Project 버튼을 표시한다.
//          New Project 클릭 시 프로젝트 생성 다이얼로그를 표시하고,
//          Open Project 클릭 시 폴더 선택 다이얼로그를 표시한다.
//          프로젝트 선택 후에는 설정 파일에 경로를 저장하고 재시작 안내 다이얼로그를 표시한다.
// @deps    Editor/ProjectContext, Editor/ProjectCreator,
//          Editor/ImGui/NativeFileDialog
// @exports
//   class ImGuiStartupPanel
//     Draw(): void                                    — ImGui 렌더링
//     ShowNewProjectDialog(): void                    — 외부에서 New Project 다이얼로그 표시
//     OpenExistingProject(): void                     — 외부에서 Open Project 실행
// @note    프로젝트 생성/열기 후 설정 파일에 경로를 저장하고, 재시작 안내 모달을 표시한 뒤
//          사용자가 "Exit" 버튼을 누르면 Environment.Exit(0)으로 프로세스를 종료한다.
//          ProjectContext.IsProjectLoaded == true이면 Draw()에서 welcome 화면을 건너뛴다.
// ------------------------------------------------------------
using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using IronRose.Engine.Editor.ImGuiEditor;
using Debug = RoseEngine.EditorDebug;

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
        private volatile bool _waitingForDialog;
        private bool _showRestartNotice;
        private string? _selectedProjectPath;
        private string? _workspacePath;

        // 백그라운드 스레드 → 메인 스레드 전달용 (volatile)
        private volatile string? _pendingBrowsePath;
        private volatile string? _pendingOpenProjectPath;
        private volatile string? _pendingErrorFromOpen;

        public void Draw()
        {
            // 백그라운드 스레드 결과를 메인 스레드에서 적용
            DrainPendingResults();

            // 프로젝트 미로드 시: welcome 화면 표시
            if (!ProjectContext.IsProjectLoaded)
            {
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
                    var title = "IronRose Engine";
                    var textSize = ImGui.CalcTextSize(title);
                    ImGui.SetCursorPosX((420 - textSize.X) * 0.5f);
                    ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.3f, 1.0f), title);

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                    ImGui.Spacing();

                    var desc = "Create a new project or open an existing one.";
                    var descSize = ImGui.CalcTextSize(desc);
                    ImGui.SetCursorPosX((420 - descSize.X) * 0.5f);
                    ImGui.TextDisabled(desc);

                    ImGui.Spacing();
                    ImGui.Spacing();
                    ImGui.Spacing();

                    float buttonWidth = 220;
                    float buttonHeight = 40;

                    ImGui.SetCursorPosX((420 - buttonWidth) * 0.5f);
                    if (ImGui.Button("New Project", new Vector2(buttonWidth, buttonHeight)))
                        ShowNewProjectDialog();

                    ImGui.Spacing();
                    ImGui.SetCursorPosX((420 - buttonWidth) * 0.5f);
                    if (ImGui.Button("Open Project", new Vector2(buttonWidth, buttonHeight)))
                        OpenExistingProject();

                    ImGui.Spacing();
                    ImGui.Spacing();
                    ImGui.Spacing();

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
            }

            // New Project 다이얼로그는 프로젝트 로드 여부와 무관하게 표시
            if (_showNewProjectDialog)
                DrawNewProjectDialog();

            DrawRestartNoticeDialog();
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
            if (_waitingForDialog) return;
            _errorMessage = null;
            _waitingForDialog = true;

            Task.Run(() =>
            {
                var folder = NativeFileDialog.PickFolder("Open Project");
                if (!string.IsNullOrEmpty(folder))
                {
                    if (!File.Exists(Path.Combine(folder, "project.toml")))
                    {
                        _pendingErrorFromOpen = "Selected folder does not contain a project.toml file.";
                        Debug.LogWarning($"[StartupPanel] Not a valid project: {folder}");
                    }
                    else
                    {
                        _pendingOpenProjectPath = folder;
                    }
                }
                _waitingForDialog = false;
            });
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
                if (ImGui.Button("Browse...", new Vector2(70, 0)) && !_waitingForDialog)
                {
                    _waitingForDialog = true;
                    var initialPath = _newProjectPath;
                    Task.Run(() =>
                    {
                        var result = NativeFileDialog.PickFolder("Select Location", initialPath);
                        if (result != null)
                            _pendingBrowsePath = result;
                        _waitingForDialog = false;
                    });
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
                        NativeFileDialog.KillRunning();
                        SetProjectAndNotifyRestart(fullPath);
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

        /// <summary>프로젝트 경로를 설정 파일에 저장하고 재시작 안내를 표시합니다.</summary>
        private void SetProjectAndNotifyRestart(string projectDir)
        {
            Debug.Log($"[StartupPanel] Project selected: {projectDir}");
            ProjectContext.SaveLastProjectPath(projectDir);
            _workspacePath = ProjectCreator.UpdateEngineWorkspace(projectDir);
            _selectedProjectPath = projectDir;
            _showRestartNotice = true;
        }

        /// <summary>백그라운드 스레드에서 설정된 결과를 메인 스레드에서 적용합니다.</summary>
        private void DrainPendingResults()
        {
            var browsePath = _pendingBrowsePath;
            if (browsePath != null)
            {
                _newProjectPath = browsePath;
                _pendingBrowsePath = null;
            }

            var openPath = _pendingOpenProjectPath;
            if (openPath != null)
            {
                _pendingOpenProjectPath = null;
                SetProjectAndNotifyRestart(openPath);
            }

            var openError = _pendingErrorFromOpen;
            if (openError != null)
            {
                _errorMessage = openError;
                _pendingErrorFromOpen = null;
            }
        }

        private void DrawRestartNoticeDialog()
        {
            if (!_showRestartNotice) return;

            ImGui.OpenPopup("##RestartNotice");
            var center = ImGui.GetMainViewport().GetCenter();
            ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(500, 240));

            if (ImGui.BeginPopupModal("##RestartNotice", ref _showRestartNotice,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
            {
                ImGui.Spacing();
                ImGui.TextWrapped("Project has been set. Please restart the editor.");
                ImGui.Spacing();
                ImGui.TextDisabled(_selectedProjectPath ?? "");
                ImGui.Spacing();

                if (_workspacePath != null)
                {
                    ImGui.Separator();
                    ImGui.Spacing();
                    ImGui.TextWrapped("To open with VS Code (with full IntelliSense), run:");
                    ImGui.Spacing();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.1f, 0.3f, 0.7f, 1.0f));
                    ImGui.TextWrapped($"  code \"{_workspacePath}\"");
                    ImGui.PopStyleColor();
                    ImGui.Spacing();
                    ImGui.TextDisabled("A solution (engine + game) has been generated for IntelliSense.");
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                float buttonWidth = 120;
                ImGui.SetCursorPosX((500 - buttonWidth) * 0.5f);
                if (ImGui.Button("Exit", new Vector2(buttonWidth, 0)))
                {
                    NativeFileDialog.KillRunning();
                    Environment.Exit(0);
                }
                ImGui.EndPopup();
            }
        }
    }
}
