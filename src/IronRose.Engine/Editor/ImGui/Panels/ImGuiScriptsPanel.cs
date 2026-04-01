// ------------------------------------------------------------
// @file    ImGuiScriptsPanel.cs
// @brief   Scripts View 패널. Scripts 폴더의 .cs 파일을 트리로 표시하고
//          생성/삭제/이름변경/복제/외부 에디터 열기 등의 파일 관리 기능 제공.
// @deps    IronRose.Engine/ProjectContext, IronRose.Engine/ProjectSettings,
//          IronRose.Engine.Editor.ImGuiEditor.Panels/EditorModal, RoseEngine/Debug, RoseEngine/Component
// @exports
//   class ImGuiScriptsPanel : IEditorPanel
//     IsOpen: bool                           — 패널 열림/닫힘 상태
//     SelectedScriptPath: string?            — 현재 선택된 스크립트 절대 경로
//     Draw(): void                           — ImGui 패널 렌더링
//     ResolveComponentType(string): Type?    — .cs 경로에서 Component 타입 검색 (internal)
//     _draggedScriptPath: string?            — 드래그 중인 스크립트 경로 (internal static)
//     DragPayloadType: string                — 드래그 페이로드 타입 식별자 (internal const)
// @note    Draw() 첫 호출 시 lazy 초기화 (ProjectContext.IsProjectLoaded 이후).
//          FindRootDirectories()는 ProjectContext.ScriptsPath 기반.
//          FileSystemWatcher로 .cs 파일 변경 감지하여 트리 자동 갱신.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ImGuiNET;
using RoseEngine;
using Debug = RoseEngine.EditorDebug;
using Vector2 = System.Numerics.Vector2;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Scripts View 패널 — Scripts 폴더의 .cs 파일을 트리로 표시하고 관리.
    /// </summary>
    public class ImGuiScriptsPanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        /// <summary>현재 선택된 스크립트의 절대 경로.</summary>
        public string? SelectedScriptPath => _selectedPath;

        /// <summary>드래그 중인 스크립트 파일 경로 (다른 패널에서 참조).</summary>
        internal static string? _draggedScriptPath;
        internal const string DragPayloadType = "SCRIPT_PATH";

        // ── Root directories ──
        private string? _scriptsRoot;

        // ── Tree state ──
        private ScriptFolderNode? _scriptsTree;
        private bool _needsRebuild = true;
        private bool _initialized;
        private readonly HashSet<string> _openFolders = new();
        private string _selectedPath = "";
        private string _searchFilter = "";

        // ── FileSystemWatchers ──
        private FileSystemWatcher? _scriptsWatcher;

        // ── Context menu state ──
        private bool _openCreateScriptPopup;
        private bool _openCreateFolderPopup;
        private bool _openRenamePopup;
        private bool _openDeleteConfirmPopup;
        private string _createBuffer = "";
        private string _renameBuffer = "";
        private string _contextTargetFolder = "";
        private string _contextTargetFile = "";

        // ── Excluded directories ──
        private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj"
        };

        public ImGuiScriptsPanel()
        {
        }

        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;

            if (!_initialized)
            {
                _initialized = true;
                FindRootDirectories();
                SetupWatchers();
                _needsRebuild = true;
            }

            if (!IsOpen) return;

            if (_needsRebuild)
            {
                RebuildTree();
                _needsRebuild = false;
            }

            var scriptsVisible = ImGui.Begin("Scripts", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Scripts");
            if (scriptsVisible)
            {
                // ── Toolbar ──
                DrawToolbar();

                ImGui.Separator();

                // ── Tree view ──
                if (ImGui.BeginChild("ScriptsTree", Vector2.Zero, ImGuiChildFlags.None,
                    ImGuiWindowFlags.HorizontalScrollbar))
                {
                    if (_scriptsTree != null)
                        DrawFolderNode(_scriptsTree, isRoot: true);

                    // Empty area context menu
                    if (ImGui.BeginPopupContextWindow("##ScriptsEmptyCtx",
                        ImGuiPopupFlags.NoOpenOverItems | ImGuiPopupFlags.MouseButtonRight))
                    {
                        if (ImGui.MenuItem("Refresh"))
                            _needsRebuild = true;
                        ImGui.EndPopup();
                    }
                }
                ImGui.EndChild();
            }
            ImGui.End();

            // ── Modal popups (drawn outside window) ──
            DrawModals();
        }

        // =====================================================================
        // Toolbar
        // =====================================================================

        private void DrawToolbar()
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##ScriptsSearch", "Search scripts...", ref _searchFilter, 256);
        }

        // =====================================================================
        // Tree rendering
        // =====================================================================

        private void DrawFolderNode(ScriptFolderNode node, bool isRoot)
        {
            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isRoot)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;

            bool hasVisibleChildren = HasVisibleChildren(node);
            if (!hasVisibleChildren && !isRoot)
                return; // hide empty folders when filtering

            // Restore open state
            if (_openFolders.Contains(node.FullPath) || isRoot)
                ImGui.SetNextItemOpen(true, ImGuiCond.Once);

            bool open = ImGui.TreeNodeEx($"{node.Name}##{node.FullPath}", flags);

            // Track open/close state
            if (open)
                _openFolders.Add(node.FullPath);
            else
                _openFolders.Remove(node.FullPath);

            // Folder context menu
            if (ImGui.BeginPopupContextItem($"##folderctx_{node.FullPath}"))
            {
                if (ImGui.MenuItem("Create Script"))
                {
                    _contextTargetFolder = node.FullPath;
                    _createBuffer = "NewScript";
                    _openCreateScriptPopup = true;
                }
                if (ImGui.MenuItem("Create Folder"))
                {
                    _contextTargetFolder = node.FullPath;
                    _createBuffer = "NewFolder";
                    _openCreateFolderPopup = true;
                }
                ImGui.EndPopup();
            }

            if (open)
            {
                // Sub-folders first
                foreach (var sub in node.SubFolders)
                    DrawFolderNode(sub, isRoot: false);

                // Script files
                foreach (var script in node.Scripts)
                {
                    if (!MatchesFilter(script.FileName))
                        continue;

                    var scriptFlags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen
                                      | ImGuiTreeNodeFlags.SpanAvailWidth;
                    if (script.FullPath == _selectedPath)
                        scriptFlags |= ImGuiTreeNodeFlags.Selected;

                    ImGui.TreeNodeEx($"{script.FileName}##{script.FullPath}", scriptFlags);

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                        _selectedPath = script.FullPath;

                    // Double-click to open in external editor
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        OpenInExternalEditor(script.FullPath);

                    // Drag source for adding components via drag-and-drop
                    if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
                    {
                        _draggedScriptPath = script.FullPath;
                        unsafe
                        {
                            int dummy = 1;
                            ImGui.SetDragDropPayload(DragPayloadType, (IntPtr)(&dummy), sizeof(int));
                        }
                        ImGui.Text(script.FileName);
                        ImGui.EndDragDropSource();
                    }

                    // File context menu
                    if (ImGui.BeginPopupContextItem($"##scriptctx_{script.FullPath}"))
                    {
                        if (ImGui.MenuItem("Open in Editor"))
                        {
                            OpenInExternalEditor(script.FullPath);
                        }
                        ImGui.Separator();
                        if (ImGui.MenuItem("Duplicate"))
                        {
                            DuplicateScript(script.FullPath);
                        }
                        if (ImGui.MenuItem("Rename"))
                        {
                            _contextTargetFile = script.FullPath;
                            _renameBuffer = Path.GetFileNameWithoutExtension(script.FileName);
                            _openRenamePopup = true;
                        }
                        if (ImGui.MenuItem("Delete"))
                        {
                            _contextTargetFile = script.FullPath;
                            _openDeleteConfirmPopup = true;
                        }
                        ImGui.Separator();
                        if (ImGui.MenuItem("Open Containing Folder"))
                        {
                            OpenContainingFolder(Path.GetDirectoryName(script.FullPath) ?? "");
                        }
                        ImGui.EndPopup();
                    }
                }

                ImGui.TreePop();
            }
        }

        private bool HasVisibleChildren(ScriptFolderNode node)
        {
            if (string.IsNullOrEmpty(_searchFilter))
                return true;

            foreach (var s in node.Scripts)
            {
                if (MatchesFilter(s.FileName))
                    return true;
            }
            foreach (var sub in node.SubFolders)
            {
                if (HasVisibleChildren(sub))
                    return true;
            }
            return false;
        }

        private bool MatchesFilter(string fileName)
        {
            if (string.IsNullOrEmpty(_searchFilter))
                return true;
            return fileName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // Modal popups
        // =====================================================================

        private void DrawModals()
        {
            // ── Create Script ──
            var createResult = EditorModal.InputTextPopup(
                "Create Script", "Script name:", ref _openCreateScriptPopup, ref _createBuffer, "Create");
            if (createResult == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_createBuffer))
            {
                CreateScript(_contextTargetFolder, _createBuffer.Trim());
            }

            // ── Create Folder ──
            var folderResult = EditorModal.InputTextPopup(
                "Create Folder", "Folder name:", ref _openCreateFolderPopup, ref _createBuffer, "Create");
            if (folderResult == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_createBuffer))
            {
                CreateFolder(_contextTargetFolder, _createBuffer.Trim());
            }

            // ── Rename ──
            var renameResult = EditorModal.InputTextPopup(
                "Rename Script", "New name:", ref _openRenamePopup, ref _renameBuffer, "Rename");
            if (renameResult == EditorModal.Result.Confirmed && !string.IsNullOrWhiteSpace(_renameBuffer))
            {
                RenameScript(_contextTargetFile, _renameBuffer.Trim());
            }

            // ── Delete confirmation ──
            DrawDeleteConfirmModal();
        }

        private void DrawDeleteConfirmModal()
        {
            if (_openDeleteConfirmPopup)
            {
                ImGui.OpenPopup("Delete Script?");
                _openDeleteConfirmPopup = false;
            }
            if (!ImGui.BeginPopupModal("Delete Script?", ImGuiWindowFlags.AlwaysAutoResize))
                return;

            string fileName = Path.GetFileName(_contextTargetFile);
            ImGui.Text($"Delete \"{fileName}\"?");
            ImGui.Text("This cannot be undone.");

            if (ImGui.Button("Delete"))
            {
                DeleteScript(_contextTargetFile);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        // =====================================================================
        // File operations
        // =====================================================================

        private void CreateScript(string folderPath, string name)
        {
            // Sanitize: remove .cs if user typed it
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                name = name[..^3];

            // Validate C# identifier
            if (!IsValidIdentifier(name))
            {
                Debug.LogWarning($"[Scripts] Invalid script name: {name}");
                return;
            }

            string filePath = Path.Combine(folderPath, name + ".cs");
            if (File.Exists(filePath))
            {
                Debug.LogWarning($"[Scripts] File already exists: {filePath}");
                return;
            }

            string template = GenerateMonoBehaviourTemplate(name);
            File.WriteAllText(filePath, template, new System.Text.UTF8Encoding(true));
            Debug.Log($"[Scripts] Created: {filePath}");

            _needsRebuild = true;
            _selectedPath = filePath;
        }

        private void CreateFolder(string parentPath, string name)
        {
            string dirPath = Path.Combine(parentPath, name);
            if (Directory.Exists(dirPath))
            {
                Debug.LogWarning($"[Scripts] Folder already exists: {dirPath}");
                return;
            }

            Directory.CreateDirectory(dirPath);
            Debug.Log($"[Scripts] Created folder: {dirPath}");
            _needsRebuild = true;
        }

        private void DuplicateScript(string sourcePath)
        {
            if (!File.Exists(sourcePath)) return;

            string dir = Path.GetDirectoryName(sourcePath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            string originalClassName = baseName;

            // Generate unique copy name
            string newName = GenerateUniqueCopyName(dir, baseName);
            string newPath = Path.Combine(dir, newName + ".cs");

            // Read and replace class name
            string content = File.ReadAllText(sourcePath);
            content = content.Replace(originalClassName, newName);

            File.WriteAllText(newPath, content, new System.Text.UTF8Encoding(true));
            Debug.Log($"[Scripts] Duplicated: {newPath}");

            _needsRebuild = true;
            _selectedPath = newPath;
        }

        private void RenameScript(string filePath, string newName)
        {
            if (!File.Exists(filePath)) return;

            if (newName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                newName = newName[..^3];

            if (!IsValidIdentifier(newName))
            {
                Debug.LogWarning($"[Scripts] Invalid name: {newName}");
                return;
            }

            string dir = Path.GetDirectoryName(filePath) ?? "";
            string oldName = Path.GetFileNameWithoutExtension(filePath);
            string newPath = Path.Combine(dir, newName + ".cs");

            if (File.Exists(newPath))
            {
                Debug.LogWarning($"[Scripts] File already exists: {newPath}");
                return;
            }

            // Replace class name in content
            string content = File.ReadAllText(filePath);
            content = content.Replace(oldName, newName);
            File.WriteAllText(newPath, content, new System.Text.UTF8Encoding(true));
            File.Delete(filePath);

            Debug.Log($"[Scripts] Renamed: {oldName} → {newName}");
            _needsRebuild = true;
            _selectedPath = newPath;
        }

        private void DeleteScript(string filePath)
        {
            if (!File.Exists(filePath)) return;

            File.Delete(filePath);
            Debug.Log($"[Scripts] Deleted: {filePath}");

            if (_selectedPath == filePath)
                _selectedPath = "";

            _needsRebuild = true;
        }

        // =====================================================================
        // Tree building
        // =====================================================================

        private void RebuildTree()
        {
            if (_scriptsRoot != null && Directory.Exists(_scriptsRoot))
                _scriptsTree = BuildFolderNode(_scriptsRoot, "Scripts");
            else
                _scriptsTree = null;
        }

        private static ScriptFolderNode BuildFolderNode(string fullPath, string displayName)
        {
            var node = new ScriptFolderNode
            {
                Name = displayName,
                FullPath = fullPath,
            };

            // Sub-directories
            try
            {
                foreach (var dir in Directory.GetDirectories(fullPath).OrderBy(d => d))
                {
                    string dirName = Path.GetFileName(dir);
                    if (ExcludedDirs.Contains(dirName))
                        continue;

                    var child = BuildFolderNode(dir, dirName);
                    child.Parent = node;
                    node.SubFolders.Add(child);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Scripts] Error scanning dirs in {fullPath}: {ex.Message}");
            }

            // .cs files
            try
            {
                foreach (var file in Directory.GetFiles(fullPath, "*.cs").OrderBy(f => f))
                {
                    node.Scripts.Add(new ScriptFileEntry
                    {
                        FileName = Path.GetFileName(file),
                        FullPath = Path.GetFullPath(file),
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Scripts] Error scanning files in {fullPath}: {ex.Message}");
            }

            return node;
        }

        // =====================================================================
        // Directory discovery
        // =====================================================================

        private void FindRootDirectories()
        {
            var scriptsDir = ProjectContext.ScriptsPath;

            if (Directory.Exists(scriptsDir))
                _scriptsRoot = scriptsDir;

            // Fallback: Scripts 디렉토리가 없으면 생성
            if (_scriptsRoot == null)
            {
                _scriptsRoot = scriptsDir;
                Directory.CreateDirectory(_scriptsRoot);
            }

            Debug.Log($"[Scripts] Scripts root: {_scriptsRoot}");
        }

        // =====================================================================
        // FileSystemWatchers
        // =====================================================================

        private void SetupWatchers()
        {
            if (_scriptsRoot != null)
                _scriptsWatcher = CreateWatcher(_scriptsRoot);
        }

        private FileSystemWatcher CreateWatcher(string path)
        {
            var watcher = new FileSystemWatcher(path)
            {
                Filter = "*.cs",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += (_, _) => _needsRebuild = true;
            watcher.Changed += OnFileChanged;
            return watcher;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            _needsRebuild = true;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static string GenerateMonoBehaviourTemplate(string className)
        {
            return $@"using RoseEngine;

public class {className} : MonoBehaviour
{{
    public override void Start()
    {{

    }}

    public override void Update()
    {{

    }}
}}
";
        }

        private static string GenerateUniqueCopyName(string dir, string baseName)
        {
            // Try baseName_Copy, baseName_Copy2, baseName_Copy3, ...
            string candidate = baseName + "_Copy";
            if (!File.Exists(Path.Combine(dir, candidate + ".cs")))
                return candidate;

            for (int i = 2; i < 100; i++)
            {
                candidate = baseName + "_Copy" + i;
                if (!File.Exists(Path.Combine(dir, candidate + ".cs")))
                    return candidate;
            }
            return baseName + "_Copy" + Guid.NewGuid().ToString("N")[..6];
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");
        }

        private static void OpenInExternalEditor(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            string editor = ProjectSettings.ExternalScriptEditor;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = editor,
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false,
                });
                Debug.Log($"[Scripts] Opened in {editor}: {filePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Scripts] Failed to open editor '{editor}': {ex.Message}");
            }
        }

        private static void OpenContainingFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

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

        // =====================================================================
        // Type resolution (for drag-and-drop AddComponent)
        // =====================================================================

        /// <summary>
        /// .cs 파일 경로에서 클래스명을 추출하고, 로드된 어셈블리에서 해당 Component 타입을 찾는다.
        /// </summary>
        internal static Type? ResolveComponentType(string scriptPath)
        {
            string className = Path.GetFileNameWithoutExtension(scriptPath);
            if (string.IsNullOrEmpty(className)) return null;

            var baseType = typeof(Component);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName == null) continue;
                if (asmName.StartsWith("System") || asmName.StartsWith("Microsoft")
                    || asmName.StartsWith("netstandard") || asmName.StartsWith("ImGui"))
                    continue;

                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.IsAbstract || t.IsInterface) continue;
                        if (t.Name != className) continue;
                        if (!baseType.IsAssignableFrom(t)) continue;
                        return t;
                    }
                }
                catch { /* ReflectionTypeLoadException 등 무시 */ }
            }
            return null;
        }

        // =====================================================================
        // Data structures
        // =====================================================================

        private class ScriptFolderNode
        {
            public string Name = "";
            public string FullPath = "";
            public ScriptFolderNode? Parent;
            public List<ScriptFolderNode> SubFolders = new();
            public List<ScriptFileEntry> Scripts = new();
        }

        private struct ScriptFileEntry
        {
            public string FileName;
            public string FullPath;
        }
    }
}
