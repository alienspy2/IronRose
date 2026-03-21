using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using RoseEngine;
using SNVector2 = System.Numerics.Vector2;
using SNVector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiHierarchyPanel : IEditorPanel
    {
        internal static ImGuiHierarchyPanel? Instance { get; private set; }
        internal static HashSet<string>? PendingExpandedGuids;
        internal IReadOnlyCollection<int> OpenNodeIds => _openNodeIds;

        private bool _isOpen = true;
        private bool _isWindowFocused;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }
        public bool IsWindowFocused => _isWindowFocused;
        public int? SelectedGameObjectId => EditorSelection.SelectedGameObjectId;
        public long SelectionVersion => EditorSelection.SelectionVersion;

        // Shift+Click 범위 선택용 — 매 프레임 트리 순회 순서로 재구축
        // _flatOrderedIds: 현재 프레임 빌드 중, _prevFlatOrderedIds: 이전 프레임 완성본 (RangeSelect용)
        private List<int> _flatOrderedIds = new();
        private List<int> _prevFlatOrderedIds = new();

        // ── Keyboard navigation ──
        private readonly HashSet<int> _openNodeIds = new();
        private int? _toggleNodeId;
        private bool _suppressDirty;
        private bool _needScroll;

        // ── Drag & Drop state ──
        private const string DragPayloadType = "HIERARCHY_GO";
        private static List<int>? _draggedIds;
        private int? _deferredSelectId;       // 멀티셀렉트 드래그 시 선택 해제 방지
        private enum DropZone { None, Above, Onto, Below }

        // ── Rename (F2) ──
        private int? _renamingId;
        private string _renameBuffer = "";
        private bool _focusRenameInput;

        // ── Search / Filter ──
        private string _searchFilter = "";
        private HashSet<int>? _matchedIds;         // 검색 결과 + 조상 노드 ID
        private static Type[]? _cachedComponentTypes;

        /// <summary>핫 리로드 후 컴포넌트 타입 캐시 무효화.</summary>
        internal static void InvalidateComponentTypeCache() => _cachedComponentTypes = null;

        public ImGuiHierarchyPanel()
        {
            Instance = this;
        }

        public void Draw()
        {
            if (!ProjectContext.IsProjectLoaded) return;

            if (!IsOpen) { _isWindowFocused = false; return; }

            var beginResult = ImGui.Begin("Hierarchy", ref _isOpen);
            if (beginResult)
            {
                _isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

                // ── Apply persisted expand state from scene load ──
                if (PendingExpandedGuids != null)
                {
                    var pending = PendingExpandedGuids;
                    PendingExpandedGuids = null;
                    _openNodeIds.Clear();
                    foreach (var go in SceneManager.AllGameObjects)
                    {
                        if (go._isDestroyed || go._isEditorInternal) continue;
                        if (pending.Contains(go.guid))
                            _openNodeIds.Add(go.GetInstanceID());
                    }
                    _suppressDirty = true;
                }

                // ── Search bar ──
                bool hasSearchText = _searchFilter.Length > 0;
                if (hasSearchText)
                {
                    float clearBtnW = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X;
                    ImGui.SetNextItemWidth(-clearBtnW);
                }
                else
                {
                    ImGui.SetNextItemWidth(-1);
                }
                ImGui.InputTextWithHint("##HierarchySearch", "Search...", ref _searchFilter, 256);
                if (hasSearchText)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("X##HierarchyClear"))
                        _searchFilter = "";
                }

                var allObjects = SceneManager.AllGameObjects;

                // 검색 필터 적용
                bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);
                _matchedIds = hasFilter ? BuildMatchSet(allObjects, _searchFilter) : null;

                // Build parent→children map
                var roots = new List<GameObject>();
                var childMap = new Dictionary<int, List<GameObject>>();

                foreach (var go in allObjects)
                {
                    if (go._isEditorInternal) continue;
                    // 검색 중이면 매치 셋에 없는 오브젝트는 스킵
                    if (_matchedIds != null && !_matchedIds.Contains(go.GetInstanceID()))
                        continue;

                    var parent = go.transform.parent;
                    // 부모가 필터링되어 없으면 루트 취급
                    bool parentVisible = parent != null
                        && (_matchedIds == null || _matchedIds.Contains(parent.gameObject.GetInstanceID()));

                    if (parent == null || !parentVisible)
                    {
                        roots.Add(go);
                    }
                    else
                    {
                        int pid = parent.gameObject.GetInstanceID();
                        if (!childMap.TryGetValue(pid, out var children))
                        {
                            children = new List<GameObject>();
                            childMap[pid] = children;
                        }
                        children.Add(go);
                    }
                }

                // _parent._children 순서에 맞게 정렬 (SetSiblingIndex 반영)
                foreach (var pair in childMap)
                    pair.Value.Sort((a, b) =>
                        a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));

                // 이전 프레임 완성본 보존 후 현재 프레임 빌드 시작
                (_prevFlatOrderedIds, _flatOrderedIds) = (_flatOrderedIds, _prevFlatOrderedIds);
                _flatOrderedIds.Clear();
                foreach (var root in roots)
                    DrawNode(root, childMap);

                // ── Keyboard navigation ──
                HandleKeyboardNavigation(childMap);

                // 빈 공간에 드롭 → 루트로 이동
                DrawRootDropZone();

                // ── Deferred select (멀티셀렉트 상태에서 드래그 없이 클릭 완료 시) ──
                if (_deferredSelectId.HasValue)
                {
                    if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                        _deferredSelectId = null;
                    else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        EditorSelection.Select(_deferredSelectId.Value);
                        _deferredSelectId = null;
                    }
                }
            }
            _suppressDirty = false;
            ImGui.End();
        }

        private void DrawNode(GameObject go, Dictionary<int, List<GameObject>> childMap)
        {
            int id = go.GetInstanceID();
            _flatOrderedIds.Add(id);

            bool hasChildren = childMap.ContainsKey(id);
            bool isSelected = EditorSelection.IsSelected(id);
            bool searching = _matchedIds != null;
            bool directMatch = searching && IsDirectMatch(go);

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (!hasChildren)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (isSelected)
                flags |= ImGuiTreeNodeFlags.Selected;
            // 검색 중이면 자동으로 모든 노드 펼치기
            if (searching && hasChildren)
                flags |= ImGuiTreeNodeFlags.DefaultOpen;

            // 키보드로 열기/닫기 (검색 중이 아닐 때만)
            if (!searching && _toggleNodeId == id)
            {
                ImGui.SetNextItemOpen(_openNodeIds.Contains(id));
                _toggleNodeId = null;
            }
            // 씬 로드 후 첫 프레임: ImGui TreeNode 상태를 _openNodeIds와 동기화
            else if (_suppressDirty && hasChildren && _openNodeIds.Contains(id))
            {
                ImGui.SetNextItemOpen(true);
            }

            // ── Rename 모드: InputText로 대체 ──
            if (_renamingId == id)
            {
                // 트리 구조 유지를 위해 보이지 않는 TreeNode를 렌더
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
                // 빈 라벨의 TreeNode (들여쓰기 유지용)
                ImGui.TreeNodeEx($"##rename_placeholder_{id}", flags | ImGuiTreeNodeFlags.AllowOverlap);

                ImGui.SameLine();

                bool justRequested = _focusRenameInput;
                if (_focusRenameInput)
                {
                    ImGui.SetKeyboardFocusHere();
                    _focusRenameInput = false;
                }

                ImGui.SetNextItemWidth(-1);
                bool entered = ImGui.InputText($"##rename_{id}", ref _renameBuffer, 256,
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                bool isActive = ImGui.IsItemActive();

                // 매 프레임 진단 (첫 몇 프레임만 로그)
                if (justRequested || !isActive)
                    Debug.Log($"[DiagRename] Hierarchy InputText frame: id={id}, entered={entered}, isActive={isActive}, justRequested={justRequested}, escKey={ImGui.IsKeyPressed(ImGuiKey.Escape)}, windowFocused={ImGui.IsWindowFocused()}");

                if (entered)
                {
                    CommitRename("Enter key");
                }
                else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    CancelRename("Escape key");
                }
                else if (!isActive && !justRequested)
                {
                    // 포커스를 잃으면 적용
                    Debug.LogWarning($"[DiagRename] Hierarchy InputText lost focus unexpectedly: id={id}");
                    CommitRename("focus lost");
                }

                // rename 모드에서는 자식 노드 렌더링만 계속
                if (hasChildren && _openNodeIds.Contains(id))
                {
                    // TreeNode를 Leaf로 렌더했으므로 수동으로 Indent
                    ImGui.TreePush($"##rename_children_{id}");
                    foreach (var child in childMap[id])
                        DrawNode(child, childMap);
                    ImGui.TreePop();
                }
                return;
            }

            // 검색 매치 하이라이트 (비매치 조상은 dimmed)
            bool pushedColor = false;
            bool isPrefabNode = IsPrefabHierarchy(go);
            if (searching && !directMatch)
            {
                ImGui.PushStyleColor(ImGuiCol.Text,
                    new SNVector4(1f, 1f, 1f, 0.4f));
                pushedColor = true;
            }
            else if (isPrefabNode)
            {
                // 프리팹 인스턴스 + 그 자식: 어두운 파란색
                ImGui.PushStyleColor(ImGuiCol.Text,
                    new SNVector4(0.35f, 0.55f, 0.85f, 1.0f));
                pushedColor = true;
            }

            string label = go.activeSelf ? go.name : $"({go.name})";
            bool opened = ImGui.TreeNodeEx($"{label}##{id}", flags);

            if (pushedColor)
                ImGui.PopStyleColor();

            // ── Prefab ">" 오버레이 (오른쪽 끝, DrawList로 직접 렌더) ──
            bool prefabArrowClicked = false;
            if (go.GetComponent<PrefabInstance>() != null)
            {
                var itemMin = ImGui.GetItemRectMin();
                var itemMax = ImGui.GetItemRectMax();
                var dl = ImGui.GetWindowDrawList();
                var arrowText = ">";
                var arrowSize = ImGui.CalcTextSize(arrowText);
                float pad = 4f;
                float arrowX = itemMax.X - arrowSize.X - pad;
                float arrowY = itemMin.Y + (itemMax.Y - itemMin.Y - arrowSize.Y) * 0.5f;
                var arrowMin = new SNVector2(arrowX - pad, itemMin.Y);
                var arrowMax = new SNVector2(itemMax.X, itemMax.Y);

                var mousePos = ImGui.GetMousePos();
                bool hovered = mousePos.X >= arrowMin.X && mousePos.X <= arrowMax.X
                    && mousePos.Y >= arrowMin.Y && mousePos.Y <= arrowMax.Y;

                if (hovered)
                    dl.AddRectFilled(arrowMin, arrowMax,
                        ImGui.GetColorU32(new SNVector4(0.3f, 0.5f, 0.8f, 0.4f)), 3f);

                uint arrowColor = ImGui.GetColorU32(new SNVector4(0.35f, 0.55f, 0.85f, 1.0f));
                dl.AddText(new SNVector2(arrowX, arrowY), arrowColor, arrowText);

                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    prefabArrowClicked = true;
                    var prefabPath = PrefabUtility.GetPrefabAssetPath(go);
                    if (prefabPath != null)
                        PrefabEditMode.Enter(prefabPath);
                }
            }

            // 마우스 클릭 시 open 상태 동기화
            if (hasChildren)
            {
                bool wasOpen = _openNodeIds.Contains(id);
                if (opened) _openNodeIds.Add(id);
                else _openNodeIds.Remove(id);
                if (opened != wasOpen && !_suppressDirty)
                    SceneManager.GetActiveScene().isDirty = true;
            }

            // 선택된 노드로 자동 스크롤
            if (isSelected && _needScroll)
            {
                ImGui.SetScrollHereY();
                _needScroll = false;
            }

            // ── Drag source ──
            HandleDragSource(go);

            // ── Click selection (드래그 시작이 아닌 경우만, ">" 클릭 제외) ──
            if (!prefabArrowClicked && ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var io = ImGui.GetIO();
                if (io.KeyCtrl)
                    EditorSelection.ToggleSelect(id);
                else if (io.KeyShift)
                    EditorSelection.RangeSelect(id, _prevFlatOrderedIds);
                else if (EditorSelection.IsSelected(id) && EditorSelection.SelectedGameObjectIds.Count > 1)
                    _deferredSelectId = id;   // 멀티셀렉트 드래그를 위해 선택 해제 지연
                else
                    EditorSelection.Select(id);
            }

            // ── 더블클릭 → 프리팹이면 Edit Mode, 아니면 Rename ──
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                var prefabComp = go.GetComponent<PrefabInstance>();
                if (prefabComp != null)
                {
                    var prefabPath = PrefabUtility.GetPrefabAssetPath(go);
                    if (prefabPath != null)
                        PrefabEditMode.Enter(prefabPath);
                }
                else
                {
                    BeginRename(id);
                }
            }

            // ── Context menu (우클릭 → 자식으로 생성 + Unpack Prefab) ──
            if (ImGui.BeginPopupContextItem($"##ctx_{id}"))
            {
                DrawCreateContextMenu(id, go);
                ImGui.EndPopup();
            }

            // ── Drop target ──
            HandleDropTarget(go);

            if (hasChildren && opened)
            {
                foreach (var child in childMap[id])
                    DrawNode(child, childMap);
                ImGui.TreePop();
            }
        }

        // ================================================================
        // Keyboard navigation
        // ================================================================

        private void HandleKeyboardNavigation(Dictionary<int, List<GameObject>> childMap)
        {
            if (!ImGui.IsWindowFocused() || _flatOrderedIds.Count == 0) return;

            int? selectedId = EditorSelection.SelectedGameObjectId;

            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            {
                int idx = selectedId.HasValue ? _flatOrderedIds.IndexOf(selectedId.Value) : -1;
                if (idx < _flatOrderedIds.Count - 1)
                {
                    EditorSelection.Select(_flatOrderedIds[idx + 1]);
                    _needScroll = true;
                }
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            {
                int idx = selectedId.HasValue ? _flatOrderedIds.IndexOf(selectedId.Value) : _flatOrderedIds.Count;
                if (idx > 0)
                {
                    EditorSelection.Select(_flatOrderedIds[idx - 1]);
                    _needScroll = true;
                }
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            {
                if (selectedId.HasValue && childMap.ContainsKey(selectedId.Value))
                {
                    if (!_openNodeIds.Contains(selectedId.Value))
                    {
                        // 접혀있으면 → 펼치기
                        _openNodeIds.Add(selectedId.Value);
                        _toggleNodeId = selectedId.Value;
                        SceneManager.GetActiveScene().isDirty = true;
                    }
                    else
                    {
                        // 이미 펼쳐져 있으면 → 첫 번째 자식으로 이동
                        var firstChild = childMap[selectedId.Value][0];
                        EditorSelection.Select(firstChild.GetInstanceID());
                        _needScroll = true;
                    }
                }
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            {
                if (selectedId.HasValue)
                {
                    if (childMap.ContainsKey(selectedId.Value) && _openNodeIds.Contains(selectedId.Value))
                    {
                        // 펼쳐진 노드 → 접기
                        _openNodeIds.Remove(selectedId.Value);
                        _toggleNodeId = selectedId.Value;
                        SceneManager.GetActiveScene().isDirty = true;
                    }
                    else
                    {
                        // 접혀있거나 리프 → 부모로 이동
                        var go = UndoUtility.FindGameObjectById(selectedId.Value);
                        if (go?.transform.parent != null)
                        {
                            EditorSelection.Select(go.transform.parent.gameObject.GetInstanceID());
                            _needScroll = true;
                        }
                    }
                }
            }

            // ── F2 → Rename 모드 진입 ──
            if (ImGui.IsKeyPressed(ImGuiKey.F2) && selectedId.HasValue && _renamingId == null)
            {
                BeginRename(selectedId.Value);
            }

            // ── Alt+Shift+A → Active 토글 (멀티셀렉트 지원) ──
            var io = ImGui.GetIO();
            if (io.KeyAlt && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.A))
            {
                var selectedIds = EditorSelection.SelectedGameObjectIds;
                if (selectedIds.Count > 0)
                {
                    // 첫 번째 선택 오브젝트의 activeSelf를 기준으로 토글 방향 결정
                    var firstGo = UndoUtility.FindGameObjectById(selectedIds.First());
                    if (firstGo != null)
                    {
                        bool newActive = !firstGo.activeSelf;
                        var actions = new List<IUndoAction>();

                        foreach (var id in selectedIds)
                        {
                            var go = UndoUtility.FindGameObjectById(id);
                            if (go == null) continue;
                            bool oldActive = go.activeSelf;
                            go.SetActive(newActive);
                            actions.Add(new SetActiveAction(
                                $"Toggle Active {go.name}", id, oldActive, newActive));
                        }

                        if (actions.Count == 1)
                            UndoSystem.Record(actions[0]);
                        else if (actions.Count > 1)
                            UndoSystem.Record(new CompoundUndoAction("Toggle Active", actions));

                        SceneManager.GetActiveScene().isDirty = true;
                    }
                }
            }
        }

        // ================================================================
        // Rename helpers
        // ================================================================

        private void BeginRename(int id)
        {
            var go = UndoUtility.FindGameObjectById(id);
            if (go == null) return;
            _renamingId = id;
            _renameBuffer = go.name;
            _focusRenameInput = true;
            Debug.Log($"[DiagRename] Hierarchy BeginRename: id={id}, name='{go.name}'");
        }

        private void CommitRename(string caller = "unknown")
        {
            if (_renamingId == null) return;
            var go = UndoUtility.FindGameObjectById(_renamingId.Value);
            Debug.Log($"[DiagRename] Hierarchy CommitRename from '{caller}': id={_renamingId}, buffer='{_renameBuffer}'");
            if (go != null && _renameBuffer.Length > 0 && _renameBuffer != go.name)
            {
                string oldName = go.name;
                go.name = _renameBuffer;
                UndoSystem.Record(new RenameGameObjectAction(
                    $"Rename '{oldName}' → '{_renameBuffer}'",
                    _renamingId.Value, oldName, _renameBuffer));
            }
            _renamingId = null;
        }

        private void CancelRename(string caller = "unknown")
        {
            Debug.Log($"[DiagRename] Hierarchy CancelRename from '{caller}': id={_renamingId}");
            _renamingId = null;
        }

        // ================================================================
        // Drag Source
        // ================================================================

        private static void HandleDragSource(GameObject go)
        {
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                // 선택된 오브젝트 중 하나를 드래그하면 선택 전체를 이동
                if (EditorSelection.IsSelected(go.GetInstanceID()) &&
                    EditorSelection.SelectedGameObjectIds.Count > 1)
                {
                    _draggedIds = new List<int>(EditorSelection.SelectedGameObjectIds);
                }
                else
                {
                    _draggedIds = new List<int> { go.GetInstanceID() };
                }

                unsafe
                {
                    int dummy = 1;
                    ImGui.SetDragDropPayload(DragPayloadType, (IntPtr)(&dummy), sizeof(int));
                }

                // 드래그 프리뷰
                if (_draggedIds.Count == 1)
                    ImGui.Text(go.name);
                else
                    ImGui.Text($"{go.name} (+{_draggedIds.Count - 1})");

                ImGui.EndDragDropSource();
            }
        }

        // ================================================================
        // Drop Target
        // ================================================================

        private void HandleDropTarget(GameObject targetGo)
        {
            if (ImGui.BeginDragDropTarget())
            {
                // 마우스 Y 위치로 드롭 존 판별
                var itemMin = ImGui.GetItemRectMin();
                var itemMax = ImGui.GetItemRectMax();
                float itemH = itemMax.Y - itemMin.Y;
                float mouseY = ImGui.GetMousePos().Y;
                float relY = mouseY - itemMin.Y;

                DropZone zone;
                if (relY < itemH * 0.25f)
                    zone = DropZone.Above;
                else if (relY > itemH * 0.75f)
                    zone = DropZone.Below;
                else
                    zone = DropZone.Onto;

                // 시각적 피드백
                var drawList = ImGui.GetWindowDrawList();
                uint lineColor = ImGui.GetColorU32(new SNVector4(0.3f, 0.6f, 1.0f, 1.0f));
                uint highlightColor = ImGui.GetColorU32(new SNVector4(0.3f, 0.6f, 1.0f, 0.3f));

                switch (zone)
                {
                    case DropZone.Above:
                        drawList.AddLine(
                            new SNVector2(itemMin.X, itemMin.Y),
                            new SNVector2(itemMax.X, itemMin.Y),
                            lineColor, 2.0f);
                        break;
                    case DropZone.Below:
                        drawList.AddLine(
                            new SNVector2(itemMin.X, itemMax.Y),
                            new SNVector2(itemMax.X, itemMax.Y),
                            lineColor, 2.0f);
                        break;
                    case DropZone.Onto:
                        drawList.AddRectFilled(itemMin, itemMax, highlightColor);
                        break;
                }

                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload(DragPayloadType);
                    if (payload.NativePtr != null && _draggedIds != null)
                    {
                        ExecuteDrop(_draggedIds, targetGo, zone);
                        _draggedIds = null;
                    }

                    // Project Panel에서 .prefab 에셋 드롭 → Onto만 허용 (자식으로 추가)
                    var assetPayload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                    if (assetPayload.NativePtr != null)
                    {
                        var assetPath = ImGuiProjectPanel._draggedAssetPath;
                        if (assetPath != null && Path.GetExtension(assetPath).Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                        {
                            var parent = zone == DropZone.Onto ? targetGo : targetGo.transform.parent?.gameObject;
                            SpawnPrefabAsChild(assetPath, parent);
                        }
                    }

                    // Scripts Panel에서 .cs 스크립트 드롭 → 컴포넌트 추가
                    var scriptPayload = ImGui.AcceptDragDropPayload(ImGuiScriptsPanel.DragPayloadType);
                    if (scriptPayload.NativePtr != null)
                    {
                        var scriptPath = ImGuiScriptsPanel._draggedScriptPath;
                        if (scriptPath != null)
                        {
                            var compType = ImGuiScriptsPanel.ResolveComponentType(scriptPath);
                            if (compType != null)
                            {
                                targetGo.AddComponent(compType);
                                UndoSystem.Record(new AddComponentAction(
                                    $"Add {compType.Name}", targetGo.GetInstanceID(), compType));
                                SceneManager.GetActiveScene().isDirty = true;
                                EditorDebug.Log($"[Scripts] Added component {compType.Name} to {targetGo.name}");
                            }
                            else
                            {
                                EditorDebug.LogWarning($"[Scripts] No Component type found for: {Path.GetFileNameWithoutExtension(scriptPath)}");
                            }
                        }
                    }
                }

                ImGui.EndDragDropTarget();
            }
        }

        // ================================================================
        // Root drop zone (패널 하단 빈 공간)
        // ================================================================

        private void DrawRootDropZone()
        {
            // 나머지 빈 영역을 InvisibleButton 으로 채움
            float remainH = ImGui.GetContentRegionAvail().Y;
            if (remainH < 20f) remainH = 20f;
            ImGui.InvisibleButton("##root_drop", new SNVector2(-1, remainH));

            // Prefab Edit Mode에서는 루트 레벨 조작 차단
            bool blockRootLevel = EditorState.IsEditingPrefab;

            // ── Context menu (빈 공간 우클릭 → 루트에 생성) ──
            if (!blockRootLevel && ImGui.BeginPopupContextItem("##ctx_root"))
            {
                DrawCreateContextMenu(null);
                ImGui.EndPopup();
            }

            if (ImGui.BeginDragDropTarget())
            {
                // 시각적 피드백
                var drawList = ImGui.GetWindowDrawList();
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                uint lineColor = ImGui.GetColorU32(new SNVector4(0.3f, 0.6f, 1.0f, 1.0f));
                drawList.AddLine(
                    new SNVector2(min.X, min.Y),
                    new SNVector2(max.X, min.Y),
                    lineColor, 2.0f);

                unsafe
                {
                    if (!blockRootLevel)
                    {
                        var payload = ImGui.AcceptDragDropPayload(DragPayloadType);
                        if (payload.NativePtr != null && _draggedIds != null)
                        {
                            ExecuteRootDrop(_draggedIds);
                            _draggedIds = null;
                        }
                    }

                    // Project Panel에서 .prefab 에셋 드롭
                    var assetPayload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                    if (assetPayload.NativePtr != null)
                    {
                        var assetPath = ImGuiProjectPanel._draggedAssetPath;
                        if (assetPath != null && Path.GetExtension(assetPath).Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                        {
                            // Prefab Edit Mode이면 root GO 자식으로, 아니면 씬 루트에
                            var parent = blockRootLevel ? FindPrefabEditRoot() : null;
                            SpawnPrefabAsChild(assetPath, parent);
                        }
                    }
                }

                ImGui.EndDragDropTarget();
            }
        }

        // ================================================================
        // Execute drop operations
        // ================================================================

        private void ExecuteDrop(List<int> draggedIds, GameObject targetGo, DropZone zone)
        {
            int targetId = targetGo.GetInstanceID();

            // 자기 자신 위에 드롭 방지
            if (draggedIds.Contains(targetId)) return;

            // 순환 참조 방지: 드래그 대상이 타겟의 조상이면 무시
            foreach (int did in draggedIds)
            {
                var dgo = UndoUtility.FindGameObjectById(did);
                if (dgo != null && targetGo.transform.IsChildOf(dgo.transform))
                    return;
            }

            // 트리 순서로 정렬하여 상대적 순서 유지
            var sortedIds = draggedIds
                .OrderBy(id =>
                {
                    int idx = _flatOrderedIds.IndexOf(id);
                    return idx >= 0 ? idx : int.MaxValue;
                })
                .ToList();

            // Below는 역순으로 처리해야 올바른 삽입 순서 유지
            if (zone == DropZone.Below)
                sortedIds.Reverse();

            var actions = new List<IUndoAction>();

            foreach (int did in sortedIds)
            {
                var dgo = UndoUtility.FindGameObjectById(did);
                if (dgo == null) continue;

                int? oldParentId = dgo.transform.parent?.gameObject.GetInstanceID();
                int oldSiblingIdx = dgo.transform.GetSiblingIndex();

                int? newParentId;
                int newSiblingIdx;

                switch (zone)
                {
                    case DropZone.Onto:
                        // 타겟의 자식으로 추가 (마지막)
                        newParentId = targetId;
                        newSiblingIdx = targetGo.transform.childCount;
                        break;

                    case DropZone.Above:
                        // 타겟과 같은 부모, 타겟의 앞에 삽입
                        newParentId = targetGo.transform.parent?.gameObject.GetInstanceID();
                        newSiblingIdx = targetGo.transform.GetSiblingIndex();
                        break;

                    case DropZone.Below:
                        // 타겟과 같은 부모, 타겟의 뒤에 삽입
                        newParentId = targetGo.transform.parent?.gameObject.GetInstanceID();
                        newSiblingIdx = targetGo.transform.GetSiblingIndex() + 1;
                        break;

                    default:
                        continue;
                }

                // 같은 부모 내에서의 이동 시 인덱스 보정
                if (oldParentId == newParentId && oldSiblingIdx < newSiblingIdx)
                    newSiblingIdx--;

                // Prefab Edit Mode에서 루트 레벨(parent==null)로의 이동 차단
                if (EditorState.IsEditingPrefab && !newParentId.HasValue)
                    continue;

                // 변경사항 없으면 스킵
                if (oldParentId == newParentId && oldSiblingIdx == newSiblingIdx)
                    continue;

                var newParentTransform = newParentId.HasValue
                    ? UndoUtility.FindGameObjectById(newParentId.Value)?.transform
                    : null;

                dgo.transform.SetParent(newParentTransform);
                dgo.transform.SetSiblingIndex(newSiblingIdx);

                actions.Add(new ReparentAction(
                    $"Reparent {dgo.name}", dgo,
                    oldParentId, newParentId,
                    oldSiblingIdx, newSiblingIdx));
            }

            if (actions.Count > 0)
            {
                if (actions.Count == 1)
                    UndoSystem.Record(actions[0]);
                else
                    UndoSystem.Record(new CompoundUndoAction("Reparent GameObjects", actions));
            }
        }

        private void ExecuteRootDrop(List<int> draggedIds)
        {
            // Prefab Edit Mode에서 루트 레벨 드롭 차단
            if (EditorState.IsEditingPrefab) return;

            // 트리 순서로 정렬하여 상대적 순서 유지
            var sortedIds = draggedIds
                .OrderBy(id =>
                {
                    int idx = _flatOrderedIds.IndexOf(id);
                    return idx >= 0 ? idx : int.MaxValue;
                })
                .ToList();

            var actions = new List<IUndoAction>();

            foreach (int did in sortedIds)
            {
                var dgo = UndoUtility.FindGameObjectById(did);
                if (dgo == null) continue;

                int? oldParentId = dgo.transform.parent?.gameObject.GetInstanceID();
                int oldSiblingIdx = dgo.transform.GetSiblingIndex();

                // 이미 루트이고 마지막이면 스킵
                if (oldParentId == null)
                    continue;

                dgo.transform.SetParent(null);
                int newSiblingIdx = dgo.transform.GetSiblingIndex();

                actions.Add(new ReparentAction(
                    $"Move {dgo.name} to root", dgo,
                    oldParentId, null,
                    oldSiblingIdx, newSiblingIdx));
            }

            if (actions.Count > 0)
            {
                if (actions.Count == 1)
                    UndoSystem.Record(actions[0]);
                else
                    UndoSystem.Record(new CompoundUndoAction("Move to root", actions));
            }
        }

        // ================================================================
        // Search / Filter
        // ================================================================

        /// <summary>
        /// 검색어가 Component 타입 이름과 정확히 일치하면 해당 Type을 반환.
        /// 그렇지 않으면 null (이름 서브스트링 검색 모드).
        /// </summary>
        private static Type? ResolveComponentType(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            if (_cachedComponentTypes == null)
            {
                var baseType = typeof(Component);
                var types = new List<Type>();
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
                            if (!baseType.IsAssignableFrom(t)) continue;
                            types.Add(t);
                        }
                    }
                    catch { }
                }
                _cachedComponentTypes = types.ToArray();
            }

            foreach (var t in _cachedComponentTypes)
            {
                if (string.Equals(t.Name, query, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// 검색 결과에 해당하는 GameObject ID + 그 조상 ID를 HashSet으로 반환.
        /// 트리를 올바르게 펼칠 수 있도록 조상 경로도 포함한다.
        /// </summary>
        private static HashSet<int> BuildMatchSet(
            IReadOnlyList<GameObject> allObjects, string filter)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(filter)) return result;

            string trimmed = filter.Trim();
            Type? compType = ResolveComponentType(trimmed);

            foreach (var go in allObjects)
            {
                if (go._isEditorInternal) continue;

                bool match;
                if (compType != null)
                {
                    // 타입 검색: 해당 컴포넌트를 가지고 있는가?
                    match = go.GetComponent(compType) != null;
                }
                else
                {
                    // 이름 중간어 검색 (ignore case)
                    match = go.name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                if (match)
                {
                    // 자기 자신 + 모든 조상 추가
                    result.Add(go.GetInstanceID());
                    var p = go.transform.parent;
                    while (p != null)
                    {
                        result.Add(p.gameObject.GetInstanceID());
                        p = p.parent;
                    }
                }
            }

            return result;
        }

        /// <summary>이 GO가 실제 검색 매치인지 (조상이 아닌) 판별.</summary>
        private bool IsDirectMatch(GameObject go)
        {
            if (string.IsNullOrWhiteSpace(_searchFilter)) return false;
            string trimmed = _searchFilter.Trim();
            Type? compType = ResolveComponentType(trimmed);
            if (compType != null)
                return go.GetComponent(compType) != null;
            return go.name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ================================================================
        // Context menu — Create GameObject
        // ================================================================

        internal static void DrawCreateContextMenu(int? parentId, GameObject? contextGo = null)
        {
            if (ImGui.MenuItem("Create Empty"))
                GameObjectFactory.CreateWithUndo(CreateGameObjectType.Empty, parentId);

            ImGui.Separator();

            if (ImGui.BeginMenu("3D Object"))
            {
                if (ImGui.MenuItem("Cube"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.Cube, parentId);
                if (ImGui.MenuItem("Sphere"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.Sphere, parentId);
                if (ImGui.MenuItem("Capsule"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.Capsule, parentId);
                if (ImGui.MenuItem("Cylinder"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.Cylinder, parentId);
                if (ImGui.MenuItem("Plane"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.Plane, parentId);
                if (ImGui.MenuItem("Quad"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.Quad, parentId);
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Light"))
            {
                if (ImGui.MenuItem("Directional Light"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.DirectionalLight, parentId);
                if (ImGui.MenuItem("Point Light"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.PointLight, parentId);
                if (ImGui.MenuItem("Spot Light"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.SpotLight, parentId);
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Camera"))
                GameObjectFactory.CreateWithUndo(CreateGameObjectType.Camera, parentId);

            if (ImGui.BeginMenu("UI"))
            {
                if (ImGui.MenuItem("Canvas"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UICanvas, parentId);
                if (ImGui.MenuItem("Panel"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIPanel, parentId);
                if (ImGui.MenuItem("Text"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIText, parentId);
                if (ImGui.MenuItem("Image"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIImage, parentId);
                if (ImGui.MenuItem("Button"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIButton, parentId);
                if (ImGui.MenuItem("Slider"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UISlider, parentId);
                if (ImGui.MenuItem("Toggle"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIToggle, parentId);
                if (ImGui.MenuItem("Scroll View"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIScrollView, parentId);
                if (ImGui.MenuItem("Input Field"))
                    GameObjectFactory.CreateWithUndo(CreateGameObjectType.UIInputField, parentId);
                ImGui.EndMenu();
            }

            if (contextGo != null && contextGo.GetComponent<PrefabInstance>() != null)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Unpack Prefab"))
                {
                    PrefabUtility.UnpackPrefabInstance(contextGo);
                    SceneManager.GetActiveScene().isDirty = true;
                }
            }

            // Properties — 고정 Inspector 창 열기
            if (contextGo != null)
            {
                ImGui.Separator();
                if (ImGui.MenuItem("Properties"))
                {
                    ImGuiPropertyWindow.RequestOpenGameObject(
                        contextGo.GetInstanceID(), contextGo.name);
                }
            }
        }

        // ================================================================
        // Prefab helpers
        // ================================================================

        /// <summary>
        /// GO 자체에 PrefabInstance가 있거나, 조상 중 하나가 PrefabInstance를 가지고 있으면 true.
        /// </summary>
        private static bool IsPrefabHierarchy(GameObject go)
            => PrefabUtility.IsInPrefabHierarchy(go);

        /// <summary>
        /// .prefab 에셋을 인스턴스화하여 parent의 자식으로 배치.
        /// parent가 null이면 씬 루트에 배치.
        /// </summary>
        private static void SpawnPrefabAsChild(string prefabPath, GameObject? parent)
        {
            var go = PrefabUtility.InstantiatePrefabByPath(prefabPath, Vector3.zero, Quaternion.identity);
            if (go == null) return;

            if (parent != null)
                go.transform.SetParent(parent.transform, false);

            EditorSelection.SelectGameObject(go);
            SceneManager.GetActiveScene().isDirty = true;
        }

        /// <summary>Prefab Edit Mode의 루트 GO를 찾아 반환.</summary>
        private static GameObject? FindPrefabEditRoot()
        {
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && !go._isEditorInternal && go.transform.parent == null)
                    return go;
            }
            return null;
        }
    }
}
