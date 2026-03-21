// ------------------------------------------------------------
// @file    ImGuiLayoutManager.cs
// @brief   ImGui 독 레이아웃의 저장/복원/기본 레이아웃 적용을 관리한다.
//          레이아웃 데이터는 EditorState에 통합 저장되며, 레거시 INI 파일 마이그레이션도 지원.
// @deps    IronRose.Engine/ProjectContext, IronRose.Engine.Editor/EditorState,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/*, ImGuiNET
// @exports
//   class ImGuiLayoutManager (internal, sealed)
//     NeedsLayout: bool                         — 기본 레이아웃 적용 필요 여부
//     TryLoadSaved(): void                      — 저장된 레이아웃 로드 (EditorState 우선, 레거시 INI 폴백)
//     RequestReset(): void                      — 레이아웃 리셋 요청
//     ApplyDefaultIfNeeded(...): void           — 기본 레이아웃 적용
//     UpdateAutoSave(float): void               — 자동 저장 타이머 업데이트
//     Save(): void                              — 현재 레이아웃을 EditorState에 저장
// @note    레거시 EditorLayout.ini 파일 경로는 ProjectContext.ProjectRoot를 기준으로 해석.
//          마이그레이션 시 INI 데이터를 EditorState로 이관 후 .rose_editor_state.toml에 저장.
// ------------------------------------------------------------
using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using Silk.NET.Windowing;
using Debug = RoseEngine.EditorDebug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// 독 레이아웃 저장/복원/기본 레이아웃 적용.
    /// ImGuiOverlay에서 분리 (Phase 15 — M-4).
    /// 레이아웃 데이터는 EditorState (.rose_editor_state.toml) 에 통합 저장.
    /// </summary>
    internal sealed class ImGuiLayoutManager
    {
        private const string LegacyLayoutPath = "EditorLayout.ini";
        private const float AutoSaveInterval = 3f;

        private bool _needsDefaultLayout = true;
        private bool _resetLayoutRequested;
        private float _autoSaveTimer;

        public bool NeedsLayout => _needsDefaultLayout || _resetLayoutRequested;

        /// <summary>저장된 레이아웃이 있으면 로드 (EditorState 우선, 레거시 INI 파일 폴백).</summary>
        public void TryLoadSaved()
        {
            // 1) EditorState에 저장된 레이아웃 데이터 우선
            if (!string.IsNullOrEmpty(EditorState.ImGuiLayoutData))
            {
                ImGui.LoadIniSettingsFromMemory(EditorState.ImGuiLayoutData);
                _needsDefaultLayout = false;
                Debug.Log("[ImGui] Layout loaded from .rose_editor_state.toml");
                return;
            }

            // 2) 레거시 EditorLayout.ini 파일에서 마이그레이션
            var legacyFullPath = Path.Combine(ProjectContext.ProjectRoot, LegacyLayoutPath);
            if (File.Exists(legacyFullPath))
            {
                ImGui.LoadIniSettingsFromDisk(legacyFullPath);
                _needsDefaultLayout = false;

                // 마이그레이션: INI → EditorState로 이관
                EditorState.ImGuiLayoutData = ImGui.SaveIniSettingsToMemory();
                EditorState.Save();
                Debug.Log("[ImGui] Layout migrated from " + LegacyLayoutPath + " → .rose_editor_state.toml");
            }
        }

        public void RequestReset()
        {
            _resetLayoutRequested = true;
        }

        /// <summary>기본 레이아웃 적용이 필요하면 적용하고 패널을 열기.</summary>
        public void ApplyDefaultIfNeeded(uint dockspaceId, IWindow window,
            ImGuiHierarchyPanel hierarchy, ImGuiInspectorPanel inspector,
            ImGuiSceneEnvironmentPanel sceneEnvironment,
            ImGuiConsolePanel console,
            ImGuiGameViewPanel gameView, ImGuiProjectPanel project,
            ImGuiSceneViewPanel? sceneView = null,
            ImGuiScriptsPanel? scripts = null)
        {
            if (!_needsDefaultLayout && !_resetLayoutRequested) return;

            _needsDefaultLayout = false;
            _resetLayoutRequested = false;

            var size = new Vector2(window.Size.X, window.Size.Y);

            ImGuiDockBuilder.RemoveNode(dockspaceId);
            ImGuiDockBuilder.AddNode(dockspaceId, ImGuiDockBuilder.DockNodeFlagsDockSpace);
            ImGuiDockBuilder.SetNodeSize(dockspaceId, size);

            ImGuiDockBuilder.SplitNode(dockspaceId, ImGuiDockBuilder.DirDown, 0.25f, out uint bottomId, out uint topId);
            ImGuiDockBuilder.SplitNode(topId, ImGuiDockBuilder.DirLeft, 0.18f, out uint leftId, out uint centerRightId);
            ImGuiDockBuilder.SplitNode(centerRightId, ImGuiDockBuilder.DirRight, 0.27f, out uint rightId, out uint centerId);

            // Split center into Scene View (left) and Game View (right)
            ImGuiDockBuilder.SplitNode(centerId, ImGuiDockBuilder.DirLeft, 0.5f, out uint sceneViewId, out uint gameViewId);

            ImGuiDockBuilder.DockWindow("Hierarchy", leftId);
            ImGuiDockBuilder.DockWindow("Scene View", sceneViewId);
            ImGuiDockBuilder.DockWindow("Game View", gameViewId);
            ImGuiDockBuilder.DockWindow("Inspector", rightId);
            ImGuiDockBuilder.DockWindow("Scene Environment", rightId);
            ImGuiDockBuilder.SplitNode(bottomId, ImGuiDockBuilder.DirLeft, 0.40f,
                out uint bottomLeftId, out uint bottomRightId);

            ImGuiDockBuilder.DockWindow("Project", bottomLeftId);
            ImGuiDockBuilder.DockWindow("Scripts", bottomLeftId);
            ImGuiDockBuilder.DockWindow("Console", bottomRightId);

            ImGuiDockBuilder.Finish(dockspaceId);

            hierarchy.IsOpen = true;
            inspector.IsOpen = true;
            sceneEnvironment.IsOpen = true;
            console.IsOpen = true;
            gameView.IsOpen = true;
            project.IsOpen = true;
            if (sceneView != null) sceneView.IsOpen = true;
            if (scripts != null) scripts.IsOpen = true;

            Debug.Log("[ImGui] Default layout applied");
        }

        /// <summary>자동 저장 타이머 업데이트.</summary>
        public void UpdateAutoSave(float deltaTime)
        {
            _autoSaveTimer += deltaTime;
            if (_autoSaveTimer >= AutoSaveInterval)
            {
                _autoSaveTimer = 0f;
                Save();
            }
        }

        public void Save()
        {
            try
            {
                EditorState.ImGuiLayoutData = ImGui.SaveIniSettingsToMemory();
                EditorState.Save();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] Layout save failed: {ex.Message}");
            }
        }
    }
}
