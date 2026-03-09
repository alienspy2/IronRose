using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using IronRose.AssetPipeline;
using RoseEngine;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Project Settings 패널 — rose_projectSettings.toml 기반 프로젝트 전역 설정.
    /// 현재: 활성 렌더러 프로파일 선택.
    /// </summary>
    public class ImGuiProjectSettingsPanel : IEditorPanel
    {
        private bool _isOpen = false;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        // 프로파일 목록 캐시
        private readonly List<(string guid, string path, string name)> _profileList = new();
        private int _profileListFrame;
        private const int ProfileListRefreshInterval = 60;

        // Asset browser popup (Renderer)
        private bool _openAssetBrowser;
        private string _assetBrowserSearch = "";

        // Asset browser popup (Scene)
        private bool _openSceneBrowser;
        private string _sceneBrowserSearch = "";
        private readonly List<(string path, string name)> _sceneList = new();
        private int _sceneListFrame;

        public void Draw()
        {
            if (!IsOpen) return;

            if (ImGui.Begin("Project Settings", ref _isOpen))
            {
                if (ImGui.CollapsingHeader("Build", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawStartSceneSelector();
                }

                ImGui.Spacing();

                if (ImGui.CollapsingHeader("Editor", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawExternalScriptEditorSelector();
                }

                ImGui.Spacing();

                if (ImGui.CollapsingHeader("Renderer", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawRendererProfileSelector();
                }
            }
            ImGui.End();
        }

        private void DrawStartSceneSelector()
        {
            RefreshSceneListIfNeeded();

            var activePath = ProjectSettings.StartScenePath;
            string displayName = "(None)";
            if (!string.IsNullOrEmpty(activePath))
                displayName = Path.GetFileNameWithoutExtension(activePath);

            ImGui.Text("Start Scene");
            ImGui.SameLine();
            float availW = ImGui.GetContentRegionAvail().X;
            float selectableW = availW - 24f;

            // Object link
            if (!string.IsNullOrEmpty(activePath))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{displayName}##StartScene", false, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(selectableW, 0)))
                    EditorBridge.PingAsset(activePath);
                ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(activePath);
            }
            else
            {
                ImGui.Button($"{displayName}##StartScene", new System.Numerics.Vector2(selectableW, 0));
            }

            // Drag-drop target (.scene)
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                    if (payload.NativePtr != null)
                    {
                        var droppedPath = ImGuiProjectPanel._draggedAssetPath;
                        if (!string.IsNullOrEmpty(droppedPath) &&
                            droppedPath.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
                        {
                            ProjectSettings.StartScenePath = droppedPath;
                            ProjectSettings.Save();
                            Debug.Log($"[ProjectSettings] Start scene set: {droppedPath}");
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Browse button (◎)
            ImGui.SameLine();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
            if (ImGui.Button("\u25ce##StartScene_browse"))
            {
                _openSceneBrowser = true;
                _sceneBrowserSearch = "";
            }
            ImGui.PopStyleVar();

            DrawSceneBrowserPopup();
        }

        private void DrawSceneBrowserPopup()
        {
            if (_openSceneBrowser)
            {
                ImGui.OpenPopup("Select Start Scene##popup");
                _openSceneBrowser = false;
            }

            if (!ImGui.BeginPopup("Select Start Scene##popup")) return;

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##sceneSearch", "Search...", ref _sceneBrowserSearch, 256);
            ImGui.Separator();

            var currentPath = ProjectSettings.StartScenePath ?? "";
            string searchLower = _sceneBrowserSearch.ToLowerInvariant();

            // (None) option
            if (string.IsNullOrEmpty(_sceneBrowserSearch) || "none".Contains(searchLower))
            {
                if (ImGui.Selectable("(None)", string.IsNullOrEmpty(currentPath)))
                {
                    ProjectSettings.StartScenePath = null;
                    ProjectSettings.Save();
                    ImGui.CloseCurrentPopup();
                }
            }

            for (int i = 0; i < _sceneList.Count; i++)
            {
                var (path, name) = _sceneList[i];
                if (!string.IsNullOrEmpty(_sceneBrowserSearch) &&
                    !name.ToLowerInvariant().Contains(searchLower))
                    continue;

                if (ImGui.Selectable(name, path == currentPath))
                {
                    ProjectSettings.StartScenePath = path;
                    ProjectSettings.Save();
                    Debug.Log($"[ProjectSettings] Start scene set: {path}");
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(path);
            }

            ImGui.EndPopup();
        }

        private void RefreshSceneListIfNeeded()
        {
            _sceneListFrame++;
            if (_sceneListFrame < ProfileListRefreshInterval && _sceneList.Count > 0) return;
            _sceneListFrame = 0;

            _sceneList.Clear();
            var scenesDir = Path.Combine("Assets", "Scenes");
            if (!Directory.Exists(scenesDir)) return;

            foreach (var file in Directory.GetFiles(scenesDir, "*.scene", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(".", file).Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(file);
                _sceneList.Add((relPath, name));
            }
            _sceneList.Sort((a, b) =>
                string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        // ── Known editor presets ──
        private static readonly (string label, string command)[] EditorPresets =
        {
            ("Visual Studio Code", "code"),
            ("Rider", "rider"),
            ("Visual Studio", "devenv"),
            ("Vim", "vim"),
            ("Custom...", ""),
        };

        private bool _openCustomEditorPopup;
        private string _customEditorBuffer = "";

        private void DrawExternalScriptEditorSelector()
        {
            ImGui.Text("External Script Editor");

            string current = ProjectSettings.ExternalScriptEditor;
            string displayLabel = current;
            foreach (var (label, command) in EditorPresets)
            {
                if (command == current) { displayLabel = label; break; }
            }

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.BeginCombo("##ExternalEditor", displayLabel))
            {
                foreach (var (label, command) in EditorPresets)
                {
                    bool isSelected = (command == current);
                    if (string.IsNullOrEmpty(command))
                    {
                        // "Custom..." option
                        if (ImGui.Selectable(label, false))
                        {
                            _customEditorBuffer = current;
                            _openCustomEditorPopup = true;
                        }
                    }
                    else
                    {
                        if (ImGui.Selectable(label, isSelected))
                        {
                            ProjectSettings.ExternalScriptEditor = command;
                            ProjectSettings.Save();
                            Debug.Log($"[ProjectSettings] External script editor set: {command}");
                        }
                    }
                }
                ImGui.EndCombo();
            }

            // Custom editor input popup
            var result = EditorModal.InputTextPopup(
                "Custom Editor", "Editor command:", ref _openCustomEditorPopup, ref _customEditorBuffer, "OK");
            if (result == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_customEditorBuffer))
            {
                ProjectSettings.ExternalScriptEditor = _customEditorBuffer.Trim();
                ProjectSettings.Save();
                Debug.Log($"[ProjectSettings] External script editor set: {_customEditorBuffer.Trim()}");
            }
        }

        private void DrawRendererProfileSelector()
        {
            RefreshProfileListIfNeeded();

            var activeGuid = ProjectSettings.ActiveRendererProfileGuid ?? "";
            string? activePath = null;
            string displayName = "(None)";

            for (int i = 0; i < _profileList.Count; i++)
            {
                if (_profileList[i].guid == activeGuid)
                {
                    activePath = _profileList[i].path;
                    displayName = _profileList[i].name;
                    break;
                }
            }

            ImGui.Text("Active Renderer Profile");
            ImGui.SameLine();
            float availW = ImGui.GetContentRegionAvail().X;
            float selectableW = availW - 24f;

            // Object link — Inspector DrawPingableLabel 패턴
            if (activePath != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{displayName}##ActiveRendererProfile", false, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(selectableW, 0)))
                    EditorBridge.PingAsset(activePath);
                ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(activePath);
            }
            else
            {
                ImGui.Button($"{displayName}##ActiveRendererProfile", new System.Numerics.Vector2(selectableW, 0));
            }

            // Drag-drop target (.renderer)
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                    if (payload.NativePtr != null)
                    {
                        var droppedPath = ImGuiProjectPanel._draggedAssetPath;
                        if (!string.IsNullOrEmpty(droppedPath) &&
                            droppedPath.EndsWith(".renderer", StringComparison.OrdinalIgnoreCase))
                        {
                            var db = Resources.GetAssetDatabase();
                            var guid = db?.GetGuidFromPath(droppedPath);
                            if (guid != null)
                                ActivateProfile(guid, droppedPath);
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Browse button (◎)
            ImGui.SameLine();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
            if (ImGui.Button("\u25ce##RendererProfile_browse"))
            {
                _openAssetBrowser = true;
                _assetBrowserSearch = "";
            }
            ImGui.PopStyleVar();

            DrawRendererBrowserPopup();
        }

        private void DrawRendererBrowserPopup()
        {
            if (_openAssetBrowser)
            {
                ImGui.OpenPopup("Select Renderer Profile##popup");
                _openAssetBrowser = false;
            }

            if (!ImGui.BeginPopup("Select Renderer Profile##popup")) return;

            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##rpSearch", "Search...", ref _assetBrowserSearch, 256);
            ImGui.Separator();

            var activeGuid = ProjectSettings.ActiveRendererProfileGuid ?? "";
            string searchLower = _assetBrowserSearch.ToLowerInvariant();

            // (None) option
            if (string.IsNullOrEmpty(_assetBrowserSearch) || "none".Contains(searchLower))
            {
                if (ImGui.Selectable("(None)", string.IsNullOrEmpty(activeGuid)))
                {
                    ProjectSettings.ActiveRendererProfileGuid = null;
                    ProjectSettings.Save();
                    if (!EditorPlayMode.IsInPlaySession)
                    {
                        RenderSettings.activeRendererProfile = null;
                        RenderSettings.activeRendererProfileGuid = null;
                    }
                    ImGui.CloseCurrentPopup();
                }
            }

            for (int i = 0; i < _profileList.Count; i++)
            {
                var (guid, path, name) = _profileList[i];
                if (!string.IsNullOrEmpty(_assetBrowserSearch) &&
                    !name.ToLowerInvariant().Contains(searchLower))
                    continue;

                if (ImGui.Selectable(name, guid == activeGuid))
                {
                    ActivateProfile(guid, path);
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(path);
            }

            ImGui.EndPopup();
        }

        private void RefreshProfileListIfNeeded()
        {
            _profileListFrame++;
            if (_profileListFrame < ProfileListRefreshInterval && _profileList.Count > 0) return;
            _profileListFrame = 0;

            _profileList.Clear();
            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            foreach (var path in db.GetAllAssetPaths())
            {
                if (!path.EndsWith(".renderer", StringComparison.OrdinalIgnoreCase)) continue;
                var guid = db.GetGuidFromPath(path);
                if (guid == null) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                _profileList.Add((guid, path, name));
            }
            _profileList.Sort((a, b) =>
                string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>프로파일을 활성화. Edit mode에서는 즉시 반영, Play mode에서는 설정만 저장.</summary>
        public void ActivateProfile(string guid, string path)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            var profile = db.Load<RendererProfile>(path);
            if (profile == null)
            {
                Debug.LogWarning($"[ProjectSettings] Failed to load profile: {path}");
                return;
            }

            // ProjectSettings에 저장 (항상)
            ProjectSettings.ActiveRendererProfileGuid = guid;
            ProjectSettings.Save();

            if (!EditorPlayMode.IsInPlaySession)
            {
                // Edit mode: 즉시 반영
                RenderSettings.activeRendererProfile = profile;
                RenderSettings.activeRendererProfileGuid = guid;
                profile.ApplyToRenderSettings();
                Debug.Log($"[ProjectSettings] Activated renderer profile: {profile.name} ({guid})");
            }
            else
            {
                Debug.Log($"[ProjectSettings] Renderer profile saved (will apply after play mode): {profile.name}");
            }
        }
    }
}
