using System.IO;
using ImGuiNET;
using IronRose.AssetPipeline;
using RoseEngine;
using SNVector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Prefab Variant Tree View — 선택된 프리팹의 Variant 계층을 트리로 표시.
    /// Project Panel에서 .prefab 선택 시 또는 Hierarchy에서 프리팹 인스턴스 선택 시 표시.
    /// </summary>
    public class ImGuiVariantTreePanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        // 현재 표시 중인 프리팹 GUID
        private string? _currentPrefabGuid;
        private PrefabVariantTree.TreeNode? _treeRoot;
        private bool _needsRebuild = true;

        public void Draw()
        {
            if (!_isOpen) return;

            if (ImGui.Begin("Variant Tree", ref _isOpen))
            {
                // 현재 선택에 맞는 프리팹 GUID 결정
                var prefabGuid = ResolvePrefabGuid();

                if (prefabGuid == null)
                {
                    ImGui.TextDisabled("Select a prefab to view variant tree");
                    ImGui.End();
                    return;
                }

                // 프리팹 변경 또는 rebuild 필요 시 트리 갱신
                if (prefabGuid != _currentPrefabGuid || _needsRebuild)
                {
                    _currentPrefabGuid = prefabGuid;
                    _treeRoot = PrefabVariantTree.Instance.BuildTree(prefabGuid);
                    _needsRebuild = false;
                }

                if (_treeRoot == null)
                {
                    ImGui.TextDisabled("No variant tree available");
                    ImGui.End();
                    return;
                }

                // 트리 렌더
                DrawTreeNode(_treeRoot, prefabGuid);

                // ── 하단 정보 + 버튼 ──
                ImGui.Separator();
                DrawBottomInfo(prefabGuid);
            }
            ImGui.End();
        }

        /// <summary>Variant 트리 rebuild 요청. 프리팹 파일 변경 시 호출.</summary>
        public void RequestRebuild()
        {
            _needsRebuild = true;
        }

        private static string? ResolvePrefabGuid()
        {
            // 1. 프리팹 편집 모드 중이면 편집 중인 프리팹의 GUID 사용
            if (EditorState.IsEditingPrefab && !string.IsNullOrEmpty(EditorState.EditingPrefabGuid))
                return EditorState.EditingPrefabGuid;

            // 2. Hierarchy에서 프리팹 인스턴스 선택 시
            var selectedGo = EditorSelection.SelectedGameObject;
            if (selectedGo != null)
            {
                var inst = selectedGo.GetComponent<PrefabInstance>();
                if (inst != null && !string.IsNullOrEmpty(inst.prefabGuid))
                    return inst.prefabGuid;
            }

            // 3. Project Panel에서 .prefab 선택 시 — EditorBridge로 감지
            // (현재는 직접 접근 불가 — Overlay에서 전달 예정)
            return null;
        }

        private void DrawTreeNode(PrefabVariantTree.TreeNode node, string selectedGuid)
        {
            bool isSelected = node.Guid == selectedGuid;
            bool hasChildren = node.Children.Count > 0;

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
            if (!hasChildren)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (isSelected)
                flags |= ImGuiTreeNodeFlags.Selected;

            // 색상: Base = 파란, Variant = 보라
            var color = node.IsVariant
                ? new SNVector4(0.7f, 0.5f, 1.0f, 1.0f)
                : new SNVector4(0.4f, 0.7f, 1.0f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.Text, color);

            string typeLabel = node.IsVariant ? "(Variant)" : "(Base)";
            bool opened = ImGui.TreeNodeEx($"{node.DisplayName}  {typeLabel}##{node.Guid}", flags);

            ImGui.PopStyleColor();

            // 클릭 시 Prefab Edit Mode 진입
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && node.Path != null)
            {
                PrefabEditMode.Enter(node.Path);
            }

            if (hasChildren && opened)
            {
                foreach (var child in node.Children)
                    DrawTreeNode(child, selectedGuid);
                ImGui.TreePop();
            }
        }

        private static void DrawBottomInfo(string prefabGuid)
        {
            var db = Resources.GetAssetDatabase();
            var path = db?.GetPathFromGuid(prefabGuid);
            var displayName = path != null ? Path.GetFileNameWithoutExtension(path) : prefabGuid;

            ImGui.Text($"Selected: {displayName}");

            var tree = PrefabVariantTree.Instance;
            var parentGuid = tree.GetParentGuid(prefabGuid);
            if (parentGuid != null)
            {
                var parentPath = db?.GetPathFromGuid(parentGuid);
                var parentName = parentPath != null ? Path.GetFileNameWithoutExtension(parentPath) : parentGuid;
                ImGui.Text($"Base: {parentName}");
            }
            else
            {
                ImGui.TextDisabled("Base: (root)");
            }

            var children = tree.GetChildVariants(prefabGuid);
            ImGui.Text($"Variants: {children?.Count ?? 0}");

            ImGui.Spacing();

            float availW = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button("Open in Prefab Editor", new System.Numerics.Vector2(availW, 0)))
            {
                if (path != null)
                    PrefabEditMode.Enter(path);
            }
            if (ImGui.Button("Create Variant", new System.Numerics.Vector2(availW, 0)))
            {
                if (path != null)
                {
                    var dir = Path.GetDirectoryName(path) ?? "Assets/Prefabs";
                    var baseName = Path.GetFileNameWithoutExtension(path);
                    var variantPath = Path.Combine(dir, $"{baseName}_Variant.prefab");
                    int counter = 1;
                    while (File.Exists(variantPath))
                    {
                        variantPath = Path.Combine(dir, $"{baseName}_Variant{counter}.prefab");
                        counter++;
                    }
                    PrefabUtility.CreateVariant(prefabGuid, variantPath);
                    PrefabVariantTree.Instance.Rebuild();
                    tree = PrefabVariantTree.Instance;
                }
            }
        }
    }
}
