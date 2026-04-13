using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ImGuiNET;
using IronRose.AssetPipeline;
using Tomlyn.Model;
using IronRose.Engine.Editor;
using RoseEngine;
using Vector2 = System.Numerics.Vector2;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiProjectPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private FolderNode? _root;
        private FolderNode? _selectedFolder;
        private string? _selectedAssetPath; // Primary (마지막 클릭) — Inspector/Drag 하위 호환
        private readonly HashSet<string> _selectedAssetPaths = new(); // 멀티셀렉션
        public string? SelectedAssetPath => _selectedAssetPath;
        public IReadOnlyCollection<string> SelectedAssetPaths => _selectedAssetPaths;

        // Drag-drop: 드래그 중인 에셋 경로 (ImGui payload 대신 static 필드로 전달)
        internal static string? _draggedAssetPath;

        private long _assetSelectionVersion;
        public long AssetSelectionVersion => _assetSelectionVersion;

        // Ping: 트리 노드를 자동으로 펼치기 위한 경로 집합
        private HashSet<string>? _pingOpenPaths;

        // Search
        private string _searchQuery = "";

        // Splitter
        private float _treeWidthRatio = 0.35f;
        private const float SplitterThickness = 4f;
        private const float MinRatio = 0.15f;
        private const float MaxRatio = 0.60f;

        // Create folder popup state
        private bool _openCreateFolderPopup;
        private string _newFolderName = "";
        private FolderNode? _createFolderTarget;

        // Create material popup state
        private bool _openCreateMaterialPopup;
        private string _newMaterialName = "";
        private FolderNode? _createMaterialTargetFolder;

        // Create animation clip popup state
        private bool _openCreateAnimClipPopup;
        private string _newAnimClipName = "";
        private FolderNode? _createAnimClipTargetFolder;

        // Create renderer profile popup state
        private bool _openCreateRendererProfilePopup;
        private string _newRendererProfileName = "";
        private FolderNode? _createRendererProfileTargetFolder;

        // Create post process profile popup state
        private bool _openCreatePPProfilePopup;
        private string _newPPProfileName = "";
        private FolderNode? _createPPProfileTargetFolder;

        // Rename folder popup state
        private bool _openRenameFolderPopup;
        private string _renameFolderName = "";
        private FolderNode? _renameFolderTarget;

        // Delete folder popup state
        private bool _openDeleteFolderPopup;
        private FolderNode? _deleteFolderTarget;

        // Keyboard navigation
        private readonly List<FolderNode> _visibleFolderNodes = new();
        private bool _needScrollFolder;
        private bool _needScrollAsset;
        private readonly HashSet<string> _openFolderPaths = new();
        private string? _toggleFolderPath;
        private readonly HashSet<string> _openAssetPaths = new();
        private string? _toggleAssetPath;

        // Flat ordered asset paths for Shift+Click range selection (rebuilt per frame)
        private readonly List<string> _flatAssetPaths = new();

        // Focus tracking
        private bool _isWindowFocused;
        public bool IsWindowFocused => _isWindowFocused;

        // Double-click to open scene / prefab / renderer profile
        private string? _pendingOpenScenePath;
        private string? _pendingOpenPrefabPath;
        private string? _pendingOpenAnimPath;
        private string? _pendingActivateRendererPath;

        // Deferred selection: mouse-down 시 경로만 저장, mouse-up 시 실제 선택 (드래그 우선)
        private string? _pendingSelectPath;
        private bool _pendingSelectCtrl;
        private bool _pendingSelectShift;

        // Delete asset confirmation
        private bool _openDeleteConfirmPopup;
        private string? _deleteTargetPath; // 단건 삭제 (하위 호환)
        private List<string>? _deleteTargetPaths; // 복수 삭제

        // Thumbnail generation request
        private string? _pendingThumbnailFolder;

        // AI image generation dialog + deferred-open trigger
        private readonly AiImageGenerateDialog _aiImageDialog = new();
        private string? _openAiImageDialogForFolder;

        // Rename asset popup state
        private bool _openRenameAssetPopup;
        private string _renameAssetName = "";
        private string? _renameAssetPath;

        // Sub-asset 타입별 색상

        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;

            // Drain background AI image generation results (UI thread only).
            AiImageGenerationService.DrainResults(result =>
            {
                if (result.Success && !string.IsNullOrEmpty(result.AbsoluteOutputPath))
                {
                    var db = Resources.GetAssetDatabase() as AssetDatabase;
                    db?.ReimportAsync(result.AbsoluteOutputPath);
                    AiImageHistory.RecordSuccess(
                        result.Request.StylePrompt,
                        result.Request.Prompt,
                        result.Request.Refine,
                        result.Request.Alpha);
                    RoseEngine.Debug.Log($"[AiImageGen] {result.Message}");
                }
                else
                {
                    RoseEngine.Debug.LogWarning($"[AiImageGen] {result.Message}");
                }
            });

            // Deferred open of AI image dialog (set by context menu)
            if (_openAiImageDialogForFolder != null)
            {
                _aiImageDialog.Open(_openAiImageDialogForFolder);
                _openAiImageDialogForFolder = null;
            }

            // Ping 요청 처리 (닫혀있으면 소비만 하고 무시)
            var pingPath = EditorBridge.ConsumePingAssetPath();
            if (pingPath != null && IsOpen)
                HandlePing(pingPath);

            if (!IsOpen) { _isWindowFocused = false; return; }

            var projectVisible = ImGui.Begin("Project", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Project");
            if (projectVisible)
            {
                _isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

                // Lazy build on first draw, or auto-refresh when assets change
                var db = Resources.GetAssetDatabase();
                if (_root == null || db?.ProjectDirty == true)
                {
                    RebuildTree();
                    if (db != null) db.ProjectDirty = false;
                }

                // Toolbar: Refresh + Search
                if (ImGui.Button("Refresh"))
                    RebuildTree();
                ImGui.SameLine();
                bool hasSearchQuery = _searchQuery.Length > 0;
                float searchAvail = ImGui.GetContentRegionAvail().X;
                if (hasSearchQuery)
                {
                    float clearBtnW = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;
                    searchAvail -= clearBtnW;
                }
                ImGui.SetNextItemWidth(searchAvail);
                ImGui.InputTextWithHint("##SearchAssets", "Search... (t:mesh, t:texture)", ref _searchQuery, 256);
                if (hasSearchQuery)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("X##SearchClear"))
                    {
                        _searchQuery = "";
                        if (_selectedAssetPath != null)
                            HandlePing(_selectedAssetPath);
                    }
                }

                ImGui.Separator();

                if (_root != null)
                {
                    if (!string.IsNullOrWhiteSpace(_searchQuery))
                    {
                        DrawSearchResults();
                    }
                    else
                    {
                    float availWidth = ImGui.GetContentRegionAvail().X;
                    float treeWidth = availWidth * _treeWidthRatio;
                    float availHeight = ImGui.GetContentRegionAvail().Y;

                    // Left: folder tree
                    ImGui.BeginChild("FolderTree", new Vector2(treeWidth, availHeight), ImGuiChildFlags.Border);
                    _visibleFolderNodes.Clear();
                    // Root "Assets" 노드를 기본적으로 펼쳐서 표시
                    if (!_openFolderPaths.Contains(_root.FullPath))
                        _openFolderPaths.Add(_root.FullPath);
                    ImGui.SetNextItemOpen(true, ImGuiCond.Once);
                    DrawFolderTree(_root);

                    // Keyboard navigation
                    if (ImGui.IsWindowFocused() && _visibleFolderNodes.Count > 0)
                    {
                        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                        {
                            int idx = _selectedFolder != null ? _visibleFolderNodes.IndexOf(_selectedFolder) : -1;
                            if (idx < _visibleFolderNodes.Count - 1)
                            {
                                _selectedFolder = _visibleFolderNodes[idx + 1];
                                _needScrollFolder = true;
                            }
                        }
                        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                        {
                            int idx = _selectedFolder != null ? _visibleFolderNodes.IndexOf(_selectedFolder) : _visibleFolderNodes.Count;
                            if (idx > 0)
                            {
                                _selectedFolder = _visibleFolderNodes[idx - 1];
                                _needScrollFolder = true;
                            }
                        }
                        else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
                        {
                            if (_selectedFolder != null && _selectedFolder.Children.Count > 0)
                            {
                                if (!_openFolderPaths.Contains(_selectedFolder.FullPath))
                                {
                                    // 접혀있으면 → 펼치기
                                    _openFolderPaths.Add(_selectedFolder.FullPath);
                                    _toggleFolderPath = _selectedFolder.FullPath;
                                }
                                else
                                {
                                    // 이미 펼쳐져 있으면 → 첫 번째 자식으로 이동
                                    var firstChild = _selectedFolder.Children.Values.OrderBy(c => c.Name).FirstOrDefault();
                                    if (firstChild != null)
                                    {
                                        _selectedFolder = firstChild;
                                        _needScrollFolder = true;
                                    }
                                }
                            }
                        }
                        else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
                        {
                            if (_selectedFolder != null)
                            {
                                if (_selectedFolder.Children.Count > 0 && _openFolderPaths.Contains(_selectedFolder.FullPath))
                                {
                                    // 펼쳐진 폴더 → 접기
                                    _openFolderPaths.Remove(_selectedFolder.FullPath);
                                    _toggleFolderPath = _selectedFolder.FullPath;
                                }
                                else if (_selectedFolder.Parent != null)
                                {
                                    // 접혀있거나 리프 → 부모로 이동
                                    _selectedFolder = _selectedFolder.Parent;
                                    _needScrollFolder = true;
                                }
                            }
                        }

                        // F2 → 선택된 폴더 이름 변경 (루트 제외)
                        if (ImGui.IsKeyPressed(ImGuiKey.F2) && _selectedFolder != null && _selectedFolder.Parent != null)
                        {
                            _renameFolderTarget = _selectedFolder;
                            _renameFolderName = _selectedFolder.Name;
                            _openRenameFolderPopup = true;
                        }

                        // Ctrl+V → 선택된 폴더에 에셋 붙여넣기
                        if (!ImGui.GetIO().WantTextInput && ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift
                            && ImGui.IsKeyPressed(ImGuiKey.V) && _selectedFolder != null)
                        {
                            if (EditorClipboard.ClipboardKind == EditorClipboard.Kind.Assets
                                && EditorClipboard.PasteAssets(_selectedFolder.FullPath))
                                RebuildTree();
                        }
                    }

                    // Context menu on empty space (right-click)
                    if (ImGui.BeginPopupContextWindow("##FolderTreeContext", ImGuiPopupFlags.NoOpenOverItems | ImGuiPopupFlags.MouseButtonRight))
                    {
                        if (ImGui.MenuItem("Create Folder"))
                        {
                            _createFolderTarget = _selectedFolder ?? _root;
                            _openCreateFolderPopup = true;
                            _newFolderName = "New Folder";
                        }
                        if (ImGui.MenuItem("Open Containing Folder"))
                        {
                            var target = _selectedFolder ?? _root;
                            if (target != null)
                                OpenInFileManager(Path.GetFullPath(target.FullPath));
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.EndChild();

                    ImGui.SameLine(0, 0);

                    // Splitter (드래그 가능한 영역)
                    ImGui.InvisibleButton("##Splitter", new Vector2(SplitterThickness, availHeight));
                    if (ImGui.IsItemActive())
                    {
                        float delta = ImGui.GetIO().MouseDelta.X;
                        if (delta != 0)
                        {
                            _treeWidthRatio += delta / availWidth;
                            _treeWidthRatio = Math.Clamp(_treeWidthRatio, MinRatio, MaxRatio);
                        }
                    }
                    if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

                    ImGui.SameLine(0, 0);

                    // Right: asset list
                    ImGui.BeginChild("AssetList", Vector2.Zero, ImGuiChildFlags.Border);
                    if (_selectedFolder != null)
                    {
                        var orderedAssets = _selectedFolder.Assets.OrderBy(a => a.FileName).ToList();

                        // Rebuild flat path list for Shift+Click
                        _flatAssetPaths.Clear();
                        foreach (var asset in orderedAssets)
                        {
                            _flatAssetPaths.Add(asset.FullPath);
                            if (asset.SubAssets.Count > 0)
                            {
                                foreach (var sub in asset.SubAssets)
                                    _flatAssetPaths.Add(SubAssetPath.Build(asset.FullPath, sub.Type, sub.Index));
                            }
                        }

                        if (orderedAssets.Count == 0)
                        {
                            ImGui.TextDisabled("Empty folder");
                        }
                        else
                        {
                            foreach (var asset in orderedAssets)
                            {
                                DrawAssetEntry(asset);
                            }
                        }

                        // Keyboard navigation (arrow keys → 단일 선택으로 이동)
                        if (ImGui.IsWindowFocused())
                        {
                            if (orderedAssets.Count > 0)
                            {
                                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                                {
                                    int idx = orderedAssets.FindIndex(a => a.FullPath == _selectedAssetPath);
                                    if (idx < orderedAssets.Count - 1)
                                    {
                                        SelectSingleAsset(orderedAssets[idx + 1].FullPath);
                                        _assetSelectionVersion++;
                                        _needScrollAsset = true;
                                    }
                                }
                                else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                                {
                                    int idx = orderedAssets.FindIndex(a => a.FullPath == _selectedAssetPath);
                                    if (idx > 0)
                                    {
                                        SelectSingleAsset(orderedAssets[idx - 1].FullPath);
                                        _assetSelectionVersion++;
                                        _needScrollAsset = true;
                                    }
                                }
                                else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
                                {
                                    var sel = orderedAssets.Find(a => a.FullPath == _selectedAssetPath);
                                    if (sel.FullPath != null && sel.SubAssets.Count > 0)
                                    {
                                        _openAssetPaths.Add(sel.FullPath);
                                        _toggleAssetPath = sel.FullPath;
                                    }
                                }
                                else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
                                {
                                    var sel = orderedAssets.Find(a => a.FullPath == _selectedAssetPath);
                                    if (sel.FullPath != null && sel.SubAssets.Count > 0)
                                    {
                                        _openAssetPaths.Remove(sel.FullPath);
                                        _toggleAssetPath = sel.FullPath;
                                    }
                                }
                            }

                            // Ctrl+A → 전체 선택
                            if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A) && orderedAssets.Count > 0)
                            {
                                _selectedAssetPaths.Clear();
                                foreach (var a in orderedAssets)
                                    _selectedAssetPaths.Add(a.FullPath);
                                _selectedAssetPath = orderedAssets[^1].FullPath;
                                _assetSelectionVersion++;
                            }

                            // Delete key
                            if (ImGui.IsKeyPressed(ImGuiKey.Delete) && HasSelectedAsset)
                            {
                                _deleteTargetPaths = _selectedAssetPaths
                                    .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                                    .ToList();
                                if (_deleteTargetPaths.Count > 0)
                                    _openDeleteConfirmPopup = true;
                            }

                            // F2 key → rename selected asset
                            if (ImGui.IsKeyPressed(ImGuiKey.F2) && _selectedAssetPath != null
                                && !SubAssetPath.IsSubAssetPath(_selectedAssetPath)
                                && File.Exists(_selectedAssetPath))
                            {
                                _renameAssetPath = _selectedAssetPath;
                                _renameAssetName = Path.GetFileNameWithoutExtension(_selectedAssetPath);
                                _openRenameAssetPopup = true;
                            }

                            // Ctrl+C / X / V — 에셋 복사·잘라내기·붙여넣기
                            if (!ImGui.GetIO().WantTextInput && ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift)
                            {
                                if (ImGui.IsKeyPressed(ImGuiKey.C) && HasSelectedAsset)
                                {
                                    var paths = _selectedAssetPaths
                                        .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                                        .ToList();
                                    EditorClipboard.CopyAssets(paths, cut: false);
                                }
                                else if (ImGui.IsKeyPressed(ImGuiKey.X) && HasSelectedAsset)
                                {
                                    var paths = _selectedAssetPaths
                                        .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                                        .ToList();
                                    EditorClipboard.CopyAssets(paths, cut: true);
                                }
                                else if (ImGui.IsKeyPressed(ImGuiKey.V) && _selectedFolder != null)
                                {
                                    if (EditorClipboard.PasteAssets(_selectedFolder.FullPath))
                                        RebuildTree();
                                }
                            }
                        }

                        // (Hierarchy GO drop target은 EndChild() 뒤에서 child window 전체를 커버)

                        // Context menu on empty space (right-click)
                        if (ImGui.BeginPopupContextWindow("##AssetListContext", ImGuiPopupFlags.NoOpenOverItems | ImGuiPopupFlags.MouseButtonRight))
                        {
                            if (ImGui.BeginMenu("Create"))
                            {
                                if (ImGui.MenuItem("Folder"))
                                {
                                    _createFolderTarget = _selectedFolder;
                                    _openCreateFolderPopup = true;
                                    _newFolderName = "New Folder";
                                }
                                if (ImGui.MenuItem("Material"))
                                {
                                    _createMaterialTargetFolder = _selectedFolder;
                                    _openCreateMaterialPopup = true;
                                    _newMaterialName = "New Material";
                                }
                                if (ImGui.MenuItem("Animation Clip"))
                                {
                                    _createAnimClipTargetFolder = _selectedFolder;
                                    _openCreateAnimClipPopup = true;
                                    _newAnimClipName = "New Animation";
                                }
                                if (ImGui.MenuItem("Renderer Profile"))
                                {
                                    _createRendererProfileTargetFolder = _selectedFolder;
                                    _openCreateRendererProfilePopup = true;
                                    _newRendererProfileName = "New Renderer";
                                }
                                if (ImGui.MenuItem("Post Process Profile"))
                                {
                                    _createPPProfileTargetFolder = _selectedFolder;
                                    _openCreatePPProfilePopup = true;
                                    _newPPProfileName = "New PP Profile";
                                }
                                ImGui.EndMenu();
                            }
                            // AI image generation (only when pref toggle is on and a folder is selected)
                            if (EditorPreferences.EnableAiAssetGeneration && _selectedFolder != null)
                            {
                                ImGui.Separator();
                                if (ImGui.MenuItem("Generate with AI (Texture)..."))
                                {
                                    _openAiImageDialogForFolder = Path.GetFullPath(_selectedFolder.FullPath);
                                }
                                ImGui.Separator();
                            }
                            if (ImGui.MenuItem("Create Thumbnails"))
                                _pendingThumbnailFolder = _selectedFolder!.FullPath;
                            var thumbPath1 = Path.Combine(Path.GetFullPath(_selectedFolder!.FullPath), ".thumbnails.png");
                            if (File.Exists(thumbPath1))
                            {
                                if (ImGui.MenuItem("Show Thumbnail"))
                                    Editor.ThumbnailGenerator.OpenWithOS(thumbPath1);
                            }
                            if (ImGui.MenuItem("Open Containing Folder"))
                            {
                                OpenInFileManager(Path.GetFullPath(_selectedFolder!.FullPath));
                            }
                            ImGui.EndPopup();
                        }
                    }
                    ImGui.EndChild();

                    // Drop target: Hierarchy GO → Prefab 저장 (AssetList child window 전체를 커버)
                    // EndChild() 뒤에 배치하면 child window 영역 전체가 드롭 타겟이 된다.
                    if (ImGui.BeginDragDropTarget())
                    {
                        unsafe
                        {
                            var goPayload = ImGui.AcceptDragDropPayload("HIERARCHY_GO");
                            if (goPayload.NativePtr != null && _selectedFolder != null)
                            {
                                SaveDraggedGOsAsPrefab(_selectedFolder.FullPath);
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }
                    } // end normal view

                    // Create Folder modal (must be outside child window)
                    {
                        var r = EditorModal.InputTextPopup("Create Folder##Modal", "Folder name:",
                            ref _openCreateFolderPopup, ref _newFolderName, "Create");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_newFolderName))
                            CreateFolder(_newFolderName.Trim());
                    }

                    // Create Material modal
                    {
                        var r = EditorModal.InputTextPopup("Create Material##Modal", "Material name:",
                            ref _openCreateMaterialPopup, ref _newMaterialName, "Create");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_newMaterialName))
                            CreateMaterialFile(_newMaterialName.Trim());
                        else if (r == EditorModal.Result.Cancelled)
                            _createMaterialTargetFolder = null;
                    }

                    // Create Animation Clip modal
                    {
                        var r = EditorModal.InputTextPopup("Create Animation Clip##Modal", "Clip name:",
                            ref _openCreateAnimClipPopup, ref _newAnimClipName, "Create");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_newAnimClipName))
                            CreateAnimationClipFile(_newAnimClipName.Trim());
                        else if (r == EditorModal.Result.Cancelled)
                            _createAnimClipTargetFolder = null;
                    }

                    // Create Renderer Profile modal
                    {
                        var r = EditorModal.InputTextPopup("Create Renderer Profile##Modal", "Profile name:",
                            ref _openCreateRendererProfilePopup, ref _newRendererProfileName, "Create");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_newRendererProfileName))
                            CreateRendererProfileFile(_newRendererProfileName.Trim());
                        else if (r == EditorModal.Result.Cancelled)
                            _createRendererProfileTargetFolder = null;
                    }

                    // Create Post Process Profile modal
                    {
                        var r = EditorModal.InputTextPopup("Create PP Profile##Modal", "Profile name:",
                            ref _openCreatePPProfilePopup, ref _newPPProfileName, "Create");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_newPPProfileName))
                            CreatePPProfileFile(_newPPProfileName.Trim());
                        else if (r == EditorModal.Result.Cancelled)
                            _createPPProfileTargetFolder = null;
                    }

                    // Delete Asset confirmation modal
                    if (_openDeleteConfirmPopup)
                    {
                        ImGui.OpenPopup("Delete Asset?##Modal");
                        _openDeleteConfirmPopup = false;
                    }
                    if (ImGui.BeginPopupModal("Delete Asset?##Modal", ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        if (_deleteTargetPaths != null && _deleteTargetPaths.Count > 1)
                        {
                            ImGui.Text($"Delete {_deleteTargetPaths.Count} assets?");
                        }
                        else
                        {
                            var path = _deleteTargetPaths?.Count > 0 ? _deleteTargetPaths[0] : _deleteTargetPath;
                            var fileName = path != null ? Path.GetFileName(path) : "";
                            ImGui.Text($"Delete \"{fileName}\"?");
                        }
                        ImGui.Text("This cannot be undone.");
                        ImGui.Spacing();

                        if (ImGui.Button("Yes"))
                        {
                            DeleteSelectedAssets();
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("No"))
                        {
                            _deleteTargetPath = null;
                            _deleteTargetPaths = null;
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.EndPopup();
                    }

                    // Rename Folder modal
                    {
                        var r = EditorModal.InputTextPopup("Rename Folder##Modal", "New name:",
                            ref _openRenameFolderPopup, ref _renameFolderName, "Rename");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_renameFolderName))
                            RenameFolder(_renameFolderTarget!, _renameFolderName.Trim());
                        else if (r == EditorModal.Result.Cancelled)
                            _renameFolderTarget = null;
                    }

                    // Delete Folder confirmation modal
                    if (_openDeleteFolderPopup)
                    {
                        ImGui.OpenPopup("Delete Folder?##Modal");
                        _openDeleteFolderPopup = false;
                    }
                    if (ImGui.BeginPopupModal("Delete Folder?##Modal", ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        var folderName = _deleteFolderTarget?.Name ?? "";
                        ImGui.Text($"Delete folder \"{folderName}\" and all its contents?");
                        ImGui.Text("This cannot be undone.");
                        ImGui.Spacing();

                        if (ImGui.Button("Yes##DeleteFolderYes"))
                        {
                            DeleteFolder(_deleteFolderTarget!);
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("No##DeleteFolderNo"))
                        {
                            _deleteFolderTarget = null;
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.EndPopup();
                    }

                    // Rename Asset modal
                    {
                        var ext = _renameAssetPath != null ? Path.GetExtension(_renameAssetPath) : "";
                        var r = EditorModal.InputTextPopup("Rename Asset##Modal", $"New name ({ext}):",
                            ref _openRenameAssetPopup, ref _renameAssetName, "Rename");
                        if (r == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_renameAssetName) && _renameAssetPath != null)
                            RenameAsset(_renameAssetPath, _renameAssetName.Trim());
                        else if (r == EditorModal.Result.Cancelled)
                            _renameAssetPath = null;
                    }

                    // Ping 처리가 완료되면 경로 집합 초기화
                    _pingOpenPaths = null;
                }
            }
            // Deferred selection: 드래그가 시작되면 선택을 취소하고, 마우스가 릴리즈되면 선택 확정
            if (_pendingSelectPath != null)
            {
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
                {
                    // 드래그 시작됨 → 선택 취소
                    _pendingSelectPath = null;
                }
                else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    // 마우스 릴리즈 + 드래그 없음 → 선택 확정
                    HandleAssetClick(_pendingSelectPath, _pendingSelectCtrl, _pendingSelectShift);
                    _pendingSelectPath = null;
                }
            }

            // AI image generation dialog (popups render independently of the panel window)
            _aiImageDialog.Draw();

            ImGui.End();
        }

        private void DrawAssetEntry(AssetEntry asset)
        {
            bool selected = _selectedAssetPaths.Contains(asset.FullPath);

            if (asset.SubAssets.Count > 0)
            {
                // Sub-asset이 있는 에셋: TreeNode로 표시
                var flags = ImGuiTreeNodeFlags.OpenOnArrow;
                if (selected)
                    flags |= ImGuiTreeNodeFlags.Selected;

                // 키보드로 열기/닫기
                if (_toggleAssetPath == asset.FullPath)
                {
                    ImGui.SetNextItemOpen(_openAssetPaths.Contains(asset.FullPath));
                    _toggleAssetPath = null;
                }

                bool opened = ImGui.TreeNodeEx($"{asset.FileName}##{asset.FullPath}", flags);

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    DeferAssetClick(asset.FullPath);

                // 더블클릭: .scene 파일 열기
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    HandleAssetDoubleClick(asset.FullPath);

                // 우클릭 컨텍스트 메뉴
                DrawAssetContextMenu(asset);

                // 마우스 클릭 시 open 상태 동기화
                if (opened)
                    _openAssetPaths.Add(asset.FullPath);
                else
                    _openAssetPaths.Remove(asset.FullPath);

                // Drag-drop source: 전체 에셋 드래그
                DrawDragSource(asset.FullPath, asset.FileName);

                if (selected && (_pingOpenPaths != null || _needScrollAsset))
                {
                    ImGui.SetScrollHereY();
                    _needScrollAsset = false;
                }

                if (opened)
                {
                    foreach (var sub in asset.SubAssets)
                    {
                        var subPath = SubAssetPath.Build(asset.FullPath, sub.Type, sub.Index);
                        bool subSelected = _selectedAssetPaths.Contains(subPath);

                        var subDisplayText = $"    {sub.Type}: {sub.Name}";
                        var subSelectableSize = new Vector2(ImGui.CalcTextSize(subDisplayText).X, 0);
                        if (ImGui.Selectable($"    {sub.Type}: {sub.Name}##{sub.Guid}", subSelected, ImGuiSelectableFlags.None, subSelectableSize))
                            DeferAssetClick(subPath);

                        // Drag-drop source: 서브에셋 드래그
                        DrawDragSource(subPath, $"{sub.Type}: {sub.Name}");

                        // Material sub-asset 컨텍스트 메뉴
                        if (sub.Type == "Material" && ImGui.BeginPopupContextItem($"##subctx_{sub.Guid}"))
                        {
                            if (ImGui.MenuItem("Duplicate as .mat"))
                            {
                                DuplicateSubAssetMaterial(asset.FullPath, subPath, sub.Name);
                            }
                            ImGui.EndPopup();
                        }

                        // Texture2D sub-asset 컨텍스트 메뉴
                        if (sub.Type == "Texture2D" && ImGui.BeginPopupContextItem($"##subctx_{sub.Guid}"))
                        {
                            if (ImGui.MenuItem("Duplicate as .png"))
                            {
                                DuplicateSubAssetTexture(asset.FullPath, subPath, sub.Name);
                            }
                            ImGui.EndPopup();
                        }

                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"GUID: {sub.Guid}");
                    }
                    ImGui.TreePop();
                }
            }
            else
            {
                // 일반 에셋: 기존 Selectable (이름 너비만큼만 선택 영역)
                var displayText = $"  {asset.FileName}";
                var selectableSize = new Vector2(ImGui.CalcTextSize(displayText).X, 0);
                if (ImGui.Selectable(displayText, selected, ImGuiSelectableFlags.None, selectableSize))
                    DeferAssetClick(asset.FullPath);

                // 더블클릭: .scene 파일 열기
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    HandleAssetDoubleClick(asset.FullPath);

                // 우클릭 컨텍스트 메뉴
                DrawAssetContextMenu(asset);

                // Drag-drop source
                DrawDragSource(asset.FullPath, asset.FileName);

                if (selected && (_pingOpenPaths != null || _needScrollAsset))
                {
                    ImGui.SetScrollHereY();
                    _needScrollAsset = false;
                }
            }
        }

        private void DrawAssetContextMenu(AssetEntry asset)
        {
            if (ImGui.BeginPopupContextItem($"##assetctx_{asset.FullPath}"))
            {
                var db = Resources.GetAssetDatabase() as AssetDatabase;
                bool canReimport = db != null && !db.IsReimporting;
                if (ImGui.MenuItem("Reimport", canReimport))
                {
                    db!.ReimportAsync(asset.FullPath);
                }
                // Texture 에셋: Displacement → Normal Map 변환
                var assetExt = Path.GetExtension(asset.FullPath).ToLowerInvariant();
                if (assetExt is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp")
                {
                    if (ImGui.MenuItem("Convert Displacement to Normal Map"))
                    {
                        ConvertDisplacementToNormalMap(asset.FullPath);
                    }
                }

                // Prefab: Create Variant
                if (assetExt == ".prefab")
                {
                    if (ImGui.MenuItem("Create Variant"))
                    {
                        var prefabGuid = db?.GetGuidFromPath(asset.FullPath);
                        if (!string.IsNullOrEmpty(prefabGuid))
                        {
                            var dir = Path.GetDirectoryName(asset.FullPath) ?? "Assets/Prefabs";
                            var baseName = Path.GetFileNameWithoutExtension(asset.FullPath);
                            var variantPath = Path.Combine(dir, $"{baseName}_Variant.prefab");
                            int counter = 1;
                            while (File.Exists(variantPath))
                            {
                                variantPath = Path.Combine(dir, $"{baseName}_Variant{counter}.prefab");
                                counter++;
                            }
                            PrefabUtility.CreateVariant(prefabGuid!, variantPath);
                            PrefabVariantTree.Instance.Rebuild();
                        }
                    }
                    ImGui.Separator();
                }

                if (ImGui.MenuItem("Rename"))
                {
                    _renameAssetPath = asset.FullPath;
                    _renameAssetName = Path.GetFileNameWithoutExtension(asset.FullPath);
                    _openRenameAssetPopup = true;
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Delete"))
                {
                    _deleteTargetPaths = _selectedAssetPaths
                        .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                        .ToList();
                    if (!_deleteTargetPaths.Contains(asset.FullPath) && File.Exists(asset.FullPath))
                        _deleteTargetPaths.Add(asset.FullPath);
                    if (_deleteTargetPaths.Count > 0)
                        _openDeleteConfirmPopup = true;
                }
                if (ImGui.MenuItem("Open Containing Folder"))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(asset.FullPath));
                    if (dir != null)
                        OpenInFileManager(dir);
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Properties"))
                {
                    ImGuiPropertyWindow.RequestOpenAsset(asset.FullPath);
                }
                ImGui.EndPopup();
            }
        }

        private void DeferAssetClick(string path)
        {
            _pendingSelectPath = path;
            var io = ImGui.GetIO();
            _pendingSelectCtrl = io.KeyCtrl;
            _pendingSelectShift = io.KeyShift;
        }

        private void HandleAssetClick(string path, bool ctrl = false, bool shift = false)
        {
            if (ctrl)
            {
                // Ctrl+Click: 토글
                if (_selectedAssetPaths.Contains(path))
                {
                    _selectedAssetPaths.Remove(path);
                    // Primary가 제거되면 다른 것으로 교체
                    if (_selectedAssetPath == path)
                        _selectedAssetPath = _selectedAssetPaths.Count > 0 ? _selectedAssetPaths.Last() : null;
                }
                else
                {
                    _selectedAssetPaths.Add(path);
                    _selectedAssetPath = path;
                }
            }
            else if (shift && _selectedAssetPath != null)
            {
                // Shift+Click: 범위 선택
                int anchorIdx = _flatAssetPaths.IndexOf(_selectedAssetPath);
                int targetIdx = _flatAssetPaths.IndexOf(path);
                if (anchorIdx >= 0 && targetIdx >= 0)
                {
                    int from = Math.Min(anchorIdx, targetIdx);
                    int to = Math.Max(anchorIdx, targetIdx);
                    _selectedAssetPaths.Clear();
                    for (int i = from; i <= to; i++)
                        _selectedAssetPaths.Add(_flatAssetPaths[i]);
                    _selectedAssetPath = path;
                }
                else
                {
                    SelectSingleAsset(path);
                }
            }
            else
            {
                // 일반 클릭: 단일 선택
                SelectSingleAsset(path);
            }
            _assetSelectionVersion++;
        }

        /// <summary>
        /// Hierarchy에서 GO를 드래그하여 Project 폴더에 드롭 시 프리팹으로 저장.
        /// 저장 후 원본 GO를 PrefabInstance로 전환.
        /// </summary>
        private void SaveDraggedGOsAsPrefab(string folderPath)
        {
            // 현재 선택된 GO를 프리팹으로 저장
            var selectedIds = EditorSelection.SelectedGameObjectIds;
            if (selectedIds.Count == 0) return;

            foreach (var goId in selectedIds)
            {
                var go = SceneManager.AllGameObjects
                    .FirstOrDefault(g => !g._isDestroyed && g.GetInstanceID() == goId);
                if (go == null) continue;

                // 이미 PrefabInstance면 스킵
                if (go.GetComponent<PrefabInstance>() != null) continue;

                var prefabPath = Path.Combine(folderPath, $"{go.name}.prefab");
                int counter = 1;
                while (File.Exists(prefabPath))
                {
                    prefabPath = Path.Combine(folderPath, $"{go.name} ({counter}).prefab");
                    counter++;
                }

                // 프리팹 저장 + GO를 PrefabInstance로 전환
                var guid = PrefabUtility.SaveAsPrefab(go, prefabPath);
                if (!string.IsNullOrEmpty(guid))
                {
                    var inst = go.GetComponent<PrefabInstance>();
                    if (inst == null)
                    {
                        inst = go.AddComponent<PrefabInstance>();
                        inst.prefabGuid = guid;
                    }
                }
            }

            SceneManager.GetActiveScene().isDirty = true;
            PrefabVariantTree.Instance.Rebuild();
            RebuildTree();
        }

        private void HandleAssetDoubleClick(string path)
        {
            if (SubAssetPath.IsSubAssetPath(path)) return;
            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".scene", StringComparison.OrdinalIgnoreCase))
                _pendingOpenScenePath = path;
            else if (string.Equals(ext, ".prefab", StringComparison.OrdinalIgnoreCase))
                _pendingOpenPrefabPath = path;
            else if (string.Equals(ext, ".anim", StringComparison.OrdinalIgnoreCase))
                _pendingOpenAnimPath = path;
            else if (string.Equals(ext, ".renderer", StringComparison.OrdinalIgnoreCase))
                _pendingActivateRendererPath = path;
        }

        private void SelectSingleAsset(string path)
        {
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(path);
            _selectedAssetPath = path;
        }

        /// <summary>더블클릭으로 열려고 하는 .scene 파일 경로를 소비한다.</summary>
        public string? ConsumePendingOpenScenePath()
        {
            var p = _pendingOpenScenePath;
            _pendingOpenScenePath = null;
            return p;
        }

        /// <summary>더블클릭으로 열려고 하는 .prefab 파일 경로를 소비한다.</summary>
        public string? ConsumePendingOpenPrefabPath()
        {
            var p = _pendingOpenPrefabPath;
            _pendingOpenPrefabPath = null;
            return p;
        }

        /// <summary>더블클릭으로 열려고 하는 .anim 파일 경로를 소비한다.</summary>
        public string? ConsumePendingOpenAnimPath()
        {
            var p = _pendingOpenAnimPath;
            _pendingOpenAnimPath = null;
            return p;
        }

        /// <summary>더블클릭으로 활성화하려는 .renderer 파일 경로를 소비한다.</summary>
        public string? ConsumePendingActivateRendererPath()
        {
            var p = _pendingActivateRendererPath;
            _pendingActivateRendererPath = null;
            return p;
        }

        /// <summary>컨텍스트 메뉴에서 요청된 썸네일 생성 대상 폴더를 소비한다.</summary>
        public string? ConsumePendingThumbnailFolder()
        {
            var p = _pendingThumbnailFolder;
            _pendingThumbnailFolder = null;
            return p;
        }

        private static void DrawDragSource(string fullPath, string displayName)
        {
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                _draggedAssetPath = fullPath;
                unsafe
                {
                    int dummy = 1;
                    ImGui.SetDragDropPayload("ASSET_PATH", (IntPtr)(&dummy), sizeof(int));
                }
                ImGui.Text(displayName);
                ImGui.EndDragDropSource();
            }
        }

        private void DrawFolderTree(FolderNode node)
        {
            _visibleFolderNodes.Add(node);

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (node.Children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (node == _selectedFolder)
                flags |= ImGuiTreeNodeFlags.Selected;

            // Ping 경로에 포함된 폴더는 자동으로 펼침
            bool forceOpen = _pingOpenPaths != null && _pingOpenPaths.Contains(node.FullPath);
            if (forceOpen)
                ImGui.SetNextItemOpen(true);

            // 키보드로 열기/닫기
            if (_toggleFolderPath == node.FullPath)
            {
                ImGui.SetNextItemOpen(_openFolderPaths.Contains(node.FullPath));
                _toggleFolderPath = null;
            }

            bool opened = ImGui.TreeNodeEx($"{node.Name}##{node.FullPath}", flags);

            // 마우스 클릭 시 open 상태 동기화
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                _selectedFolder = node;
            if (opened && node.Children.Count > 0)
                _openFolderPaths.Add(node.FullPath);
            else
                _openFolderPaths.Remove(node.FullPath);

            if (node == _selectedFolder && _needScrollFolder)
            {
                ImGui.SetScrollHereY();
                _needScrollFolder = false;
            }

            // Drop target: 에셋을 폴더로 드래그&드롭 이동 + Hierarchy GO → Prefab 저장
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                    if (payload.NativePtr != null && _draggedAssetPath != null)
                    {
                        MoveAssetsToFolder(_draggedAssetPath, node);
                        _draggedAssetPath = null;
                    }

                    var goPayload = ImGui.AcceptDragDropPayload("HIERARCHY_GO");
                    if (goPayload.NativePtr != null)
                    {
                        SaveDraggedGOsAsPrefab(node.FullPath);
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Folder context menu (right-click on folder node)
            if (ImGui.BeginPopupContextItem($"##folderctx_{node.FullPath}"))
            {
                if (ImGui.BeginMenu("Create"))
                {
                    if (ImGui.MenuItem("Folder"))
                    {
                        _createFolderTarget = node;
                        _openCreateFolderPopup = true;
                        _newFolderName = "New Folder";
                    }
                    if (ImGui.MenuItem("Material"))
                    {
                        _createMaterialTargetFolder = node;
                        _openCreateMaterialPopup = true;
                        _newMaterialName = "New Material";
                    }
                    if (ImGui.MenuItem("Animation Clip"))
                    {
                        _createAnimClipTargetFolder = node;
                        _openCreateAnimClipPopup = true;
                        _newAnimClipName = "New Animation";
                    }
                    if (ImGui.MenuItem("Renderer Profile"))
                    {
                        _createRendererProfileTargetFolder = node;
                        _openCreateRendererProfilePopup = true;
                        _newRendererProfileName = "New Renderer";
                    }
                    if (ImGui.MenuItem("Post Process Profile"))
                    {
                        _createPPProfileTargetFolder = node;
                        _openCreatePPProfilePopup = true;
                        _newPPProfileName = "New PP Profile";
                    }
                    ImGui.EndMenu();
                }
                if (ImGui.MenuItem("Rename Folder"))
                {
                    _renameFolderTarget = node;
                    _renameFolderName = node.Name;
                    _openRenameFolderPopup = true;
                }
                if (ImGui.MenuItem("Delete Folder"))
                {
                    _deleteFolderTarget = node;
                    _openDeleteFolderPopup = true;
                }
                ImGui.Separator();
                // AI image generation (only when pref toggle is on)
                if (EditorPreferences.EnableAiAssetGeneration)
                {
                    if (ImGui.MenuItem("Generate with AI (Texture)..."))
                    {
                        _openAiImageDialogForFolder = Path.GetFullPath(node.FullPath);
                    }
                    ImGui.Separator();
                }
                if (ImGui.MenuItem("Create Thumbnails"))
                    _pendingThumbnailFolder = node.FullPath;
                var thumbPath2 = Path.Combine(Path.GetFullPath(node.FullPath), ".thumbnails.png");
                if (File.Exists(thumbPath2))
                {
                    if (ImGui.MenuItem("Show Thumbnail"))
                        Editor.ThumbnailGenerator.OpenWithOS(thumbPath2);
                }
                if (ImGui.MenuItem("Open Containing Folder"))
                {
                    var parent = Path.GetDirectoryName(Path.GetFullPath(node.FullPath));
                    if (parent != null)
                        OpenInFileManager(parent);
                }
                ImGui.EndPopup();
            }

            if (node.Children.Count > 0 && opened)
            {
                foreach (var child in node.Children.Values.OrderBy(c => c.Name))
                    DrawFolderTree(child);
                ImGui.TreePop();
            }
        }

        private void CreateFolder(string folderName)
        {
            var target = _createFolderTarget ?? _selectedFolder;
            if (target == null) return;

            var absolutePath = Path.GetFullPath(Path.Combine(target.FullPath, folderName));
            if (!Directory.Exists(absolutePath))
                Directory.CreateDirectory(absolutePath);

            if (!target.Children.ContainsKey(folderName))
            {
                target.Children[folderName] = new FolderNode
                {
                    Name = folderName,
                    FullPath = target.FullPath + "/" + folderName,
                    Parent = target,
                };
            }

            _createFolderTarget = null;
        }

        private void CreateMaterialFile(string materialName)
        {
            var target = _createMaterialTargetFolder ?? _selectedFolder;
            if (target == null) return;

            var fileName = materialName + ".mat";
            var filePath = Path.Combine(target.FullPath, fileName);
            var absolutePath = Path.GetFullPath(filePath);

            // 동일 이름 파일이 있으면 번호 추가
            if (File.Exists(absolutePath))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(target.FullPath, $"{materialName} {counter}.mat"))))
                    counter++;
                fileName = $"{materialName} {counter}.mat";
                filePath = Path.Combine(target.FullPath, fileName);
                absolutePath = Path.GetFullPath(filePath);
            }

            MaterialImporter.WriteDefault(absolutePath);
            // .rose 메타데이터 자동 생성 (FileSystemWatcher가 감지하여 AssetDatabase에 등록)
            RoseMetadata.LoadOrCreate(absolutePath);

            // 생성된 파일 선택
            _selectedAssetPath = filePath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(filePath);
            _assetSelectionVersion++;

            _createMaterialTargetFolder = null;
        }

        private void CreateAnimationClipFile(string clipName)
        {
            var target = _createAnimClipTargetFolder ?? _selectedFolder;
            if (target == null) return;

            var fileName = clipName + ".anim";
            var filePath = Path.Combine(target.FullPath, fileName);
            var absolutePath = Path.GetFullPath(filePath);

            // 동일 이름 파일이 있으면 번호 추가
            if (File.Exists(absolutePath))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(target.FullPath, $"{clipName} {counter}.anim"))))
                    counter++;
                fileName = $"{clipName} {counter}.anim";
                filePath = Path.Combine(target.FullPath, fileName);
                absolutePath = Path.GetFullPath(filePath);
            }

            // 빈 AnimationClip 생성 후 TOML 파일로 내보내기
            var clip = new AnimationClip
            {
                name = clipName,
                frameRate = 60f,
                wrapMode = WrapMode.Once,
                length = 1f,
            };
            AnimationClipImporter.Export(clip, absolutePath);

            // .rose 메타데이터 자동 생성
            RoseMetadata.LoadOrCreate(absolutePath);

            // 생성된 파일 선택
            _selectedAssetPath = filePath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(filePath);
            _assetSelectionVersion++;

            _createAnimClipTargetFolder = null;
        }

        private void CreateRendererProfileFile(string profileName)
        {
            var target = _createRendererProfileTargetFolder ?? _selectedFolder;
            if (target == null) return;

            var fileName = profileName + ".renderer";
            var filePath = Path.Combine(target.FullPath, fileName);
            var absolutePath = Path.GetFullPath(filePath);

            // 동일 이름 파일이 있으면 번호 추가
            if (File.Exists(absolutePath))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(target.FullPath, $"{profileName} {counter}.renderer"))))
                    counter++;
                fileName = $"{profileName} {counter}.renderer";
                filePath = Path.Combine(target.FullPath, fileName);
                absolutePath = Path.GetFullPath(filePath);
            }

            RendererProfileImporter.WriteDefault(absolutePath);
            // .rose 메타데이터 자동 생성
            RoseMetadata.LoadOrCreate(absolutePath);

            // 생성된 파일 선택
            _selectedAssetPath = filePath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(filePath);
            _assetSelectionVersion++;

            _createRendererProfileTargetFolder = null;
        }

        private void CreatePPProfileFile(string profileName)
        {
            var target = _createPPProfileTargetFolder ?? _selectedFolder;
            if (target == null) return;

            var fileName = profileName + ".ppprofile";
            var filePath = Path.Combine(target.FullPath, fileName);
            var absolutePath = Path.GetFullPath(filePath);

            if (File.Exists(absolutePath))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(target.FullPath, $"{profileName} {counter}.ppprofile"))))
                    counter++;
                fileName = $"{profileName} {counter}.ppprofile";
                filePath = Path.Combine(target.FullPath, fileName);
                absolutePath = Path.GetFullPath(filePath);
            }

            PostProcessProfileImporter.WriteDefault(absolutePath);
            RoseMetadata.LoadOrCreate(absolutePath);

            _selectedAssetPath = filePath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(filePath);
            _assetSelectionVersion++;

            _createPPProfileTargetFolder = null;
        }

        private void DuplicateSubAssetMaterial(string parentFilePath, string subAssetPath, string subAssetName)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            // sub-asset Material 로드
            var mat = db.Load<Material>(subAssetPath);
            if (mat == null)
            {
                RoseEngine.EditorDebug.LogWarning($"[ProjectPanel] Failed to load sub-asset material: {subAssetPath}");
                return;
            }

            // 부모 파일과 같은 폴더에 .mat 파일 생성
            var dir = Path.GetDirectoryName(parentFilePath) ?? ".";
            var matName = subAssetName;
            var matPath = Path.Combine(dir, matName + ".mat");
            var absPath = Path.GetFullPath(matPath);

            // 동일 이름 파일이 있으면 번호 추가
            if (File.Exists(absPath))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(dir, $"{matName} {counter}.mat"))))
                    counter++;
                matName = $"{matName} {counter}";
                matPath = Path.Combine(dir, matName + ".mat");
                absPath = Path.GetFullPath(matPath);
            }

            // 텍스처 GUID 검색 (부모 파일의 sub-asset에서)
            string? mainTexGuid = null, normalMapGuid = null, mroMapGuid = null;
            var subAssets = db.GetSubAssets(parentFilePath);
            foreach (var sub in subAssets)
            {
                if (sub.type != "Texture2D") continue;
                var texPath = SubAssetPath.Build(parentFilePath, sub.type, sub.index);
                var tex = db.Load<Texture2D>(texPath);
                if (tex == null) continue;

                if (ReferenceEquals(tex, mat.mainTexture)) mainTexGuid = sub.guid;
                if (ReferenceEquals(tex, mat.normalMap)) normalMapGuid = sub.guid;
                if (ReferenceEquals(tex, mat.MROMap)) mroMapGuid = sub.guid;
            }

            MaterialImporter.WriteMaterial(absPath, mat, mainTexGuid, normalMapGuid, mroMapGuid);
            RoseMetadata.LoadOrCreate(absPath);

            // 생성된 파일 선택
            _selectedAssetPath = matPath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(matPath);
            _assetSelectionVersion++;
        }

        private void DuplicateSubAssetTexture(string parentFilePath, string subAssetPath, string subAssetName)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            // sub-asset Texture2D 로드
            var tex = db.Load<Texture2D>(subAssetPath);
            if (tex == null)
            {
                RoseEngine.EditorDebug.LogWarning($"[ProjectPanel] Failed to load sub-asset texture: {subAssetPath}");
                return;
            }

            // 부모 파일과 같은 폴더에 .png 파일 생성
            var dir = Path.GetDirectoryName(parentFilePath) ?? ".";
            var texName = subAssetName;
            var pngPath = Path.Combine(dir, texName + ".png");
            var absPath = Path.GetFullPath(pngPath);

            // 동일 이름 파일이 있으면 번호 추가
            if (File.Exists(absPath))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(dir, $"{texName} {counter}.png"))))
                    counter++;
                texName = $"{texName} {counter}";
                pngPath = Path.Combine(dir, texName + ".png");
                absPath = Path.GetFullPath(pngPath);
            }

            tex.DebugSaveToPng(absPath);
            RoseMetadata.LoadOrCreate(absPath);

            // 생성된 파일 선택
            _selectedAssetPath = pngPath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(pngPath);
            _assetSelectionVersion++;
        }

        private void ConvertDisplacementToNormalMap(string texturePath)
        {
            var absInput = Path.GetFullPath(texturePath);
            if (!File.Exists(absInput))
            {
                RoseEngine.EditorDebug.LogWarning($"[ProjectPanel] Texture not found: {texturePath}");
                return;
            }

            var dir = Path.GetDirectoryName(texturePath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(texturePath);
            var normalName = $"{baseName}_normal";
            var normalPath = Path.Combine(dir, normalName + ".png");
            var absOutput = Path.GetFullPath(normalPath);

            // 동일 이름 파일이 있으면 번호 추가
            if (File.Exists(absOutput))
            {
                int counter = 1;
                while (File.Exists(Path.GetFullPath(Path.Combine(dir, $"{normalName} {counter}.png"))))
                    counter++;
                normalName = $"{normalName} {counter}";
                normalPath = Path.Combine(dir, normalName + ".png");
                absOutput = Path.GetFullPath(normalPath);
            }

            TextureImporter.ConvertHeightToNormalMap(absInput, absOutput);
            var meta = RoseMetadata.LoadOrCreate(absOutput);
            meta.importer["srgb"] = false;
            meta.Save(absOutput + ".rose");

            // 생성된 파일 선택
            _selectedAssetPath = normalPath;
            _selectedAssetPaths.Clear();
            _selectedAssetPaths.Add(normalPath);
            _assetSelectionVersion++;
        }

        private void RenameFolder(FolderNode node, string newName)
        {
            if (node.Parent == null || node.Name == newName) return;

            var oldAbsPath = Path.GetFullPath(node.FullPath);
            var parentAbsPath = Path.GetDirectoryName(oldAbsPath)!;
            var newAbsPath = Path.Combine(parentAbsPath, newName);

            if (Directory.Exists(newAbsPath)) return; // 이미 존재하면 무시

            try
            {
                Directory.Move(oldAbsPath, newAbsPath);
            }
            catch { return; }

            // AssetDatabase 내부 경로 매핑 일괄 갱신
            var db = Resources.GetAssetDatabase() as AssetDatabase;
            db?.RenameFolderPaths(oldAbsPath, newAbsPath);

            // 트리 열림/선택 상태의 경로를 새 이름으로 갱신
            var oldRelPath = node.FullPath;
            var newRelPath = node.Parent.FullPath + "/" + newName;
            UpdateCachedPaths(_openFolderPaths, oldRelPath, newRelPath);

            // RebuildTree가 선택 폴더를 복원할 수 있도록 경로 갱신
            node.FullPath = newRelPath;

            RebuildTree();
            _renameFolderTarget = null;
        }

        /// <summary>
        /// HashSet의 경로들 중 oldPrefix로 시작하는 항목을 newPrefix로 갱신한다.
        /// </summary>
        private static void UpdateCachedPaths(HashSet<string> paths, string oldPrefix, string newPrefix)
        {
            var oldPrefixSlash = oldPrefix + "/";
            var toRemove = new List<string>();
            var toAdd = new List<string>();
            foreach (var p in paths)
            {
                if (p == oldPrefix)
                {
                    toRemove.Add(p);
                    toAdd.Add(newPrefix);
                }
                else if (p.StartsWith(oldPrefixSlash))
                {
                    toRemove.Add(p);
                    toAdd.Add(newPrefix + p.Substring(oldPrefix.Length));
                }
            }
            foreach (var r in toRemove) paths.Remove(r);
            foreach (var a in toAdd) paths.Add(a);
        }

        private void RenameAsset(string oldPath, string newName)
        {
            if (!File.Exists(oldPath)) return;

            var dir = Path.GetDirectoryName(oldPath)!;
            var ext = Path.GetExtension(oldPath);
            var newPath = Path.Combine(dir, newName + ext);

            if (oldPath == newPath) { _renameAssetPath = null; return; }
            if (File.Exists(newPath)) { _renameAssetPath = null; return; }

            var db = Resources.GetAssetDatabase() as AssetDatabase;
            if (db != null)
            {
                db.RenameAsset(oldPath, newPath);
            }
            else
            {
                File.Move(oldPath, newPath);
                var oldRose = oldPath + ".rose";
                var newRose = newPath + ".rose";
                if (File.Exists(oldRose))
                    File.Move(oldRose, newRose);
            }

            // 선택 상태 갱신
            _selectedAssetPaths.Remove(oldPath);
            _selectedAssetPaths.Add(newPath);
            if (_selectedAssetPath == oldPath)
                _selectedAssetPath = newPath;
            _assetSelectionVersion++;

            _renameAssetPath = null;
            RebuildTree();
        }

        private void MoveAssetsToFolder(string draggedPath, FolderNode targetFolder)
        {
            // 멀티셀렉션: 드래그된 에셋이 선택 목록에 포함되면 전체 이동
            var pathsToMove = _selectedAssetPaths.Contains(draggedPath)
                ? _selectedAssetPaths.ToList()
                : new List<string> { draggedPath };

            var db = Resources.GetAssetDatabase() as AssetDatabase;
            var movedNew = new List<string>();

            foreach (var oldPath in pathsToMove)
            {
                var fileName = Path.GetFileName(oldPath);
                var newPath = Path.Combine(targetFolder.FullPath, fileName);

                if (oldPath == newPath) continue;
                if (File.Exists(Path.GetFullPath(newPath))) continue;

                if (db != null)
                {
                    db.RenameAsset(oldPath, newPath);
                }
                else
                {
                    File.Move(oldPath, newPath);
                    var oldRose = oldPath + ".rose";
                    var newRose = newPath + ".rose";
                    if (File.Exists(oldRose))
                        File.Move(oldRose, newRose);
                }

                movedNew.Add(newPath);
            }

            if (movedNew.Count == 0) return;

            // 선택 상태를 이동된 경로로 갱신
            _selectedAssetPaths.Clear();
            foreach (var p in movedNew)
                _selectedAssetPaths.Add(p);
            _selectedAssetPath = movedNew[^1];
            _assetSelectionVersion++;

            _selectedFolder = targetFolder;
            RebuildTree();
        }

        private void DeleteFolder(FolderNode node)
        {
            if (node.Parent == null) return; // root 삭제 방지

            var absPath = Path.GetFullPath(node.FullPath);
            try
            {
                if (Directory.Exists(absPath))
                    Directory.Delete(absPath, true);
            }
            catch { return; }

            // 트리에서 제거
            node.Parent.Children.Remove(node.Name);

            // 선택 상태 정리
            if (_selectedFolder == node || IsDescendantOf(_selectedFolder, node))
                _selectedFolder = node.Parent;

            _selectedAssetPath = null;
            _selectedAssetPaths.Clear();
            _assetSelectionVersion++;
            _deleteFolderTarget = null;

            // FileSystemWatcher 이벤트 무시하고 직접 재빌드
            var db = Resources.GetAssetDatabase();
            if (db != null) db.ProjectDirty = false;
            RebuildTree();
        }

        private static bool IsDescendantOf(FolderNode? candidate, FolderNode ancestor)
        {
            var cur = candidate;
            while (cur != null)
            {
                if (cur == ancestor) return true;
                cur = cur.Parent;
            }
            return false;
        }

        private static void OpenInFileManager(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = false,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = false,
                });
            }
        }

        private static bool IsSubAssetMaterial(string path)
        {
            return SubAssetPath.TryParse(path, out _, out var subType, out _) && subType == "Material";
        }

        private static bool IsSubAssetTexture(string path)
        {
            return SubAssetPath.TryParse(path, out _, out var subType, out _) && subType == "Texture2D";
        }

        private static bool IsDuplicableSubAsset(string path)
        {
            return IsSubAssetMaterial(path) || IsSubAssetTexture(path);
        }

        public bool HasSelectedAsset =>
            _selectedAssetPaths.Count > 0
            && _selectedAssetPaths.Any(p =>
                (!SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                || IsDuplicableSubAsset(p));

        public void DuplicateSelectedAsset()
        {
            if (_selectedFolder == null) return;

            // Material sub-asset 복제 처리
            var matSubAssets = _selectedAssetPaths
                .Where(IsSubAssetMaterial)
                .ToList();
            foreach (var subPath in matSubAssets)
            {
                if (SubAssetPath.TryParse(subPath, out var parentPath, out _, out var subIndex))
                {
                    var db = Resources.GetAssetDatabase();
                    var subAssets = db?.GetSubAssets(parentPath);
                    var entry = subAssets?.FirstOrDefault(s => s.type == "Material" && s.index == subIndex);
                    if (entry != null)
                        DuplicateSubAssetMaterial(parentPath, subPath, entry.name);
                }
            }
            if (matSubAssets.Count > 0) return;

            // Texture2D sub-asset 복제 처리
            var texSubAssets = _selectedAssetPaths
                .Where(IsSubAssetTexture)
                .ToList();
            foreach (var subPath in texSubAssets)
            {
                if (SubAssetPath.TryParse(subPath, out var parentPath, out _, out var subIndex))
                {
                    var db = Resources.GetAssetDatabase();
                    var subAssets = db?.GetSubAssets(parentPath);
                    var entry = subAssets?.FirstOrDefault(s => s.type == "Texture2D" && s.index == subIndex);
                    if (entry != null)
                        DuplicateSubAssetTexture(parentPath, subPath, entry.name);
                }
            }
            if (texSubAssets.Count > 0) return;

            // 복제 가능한 에셋만 필터링 (서브에셋 제외, 실제 파일만)
            var targets = _selectedAssetPaths
                .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                .ToList();
            if (targets.Count == 0) return;

            var newPaths = new List<string>();

            foreach (var sourcePath in targets)
            {
                var dir = Path.GetDirectoryName(sourcePath)!;
                var ext = Path.GetExtension(sourcePath);
                var baseName = Path.GetFileNameWithoutExtension(sourcePath);

                // Generate numbered copy name: _01, _02, _03, ...
                string nameBase = baseName;
                var numSuffix = Regex.Match(baseName, @"^(.+)_(\d+)$");
                if (numSuffix.Success)
                    nameBase = numSuffix.Groups[1].Value;

                int maxNum = 0;
                var escapedExt = Regex.Escape(ext);
                var namePattern = new Regex(
                    $@"^{Regex.Escape(nameBase)}_(\d+){escapedExt}$");
                foreach (var file in Directory.GetFiles(dir))
                {
                    var m = namePattern.Match(Path.GetFileName(file));
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int num))
                        maxNum = Math.Max(maxNum, num);
                }
                string newPath = Path.Combine(dir, $"{nameBase}_{(maxNum + 1):D2}{ext}");

                // Load original metadata
                var originalMeta = RoseMetadata.LoadOrCreate(sourcePath);

                // Create new metadata with same import settings but new GUIDs
                var newMeta = new RoseMetadata();
                newMeta.version = originalMeta.version;
                newMeta.labels = originalMeta.labels?.ToArray();

                // Clone importer settings
                var newImporter = new TomlTable();
                foreach (var kvp in originalMeta.importer)
                    newImporter[kvp.Key] = kvp.Value;
                newMeta.importer = newImporter;

                // Clone sub-assets with new GUIDs
                foreach (var sub in originalMeta.subAssets)
                {
                    newMeta.subAssets.Add(new SubAssetEntry
                    {
                        name = sub.name,
                        type = sub.type,
                        index = sub.index,
                    });
                }

                // Save .rose first so FileSystemWatcher finds it when asset file appears
                newMeta.Save(newPath + ".rose");

                // Copy asset file
                File.Copy(sourcePath, newPath);

                // Add to current folder for immediate display
                var subDisplays = new List<SubAssetDisplay>();
                foreach (var sub in newMeta.subAssets)
                {
                    subDisplays.Add(new SubAssetDisplay
                    {
                        Name = sub.name,
                        Type = sub.type,
                        Index = sub.index,
                        Guid = sub.guid,
                    });
                }
                _selectedFolder.Assets.Add(new AssetEntry
                {
                    FileName = Path.GetFileName(newPath),
                    FullPath = newPath,
                    SubAssets = subDisplays,
                });

                newPaths.Add(newPath);
            }

            // Select the duplicated assets
            _selectedAssetPaths.Clear();
            foreach (var p in newPaths)
                _selectedAssetPaths.Add(p);
            _selectedAssetPath = newPaths.Count > 0 ? newPaths[^1] : null;
            _assetSelectionVersion++;
            _needScrollAsset = true;
        }

        private void DeleteSelectedAssets()
        {
            if (_selectedFolder == null) return;

            var paths = _deleteTargetPaths ?? (_deleteTargetPath != null ? new List<string> { _deleteTargetPath } : null);
            _deleteTargetPath = null;
            _deleteTargetPaths = null;
            if (paths == null || paths.Count == 0) return;

            foreach (var path in paths)
            {
                // Delete asset file
                if (File.Exists(path))
                    File.Delete(path);

                // Delete .rose sidecar
                var rosePath = path + ".rose";
                if (File.Exists(rosePath))
                    File.Delete(rosePath);

                // Remove from folder node
                _selectedFolder.Assets.RemoveAll(a => a.FullPath == path);
            }

            // Clear selection
            _selectedAssetPath = null;
            _selectedAssetPaths.Clear();
            _assetSelectionVersion++;
        }

        private void HandlePing(string assetPath)
        {
            if (_root == null)
                RebuildTree();
            if (_root == null) return;

            var normalized = assetPath.Replace('\\', '/');
            int assetsIdx = normalized.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIdx < 0) return;

            var relativePath = normalized.Substring(assetsIdx);
            var parts = relativePath.Split('/');

            _pingOpenPaths = new HashSet<string>();
            var current = _root;
            for (int i = 1; i < parts.Length - 1; i++)
            {
                var folderPath = string.Join("/", parts, 0, i + 1);
                _pingOpenPaths.Add(folderPath);

                if (current.Children.TryGetValue(parts[i], out var child))
                    current = child;
                else
                    return;
            }

            _selectedFolder = current;
            SelectSingleAsset(assetPath);
        }

        private void RebuildTree()
        {
            // 기존 선택 상태 보존
            var savedFolderPath = _selectedFolder?.FullPath;
            var savedAssetPaths = new HashSet<string>(_selectedAssetPaths);
            var savedPrimary = _selectedAssetPath;

            _root = new FolderNode { Name = "Assets", FullPath = ProjectContext.AssetsPath };
            _selectedFolder = null;
            _selectedAssetPath = null;
            _selectedAssetPaths.Clear();

            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            var allPaths = db.GetAllAssetPaths();

            foreach (var fullPath in allPaths)
            {
                var normalized = fullPath.Replace('\\', '/');

                int assetsIdx = normalized.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
                if (assetsIdx < 0) continue;

                var relativePath = normalized.Substring(assetsIdx);
                var parts = relativePath.Split('/');

                var current = _root;
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    if (!current.Children.TryGetValue(parts[i], out var child))
                    {
                        child = new FolderNode
                        {
                            Name = parts[i],
                            FullPath = Path.Combine(current.FullPath, parts[i]),
                            Parent = current,
                        };
                        current.Children[parts[i]] = child;
                    }
                    current = child;
                }

                // Sub-asset 정보 로드
                var subDisplays = new List<SubAssetDisplay>();
                var subAssets = db.GetSubAssets(fullPath);
                foreach (var sub in subAssets)
                {
                    subDisplays.Add(new SubAssetDisplay
                    {
                        Name = sub.name,
                        Type = sub.type,
                        Index = sub.index,
                        Guid = sub.guid,
                    });
                }

                current.Assets.Add(new AssetEntry
                {
                    FileName = parts[^1],
                    FullPath = fullPath,
                    SubAssets = subDisplays,
                });
            }

            // 파일시스템의 모든 하위 디렉토리를 스캔하여 빈 폴더도 트리에 포함
            var assetsAbsPath = Path.GetFullPath(_root.FullPath);
            if (Directory.Exists(assetsAbsPath))
                ScanDirectories(_root, assetsAbsPath);

            // 고아 .rose 파일 자동 삭제 (에셋 없이 .rose만 남은 경우)
            CleanOrphanRoseFiles(assetsAbsPath);

            // 이전 선택 상태 복원
            if (savedFolderPath != null)
                _selectedFolder = FindFolderByPath(_root, savedFolderPath);
            if (_selectedFolder == null && (_root.Children.Count > 0 || _root.Assets.Count > 0))
                _selectedFolder = _root;
            if (savedAssetPaths.Count > 0)
            {
                foreach (var p in savedAssetPaths)
                    _selectedAssetPaths.Add(p);
                _selectedAssetPath = savedPrimary;
            }
        }

        private static FolderNode? FindFolderByPath(FolderNode node, string fullPath)
        {
            if (node.FullPath == fullPath) return node;
            foreach (var child in node.Children.Values)
            {
                var found = FindFolderByPath(child, fullPath);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 파일시스템의 하위 디렉토리를 재귀적으로 스캔하여 에셋이 없는 빈 폴더도 트리에 추가한다.
        /// </summary>
        private const int MaxScanDepth = 32;

        private static void ScanDirectories(FolderNode node, string absolutePath, int depth = 0)
        {
            if (depth >= MaxScanDepth) return;

            try
            {
                foreach (var dir in Directory.GetDirectories(absolutePath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith('.')) continue;

                    // 심링크 순환 보호
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    if (!node.Children.ContainsKey(dirName))
                    {
                        node.Children[dirName] = new FolderNode
                        {
                            Name = dirName,
                            FullPath = node.FullPath + "/" + dirName,
                            Parent = node,
                        };
                    }
                    ScanDirectories(node.Children[dirName], dir, depth + 1);
                }
            }
            catch { /* skip inaccessible directories */ }
        }

        /// <summary>에셋 없이 .rose 사이드카만 남은 고아 파일을 재귀 삭제.</summary>
        private static void CleanOrphanRoseFiles(string directoryPath, int depth = 0)
        {
            if (depth >= MaxScanDepth || !Directory.Exists(directoryPath)) return;

            try
            {
                foreach (var rosePath in Directory.GetFiles(directoryPath, "*.rose"))
                {
                    // .rose 확장자를 제거하면 원본 에셋 경로
                    var assetPath = rosePath.Substring(0, rosePath.Length - 5); // ".rose" 제거
                    if (!File.Exists(assetPath))
                        File.Delete(rosePath);
                }

                foreach (var subDir in Directory.GetDirectories(directoryPath))
                {
                    var dirInfo = new DirectoryInfo(subDir);
                    if (dirInfo.Name.StartsWith('.') || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;
                    CleanOrphanRoseFiles(subDir, depth + 1);
                }
            }
            catch { /* skip inaccessible */ }
        }

        // ── Search ──────────────────────────────────────────────

        private void DrawSearchResults()
        {
            ParseSearchQuery(_searchQuery, out var textWords, out var typeFilters);

            var allAssets = new List<(AssetEntry asset, string folderPath)>();
            CollectAllAssets(_root!, allAssets);

            // Filter
            var filtered = new List<(AssetEntry asset, string folderPath)>();
            foreach (var (asset, folderPath) in allAssets)
            {
                // Text filter: all words must match (AND, case-insensitive, substring)
                if (textWords.Count > 0)
                {
                    bool allMatch = true;
                    foreach (var word in textWords)
                    {
                        bool found = asset.FileName.Contains(word, StringComparison.OrdinalIgnoreCase);
                        if (!found)
                        {
                            foreach (var sub in asset.SubAssets)
                            {
                                if (sub.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    break;
                                }
                            }
                        }
                        if (!found) { allMatch = false; break; }
                    }
                    if (!allMatch) continue;
                }

                // Type filter: any type must match (OR)
                if (typeFilters.Count > 0)
                {
                    bool anyMatch = false;
                    foreach (var tf in typeFilters)
                    {
                        if (MatchesTypeFilter(tf, asset))
                        {
                            anyMatch = true;
                            break;
                        }
                    }
                    if (!anyMatch) continue;
                }

                filtered.Add((asset, folderPath));
            }

            filtered.Sort((a, b) => string.Compare(a.asset.FileName, b.asset.FileName, StringComparison.OrdinalIgnoreCase));

            ImGui.TextDisabled($"{filtered.Count} result(s)");

            float availHeight = ImGui.GetContentRegionAvail().Y;
            ImGui.BeginChild("SearchResults", new Vector2(0, availHeight), ImGuiChildFlags.Border);

            if (filtered.Count == 0)
            {
                ImGui.TextDisabled("No results found");
            }
            else
            {
                // Rebuild flat paths for Shift+Click
                _flatAssetPaths.Clear();
                foreach (var (asset, _) in filtered)
                {
                    _flatAssetPaths.Add(asset.FullPath);
                    foreach (var sub in asset.SubAssets)
                        _flatAssetPaths.Add(SubAssetPath.Build(asset.FullPath, sub.Type, sub.Index));
                }

                foreach (var (asset, folderPath) in filtered)
                    DrawSearchResultEntry(asset, folderPath);

                // Keyboard: Ctrl+A → 전체 선택
                if (ImGui.IsWindowFocused() && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A) && filtered.Count > 0)
                {
                    _selectedAssetPaths.Clear();
                    foreach (var (asset, _) in filtered)
                        _selectedAssetPaths.Add(asset.FullPath);
                    _selectedAssetPath = filtered[^1].asset.FullPath;
                    _assetSelectionVersion++;
                }

                // Keyboard: Delete key
                if (ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.Delete) && HasSelectedAsset)
                {
                    _deleteTargetPaths = _selectedAssetPaths
                        .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                        .ToList();
                    if (_deleteTargetPaths.Count > 0)
                        _openDeleteConfirmPopup = true;
                }

                // Keyboard: F2 → rename
                if (ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.F2)
                    && _selectedAssetPath != null
                    && !SubAssetPath.IsSubAssetPath(_selectedAssetPath)
                    && File.Exists(_selectedAssetPath))
                {
                    _renameAssetPath = _selectedAssetPath;
                    _renameAssetName = Path.GetFileNameWithoutExtension(_selectedAssetPath);
                    _openRenameAssetPopup = true;
                }

                // Keyboard: Ctrl+C / X / V — 에셋 복사·잘라내기·붙여넣기
                if (ImGui.IsWindowFocused() && !ImGui.GetIO().WantTextInput
                    && ImGui.GetIO().KeyCtrl && !ImGui.GetIO().KeyShift)
                {
                    if (ImGui.IsKeyPressed(ImGuiKey.C) && HasSelectedAsset)
                    {
                        var paths = _selectedAssetPaths
                            .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                            .ToList();
                        EditorClipboard.CopyAssets(paths, cut: false);
                    }
                    else if (ImGui.IsKeyPressed(ImGuiKey.X) && HasSelectedAsset)
                    {
                        var paths = _selectedAssetPaths
                            .Where(p => !SubAssetPath.IsSubAssetPath(p) && File.Exists(p))
                            .ToList();
                        EditorClipboard.CopyAssets(paths, cut: true);
                    }
                    else if (ImGui.IsKeyPressed(ImGuiKey.V) && _selectedFolder != null)
                    {
                        if (EditorClipboard.PasteAssets(_selectedFolder.FullPath))
                            RebuildTree();
                    }
                }
            }

            ImGui.EndChild();
        }

        private void DrawSearchResultEntry(AssetEntry asset, string folderPath)
        {
            bool selected = _selectedAssetPaths.Contains(asset.FullPath);

            if (asset.SubAssets.Count > 0)
            {
                var flags = ImGuiTreeNodeFlags.OpenOnArrow
                          | ImGuiTreeNodeFlags.DefaultOpen;
                if (selected)
                    flags |= ImGuiTreeNodeFlags.Selected;

                bool opened = ImGui.TreeNodeEx($"{asset.FileName}##{asset.FullPath}", flags);

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    DeferAssetClick(asset.FullPath);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(folderPath);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        HandleAssetDoubleClick(asset.FullPath);
                }

                DrawDragSource(asset.FullPath, asset.FileName);

                if (opened)
                {
                    foreach (var sub in asset.SubAssets)
                    {
                        var subPath = SubAssetPath.Build(asset.FullPath, sub.Type, sub.Index);
                        bool subSelected = _selectedAssetPaths.Contains(subPath);

                        var subDisplayText = $"    {sub.Type}: {sub.Name}";
                        var subSelectableSize = new Vector2(ImGui.CalcTextSize(subDisplayText).X, 0);
                        if (ImGui.Selectable($"    {sub.Type}: {sub.Name}##{sub.Guid}", subSelected, ImGuiSelectableFlags.None, subSelectableSize))
                            DeferAssetClick(subPath);

                        DrawDragSource(subPath, $"{sub.Type}: {sub.Name}");

                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"GUID: {sub.Guid}");
                    }
                    ImGui.TreePop();
                }
            }
            else
            {
                var selectableSize = new Vector2(ImGui.CalcTextSize($"  {asset.FileName}").X, 0);
                if (ImGui.Selectable($"  {asset.FileName}##{asset.FullPath}", selected, ImGuiSelectableFlags.None, selectableSize))
                    DeferAssetClick(asset.FullPath);

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    HandleAssetDoubleClick(asset.FullPath);

                DrawDragSource(asset.FullPath, asset.FileName);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(folderPath);
            }
        }

        private static void ParseSearchQuery(string query, out List<string> textWords, out List<string> typeFilters)
        {
            textWords = new List<string>();
            typeFilters = new List<string>();

            foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase) && token.Length > 2)
                    typeFilters.Add(token.Substring(2).ToLowerInvariant());
                else
                    textWords.Add(token);
            }
        }

        private static void CollectAllAssets(FolderNode node, List<(AssetEntry asset, string folderPath)> results)
        {
            foreach (var asset in node.Assets)
                results.Add((asset, node.FullPath));

            foreach (var child in node.Children.Values)
                CollectAllAssets(child, results);
        }

        private static bool MatchesTypeFilter(string typeFilter, AssetEntry asset)
        {
            // Check sub-asset types first
            foreach (var sub in asset.SubAssets)
            {
                if (sub.Type.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check by file extension category
            var ext = Path.GetExtension(asset.FileName).ToLowerInvariant();
            return typeFilter switch
            {
                "mesh" => ext is ".glb" or ".gltf" or ".fbx" or ".obj",
                "texture" or "texture2d" => ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr",
                "material" => false, // materials are sub-assets only
                "prefab" => ext == ".prefab",
                "font" => ext is ".ttf" or ".otf",
                "scene" => ext == ".scene",
                _ => ext.TrimStart('.').Equals(typeFilter, StringComparison.OrdinalIgnoreCase),
            };
        }

        private class FolderNode
        {
            public string Name = "";
            public string FullPath = "";
            public FolderNode? Parent;
            public Dictionary<string, FolderNode> Children = new();
            public List<AssetEntry> Assets = new();
        }

        private struct AssetEntry
        {
            public string FileName;
            public string FullPath;
            public List<SubAssetDisplay> SubAssets;
        }

        private struct SubAssetDisplay
        {
            public string Name;
            public string Type;
            public int Index;
            public string Guid;
        }
    }
}
