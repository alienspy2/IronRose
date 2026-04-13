// ------------------------------------------------------------
// @file    AiImageGenerateDialog.cs
// @brief   Asset Browser 컨텍스트 메뉴의 "Generate with AI (Texture)..." 클릭 시 열리는
//          ImGui 모달 팝업. 프롬프트/파일명/토글/히스토리를 받아 AiImageGenerationService에 Enqueue한다.
// @deps    IronRose.Engine.Editor/AiImageGenerationService,
//          IronRose.Engine.Editor/AiImageHistory,
//          IronRose.Engine.Editor.ImGuiEditor/EditorModal, ImGuiNET
// @exports
//   internal sealed class AiImageGenerateDialog
//     Open(string targetFolderAbsPath): void  — 다음 Draw() 호출에 모달 팝업을 연다
//     Draw(): void                            — 매 프레임 호출, 팝업 상태를 처리
// @note    FileName 프리뷰는 매 프레임 ResolveUniqueFileName을 호출해 suffix 충돌 회피 후 확정 이름을 표시.
//          Generate 버튼 클릭 직후 다시 Resolve하여 찰나의 경쟁 조건 최소화.
//          히스토리 클릭 시 Style/Prompt만 복사하고 토글은 건드리지 않는다.
// ------------------------------------------------------------
using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using IronRose.Engine.Editor;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Asset Browser 컨텍스트 메뉴에서 열리는 AI 이미지 생성 모달 다이얼로그.
    /// ImGuiProjectPanel이 소유하며, Draw()를 매 프레임 호출해야 한다.
    /// </summary>
    internal sealed class AiImageGenerateDialog
    {
        private const string PopupId = "Generate with AI (Texture)##aiimg";

        private bool _wantOpen = false;
        private string _targetFolderAbs = "";

        // Form state
        private string _stylePrompt = "";
        private string _prompt = "";
        private string _fileName = "new_texture";
        private bool _refine = true;
        private bool _alpha = false;
        private int _selectedHistoryIndex = -1;

        /// <summary>다음 Draw() 호출에 팝업을 연다. 폼 상태는 초기화되고 토글은 히스토리 기준으로 복원.</summary>
        public void Open(string targetFolderAbsPath)
        {
            _targetFolderAbs = Path.GetFullPath(targetFolderAbsPath);
            _stylePrompt = "";
            _prompt = "";
            _fileName = "new_texture";
            var toggles = AiImageHistory.LastToggles;
            _refine = toggles.Refine;
            _alpha = toggles.Alpha;
            _selectedHistoryIndex = -1;
            _wantOpen = true;
        }

        /// <summary>매 프레임 호출. Open()이 호출된 프레임에 팝업을 띄운다.</summary>
        public void Draw()
        {
            if (_wantOpen)
            {
                ImGui.OpenPopup(PopupId);
                ImGui.SetNextWindowSize(new Vector2(560, 520), ImGuiCond.Appearing);
                _wantOpen = false;
            }

            if (!ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize))
                return;

            // Target folder (read-only display)
            ImGui.TextDisabled("Folder: " + _targetFolderAbs);
            ImGui.Separator();

            // Style prompt (3 lines)
            ImGui.TextUnformatted("Style Prompt");
            ImGui.InputTextMultiline("##aiimg_style", ref _stylePrompt, 2048,
                new Vector2(-1, ImGui.GetTextLineHeight() * 3.5f));

            // Prompt (4 lines)
            ImGui.TextUnformatted("Prompt");
            ImGui.InputTextMultiline("##aiimg_prompt", ref _prompt, 4096,
                new Vector2(-1, ImGui.GetTextLineHeight() * 4.5f));

            // File name
            ImGui.TextUnformatted("File Name");
            ImGui.InputText("##aiimg_filename", ref _fileName, 256);

            // Resolved name preview
            string previewLabel;
            if (string.IsNullOrWhiteSpace(_fileName))
            {
                previewLabel = "(file name required)";
            }
            else
            {
                string trimmed = _fileName.Trim();
                string resolved = AiImageGenerationService.ResolveUniqueFileName(_targetFolderAbs, trimmed);
                if (resolved == trimmed)
                    previewLabel = $"-> {resolved}.png";
                else
                    previewLabel = $"-> {trimmed}.png exists, will save as {resolved}.png";
            }
            ImGui.TextDisabled(previewLabel);

            // Toggles
            ImGui.Checkbox("Refine Prompt with AI", ref _refine);
            ImGui.SameLine();
            ImGui.Checkbox("Alpha Channel", ref _alpha);

            // History
            ImGui.Separator();
            ImGui.TextUnformatted("History (click to copy into prompts)");
            var entries = AiImageHistory.Entries;
            if (entries.Count == 0)
            {
                ImGui.TextDisabled("(empty)");
            }
            else
            {
                if (ImGui.BeginListBox("##aiimg_history", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 5.5f)))
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        string preview = e.Prompt.Length > 40 ? e.Prompt.Substring(0, 40) + "..." : e.Prompt;
                        string label = string.IsNullOrEmpty(e.StylePrompt)
                            ? preview
                            : $"{e.StylePrompt} | {preview}";
                        bool selected = _selectedHistoryIndex == i;
                        if (ImGui.Selectable(label + $"##h{i}", selected))
                        {
                            _selectedHistoryIndex = i;
                            _stylePrompt = e.StylePrompt;
                            _prompt = e.Prompt;
                        }
                    }
                    ImGui.EndListBox();
                }
            }

            ImGui.Separator();

            // Buttons
            bool canGenerate = !string.IsNullOrWhiteSpace(_fileName) && !string.IsNullOrWhiteSpace(_prompt);
            if (!canGenerate)
                ImGui.TextDisabled("Prompt and File Name are required.");

            ImGui.BeginDisabled(!canGenerate);
            if (ImGui.Button("Generate", new Vector2(120, 0)))
            {
                string resolved = AiImageGenerationService.ResolveUniqueFileName(_targetFolderAbs, _fileName.Trim());
                var req = new AiImageGenerationRequest(
                    TargetFolderAbsPath: _targetFolderAbs,
                    ResolvedFileName: resolved,
                    StylePrompt: _stylePrompt?.Trim() ?? "",
                    Prompt: _prompt?.Trim() ?? "",
                    Refine: _refine,
                    Alpha: _alpha);

                if (AiImageGenerationService.Enqueue(req))
                {
                    EditorModal.EnqueueAlert($"AI image generation started: {resolved}.png\n(You can continue working; a notification will appear when done.)");
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }
}
