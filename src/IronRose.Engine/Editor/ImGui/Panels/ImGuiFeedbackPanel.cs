// ------------------------------------------------------------
// @file    ImGuiFeedbackPanel.cs
// @brief   사용자 피드백을 텍스트 파일로 저장/조회/삭제하는 에디터 패널.
//          프로젝트 폴더의 feedback/ 디렉토리에 feedback_XX.txt 형식으로 저장한다.
//          각 피드백 항목에 Fix 버튼을 제공하여 claude -p로 aca-fix 에이전트를 호출하고
//          stream-json 출력을 실시간 스트리밍 표시한다.
// @deps    IronRose.Engine/ProjectContext
// @exports
//   class ImGuiFeedbackPanel : IEditorPanel
//     IsOpen: bool                — 패널 표시 여부
//     Draw(): void                — ImGui 패널 렌더링
// @note    파일 목록은 Draw() 호출 시 1초 간격으로 갱신하여 I/O 부하를 줄인다.
//          feedback 폴더가 없으면 저장 시 자동 생성한다.
//          창 크기 변경 시 입력 영역/목록 영역이 가용 공간에 맞게 리사이즈된다.
//          각 feedback 항목은 CollapsingHeader로 접었다 펼 수 있으며 내용을 표시한다.
//          Fix 기능은 claude CLI를 엔진 레포에서 실행하며, 한 번에 하나만 실행 가능하다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;
using IronRose.Engine;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiFeedbackPanel : IEditorPanel
    {
        private bool _isOpen;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private string _inputText = "";
        private string _statusMessage = "";

        // Cached feedback list
        private List<FeedbackEntry> _entries = new();
        private double _lastRefreshTime;
        private const double RefreshInterval = 1.0;

        private struct FeedbackEntry
        {
            public string FilePath;
            public string FileName;
            public string Content;
        }

        // --- Fix process state ---
        private string? _fixingPath;                                         // 현재 Fix 중인 항목의 파일 경로
        private System.Diagnostics.Process? _fixProcess;
        private readonly StringBuilder _fixOutput = new();
        private readonly object _fixOutputLock = new();
        private volatile bool _fixRunning;
        private string _fixDisplayText = "";                                 // ImGui 표시용 캐시
        private bool _fixOutputDirty;

        public void Draw()
        {
            if (!IsOpen) return;

            var feedbackVisible = ImGui.Begin("Feedback", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Feedback");
            if (feedbackVisible)
            {
                DrawInputSection();
                ImGui.Separator();
                ImGui.Spacing();
                DrawFeedbackList();

                if (!string.IsNullOrEmpty(_statusMessage))
                {
                    ImGui.Separator();
                    ImGui.TextWrapped(_statusMessage);
                }
            }
            ImGui.End();
        }

        private void DrawInputSection()
        {
            float availWidth = ImGui.GetContentRegionAvail().X;

            ImGui.Text("New Feedback:");

            // Multiline input fills available width, fixed height
            ImGui.InputTextMultiline("##feedback_input", ref _inputText, 4096,
                new Vector2(availWidth, 80));

            // Save button aligned to right
            float buttonWidth = 80f;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availWidth - buttonWidth);
            if (ImGui.Button("Save", new Vector2(buttonWidth, 0)))
            {
                SaveFeedback();
            }
        }

        private void DrawFeedbackList()
        {
            RefreshIfNeeded();

            ImGui.Text($"Feedback ({_entries.Count}):");
            ImGui.Spacing();

            // Use BeginChild to fill remaining vertical space with scrollable list
            Vector2 remaining = ImGui.GetContentRegionAvail();
            // Reserve space for status message at bottom
            float reservedHeight = string.IsNullOrEmpty(_statusMessage) ? 0f : 40f;
            float listHeight = remaining.Y - reservedHeight;
            if (listHeight < 50f) listHeight = 50f;

            if (ImGui.BeginChild("##feedback_list", new Vector2(0, listHeight),
                ImGuiChildFlags.Border, ImGuiWindowFlags.None))
            {
                int deleteIndex = -1;

                for (int i = 0; i < _entries.Count; i++)
                {
                    ImGui.PushID(i);

                    if (ImGui.CollapsingHeader(_entries[i].FileName, ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        // Indent content slightly for readability
                        ImGui.Indent(8f);

                        // Display file content with word wrapping
                        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX());
                        ImGui.TextWrapped(_entries[i].Content);
                        ImGui.PopTextWrapPos();

                        ImGui.Spacing();

                        // Delete button with subtle styling
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 0.4f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.2f, 0.2f, 0.7f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.8f, 0.2f, 0.2f, 1.0f));
                        if (ImGui.Button("Delete"))
                        {
                            deleteIndex = i;
                        }
                        ImGui.PopStyleColor(3);

                        // Fix button
                        ImGui.SameLine();
                        bool isThisFixing = _fixRunning && _fixingPath == _entries[i].FilePath;
                        if (isThisFixing)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 0.6f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 0.8f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.2f, 0.2f, 1.0f));
                            if (ImGui.Button("Stop"))
                            {
                                StopFix();
                            }
                            ImGui.PopStyleColor(3);
                        }
                        else
                        {
                            bool disabled = _fixRunning; // 다른 항목이 Fix 중
                            if (disabled) ImGui.BeginDisabled();

                            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.4f, 0.7f, 0.5f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.5f, 0.8f, 0.7f));
                            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.5f, 0.9f, 1.0f));
                            if (ImGui.Button("Fix"))
                            {
                                StartFix(i);
                            }
                            ImGui.PopStyleColor(3);

                            if (disabled) ImGui.EndDisabled();
                        }

                        // Fix 출력 영역 (이 항목이 Fix 대상일 때)
                        if (_fixingPath == _entries[i].FilePath)
                        {
                            DrawFixOutput();
                        }

                        ImGui.Unindent(8f);
                        ImGui.Spacing();
                    }

                    ImGui.PopID();
                }

                if (deleteIndex >= 0)
                {
                    DeleteFeedback(deleteIndex);
                }
            }
            ImGui.EndChild();
        }

        private void SaveFeedback()
        {
            if (string.IsNullOrWhiteSpace(_inputText))
            {
                _statusMessage = "Cannot save empty feedback.";
                return;
            }

            try
            {
                var feedbackDir = GetFeedbackDir();
                Directory.CreateDirectory(feedbackDir);

                int nextNumber = GetNextFileNumber(feedbackDir);
                var fileName = $"feedback_{nextNumber:D2}.txt";
                var filePath = Path.Combine(feedbackDir, fileName);

                File.WriteAllText(filePath, _inputText, System.Text.Encoding.UTF8);

                _statusMessage = $"Saved: {fileName}";
                _inputText = "";
                RefreshEntries();

                Debug.Log($"[Feedback] Saved: {filePath}");
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                Debug.LogError($"[Feedback] Save failed: {ex}");
            }
        }

        private void DeleteFeedback(int index)
        {
            if (index < 0 || index >= _entries.Count) return;

            try
            {
                var entry = _entries[index];
                if (File.Exists(entry.FilePath))
                {
                    File.Delete(entry.FilePath);
                    _statusMessage = $"Deleted: {entry.FileName}";
                    Debug.Log($"[Feedback] Deleted: {entry.FilePath}");
                }
                RefreshEntries();
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                Debug.LogError($"[Feedback] Delete failed: {ex}");
            }
        }

        private int GetNextFileNumber(string feedbackDir)
        {
            int max = 0;
            if (Directory.Exists(feedbackDir))
            {
                foreach (var file in Directory.GetFiles(feedbackDir, "feedback_*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var parts = name.Split('_');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int num))
                    {
                        if (num > max) max = num;
                    }
                }
            }
            return max + 1;
        }

        private void RefreshIfNeeded()
        {
            double now = ImGui.GetTime();
            if (now - _lastRefreshTime > RefreshInterval)
            {
                RefreshEntries();
                _lastRefreshTime = now;
            }
        }

        private void RefreshEntries()
        {
            _entries.Clear();
            var feedbackDir = GetFeedbackDir();

            if (!Directory.Exists(feedbackDir)) return;

            var files = Directory.GetFiles(feedbackDir, "*.txt")
                .OrderBy(f => f)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    _entries.Add(new FeedbackEntry
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        Content = File.ReadAllText(file, System.Text.Encoding.UTF8),
                    });
                }
                catch
                {
                    // Skip unreadable files
                }
            }

            _lastRefreshTime = ImGui.GetTime();
        }

        // -------------------------------------------------------
        // Fix: claude -p 로 aca-fix 에이전트 호출
        // -------------------------------------------------------

        private void StartFix(int index)
        {
            if (_fixRunning || index < 0 || index >= _entries.Count) return;

            var entry = _entries[index];
            var engineRoot = ProjectContext.EngineRoot;
            if (string.IsNullOrEmpty(engineRoot) || !Directory.Exists(engineRoot))
            {
                _statusMessage = "Error: EngineRoot path not available.";
                return;
            }

            _fixingPath = entry.FilePath;
            _fixRunning = true;
            lock (_fixOutputLock)
            {
                _fixOutput.Clear();
                _fixDisplayText = "";
                _fixOutputDirty = false;
            }

            var prompt = $"aca-fix: {entry.Content}";

            Task.Run(() =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "claude",
                        Arguments = "-p --verbose --output-format stream-json",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = engineRoot,
                    };

                    _fixProcess = System.Diagnostics.Process.Start(psi);
                    if (_fixProcess == null)
                    {
                        AppendFixOutput("[Error] Failed to start claude process.\n");
                        _fixRunning = false;
                        return;
                    }

                    // stdin 으로 프롬프트 전달 후 닫기
                    _fixProcess.StandardInput.Write(prompt);
                    _fixProcess.StandardInput.Close();

                    // stderr 를 별도 태스크로 수집
                    string stderr = "";
                    var stderrTask = Task.Run(() =>
                    {
                        try { stderr = _fixProcess.StandardError.ReadToEnd(); }
                        catch { /* ignore */ }
                    });

                    // stdout 을 한 줄씩 읽으며 스트리밍 파싱
                    string? line;
                    while ((line = _fixProcess.StandardOutput.ReadLine()) != null)
                    {
                        ProcessStreamLine(line);
                    }

                    _fixProcess.WaitForExit();
                    stderrTask.Wait(TimeSpan.FromSeconds(5));

                    if (_fixProcess.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                    {
                        AppendFixOutput($"\n[stderr] {stderr}");
                    }
                }
                catch (Exception ex)
                {
                    AppendFixOutput($"\n[Error] {ex.Message}");
                }
                finally
                {
                    _fixRunning = false;
                    try { _fixProcess?.Dispose(); } catch { /* ignore */ }
                    _fixProcess = null;
                }
            });
        }

        private void StopFix()
        {
            if (_fixProcess != null)
            {
                try
                {
                    if (!_fixProcess.HasExited)
                        _fixProcess.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }
            }
            _fixRunning = false;
        }

        private void AppendFixOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_fixOutputLock)
            {
                _fixOutput.Append(text);
                _fixOutputDirty = true;
            }
        }

        /// <summary>stream-json 라인을 파싱하여 텍스트 청크를 추출한다.</summary>
        private void ProcessStreamLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                var type = typeEl.GetString();

                switch (type)
                {
                    // Anthropic streaming: 토큰 단위 텍스트 델타
                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var deltaText))
                        {
                            AppendFixOutput(deltaText.GetString() ?? "");
                        }
                        break;

                    // Claude Code: 어시스턴트 메시지 (content 배열)
                    case "assistant":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var bt) &&
                                    bt.GetString() == "text" &&
                                    block.TryGetProperty("text", out var blockText))
                                {
                                    AppendFixOutput(blockText.GetString() ?? "");
                                }
                            }
                        }
                        break;

                    // 최종 결과
                    case "result":
                        if (root.TryGetProperty("result", out var result) &&
                            result.ValueKind == JsonValueKind.String)
                        {
                            lock (_fixOutputLock)
                            {
                                if (_fixOutput.Length == 0)
                                    _fixOutput.Append(result.GetString());
                                _fixOutputDirty = true;
                            }
                        }
                        break;
                }
            }
            catch
            {
                // JSON이 아닌 라인은 그대로 표시
                AppendFixOutput(line);
                AppendFixOutput("\n");
            }
        }

        private void DrawFixOutput()
        {
            // 백그라운드 스레드에서 갱신된 텍스트를 UI 스레드로 복사
            lock (_fixOutputLock)
            {
                if (_fixOutputDirty)
                {
                    _fixDisplayText = _fixOutput.ToString();
                    _fixOutputDirty = false;
                }
            }

            ImGui.Spacing();

            // 상태 표시
            if (_fixRunning)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Running...");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), "Completed");
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear"))
                {
                    _fixingPath = null;
                    lock (_fixOutputLock)
                    {
                        _fixOutput.Clear();
                        _fixDisplayText = "";
                        _fixOutputDirty = false;
                    }
                    return;
                }
            }

            // 출력 텍스트 영역 (스크롤 가능 Child 윈도우)
            float outputHeight = Math.Min(300f, Math.Max(80f, ImGui.GetContentRegionAvail().Y * 0.4f));
            if (ImGui.BeginChild("##fix_output", new Vector2(0, outputHeight),
                ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar))
            {
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX());
                ImGui.TextUnformatted(_fixDisplayText);
                ImGui.PopTextWrapPos();

                // 실행 중일 때 자동 스크롤
                if (_fixRunning)
                {
                    ImGui.SetScrollHereY(1.0f);
                }
            }
            ImGui.EndChild();

            ImGui.Spacing();
        }

        private static string GetFeedbackDir()
        {
            return Path.Combine(ProjectContext.ProjectRoot, "feedback");
        }
    }
}
