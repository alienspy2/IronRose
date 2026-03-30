// ------------------------------------------------------------
// @file    ImGuiFeedbackPanel.cs
// @brief   사용자 피드백을 텍스트 파일로 저장/조회/삭제하는 에디터 패널.
//          프로젝트 폴더의 feedback/ 디렉토리에 feedback_XX.txt 형식으로 저장한다.
// @deps    IronRose.Engine/ProjectContext
// @exports
//   class ImGuiFeedbackPanel : IEditorPanel
//     IsOpen: bool                — 패널 표시 여부
//     Draw(): void                — ImGui 패널 렌더링
// @note    파일 목록은 Draw() 호출 시 1초 간격으로 갱신하여 I/O 부하를 줄인다.
//          feedback 폴더가 없으면 저장 시 자동 생성한다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using IronRose.Engine;
using RoseEngine;

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

        public void Draw()
        {
            if (!IsOpen) return;

            if (ImGui.Begin("Feedback", ref _isOpen))
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
            ImGui.Text("New Feedback:");
            ImGui.InputTextMultiline("##feedback_input", ref _inputText, 4096,
                new Vector2(ImGui.GetContentRegionAvail().X, 100));

            if (ImGui.Button("Save", new Vector2(80, 0)))
            {
                SaveFeedback();
            }
        }

        private void DrawFeedbackList()
        {
            RefreshIfNeeded();

            ImGui.Text($"Feedback ({_entries.Count}):");
            ImGui.Spacing();

            int deleteIndex = -1;

            for (int i = 0; i < _entries.Count; i++)
            {
                ImGui.PushID(i);

                if (ImGui.CollapsingHeader(_entries[i].FileName, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.TextWrapped(_entries[i].Content);

                    if (ImGui.Button("Delete"))
                    {
                        deleteIndex = i;
                    }
                }

                ImGui.PopID();
            }

            if (deleteIndex >= 0)
            {
                DeleteFeedback(deleteIndex);
            }
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

        private static string GetFeedbackDir()
        {
            return Path.Combine(ProjectContext.ProjectRoot, "feedback");
        }
    }
}
