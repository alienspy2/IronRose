// ------------------------------------------------------------
// @file    ImGuiInspectorPanel.cs
// @brief   선택된 GameObject/Asset의 컴포넌트와 속성을 Inspector 패널에 표시하고 편집한다.
//          컴포넌트별 Edit 버튼(Edit Collider, Edit Canvas, Edit Animation)도 여기서 렌더링한다.
// @deps    IronRose.Engine.Editor/EditorState, IronRose.Engine.Editor/CanvasEditMode,
//          IronRose.Engine.Editor/EditorSelection, IronRose.Engine.Editor/UndoSystem,
//          IronRose.AssetPipeline, IronRose.Rendering
// @exports
//   class ImGuiInspectorPanel : IEditorPanel
//     Draw(): void                              — Inspector 패널 렌더링
// @note    "Edit Canvas" 버튼은 Canvas 컴포넌트에 대해 CanvasEditMode.Enter/Exit를 토글한다.
//          "Edit Collider" 패턴과 동일하게 녹색 하이라이트로 활성 상태를 표시.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using IronRose.AssetPipeline;
using IronRose.Engine.Editor;
using IronRose.Rendering;
using IronRose.Engine.Editor.ImGuiEditor;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;
using Veldrid;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiInspectorPanel : IEditorPanel
    {
        private readonly GraphicsDevice _device;
        private readonly VeldridImGuiRenderer _imguiRenderer;

        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        /// <summary>Record 모드 통지용 Animation Editor 참조.</summary>
        public ImGuiAnimationEditorPanel? AnimEditor { get; set; }

        // 프로퍼티에서 제외할 이름들 (베이스 클래스 / 복잡한 참조)
        private static readonly HashSet<string> SkipPropertyNames = new()
        {
            "gameObject", "transform", "name", "tag", "enabled",
        };

        // 프로퍼티에서 제외할 타입들 (복잡한 참조 오브젝트)
        private static readonly HashSet<Type> SkipPropertyTypes = new()
        {
            typeof(Material), typeof(Mesh), typeof(Sprite), typeof(Font),
            typeof(GameObject), typeof(Transform), typeof(Texture2D), typeof(MipMesh),
            typeof(PostProcessProfile),
        };

        // ── Undo tracking ──
        private readonly InspectorUndoTracker _undoTracker = new();
        private int _currentInspectedGoId;

        // ── Component Clipboard ──
        private static TomlTable? _clipboardComponent;

        // ── Add Component 팝업 상태 ──
        private string _addComponentSearch = "";
        private bool _addComponentFocusSearch;
        private static Type[]? _cachedComponentTypes;

        /// <summary>핫 리로드 후 Add Component 메뉴 갱신을 위해 캐시 무효화.</summary>
        internal static void InvalidateComponentTypeCache() => _cachedComponentTypes = null;

        // ── Asset Inspector 상태 ──
        private string? _currentAssetPath;
        private RoseMetadata? _assetMeta;
        private TomlTable? _editedImporter; // 편집 중인 로컬 복사본
        private bool _hasChanges;
        private IReadOnlyCollection<string>? _allSelectedAssetPaths; // Project 패널 원본
        private List<string> _editingAssetPaths = new(); // 셀렉션 변경 시 계산된 같은 importer type 에셋 목록
        private readonly HashSet<string> _mixedImporterKeys = new(); // 값이 통일되지 않은 importer 키

        // ── Asset Preview 캐시 ──
        private string? _previewCachedPath;
        private int _previewReimportVersion;
        private IntPtr _previewTextureId;
        private int _previewTexWidth;
        private int _previewTexHeight;
        private PreviewType _previewType;
        // Material preview 캐시
        private Color _previewMatColor;
        private float _previewMatMetallic;
        private float _previewMatRoughness;
        // Mesh preview 캐시 (3D)
        private int _previewMeshVerts;
        private int _previewMeshTris;
        private string? _previewMeshName;
        private RoseEngine.Vector3 _previewMeshBoundsSize;
        private MeshPreviewRenderer? _meshPreview;
        private IntPtr _meshPreviewTextureId;
        // Font preview 캐시
        private string? _previewFontName;
        private int _previewFontSize;
        private float _previewFontLineHeight;

        private enum PreviewType { None, Texture, Material, Mesh, Font }

        // ── Material 에셋 편집 ──
        private TomlTable? _editedMatTable;  // .mat TOML 내용
        private string? _matFilePath;         // 저장할 .mat 파일 경로
        private long _matEditVersionLocal;    // Undo/Redo 감지용 로컬 버전

        // ── 마지막 선택 추적 (어느 패널이 마지막으로 선택했는지) ──
        private enum InspectorMode { None, GameObject, Asset }
        private InspectorMode _mode = InspectorMode.None;
        private int? _lastGoId;
        private long _lastGoSelectionVersion;
        private string? _lastAssetPath;
        private long _lastAssetSelectionVersion;

        // ── Prefab Variant Override 비교용 캐시 ──
        private string? _variantBaseGuid;          // 현재 편집 중인 variant의 base GUID
        private List<GameObject>? _baseTemplateGOs; // base 프리팹 hierarchy (캐시)

        // ── Asset Browser Popup ──
        private bool _openAssetBrowser;
        private string _assetBrowserSearch = "";
        private bool _assetBrowserFocusSearch;
        private string _assetBrowserTitle = "Select Asset";
        private string _assetBrowserTypeFilter = "";
        private string? _assetBrowserSelectedGuid;
        private Action<string>? _assetBrowserOnConfirm;
        private List<(string displayName, string guid, string path)>? _assetBrowserCachedList;

        public ImGuiInspectorPanel(GraphicsDevice device, VeldridImGuiRenderer imguiRenderer)
        {
            _device = device;
            _imguiRenderer = imguiRenderer;
        }

        void IEditorPanel.Draw() => Draw(null, null);

        public void Draw(int? selectedGoId, long goSelectionVersion = 0) => Draw(selectedGoId, goSelectionVersion, null, 0);

        public void Draw(int? selectedGoId, string? selectedAssetPath, long assetSelectionVersion = 0) => Draw(selectedGoId, 0, selectedAssetPath, assetSelectionVersion);

        public void Draw(int? selectedGoId, long goSelectionVersion, string? selectedAssetPath, long assetSelectionVersion, IReadOnlyCollection<string>? allAssetPaths = null)
        {
            if (!ProjectContext.IsProjectLoaded) return;

            _allSelectedAssetPaths = allAssetPaths;
            if (!IsOpen) return;

            // 어느 쪽이 변경되었는지 감지하여 모드 전환
            if (selectedGoId != _lastGoId || goSelectionVersion != _lastGoSelectionVersion)
            {
                _lastGoId = selectedGoId;
                _lastGoSelectionVersion = goSelectionVersion;
                if (selectedGoId != null)
                    _mode = InspectorMode.GameObject;
            }
            if (selectedAssetPath != _lastAssetPath || assetSelectionVersion != _lastAssetSelectionVersion)
            {
                _lastAssetPath = selectedAssetPath;
                _lastAssetSelectionVersion = assetSelectionVersion;
                if (selectedAssetPath != null)
                    _mode = InspectorMode.Asset;
                else if (_mode == InspectorMode.Asset)
                    _mode = selectedGoId != null ? InspectorMode.GameObject : InspectorMode.None;
            }

            var inspectorVisible = ImGui.Begin("Inspector", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Inspector");
            if (inspectorVisible)
            {
                switch (_mode)
                {
                    case InspectorMode.Asset when selectedAssetPath != null:
                        DrawAssetInspector(selectedAssetPath);
                        break;
                    case InspectorMode.GameObject when selectedGoId != null:
                        ClearAssetState();
                        if (EditorSelection.Count > 1)
                            DrawMultiGameObjectInspector();
                        else
                            DrawGameObjectInspector(selectedGoId.Value);
                        break;
                    default:
                        ClearAssetState();
                        ImGui.TextDisabled("No object selected");
                        break;
                }
            }
            ImGui.End();
            DrawAssetBrowserPopup();
        }

        // ── Asset Browser Popup modal ──
        private void DrawAssetBrowserPopup()
        {
            if (_openAssetBrowser)
            {
                ImGui.OpenPopup(_assetBrowserTitle + "##AssetBrowserModal");
                _openAssetBrowser = false;
            }

            bool modalOpen = true;
            if (!ImGui.BeginPopupModal(_assetBrowserTitle + "##AssetBrowserModal", ref modalOpen,
                    ImGuiWindowFlags.AlwaysAutoResize))
                return;

            // Search input
            if (_assetBrowserFocusSearch)
            {
                ImGui.SetKeyboardFocusHere();
                _assetBrowserFocusSearch = false;
            }
            ImGui.SetNextItemWidth(350f);
            ImGui.InputTextWithHint("##AssetBrowserSearch", "Search...", ref _assetBrowserSearch, 256);
            ImGui.Separator();

            // Build or reuse cached list
            if (_assetBrowserCachedList == null)
                _assetBrowserCachedList = CollectBrowsableAssets(_assetBrowserTypeFilter);

            // Apply search filter (AND, case-insensitive)
            var tokens = _assetBrowserSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filtered = new List<(string displayName, string guid, string path)>();
            foreach (var item in _assetBrowserCachedList)
            {
                bool allMatch = true;
                foreach (var token in tokens)
                {
                    if (!item.displayName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    { allMatch = false; break; }
                }
                if (allMatch) filtered.Add(item);
            }

            // Scrollable list
            ImGui.BeginChild("##AssetBrowserList", new System.Numerics.Vector2(350, 300), ImGuiChildFlags.Border);

            bool confirmed = false;

            // (None) — always first
            bool isNoneSelected = string.IsNullOrEmpty(_assetBrowserSelectedGuid);
            if (ImGui.Selectable("(None)", isNoneSelected))
            {
                _assetBrowserSelectedGuid = "";
                confirmed = true;
            }

            // Asset entries
            for (int i = 0; i < filtered.Count; i++)
            {
                var (displayName, guid, path) = filtered[i];
                bool selected = _assetBrowserSelectedGuid == guid;
                if (ImGui.Selectable($"{displayName}##{guid}", selected))
                {
                    _assetBrowserSelectedGuid = guid;
                    confirmed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(path);
            }

            ImGui.EndChild();

            // OK / Cancel buttons
            ImGui.Spacing();
            float btnW = 80f;
            float totalW = btnW * 2 + 8f;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (350f - totalW) * 0.5f);

            if (ImGui.Button("OK", new System.Numerics.Vector2(btnW, 0)))
                confirmed = true;

            ImGui.SameLine(0, 8f);
            bool cancelled = ImGui.Button("Cancel", new System.Numerics.Vector2(btnW, 0));

            if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
                confirmed = true;
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                cancelled = true;

            if (confirmed)
            {
                _assetBrowserOnConfirm?.Invoke(_assetBrowserSelectedGuid ?? "");
                _assetBrowserCachedList = null;
                ImGui.CloseCurrentPopup();
            }
            else if (cancelled || !modalOpen)
            {
                _assetBrowserCachedList = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        // ── GameObject Inspector ──

        internal void DrawGameObjectInspector(int goId)
        {
            // 에셋 편집 상태 초기화
            ClearAssetState();
            _currentInspectedGoId = goId;
            RefreshBaseTemplate();

            // Find the selected GO
            GameObject? selected = null;
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (go.GetInstanceID() == goId)
                {
                    selected = go;
                    break;
                }
            }

            if (selected == null)
            {
                ImGui.TextDisabled("Object not found");
                return;
            }

            // Name + active
            ImGui.Text(selected.name);
            ImGui.SameLine();
            bool oldActive = selected.activeSelf;
            bool active = oldActive;
            if (ImGui.Checkbox("Active", ref active))
            {
                selected.SetActive(active);
                UndoSystem.Record(new SetActiveAction(
                    $"Toggle Active {selected.name}", goId, oldActive, active));
            }

            // ── Prefab Instance header ──
            var prefabInst = selected.GetComponent<PrefabInstance>();
            bool isInPrefabHierarchy = PrefabUtility.IsInPrefabHierarchy(selected);
            // 컴포넌트 잠금: PrefabInstance 계층 내 GO는 항상 잠금 (편집모드에서도 중첩 프리팹 포함)
            bool isPrefabLocked = isInPrefabHierarchy;
            if (prefabInst != null && isPrefabLocked)
            {
                DrawPrefabInstanceHeader(selected, prefabInst);
            }

            ImGui.Separator();

            // Transform 잠금: 조상 중 PrefabInstance가 있으면 잠금 (프리팹 루트 자체는 이동 가능)
            bool isTransformLocked = PrefabUtility.HasPrefabInstanceAncestor(selected);
            bool hasRectTransform = selected.GetComponent<RectTransform>() != null;
            if (isTransformLocked)
                ImGui.BeginDisabled();
            if (!hasRectTransform && ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var t = selected.transform;

                var pos = new System.Numerics.Vector3(t.localPosition.x, t.localPosition.y, t.localPosition.z);
                if (EditorWidgets.DragFloat3Clickable("Transform.Position", "Position", ref pos, 0.1f))
                {
                    _undoTracker.BeginEdit("Transform.Position", t.localPosition);
                    t.localPosition = new Vector3(pos.X, pos.Y, pos.Z);
                    if (AnimEditor?.IsRecording == true)
                    {
                        AnimEditor.RecordProperty(selected, "Transform", "localPosition", pos.X, "x");
                        AnimEditor.RecordProperty(selected, "Transform", "localPosition", pos.Y, "y");
                        AnimEditor.RecordProperty(selected, "Transform", "localPosition", pos.Z, "z");
                    }
                }
                if (ImGui.IsItemDeactivated())
                {
                    AnimEditor?.FlushRecordUndo();
                    if (_undoTracker.EndEdit("Transform.Position", out var oldPos))
                    {
                        UndoSystem.Record(new SetTransformAction(
                            $"Change Position", goId,
                            (Vector3)oldPos!, t.localRotation, t.localScale,
                            t.localPosition, t.localRotation, t.localScale));
                    }
                }

                var euler = new System.Numerics.Vector3(t.localEulerAngles.x, t.localEulerAngles.y, t.localEulerAngles.z);
                if (EditorWidgets.DragFloat3Clickable("Transform.Rotation", "Rotation", ref euler, 1f))
                {
                    _undoTracker.BeginEdit("Transform.Rotation", t.localRotation);
                    t.localEulerAngles = new Vector3(euler.X, euler.Y, euler.Z);
                    if (AnimEditor?.IsRecording == true)
                    {
                        AnimEditor.RecordProperty(selected, "Transform", "localEulerAngles", euler.X, "x");
                        AnimEditor.RecordProperty(selected, "Transform", "localEulerAngles", euler.Y, "y");
                        AnimEditor.RecordProperty(selected, "Transform", "localEulerAngles", euler.Z, "z");
                    }
                }
                if (ImGui.IsItemDeactivated())
                {
                    AnimEditor?.FlushRecordUndo();
                    if (_undoTracker.EndEdit("Transform.Rotation", out var oldRot))
                    {
                        UndoSystem.Record(new SetTransformAction(
                            $"Change Rotation", goId,
                            t.localPosition, (Quaternion)oldRot!, t.localScale,
                            t.localPosition, t.localRotation, t.localScale));
                    }
                }

                var scale = new System.Numerics.Vector3(t.localScale.x, t.localScale.y, t.localScale.z);
                if (EditorWidgets.DragFloat3Clickable("Transform.Scale", "Scale", ref scale, 0.1f))
                {
                    _undoTracker.BeginEdit("Transform.Scale", t.localScale);
                    t.localScale = new Vector3(scale.X, scale.Y, scale.Z);
                    if (AnimEditor?.IsRecording == true)
                    {
                        AnimEditor.RecordProperty(selected, "Transform", "localScale", scale.X, "x");
                        AnimEditor.RecordProperty(selected, "Transform", "localScale", scale.Y, "y");
                        AnimEditor.RecordProperty(selected, "Transform", "localScale", scale.Z, "z");
                    }
                }
                if (ImGui.IsItemDeactivated())
                {
                    AnimEditor?.FlushRecordUndo();
                    if (_undoTracker.EndEdit("Transform.Scale", out var oldScale))
                    {
                        UndoSystem.Record(new SetTransformAction(
                            $"Change Scale", goId,
                            t.localPosition, t.localRotation, (Vector3)oldScale!,
                            t.localPosition, t.localRotation, t.localScale));
                    }
                }
            }
            if (isTransformLocked)
                ImGui.EndDisabled();

            // RectTransform (UI)
            var rectTransform = selected.GetComponent<RectTransform>();
            if (rectTransform != null)
            {
                if (isTransformLocked)
                    ImGui.BeginDisabled();
                if (ImGui.CollapsingHeader("RectTransform", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    bool xStretched = MathF.Abs(rectTransform.anchorMin.x - rectTransform.anchorMax.x) > 1e-5f;
                    bool yStretched = MathF.Abs(rectTransform.anchorMin.y - rectTransform.anchorMax.y) > 1e-5f;

                    // ── Position / Size fields (Label-Value table) ──
                    const float rtLabelW = 52f;
                    if (ImGui.BeginTable("##RtFields", 4))
                    {
                        ImGui.TableSetupColumn("L1", ImGuiTableColumnFlags.WidthFixed, rtLabelW);
                        ImGui.TableSetupColumn("V1", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("L2", ImGuiTableColumnFlags.WidthFixed, rtLabelW);
                        ImGui.TableSetupColumn("V2", ImGuiTableColumnFlags.WidthStretch);

                        // ── Row 1: Pos X / Left  |  Pos Y / Top ──
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(xStretched ? "Left" : "Pos X");

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        if (!xStretched)
                        {
                            float posX = rectTransform.anchoredPosition.x;
                            if (EditorWidgets.DragFloatClickable("rt.PosX", "##rtPosX", ref posX, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.AnchoredPos", rectTransform.anchoredPosition);
                                rectTransform.anchoredPosition = new Vector2(posX, rectTransform.anchoredPosition.y);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.AnchoredPos", out var oldAP1))
                                UndoSystem.Record(new SetPropertyAction("Change Position", goId, "RectTransform", "anchoredPosition", oldAP1, rectTransform.anchoredPosition));
                        }
                        else
                        {
                            float left = rectTransform.offsetMin.x;
                            if (EditorWidgets.DragFloatClickable("rt.Left", "##rtLeft", ref left, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.OffsetMin", rectTransform.offsetMin);
                                var om = rectTransform.offsetMin;
                                rectTransform.offsetMin = new Vector2(left, om.y);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.OffsetMin", out var oldOM1))
                                UndoSystem.Record(new SetPropertyAction("Change Left", goId, "RectTransform", "anchoredPosition", oldOM1, rectTransform.anchoredPosition));
                        }

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(yStretched ? "Top" : "Pos Y");

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        if (!yStretched)
                        {
                            float posY = rectTransform.anchoredPosition.y;
                            if (EditorWidgets.DragFloatClickable("rt.PosY", "##rtPosY", ref posY, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.AnchoredPos2", rectTransform.anchoredPosition);
                                rectTransform.anchoredPosition = new Vector2(rectTransform.anchoredPosition.x, posY);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.AnchoredPos2", out var oldAP2))
                                UndoSystem.Record(new SetPropertyAction("Change Position", goId, "RectTransform", "anchoredPosition", oldAP2, rectTransform.anchoredPosition));
                        }
                        else
                        {
                            float top = rectTransform.offsetMin.y;
                            if (EditorWidgets.DragFloatClickable("rt.Top", "##rtTop", ref top, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.OffsetMin2", rectTransform.offsetMin);
                                var om = rectTransform.offsetMin;
                                rectTransform.offsetMin = new Vector2(om.x, top);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.OffsetMin2", out var oldOM2))
                                UndoSystem.Record(new SetPropertyAction("Change Top", goId, "RectTransform", "anchoredPosition", oldOM2, rectTransform.anchoredPosition));
                        }

                        // ── Row 2: Width / Right  |  Height / Bottom ──
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(xStretched ? "Right" : "Width");

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        if (!xStretched)
                        {
                            float width = rectTransform.sizeDelta.x;
                            if (EditorWidgets.DragFloatClickable("rt.Width", "##rtWidth", ref width, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.SizeDelta", rectTransform.sizeDelta);
                                rectTransform.sizeDelta = new Vector2(width, rectTransform.sizeDelta.y);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.SizeDelta", out var oldSD1))
                                UndoSystem.Record(new SetPropertyAction("Change SizeDelta", goId, "RectTransform", "sizeDelta", oldSD1, rectTransform.sizeDelta));
                        }
                        else
                        {
                            float right = -rectTransform.offsetMax.x;
                            if (EditorWidgets.DragFloatClickable("rt.Right", "##rtRight", ref right, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.OffsetMax", rectTransform.offsetMax);
                                var oxm = rectTransform.offsetMax;
                                rectTransform.offsetMax = new Vector2(-right, oxm.y);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.OffsetMax", out var oldOXM1))
                                UndoSystem.Record(new SetPropertyAction("Change Right", goId, "RectTransform", "sizeDelta", oldOXM1, rectTransform.sizeDelta));
                        }

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted(yStretched ? "Bottom" : "Height");

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        if (!yStretched)
                        {
                            float height = rectTransform.sizeDelta.y;
                            if (EditorWidgets.DragFloatClickable("rt.Height", "##rtHeight", ref height, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.SizeDelta2", rectTransform.sizeDelta);
                                rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, height);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.SizeDelta2", out var oldSD2))
                                UndoSystem.Record(new SetPropertyAction("Change SizeDelta", goId, "RectTransform", "sizeDelta", oldSD2, rectTransform.sizeDelta));
                        }
                        else
                        {
                            float bottom = -rectTransform.offsetMax.y;
                            if (EditorWidgets.DragFloatClickable("rt.Bottom", "##rtBottom", ref bottom, 1f))
                            {
                                _undoTracker.BeginEdit("RectTransform.OffsetMax2", rectTransform.offsetMax);
                                var oxm = rectTransform.offsetMax;
                                rectTransform.offsetMax = new Vector2(oxm.x, -bottom);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.OffsetMax2", out var oldOXM2))
                                UndoSystem.Record(new SetPropertyAction("Change Bottom", goId, "RectTransform", "sizeDelta", oldOXM2, rectTransform.sizeDelta));
                        }

                        ImGui.EndTable();
                    }

                    ImGui.Separator();

                    // ── Anchors (foldout) ──
                    if (ImGui.TreeNode("Anchors"))
                    {
                        DrawAnchorPresetButton(rectTransform, goId);
                        ImGui.Spacing();

                        if (ImGui.BeginTable("##RtAnchors", 4))
                        {
                            ImGui.TableSetupColumn("AL1", ImGuiTableColumnFlags.WidthFixed, rtLabelW);
                            ImGui.TableSetupColumn("AV1", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("AL2", ImGuiTableColumnFlags.WidthFixed, rtLabelW);
                            ImGui.TableSetupColumn("AV2", ImGuiTableColumnFlags.WidthStretch);

                            var anchorMin = new System.Numerics.Vector2(rectTransform.anchorMin.x, rectTransform.anchorMin.y);
                            var anchorMax = new System.Numerics.Vector2(rectTransform.anchorMax.x, rectTransform.anchorMax.y);

                            // Min X / Min Y
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("Min X");
                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(-1f);
                            float aMinX = anchorMin.X;
                            if (EditorWidgets.DragFloatClickable("rt.AnchorMinX", "##aMinX", ref aMinX, 0.01f))
                            {
                                _undoTracker.BeginEdit("RectTransform.AnchorMin", rectTransform.anchorMin);
                                rectTransform.anchorMin = new Vector2(aMinX, rectTransform.anchorMin.y);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.AnchorMin", out var oldAnchorMin1))
                                UndoSystem.Record(new SetPropertyAction("Change AnchorMin", goId, "RectTransform", "anchorMin", oldAnchorMin1, rectTransform.anchorMin));

                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("Min Y");
                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(-1f);
                            float aMinY = anchorMin.Y;
                            if (EditorWidgets.DragFloatClickable("rt.AnchorMinY", "##aMinY", ref aMinY, 0.01f))
                            {
                                _undoTracker.BeginEdit("RectTransform.AnchorMinY", rectTransform.anchorMin);
                                rectTransform.anchorMin = new Vector2(rectTransform.anchorMin.x, aMinY);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.AnchorMinY", out var oldAnchorMin2))
                                UndoSystem.Record(new SetPropertyAction("Change AnchorMin", goId, "RectTransform", "anchorMin", oldAnchorMin2, rectTransform.anchorMin));

                            // Max X / Max Y
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("Max X");
                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(-1f);
                            float aMaxX = anchorMax.X;
                            if (EditorWidgets.DragFloatClickable("rt.AnchorMaxX", "##aMaxX", ref aMaxX, 0.01f))
                            {
                                _undoTracker.BeginEdit("RectTransform.AnchorMax", rectTransform.anchorMax);
                                rectTransform.anchorMax = new Vector2(aMaxX, rectTransform.anchorMax.y);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.AnchorMax", out var oldAnchorMax1))
                                UndoSystem.Record(new SetPropertyAction("Change AnchorMax", goId, "RectTransform", "anchorMax", oldAnchorMax1, rectTransform.anchorMax));

                            ImGui.TableNextColumn();
                            ImGui.AlignTextToFramePadding();
                            ImGui.TextUnformatted("Max Y");
                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(-1f);
                            float aMaxY = anchorMax.Y;
                            if (EditorWidgets.DragFloatClickable("rt.AnchorMaxY", "##aMaxY", ref aMaxY, 0.01f))
                            {
                                _undoTracker.BeginEdit("RectTransform.AnchorMaxY", rectTransform.anchorMax);
                                rectTransform.anchorMax = new Vector2(rectTransform.anchorMax.x, aMaxY);
                            }
                            if (ImGui.IsItemDeactivated() &&
                                _undoTracker.EndEdit("RectTransform.AnchorMaxY", out var oldAnchorMax2))
                                UndoSystem.Record(new SetPropertyAction("Change AnchorMax", goId, "RectTransform", "anchorMax", oldAnchorMax2, rectTransform.anchorMax));

                            ImGui.EndTable();
                        }
                        ImGui.TreePop();
                    }

                    // ── Pivot ──
                    if (ImGui.BeginTable("##RtPivot", 4))
                    {
                        ImGui.TableSetupColumn("PL1", ImGuiTableColumnFlags.WidthFixed, rtLabelW);
                        ImGui.TableSetupColumn("PV1", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("PL2", ImGuiTableColumnFlags.WidthFixed, rtLabelW);
                        ImGui.TableSetupColumn("PV2", ImGuiTableColumnFlags.WidthStretch);

                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted("Pivot X");
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        float pivotX = rectTransform.pivot.x;
                        if (EditorWidgets.DragFloatClickable("rt.PivotX", "##rtPivotX", ref pivotX, 0.01f))
                        {
                            _undoTracker.BeginEdit("RectTransform.Pivot", rectTransform.pivot);
                            rectTransform.pivot = new Vector2(pivotX, rectTransform.pivot.y);
                        }
                        if (ImGui.IsItemDeactivated() &&
                            _undoTracker.EndEdit("RectTransform.Pivot", out var oldPivotX))
                            UndoSystem.Record(new SetPropertyAction("Change Pivot", goId, "RectTransform", "pivot", oldPivotX, rectTransform.pivot));

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted("Pivot Y");
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1f);
                        float pivotY = rectTransform.pivot.y;
                        if (EditorWidgets.DragFloatClickable("rt.PivotY", "##rtPivotY", ref pivotY, 0.01f))
                        {
                            _undoTracker.BeginEdit("RectTransform.PivotY", rectTransform.pivot);
                            rectTransform.pivot = new Vector2(rectTransform.pivot.x, pivotY);
                        }
                        if (ImGui.IsItemDeactivated() &&
                            _undoTracker.EndEdit("RectTransform.PivotY", out var oldPivotY))
                            UndoSystem.Record(new SetPropertyAction("Change Pivot", goId, "RectTransform", "pivot", oldPivotY, rectTransform.pivot));

                        ImGui.EndTable();
                    }

                    // ── Rotation ──
                    {
                        var t = selected.transform;
                        var euler = new System.Numerics.Vector3(t.localEulerAngles.x, t.localEulerAngles.y, t.localEulerAngles.z);
                        if (EditorWidgets.DragFloat3Clickable("RtTransform.Rotation", "Rotation", ref euler, 1f))
                        {
                            _undoTracker.BeginEdit("RtTransform.Rotation", t.localRotation);
                            t.localEulerAngles = new Vector3(euler.X, euler.Y, euler.Z);
                        }
                        if (ImGui.IsItemDeactivated())
                        {
                            if (_undoTracker.EndEdit("RtTransform.Rotation", out var oldRot))
                            {
                                UndoSystem.Record(new SetTransformAction(
                                    "Change Rotation", goId,
                                    t.localPosition, (Quaternion)oldRot!, t.localScale,
                                    t.localPosition, t.localRotation, t.localScale));
                            }
                        }
                    }

                    // ── Scale ──
                    {
                        var t = selected.transform;
                        var scale = new System.Numerics.Vector3(t.localScale.x, t.localScale.y, t.localScale.z);
                        if (EditorWidgets.DragFloat3Clickable("RtTransform.Scale", "Scale", ref scale, 0.1f))
                        {
                            _undoTracker.BeginEdit("RtTransform.Scale", t.localScale);
                            t.localScale = new Vector3(scale.X, scale.Y, scale.Z);
                        }
                        if (ImGui.IsItemDeactivated())
                        {
                            if (_undoTracker.EndEdit("RtTransform.Scale", out var oldScale))
                            {
                                UndoSystem.Record(new SetTransformAction(
                                    "Change Scale", goId,
                                    t.localPosition, t.localRotation, (Vector3)oldScale!,
                                    t.localPosition, t.localRotation, t.localScale));
                            }
                        }
                    }
                }
                if (isTransformLocked)
                    ImGui.EndDisabled();
            }

            // Components
            Component? componentToDelete = null;
            (Component comp, bool up)? componentToMove = null;
            if (isPrefabLocked)
                ImGui.BeginDisabled();

            // Variant override 비교용 base GO
            var baseGo = _baseTemplateGOs != null ? FindBaseGameObject(selected) : null;

            foreach (var comp in selected.InternalComponents)
            {
                if (comp is Transform) continue;
                if (comp is RectTransform) continue;
                if (comp is PrefabInstance) continue;

                var type = comp.GetType();
                ImGui.PushID(comp.GetHashCode());

                // Enabled checkbox (any component with enabled property/field)
                var enabledProp = type.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                var enabledField = type.GetField("enabled", BindingFlags.Public | BindingFlags.Instance);
                bool hasEnabled = (enabledProp != null && enabledProp.PropertyType == typeof(bool) && enabledProp.CanRead && enabledProp.CanWrite)
                    || (enabledField != null && enabledField.FieldType == typeof(bool));

                if (hasEnabled)
                {
                    bool enabled = enabledProp != null && enabledProp.CanRead
                        ? (bool)enabledProp.GetValue(comp)!
                        : (bool)enabledField!.GetValue(comp)!;
                    if (ImGui.Checkbox("##enabled", ref enabled))
                    {
                        var oldEnabled = !enabled;
                        if (enabledProp != null && enabledProp.CanWrite)
                            enabledProp.SetValue(comp, enabled);
                        else
                            enabledField!.SetValue(comp, enabled);
                        UndoSystem.Record(new SetPropertyAction(
                            $"Toggle {type.Name}", goId,
                            type.Name, "enabled", oldEnabled, enabled));
                        SceneManager.GetActiveScene().isDirty = true;
                    }
                    ImGui.SameLine();
                }

                bool open = ImGui.CollapsingHeader(type.Name, ImGuiTreeNodeFlags.DefaultOpen);

                // Right-click context menu
                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("Copy"))
                    {
                        _clipboardComponent = SceneSerializer.SerializeComponent(comp);
                    }

                    bool canPasteValues = _clipboardComponent != null &&
                        _clipboardComponent.TryGetValue("type", out var ctv) &&
                        ctv?.ToString() == type.Name;

                    if (!canPasteValues) ImGui.BeginDisabled();
                    if (ImGui.MenuItem("Paste Values"))
                    {
                        PasteComponentValues(comp, _clipboardComponent!, goId);
                    }
                    if (!canPasteValues) ImGui.EndDisabled();

                    ImGui.Separator();

                    // Move Up / Move Down
                    int compIndex = selected._components.IndexOf(comp);
                    bool canMoveUp = compIndex > 0 &&
                        !(selected._components[compIndex - 1] is Transform or RectTransform or PrefabInstance);
                    bool canMoveDown = compIndex >= 0 && compIndex < selected._components.Count - 1;

                    if (!canMoveUp) ImGui.BeginDisabled();
                    if (ImGui.MenuItem("Move Up"))
                        componentToMove = (comp, true);
                    if (!canMoveUp) ImGui.EndDisabled();

                    if (!canMoveDown) ImGui.BeginDisabled();
                    if (ImGui.MenuItem("Move Down"))
                        componentToMove = (comp, false);
                    if (!canMoveDown) ImGui.EndDisabled();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Delete"))
                    {
                        componentToDelete = comp;
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();

                if (!open) continue;

                // "Edit Animation" button for Animator components
                if (comp is Animator animator && animator.clip != null)
                {
                    if (ImGui.Button("Edit Animation"))
                        OpenAnimationEditor(animator);
                }

                // "Edit Collider" toggle button for Collider components
                if (comp is Collider)
                {
                    bool editing = EditorState.IsEditingCollider;
                    if (editing)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.7f, 0.3f, 1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.3f, 0.8f, 0.4f, 1f));
                    }
                    if (ImGui.Button("Edit Collider"))
                        EditorState.IsEditingCollider = !EditorState.IsEditingCollider;
                    if (editing)
                        ImGui.PopStyleColor(2);
                }

                // "Edit Canvas" toggle button for Canvas components
                if (comp is RoseEngine.Canvas)
                {
                    bool editingCanvas = EditorState.IsEditingCanvas
                        && EditorState.EditingCanvasGoId == selected.GetInstanceID();
                    if (editingCanvas)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.2f, 0.7f, 0.3f, 1f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.3f, 0.8f, 0.4f, 1f));
                    }
                    if (ImGui.Button("Edit Canvas"))
                    {
                        if (editingCanvas)
                            CanvasEditMode.Exit();
                        else
                            CanvasEditMode.Enter(selected);
                    }
                    if (editingCanvas)
                        ImGui.PopStyleColor(2);
                }

                // Variant override 비교용 base 컴포넌트 매핑
                Component? baseComp = baseGo != null ? FindBaseComponent(comp, baseGo) : null;

                if (isPrefabLocked)
                    ImGui.EndDisabled();
                DrawAssetReferences(comp, type, isPrefabLocked);
                if (isPrefabLocked)
                    ImGui.BeginDisabled();

                DrawComponentFields(comp, type, baseComp);
                DrawComponentProperties(comp, type, baseComp);
            }

            if (isPrefabLocked)
                ImGui.EndDisabled();

            // Deferred component move
            if (componentToMove != null && !isPrefabLocked)
            {
                var (moveComp, up) = componentToMove.Value;
                var comps = selected._components;
                int idx = comps.IndexOf(moveComp);
                if (idx >= 0)
                {
                    int targetIdx = up ? idx - 1 : idx + 1;
                    if (targetIdx >= 0 && targetIdx < comps.Count)
                    {
                        (comps[idx], comps[targetIdx]) = (comps[targetIdx], comps[idx]);
                        UndoSystem.Record(new MoveComponentAction(
                            up ? "Move Component Up" : "Move Component Down",
                            goId, idx, targetIdx));
                        SceneManager.GetActiveScene().isDirty = true;
                    }
                }
            }

            // Deferred component deletion
            if (componentToDelete != null && !isPrefabLocked)
            {
                var serialized = SceneSerializer.SerializeComponent(componentToDelete);
                componentToDelete.OnComponentDestroy();
                selected.RemoveComponent(componentToDelete);
                if (serialized != null)
                {
                    UndoSystem.Record(new RemoveComponentAction(
                        $"Remove {componentToDelete.GetType().Name}", goId, serialized));
                }
                SceneManager.GetActiveScene().isDirty = true;
            }

            // ── Add Component 버튼 ── (프리팹 인스턴스에서는 비활성화)
            if (isPrefabLocked)
                ImGui.BeginDisabled();
            DrawAddComponentButton(selected);
            if (isPrefabLocked)
                ImGui.EndDisabled();

            // ── Scripts Panel에서 .cs 드래그 앤 드롭으로 컴포넌트 추가 ──
            if (!isPrefabLocked)
                HandleScriptDrop(selected);
        }

        // ── Prefab Instance Header ──

        private void DrawPrefabInstanceHeader(GameObject go, PrefabInstance inst)
        {
            ImGui.Spacing();

            // 프리팹 헤더 배경 (파란색 톤)
            var drawList = ImGui.GetWindowDrawList();
            var cursorPos = ImGui.GetCursorScreenPos();
            float availW = ImGui.GetContentRegionAvail().X;
            float headerH = ImGui.GetTextLineHeightWithSpacing() * 3.2f;
            drawList.AddRectFilled(
                cursorPos,
                new System.Numerics.Vector2(cursorPos.X + availW, cursorPos.Y + headerH),
                ImGui.GetColorU32(new System.Numerics.Vector4(0.15f, 0.25f, 0.45f, 0.6f)),
                4f);

            ImGui.Indent(8f);

            // 프리팹 이름 + 경로
            var prefabPath = PrefabUtility.GetPrefabAssetPath(go);
            var prefabName = prefabPath != null ? Path.GetFileName(prefabPath) : "Unknown Prefab";
            ImGui.TextColored(new System.Numerics.Vector4(0.35f, 0.55f, 0.85f, 1.0f),
                $"Prefab: {prefabName}");

            // 버튼 행: Open Prefab / Select Asset / Unpack
            float btnW = (availW - 32f) / 3f;
            if (ImGui.Button("Open Prefab", new System.Numerics.Vector2(btnW, 0)))
            {
                if (prefabPath != null)
                    PrefabEditMode.Enter(prefabPath);
            }
            ImGui.SameLine();
            if (ImGui.Button("Select Asset", new System.Numerics.Vector2(btnW, 0)))
            {
                if (prefabPath != null)
                    EditorBridge.PingAsset(prefabPath);
            }
            ImGui.SameLine();
            if (ImGui.Button("Unpack", new System.Numerics.Vector2(btnW, 0)))
            {
                PrefabUtility.UnpackPrefabInstance(go);
                SceneManager.GetActiveScene().isDirty = true;
            }

            // 안내 텍스트
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f),
                "Open Prefab to edit properties.");

            ImGui.Unindent(8f);
            ImGui.Spacing();
        }

        // ── Add Component ──

        private void DrawAddComponentButton(GameObject target)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            float availW = ImGui.GetContentRegionAvail().X;
            bool hasClipboard = _clipboardComponent != null;

            if (hasClipboard)
            {
                string clipTypeName = _clipboardComponent!.TryGetValue("type", out var tv)
                    ? tv?.ToString() ?? "Component" : "Component";
                float totalW = MathF.Min(availW, 380f);
                float addW = totalW * 0.55f;
                float pasteW = totalW - addW - 4f;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - totalW) * 0.5f);

                if (ImGui.Button("Add Component", new System.Numerics.Vector2(addW, 0)))
                {
                    ImGui.OpenPopup("##AddComponentPopup");
                    _addComponentSearch = "";
                    _addComponentFocusSearch = true;
                }

                ImGui.SameLine(0, 4f);

                if (ImGui.Button($"Paste ({clipTypeName})", new System.Numerics.Vector2(pasteW, 0)))
                {
                    SceneSerializer.DeserializeComponent(target, _clipboardComponent!);
                    UndoSystem.Record(new PasteComponentAction(
                        $"Paste {clipTypeName}", target.GetInstanceID(), _clipboardComponent!));
                    SceneManager.GetActiveScene().isDirty = true;
                }
            }
            else
            {
                float buttonW = MathF.Min(availW, 230f);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availW - buttonW) * 0.5f);

                if (ImGui.Button("Add Component", new System.Numerics.Vector2(buttonW, 0)))
                {
                    ImGui.OpenPopup("##AddComponentPopup");
                    _addComponentSearch = "";
                    _addComponentFocusSearch = true;
                }
            }

            if (ImGui.BeginPopup("##AddComponentPopup"))
            {
                if (_addComponentFocusSearch)
                {
                    ImGui.SetKeyboardFocusHere();
                    _addComponentFocusSearch = false;
                }
                ImGui.SetNextItemWidth(220f);
                ImGui.InputTextWithHint("##Search", "Search...", ref _addComponentSearch, 128);
                ImGui.Separator();

                var types = GetAddableComponentTypes();
                string searchLower = _addComponentSearch.ToLowerInvariant();

                foreach (var compType in types)
                {
                    // 이미 추가된 단일 컴포넌트 건너뛰기 (Transform은 항상 존재)
                    if (compType == typeof(Transform)) continue;

                    string typeName = compType.Name;
                    if (searchLower.Length > 0)
                    {
                        string nameLower = typeName.ToLowerInvariant();
                        bool match = true;
                        foreach (var token in searchLower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (!nameLower.Contains(token)) { match = false; break; }
                        }
                        if (!match) continue;
                    }

                    if (ImGui.Selectable(typeName))
                    {
                        target.AddComponent(compType);
                        UndoSystem.Record(new AddComponentAction(
                            $"Add {typeName}", target.GetInstanceID(), compType));
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.EndPopup();
            }
        }

        private static Type[] GetAddableComponentTypes()
        {
            if (_cachedComponentTypes != null) return _cachedComponentTypes;

            var baseType = typeof(Component);
            // 같은 이름의 타입 중복 방지 (Scripts 리로드 시 이전 어셈블리가 남아있을 수 있음)
            var typeMap = new Dictionary<string, Type>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // 시스템 어셈블리 제외
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
                        if (t == typeof(Transform)) continue; // Transform은 항상 자동 생성
                        // 나중에 로드된 어셈블리(최신 Scripts)가 이전 것을 덮어씀
                        typeMap[t.FullName ?? t.Name] = t;
                    }
                }
                catch
                {
                    // ReflectionTypeLoadException 등 무시
                }
            }

            var types = new List<Type>(typeMap.Values);
            types.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            _cachedComponentTypes = types.ToArray();
            return _cachedComponentTypes;
        }

        // ── Script Drag-Drop ──

        private static void HandleScriptDrop(GameObject target)
        {
            // 드래그가 진행 중일 때만 드롭 존 생성 (평소에는 클릭 이벤트를 방해하지 않음)
            unsafe
            {
                var activePayload = ImGui.GetDragDropPayload();
                if (activePayload.NativePtr == null) return;
            }

            float remainH = ImGui.GetContentRegionAvail().Y;
            if (remainH > 4f)
            {
                ImGui.InvisibleButton("##ScriptDropZone", new System.Numerics.Vector2(-1, remainH));
                if (ImGui.BeginDragDropTarget())
                {
                    unsafe
                    {
                        var payload = ImGui.AcceptDragDropPayload(ImGuiScriptsPanel.DragPayloadType);
                        if (payload.NativePtr != null)
                        {
                            var scriptPath = ImGuiScriptsPanel._draggedScriptPath;
                            if (scriptPath != null)
                            {
                                var compType = ImGuiScriptsPanel.ResolveComponentType(scriptPath);
                                if (compType != null)
                                {
                                    target.AddComponent(compType);
                                    UndoSystem.Record(new AddComponentAction(
                                        $"Add {compType.Name}", target.GetInstanceID(), compType));
                                    SceneManager.GetActiveScene().isDirty = true;
                                    RoseEngine.EditorDebug.Log($"[Scripts] Added component {compType.Name} to {target.name} via Inspector");
                                }
                                else
                                {
                                    RoseEngine.EditorDebug.LogWarning(
                                        $"[Scripts] No Component type found for: {System.IO.Path.GetFileNameWithoutExtension(scriptPath)}");
                                }
                            }
                        }
                    }
                    ImGui.EndDragDropTarget();
                }
            }
        }

        // ── Multi-Object Inspector ──

        private void DrawMultiGameObjectInspector()
        {
            ClearAssetState();

            var ids = EditorSelection.SelectedGameObjectIds;
            var gameObjects = new List<GameObject>(ids.Count);
            foreach (var id in ids)
            {
                var go = UndoUtility.FindGameObjectById(id);
                if (go != null) gameObjects.Add(go);
            }
            if (gameObjects.Count == 0)
            {
                ImGui.TextDisabled("No objects found");
                return;
            }

            ImGui.TextDisabled($"({gameObjects.Count} objects selected)");
            ImGui.SameLine();

            // ── Multi-select Active 체크박스 ──
            bool firstActive = gameObjects[0].activeSelf;
            bool allSame = true;
            for (int i = 1; i < gameObjects.Count; i++)
            {
                if (gameObjects[i].activeSelf != firstActive) { allSame = false; break; }
            }

            // mixed 상태: 체크마크 숨기고 직접 "-" 렌더
            if (!allSame)
                ImGui.PushStyleColor(ImGuiCol.CheckMark,
                    new System.Numerics.Vector4(0, 0, 0, 0));

            bool active = firstActive;
            if (ImGui.Checkbox("Active", ref active))
            {
                var actions = new List<IUndoAction>(gameObjects.Count);
                foreach (var go in gameObjects)
                {
                    bool oldActive = go.activeSelf;
                    go.SetActive(active);
                    actions.Add(new SetActiveAction(
                        $"Toggle Active {go.name}", go.GetInstanceID(), oldActive, active));
                }
                UndoSystem.Record(actions.Count == 1 ? actions[0]
                    : new CompoundUndoAction("Toggle Active", actions));
                SceneManager.GetActiveScene().isDirty = true;
            }

            // mixed → 체크박스 위에 "-" 오버레이
            if (!allSame)
            {
                ImGui.PopStyleColor();
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                float frameH = rectMax.Y - rectMin.Y;
                // 체크박스 사각형 영역 (텍스트 라벨 제외)
                var boxMax = new System.Numerics.Vector2(rectMin.X + frameH, rectMax.Y);
                var dashSize = ImGui.CalcTextSize("-");
                float cx = rectMin.X + (frameH - dashSize.X) * 0.5f;
                float cy = rectMin.Y + (frameH - dashSize.Y) * 0.5f;
                ImGui.GetWindowDrawList().AddText(
                    new System.Numerics.Vector2(cx, cy),
                    ImGui.GetColorU32(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f)),
                    "-");
            }

            ImGui.Separator();

            // Transform — 항상 공유
            if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawMultiTransform(gameObjects);
            }

            // 공유 컴포넌트 교집합
            HashSet<Type>? shared = null;
            foreach (var go in gameObjects)
            {
                var types = new HashSet<Type>();
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is Transform) continue;
                    types.Add(comp.GetType());
                }
                if (shared == null) shared = types;
                else shared.IntersectWith(types);
            }

            if (shared == null || shared.Count == 0) return;

            List<(Type compType, List<Component> components)>? multiComponentsToDelete = null;

            foreach (var compType in shared)
            {
                if (compType == typeof(PrefabInstance)) continue;

                var components = new List<Component>(gameObjects.Count);
                foreach (var go in gameObjects)
                {
                    var comp = go.InternalComponents.FirstOrDefault(c => c.GetType() == compType);
                    if (comp != null) components.Add(comp);
                }
                if (components.Count != gameObjects.Count) continue;

                ImGui.PushID(compType.GetHashCode());

                DrawMultiEnabledCheckbox(components, compType);

                bool open = ImGui.CollapsingHeader(compType.Name, ImGuiTreeNodeFlags.DefaultOpen);

                if (DrawMultiComponentContextMenu(components, compType))
                {
                    multiComponentsToDelete ??= new();
                    multiComponentsToDelete.Add((compType, components));
                }

                ImGui.PopID();

                if (!open) continue;

                DrawMultiAssetReferences(components, compType);
                DrawMultiComponentFields(components, compType);
                DrawMultiComponentProperties(components, compType);
            }

            // Deferred deletion
            if (multiComponentsToDelete != null)
            {
                foreach (var (compType, components) in multiComponentsToDelete)
                {
                    var actions = new List<IUndoAction>(components.Count);
                    foreach (var comp in components)
                    {
                        var serialized = SceneSerializer.SerializeComponent(comp);
                        comp.OnComponentDestroy();
                        comp.gameObject.RemoveComponent(comp);
                        if (serialized != null)
                        {
                            actions.Add(new RemoveComponentAction(
                                $"Remove {compType.Name}", comp.gameObject.GetInstanceID(), serialized));
                        }
                    }
                    if (actions.Count > 0)
                    {
                        UndoSystem.Record(actions.Count == 1 ? actions[0]
                            : new CompoundUndoAction($"Remove {compType.Name}", actions));
                    }
                }
                SceneManager.GetActiveScene().isDirty = true;
            }
        }

        private void DrawMultiTransform(List<GameObject> gameObjects)
        {
            // Position (local, like Unity)
            DrawMultiTransformAxis(gameObjects, "Position",
                go => go.transform.localPosition,
                (go, v) => go.transform.localPosition = v);

            // Rotation (local euler, like Unity)
            DrawMultiTransformAxis(gameObjects, "Rotation",
                go => go.transform.localEulerAngles,
                (go, v) => go.transform.localEulerAngles = v);

            // Scale (local)
            DrawMultiTransformAxis(gameObjects, "Scale",
                go => go.transform.localScale,
                (go, v) => go.transform.localScale = v);
        }

        private void DrawMultiTransformAxis(List<GameObject> gameObjects, string label,
            Func<GameObject, Vector3> getter, Action<GameObject, Vector3> setter)
        {
            var first = getter(gameObjects[0]);
            bool allSame = true;
            for (int i = 1; i < gameObjects.Count; i++)
            {
                var v = getter(gameObjects[i]);
                if (v.x != first.x || v.y != first.y || v.z != first.z) { allSame = false; break; }
            }

            var nv = new System.Numerics.Vector3(first.x, first.y, first.z);
            float speed = label == "Rotation" ? 1f : 0.1f;

            if (!allSame)
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 0.5f));

            string widgetId = $"MultiTransform.{label}";
            if (EditorWidgets.DragFloat3Clickable(widgetId, label, ref nv, speed))
            {
                // Capture old values on first edit
                if (!_multiTransformOld.ContainsKey(widgetId))
                {
                    var old = new Vector3[gameObjects.Count];
                    for (int i = 0; i < gameObjects.Count; i++)
                        old[i] = getter(gameObjects[i]);
                    _multiTransformOld[widgetId] = old;
                }

                var newVal = new Vector3(nv.X, nv.Y, nv.Z);
                foreach (var go in gameObjects)
                    setter(go, newVal);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && _multiTransformOld.Remove(widgetId, out var oldVals))
            {
                var actions = new List<IUndoAction>(gameObjects.Count);
                for (int i = 0; i < gameObjects.Count; i++)
                {
                    var go = gameObjects[i];
                    actions.Add(new SetTransformAction(
                        $"Change {label}", go.GetInstanceID(),
                        label == "Position" ? oldVals[i] : go.transform.localPosition,
                        label == "Rotation" ? RoseEngine.Quaternion.Euler(oldVals[i].x, oldVals[i].y, oldVals[i].z) : go.transform.localRotation,
                        label == "Scale" ? oldVals[i] : go.transform.localScale,
                        go.transform.localPosition, go.transform.localRotation, go.transform.localScale));
                }
                UndoSystem.Record(actions.Count == 1 ? actions[0] : new CompoundUndoAction($"Change {label}", actions));
            }

            if (!allSame)
                ImGui.PopStyleColor();
        }

        private readonly Dictionary<string, Vector3[]> _multiTransformOld = new();

        private void DrawMultiEnabledCheckbox(List<Component> components, Type compType)
        {
            var enabledProp = compType.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            var enabledField = compType.GetField("enabled", BindingFlags.Public | BindingFlags.Instance);
            bool hasEnabled = (enabledProp != null && enabledProp.PropertyType == typeof(bool)
                               && enabledProp.CanRead && enabledProp.CanWrite)
                              || (enabledField != null && enabledField.FieldType == typeof(bool));
            if (!hasEnabled) return;

            bool ReadEnabled(Component c) =>
                enabledProp != null && enabledProp.CanRead
                    ? (bool)enabledProp.GetValue(c)!
                    : (bool)enabledField!.GetValue(c)!;

            bool firstEnabled = ReadEnabled(components[0]);
            bool allSame = true;
            for (int i = 1; i < components.Count; i++)
            {
                if (ReadEnabled(components[i]) != firstEnabled) { allSame = false; break; }
            }

            if (!allSame)
                ImGui.PushStyleColor(ImGuiCol.CheckMark,
                    new System.Numerics.Vector4(0, 0, 0, 0));

            bool enabled = firstEnabled;
            if (ImGui.Checkbox("##multi_enabled", ref enabled))
            {
                var actions = new List<IUndoAction>(components.Count);
                for (int i = 0; i < components.Count; i++)
                {
                    bool oldEnabled = ReadEnabled(components[i]);
                    if (enabledProp != null && enabledProp.CanWrite)
                        enabledProp.SetValue(components[i], enabled);
                    else
                        enabledField!.SetValue(components[i], enabled);
                    actions.Add(new SetPropertyAction(
                        $"Toggle {compType.Name}", components[i].gameObject.GetInstanceID(),
                        compType.Name, "enabled", oldEnabled, enabled));
                }
                UndoSystem.Record(actions.Count == 1 ? actions[0]
                    : new CompoundUndoAction($"Toggle {compType.Name}", actions));
                SceneManager.GetActiveScene().isDirty = true;
            }

            if (!allSame)
            {
                ImGui.PopStyleColor();
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                float frameH = rectMax.Y - rectMin.Y;
                var dashSize = ImGui.CalcTextSize("-");
                float cx = rectMin.X + (frameH - dashSize.X) * 0.5f;
                float cy = rectMin.Y + (frameH - dashSize.Y) * 0.5f;
                ImGui.GetWindowDrawList().AddText(
                    new System.Numerics.Vector2(cx, cy),
                    ImGui.GetColorU32(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f)),
                    "-");
            }

            ImGui.SameLine();
        }

        private bool DrawMultiComponentContextMenu(List<Component> components, Type compType)
        {
            bool requestDelete = false;

            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Copy"))
                {
                    _clipboardComponent = SceneSerializer.SerializeComponent(components[^1]);
                }

                bool canPasteValues = _clipboardComponent != null &&
                    _clipboardComponent.TryGetValue("type", out var ctv) &&
                    ctv?.ToString() == compType.Name;

                if (!canPasteValues) ImGui.BeginDisabled();
                if (ImGui.MenuItem("Paste Values"))
                {
                    PasteMultiComponentValues(components, _clipboardComponent!);
                }
                if (!canPasteValues) ImGui.EndDisabled();

                ImGui.Separator();

                if (ImGui.MenuItem("Delete"))
                {
                    requestDelete = true;
                }

                ImGui.EndPopup();
            }

            return requestDelete;
        }

        private void DrawMultiComponentFields(List<Component> components, Type type)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                if (field.Name.StartsWith("_is") || field.Name == "gameObject" || field.Name == "enabled") continue;
                if (AssetNameExtractors.ContainsKey(field.FieldType)) continue;

                bool isPublic = field.IsPublic;
                bool hasSerialize = field.GetCustomAttribute<SerializeFieldAttribute>() != null;
                bool hasHide = field.GetCustomAttribute<HideInInspectorAttribute>() != null;

                if (!isPublic && !hasSerialize) continue;
                if (hasHide) continue;

                var header = field.GetCustomAttribute<HeaderAttribute>();
                if (header != null)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f), header.header);
                }

                var range = field.GetCustomAttribute<RangeAttribute>();
                var tooltip = field.GetCustomAttribute<TooltipAttribute>();
                bool readOnly = field.GetCustomAttribute<ReadOnlyInInspectorAttribute>() != null;
                var intDropdown = field.GetCustomAttribute<IntDropdownAttribute>();

                DrawMultiValue(components, field.Name, field.FieldType,
                    c => field.GetValue(c),
                    (c, v) => field.SetValue(c, v),
                    range, tooltip, readOnly, intDropdown);
            }
        }

        private void DrawMultiComponentProperties(List<Component> components, Type type)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.GetMethod?.IsStatic == true) continue;
                if (SkipPropertyNames.Contains(prop.Name)) continue;
                if (SkipPropertyTypes.Contains(prop.PropertyType)) continue;
                if (prop.GetCustomAttribute<HideInInspectorAttribute>() != null) continue;
                if (!IsSupportedType(prop.PropertyType)) continue;

                var range = prop.GetCustomAttribute<RangeAttribute>();
                var tooltip = prop.GetCustomAttribute<TooltipAttribute>();
                bool readOnly = prop.GetCustomAttribute<ReadOnlyInInspectorAttribute>() != null;
                var intDropdown = prop.GetCustomAttribute<IntDropdownAttribute>();

                DrawMultiValue(components, prop.Name, prop.PropertyType,
                    c => prop.GetValue(c),
                    (c, v) => prop.SetValue(c, v),
                    range, tooltip, readOnly, intDropdown);
            }
        }

        private void DrawMultiAssetReferences(List<Component> components, Type compType)
        {
            foreach (var field in compType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!AssetNameExtractors.TryGetValue(field.FieldType, out var extractor)) continue;
                DrawMultiPingableAsset(components, field.Name, field.FieldType, extractor,
                    c => field.GetValue(c),
                    field.FieldType == typeof(Material)
                        ? (c, v) => field.SetValue(c, v)
                        : null);
            }

            foreach (var prop in compType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (SkipPropertyNames.Contains(prop.Name)) continue;
                if (!AssetNameExtractors.TryGetValue(prop.PropertyType, out var extractor)) continue;
                DrawMultiPingableAsset(components, prop.Name, prop.PropertyType, extractor,
                    c => prop.GetValue(c),
                    (prop.PropertyType == typeof(Material) && prop.CanWrite)
                        ? (c, v) => prop.SetValue(c, v)
                        : null);
            }
        }

        private void DrawMultiPingableAsset(
            List<Component> components, string memberName, Type memberType,
            Func<object, string?> nameExtractor,
            Func<Component, object?> getter,
            Action<Component, object?>? materialSetter)
        {
            var values = new object?[components.Count];
            for (int i = 0; i < components.Count; i++)
                values[i] = getter(components[i]);

            bool allSame = true;
            for (int i = 1; i < values.Length; i++)
            {
                if (!ReferenceEquals(values[0], values[i])) { allSame = false; break; }
            }

            var primaryVal = values[^1];
            string displayName = primaryVal != null ? (nameExtractor(primaryVal) ?? "(None)") : "(None)";

            if (!allSame)
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 0.5f));

            ImGui.Text(memberName);
            ImGui.SameLine();

            var assetPath = FindAssetPath(primaryVal) ?? FindAssetPathByName(
                primaryVal != null ? nameExtractor(primaryVal) : null);

            if (assetPath != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, allSame
                    ? new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f)
                    : new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 0.5f));
                if (ImGui.Selectable($"{displayName}##{memberName}"))
                    EditorBridge.PingAsset(assetPath);
                ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(allSame ? assetPath : $"{assetPath} (mixed)");
            }
            else
            {
                ImGui.Button($"{displayName}##{memberName}", new System.Numerics.Vector2(
                    ImGui.GetContentRegionAvail().X, 0));
            }

            // Material drag-drop → apply to ALL components
            if (materialSetter != null && memberType == typeof(Material))
            {
                if (ImGui.BeginDragDropTarget())
                {
                    unsafe
                    {
                        var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                        if (payload.NativePtr != null)
                        {
                            var droppedPath = ImGuiProjectPanel._draggedAssetPath;
                            if (!string.IsNullOrEmpty(droppedPath))
                            {
                                bool isMaterial = droppedPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
                                if (!isMaterial && SubAssetPath.TryParse(droppedPath, out _, out var subType, out _))
                                    isMaterial = subType == "Material";

                                if (isMaterial)
                                {
                                    var db = Resources.GetAssetDatabase();
                                    var newMat = db?.Load<Material>(droppedPath);
                                    if (newMat != null)
                                    {
                                        var actions = new List<IUndoAction>(components.Count);
                                        for (int i = 0; i < components.Count; i++)
                                        {
                                            var oldVal = values[i];
                                            materialSetter(components[i], newMat);
                                            actions.Add(new SetPropertyAction(
                                                $"Set {memberName}",
                                                components[i].gameObject.GetInstanceID(),
                                                components[i].GetType().Name,
                                                memberName, oldVal, newMat));
                                        }
                                        UndoSystem.Record(actions.Count == 1 ? actions[0]
                                            : new CompoundUndoAction($"Set {memberName}", actions));
                                        SceneManager.GetActiveScene().isDirty = true;
                                    }
                                }
                            }
                        }
                    }
                    ImGui.EndDragDropTarget();
                }
            }

            if (!allSame)
                ImGui.PopStyleColor();
        }

        private void DrawMultiValue(List<Component> components, string label, Type valueType,
            Func<Component, object?> getter, Action<Component, object> setter,
            RangeAttribute? range, TooltipAttribute? tooltip, bool readOnly,
            IntDropdownAttribute? intDropdown = null)
        {
            // 모든 컴포넌트에서 값 읽기
            var values = new object?[components.Count];
            for (int i = 0; i < components.Count; i++)
                values[i] = getter(components[i]);

            // 전부 동일한지 검사
            bool allSame = true;
            for (int i = 1; i < values.Length; i++)
            {
                if (!Equals(values[0], values[i])) { allSame = false; break; }
            }

            string compTypeName = components[0].GetType().Name;
            string widgetId = $"Multi.{compTypeName}.{label}";

            if (!allSame)
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 0.5f));

            try
            {
                if (readOnly) ImGui.BeginDisabled();

                // Primary 값 (마지막 컴포넌트 = Primary GO)
                object? val = values[^1];
                bool changed = false;
                object? newVal = null;
                bool pendingSliderDeactivation = false;

                if (valueType == typeof(float))
                {
                    float f = (float)(val ?? 0f);
                    if (range != null)
                    {
                        changed = EditorWidgets.SliderFloatWithInput(widgetId, label, ref f, range.min, range.max, out pendingSliderDeactivation);
                    }
                    else
                        changed = EditorWidgets.DragFloatClickable(widgetId, label, ref f, 0.01f, "%.2f");
                    if (changed) newVal = f;
                }
                else if (valueType == typeof(int) && intDropdown != null)
                {
                    int iv = (int)(val ?? 0);
                    int current = Array.IndexOf(intDropdown.values, iv);
                    if (current < 0) current = 0;
                    string preview = intDropdown.labels[current];
                    float previewW = ImGui.CalcTextSize(preview).X;
                    float comboW = ImGui.CalcItemWidth();
                    float pad = (comboW - previewW) * 0.5f;
                    string centeredPreview = pad > 0 ? new string(' ', Math.Max(1, (int)(pad / ImGui.CalcTextSize(" ").X))) + preview : preview;
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    if (ImGui.BeginCombo(wLabel, centeredPreview))
                    {
                        float popupW = ImGui.GetContentRegionAvail().X;
                        for (int idx = 0; idx < intDropdown.labels.Length; idx++)
                        {
                            bool selected = idx == current;
                            float textW = ImGui.CalcTextSize(intDropdown.labels[idx]).X;
                            float offset = (popupW - textW) * 0.5f;
                            if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                            if (ImGui.Selectable(intDropdown.labels[idx], selected))
                            {
                                var parsed = intDropdown.values[idx];
                                var actions = new List<IUndoAction>(components.Count);
                                for (int i = 0; i < components.Count; i++)
                                {
                                    var oldV = values[i];
                                    setter(components[i], parsed);
                                    actions.Add(new SetPropertyAction(
                                        $"Change {label}", components[i].gameObject.GetInstanceID(),
                                        compTypeName, label, oldV, parsed));
                                }
                                UndoSystem.Record(actions.Count == 1 ? actions[0] : new CompoundUndoAction($"Change {label}", actions));
                            }
                            if (selected) ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                }
                else if (valueType == typeof(int))
                {
                    int iv = (int)(val ?? 0);
                    if (range != null)
                    {
                        changed = EditorWidgets.SliderIntWithInput(widgetId, label, ref iv, (int)range.min, (int)range.max, out pendingSliderDeactivation);
                    }
                    else
                        changed = EditorWidgets.DragIntClickable(widgetId, label, ref iv);
                    if (changed) newVal = iv;
                }
                else if (valueType == typeof(bool))
                {
                    bool b = (bool)(val ?? false);
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    if (ImGui.Checkbox(wLabel, ref b))
                    {
                        // Bool: 즉시 적용 + 즉시 undo
                        var actions = new List<IUndoAction>(components.Count);
                        for (int i = 0; i < components.Count; i++)
                        {
                            var oldV = values[i];
                            setter(components[i], b);
                            actions.Add(new SetPropertyAction(
                                $"Change {label}", components[i].gameObject.GetInstanceID(),
                                compTypeName, label, oldV, b));
                        }
                        UndoSystem.Record(actions.Count == 1 ? actions[0] : new CompoundUndoAction($"Change {label}", actions));
                        NotifyRecordChange(components[^1], label, b);
                    }
                }
                else if (valueType == typeof(string))
                {
                    string s = (string)(val ?? "");
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    changed = ImGui.InputText(wLabel, ref s, 256);
                    if (changed) newVal = s;
                }
                else if (valueType == typeof(Vector3))
                {
                    var v = (Vector3)(val ?? Vector3.zero);
                    var nv = new System.Numerics.Vector3(v.x, v.y, v.z);
                    changed = EditorWidgets.DragFloat3Clickable(widgetId, label, ref nv, 0.1f);
                    if (changed) newVal = new Vector3(nv.X, nv.Y, nv.Z);
                }
                else if (valueType == typeof(Color))
                {
                    var c = (Color)(val ?? Color.white);
                    changed = EditorWidgets.ColorEdit4(label, ref c, out bool colorDeactivated);
                    if (changed) newVal = c;
                    // ColorEdit4는 내부적으로 여러 ImGui 아이템을 submit 하므로,
                    // 외부의 ImGui.IsItemDeactivatedAfterEdit()로는 picker 팝업의
                    // 편집 종료를 감지할 수 없다. out 신호를 슬라이더 경로와 동일하게 병합.
                    if (colorDeactivated) pendingSliderDeactivation = true;
                }
                else if (valueType.IsEnum)
                {
                    var names = Enum.GetNames(valueType);
                    int current = Array.IndexOf(names, val?.ToString() ?? "");
                    if (current < 0) current = 0;
                    string preview = names[current];
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    float previewW = ImGui.CalcTextSize(preview).X;
                    float comboW = ImGui.CalcItemWidth();
                    float pad = (comboW - previewW) * 0.5f;
                    string centeredPreview = pad > 0 ? new string(' ', Math.Max(1, (int)(pad / ImGui.CalcTextSize(" ").X))) + preview : preview;
                    if (ImGui.BeginCombo(wLabel, centeredPreview))
                    {
                        float popupW = ImGui.GetContentRegionAvail().X;
                        for (int idx = 0; idx < names.Length; idx++)
                        {
                            bool selected = idx == current;
                            float textW = ImGui.CalcTextSize(names[idx]).X;
                            float offset = (popupW - textW) * 0.5f;
                            if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                            if (ImGui.Selectable(names[idx], selected))
                            {
                                var parsed = Enum.Parse(valueType, names[idx]);
                                var actions = new List<IUndoAction>(components.Count);
                                for (int i = 0; i < components.Count; i++)
                                {
                                    var oldV = values[i];
                                    setter(components[i], parsed);
                                    actions.Add(new SetPropertyAction(
                                        $"Change {label}", components[i].gameObject.GetInstanceID(),
                                        compTypeName, label, oldV, parsed));
                                }
                                UndoSystem.Record(actions.Count == 1 ? actions[0] : new CompoundUndoAction($"Change {label}", actions));
                            }
                            if (selected) ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                }
                else
                {
                    EditorWidgets.BeginPropertyRow(label);
                    ImGui.TextDisabled($"{val}");
                }

                bool isDeactivated = ImGui.IsItemDeactivated();
                bool isDeactivatedAfterEdit = ImGui.IsItemDeactivatedAfterEdit();

                // Drag/continuous 타입: BeginEdit → setter all → EndEdit
                if (changed && newVal != null && valueType != typeof(bool) && !valueType.IsEnum)
                {
                    if (!_multiEditOld.ContainsKey(widgetId))
                    {
                        _multiEditOld[widgetId] = (object?[])values.Clone();
                    }
                    foreach (var comp in components)
                        setter(comp, newVal);

                    // Record mode 통지 (primary component)
                    NotifyRecordChange(components[^1], label, newVal);
                }

                // allSame=false 상태에서 유저가 위젯 편집 종료 시,
                // primary 값이 안 바뀌어도 모든 컴포넌트에 통일 적용
                if (!allSame && isDeactivated && !changed
                    && valueType != typeof(bool) && !valueType.IsEnum
                    && !_multiEditOld.ContainsKey(widgetId))
                {
                    object? currentVal = val;
                    if (currentVal != null)
                    {
                        var actions = new List<IUndoAction>(components.Count);
                        for (int i = 0; i < components.Count; i++)
                        {
                            var oldV = values[i];
                            if (!Equals(oldV, currentVal))
                            {
                                setter(components[i], currentVal);
                                actions.Add(new SetPropertyAction(
                                    $"Change {label}", components[i].gameObject.GetInstanceID(),
                                    compTypeName, label, oldV, currentVal));
                            }
                        }
                        if (actions.Count > 0)
                            UndoSystem.Record(actions.Count == 1 ? actions[0]
                                : new CompoundUndoAction($"Change {label}", actions));
                    }
                }

                if ((isDeactivatedAfterEdit || pendingSliderDeactivation) && _multiEditOld.Remove(widgetId, out var oldValues))
                {
                    var actions = new List<IUndoAction>(components.Count);
                    for (int i = 0; i < components.Count; i++)
                    {
                        actions.Add(new SetPropertyAction(
                            $"Change {label}", components[i].gameObject.GetInstanceID(),
                            compTypeName, label, oldValues[i], getter(components[i])));
                    }
                    UndoSystem.Record(actions.Count == 1 ? actions[0] : new CompoundUndoAction($"Change {label}", actions));
                }

                if (tooltip != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(tooltip.tooltip);

                if (readOnly) ImGui.EndDisabled();
            }
            catch (Exception ex)
            {
                if (readOnly) ImGui.EndDisabled();
                RoseEngine.EditorDebug.LogError($"[MultiValue] EXCEPTION in {label}: {ex}");
                ImGui.TextDisabled($"{label}: (error)");
            }

            if (!allSame)
                ImGui.PopStyleColor();
        }

        private readonly Dictionary<string, object?[]> _multiEditOld = new();

        // ── Record mode notification ──

        private void NotifyRecordChange(Component comp, string label, object newVal)
        {
            if (AnimEditor?.IsRecording != true) return;
            var go = comp.gameObject;
            var compType = comp.GetType().Name;

            switch (newVal)
            {
                case float f:
                    AnimEditor.RecordProperty(go, compType, label, f);
                    break;
                case int i:
                    AnimEditor.RecordProperty(go, compType, label, (float)i);
                    break;
                case bool b:
                    AnimEditor.RecordProperty(go, compType, label, b ? 1f : 0f);
                    break;
                case Vector3 v:
                    AnimEditor.RecordProperty(go, compType, label, v.x, "x");
                    AnimEditor.RecordProperty(go, compType, label, v.y, "y");
                    AnimEditor.RecordProperty(go, compType, label, v.z, "z");
                    break;
                case Color c:
                    AnimEditor.RecordProperty(go, compType, label, c.r, "r");
                    AnimEditor.RecordProperty(go, compType, label, c.g, "g");
                    AnimEditor.RecordProperty(go, compType, label, c.b, "b");
                    AnimEditor.RecordProperty(go, compType, label, c.a, "a");
                    break;
            }
            AnimEditor.FlushRecordUndo();
        }

        // ── Asset Inspector ──

        private void ClearAssetState()
        {
            if (_currentAssetPath != null)
            {
                _currentAssetPath = null;
                _assetMeta = null;
                _editedImporter = null;
                _hasChanges = false;
                _editingAssetPaths.Clear();
                _mixedImporterKeys.Clear();
            }
            ClearPreviewCache();
        }

        private void ClearPreviewCache()
        {
            _previewCachedPath = null;
            _previewTextureId = IntPtr.Zero;
            _meshPreviewTextureId = IntPtr.Zero;
            _previewType = PreviewType.None;
            // MeshPreviewRenderer는 재사용 (GPU 리소스 비용이 높으므로 매번 재생성하지 않음)
        }

        // ── Prefab Variant Override 비교 ──

        /// <summary>
        /// Variant 편집 중이면 base 프리팹 템플릿을 로드/캐시.
        /// 편집 중이 아니거나 base가 아니면 캐시 클리어.
        /// </summary>
        private void RefreshBaseTemplate()
        {
            if (!EditorState.IsEditingPrefab || string.IsNullOrEmpty(EditorState.EditingPrefabPath))
            {
                _variantBaseGuid = null;
                _baseTemplateGOs = null;
                return;
            }

            var baseGuid = PrefabImporter.GetBasePrefabGuidFromFile(EditorState.EditingPrefabPath);
            if (baseGuid == null)
            {
                // Base 프리팹 편집 — override 비교 불필요
                _variantBaseGuid = null;
                _baseTemplateGOs = null;
                return;
            }

            // 캐시 유효성 확인
            if (baseGuid == _variantBaseGuid && _baseTemplateGOs != null)
                return;

            _variantBaseGuid = baseGuid;
            _baseTemplateGOs = null;

            var db = Resources.GetAssetDatabase();
            var basePath = db?.GetPathFromGuid(baseGuid);
            if (basePath == null) return;

            var importer = new PrefabImporter(db!);
            var baseRoot = importer.LoadPrefab(basePath);
            if (baseRoot == null) return;

            _baseTemplateGOs = new List<GameObject>();
            PrefabUtility.CollectHierarchy(baseRoot, _baseTemplateGOs);
        }

        /// <summary>
        /// 현재 GO에 대응하는 base GO를 찾아 반환.
        /// hierarchy 내 인덱스로 매핑.
        /// </summary>
        private GameObject? FindBaseGameObject(GameObject current)
        {
            if (_baseTemplateGOs == null) return null;

            // 현재 씬의 전체 hierarchy를 수집하여 인덱스 매핑
            var allGOs = new List<GameObject>();
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isEditorInternal && !go._isDestroyed && go.transform.parent == null)
                {
                    PrefabUtility.CollectHierarchy(go, allGOs);
                    break; // 프리팹 편집은 루트 1개
                }
            }

            int idx = allGOs.IndexOf(current);
            if (idx < 0 || idx >= _baseTemplateGOs.Count) return null;
            return _baseTemplateGOs[idx];
        }

        /// <summary>
        /// 현재 컴포넌트에 대응하는 base 컴포넌트를 타입+순서로 매핑하여 반환.
        /// </summary>
        private static Component? FindBaseComponent(Component current, GameObject baseGo)
        {
            var typeName = current.GetType().Name;
            int order = 0;
            foreach (var c in current.gameObject.InternalComponents)
            {
                if (c == current) break;
                if (c.GetType().Name == typeName) order++;
            }

            int count = 0;
            foreach (var c in baseGo.InternalComponents)
            {
                if (c.GetType().Name == typeName)
                {
                    if (count == order) return c;
                    count++;
                }
            }
            return null;
        }

        /// <summary>
        /// 두 값이 동일한지 비교 (float epsilon 허용).
        /// SceneSerializer.ValuesEqual과 동일한 로직.
        /// </summary>
        private static bool OverrideValuesEqual(object? a, object? b, Type type)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (type == typeof(float)) return MathF.Abs((float)a - (float)b) < 1e-4f;
            if (type == typeof(Vector3))
            {
                var va = (Vector3)a; var vb = (Vector3)b;
                return MathF.Abs(va.x - vb.x) < 1e-4f && MathF.Abs(va.y - vb.y) < 1e-4f && MathF.Abs(va.z - vb.z) < 1e-4f;
            }
            if (type == typeof(Vector2))
            {
                var va = (Vector2)a; var vb = (Vector2)b;
                return MathF.Abs(va.x - vb.x) < 1e-4f && MathF.Abs(va.y - vb.y) < 1e-4f;
            }
            if (type == typeof(Quaternion))
            {
                var qa = (Quaternion)a; var qb = (Quaternion)b;
                return MathF.Abs(qa.x - qb.x) < 1e-4f && MathF.Abs(qa.y - qb.y) < 1e-4f
                    && MathF.Abs(qa.z - qb.z) < 1e-4f && MathF.Abs(qa.w - qb.w) < 1e-4f;
            }
            if (type == typeof(Vector4))
            {
                var va = (Vector4)a; var vb = (Vector4)b;
                return MathF.Abs(va.x - vb.x) < 1e-4f && MathF.Abs(va.y - vb.y) < 1e-4f
                    && MathF.Abs(va.z - vb.z) < 1e-4f && MathF.Abs(va.w - vb.w) < 1e-4f;
            }
            if (type == typeof(Color))
            {
                var ca = (Color)a; var cb = (Color)b;
                return MathF.Abs(ca.r - cb.r) < 1e-4f && MathF.Abs(ca.g - cb.g) < 1e-4f
                    && MathF.Abs(ca.b - cb.b) < 1e-4f && MathF.Abs(ca.a - cb.a) < 1e-4f;
            }
            if (type == typeof(double)) return Math.Abs((double)a - (double)b) < 1e-4;
            return a.Equals(b);
        }

        /// <summary>GPU 프리뷰 리소스 해제. 독립 PropertyWindow 닫힐 때 호출.</summary>
        internal void DisposePreviews()
        {
            _meshPreview?.Dispose();
            _meshPreview = null;
            ClearPreviewCache();
        }

        internal void DrawAssetInspector(string assetPath)
        {
            // 서브에셋 경로인 경우 별도 처리
            if (SubAssetPath.IsSubAssetPath(assetPath))
            {
                DrawSubAssetInspector(assetPath);
                return;
            }

            // 선택 에셋이 바뀌거나, 외부(CLI 등)에서 reimport가 발생하면 메타 재로드
            var curReimportVer = Resources.GetAssetDatabase()?.ReimportVersion ?? 0;
            bool assetChanged = _currentAssetPath != assetPath;
            bool externalReimport = !assetChanged && !_hasChanges
                                    && _previewReimportVersion != curReimportVer;
            if (assetChanged || externalReimport)
            {
                _currentAssetPath = assetPath;
                _assetMeta = RoseMetadata.LoadOrCreate(assetPath);
                _editedImporter = CloneTomlTable(_assetMeta.importer);
                _hasChanges = false;

                // Material 에셋이면 .mat TOML도 로드
                var iType = _assetMeta.importer.TryGetValue("type", out var tv) ? tv?.ToString() ?? "" : "";
                if (iType == "MaterialImporter")
                {
                    _matFilePath = assetPath;
                    _editedMatTable = Toml.ToModel(File.ReadAllText(assetPath));
                    _matEditVersionLocal = MaterialPropertyUndoAction.GlobalVersion;
                }
                else
                {
                    _editedMatTable = null;
                    _matFilePath = null;
                }

                // 같은 importer type인 선택 에셋 목록 + mixed 키 계산
                _editingAssetPaths.Clear();
                _editingAssetPaths.Add(assetPath);
                _mixedImporterKeys.Clear();
                if (_allSelectedAssetPaths != null && _allSelectedAssetPaths.Count > 1)
                {
                    var otherImporters = new List<TomlTable>();
                    foreach (var path in _allSelectedAssetPaths)
                    {
                        if (path == assetPath || SubAssetPath.IsSubAssetPath(path)) continue;
                        var meta = RoseMetadata.LoadOrCreate(path);
                        var type = meta.importer.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "";
                        if (type == iType)
                        {
                            _editingAssetPaths.Add(path);
                            otherImporters.Add(meta.importer);
                        }
                    }
                    // primary의 각 키에 대해 다른 에셋과 비교
                    foreach (var kvp in _assetMeta.importer)
                    {
                        if (kvp.Key == "type") continue;
                        foreach (var other in otherImporters)
                        {
                            if (!other.TryGetValue(kvp.Key, out var otherVal)
                                || !Equals(kvp.Value, otherVal))
                            {
                                _mixedImporterKeys.Add(kvp.Key);
                                break;
                            }
                        }
                    }
                }
            }

            if (_assetMeta == null || _editedImporter == null) return;

            var importerType = _editedImporter.TryGetValue("type", out var typeVal)
                ? typeVal?.ToString() ?? "" : "";

            // Undo/Redo에 의해 .mat 파일이 변경되었으면 다시 읽기
            if (importerType == "MaterialImporter" && _matEditVersionLocal != MaterialPropertyUndoAction.GlobalVersion)
            {
                _matEditVersionLocal = MaterialPropertyUndoAction.GlobalVersion;
                if (_matFilePath != null && File.Exists(_matFilePath))
                    _editedMatTable = Toml.ToModel(File.ReadAllText(_matFilePath));
                ClearPreviewCache();
            }

            // Asset Info
            if (ImGui.CollapsingHeader("Asset Info", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (_editingAssetPaths.Count > 1)
                {
                    ImGui.TextDisabled($"({_editingAssetPaths.Count} assets selected)");
                }

                ImGui.Text("Path");
                ImGui.SameLine(80);
                ImGui.TextWrapped(assetPath);

                ImGui.Text("Type");
                ImGui.SameLine(80);
                ImGui.Text(importerType);

                ImGui.Text("GUID");
                ImGui.SameLine(80);
                ImGui.TextDisabled(_assetMeta.guid);
            }

            if (importerType == "MaterialImporter")
            {
                // Material 에셋: Import Settings 대신 Material 속성 편집 UI
                if (_editedMatTable != null)
                    DrawMaterialEditor(assetPath);
            }
            else if (importerType == "PostProcessProfileImporter")
            {
                DrawPostProcessProfileEditor(assetPath);
            }
            else if (importerType == "RendererProfileImporter")
            {
                DrawRendererProfileEditor(assetPath);
            }
            else
            {
                // Import Settings
                if (ImGui.CollapsingHeader("Import Settings", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    switch (importerType)
                    {
                        case "MeshImporter":
                            DrawMeshImporterSettings();
                            break;
                        case "TextureImporter":
                            DrawTextureImporterSettings();
                            break;
                        case "FontImporter":
                            DrawFontImporterSettings();
                            break;
                        case "AnimationClipImporter":
                            DrawAnimationClipInfo();
                            break;
                        case "PrefabImporter":
                            ImGui.TextDisabled("No editable settings");
                            break;
                        default:
                            ImGui.TextDisabled($"Unknown importer: {importerType}");
                            break;
                    }
                }

                // Apply / Revert (Material은 즉시 적용이므로 제외)
                ImGui.Separator();
                ImGui.Spacing();

                bool disabled = !_hasChanges;
                if (disabled) ImGui.BeginDisabled();

                string applyLabel = _editingAssetPaths.Count > 1
                    ? $"Apply ({_editingAssetPaths.Count})"
                    : "Apply";
                if (ImGui.Button(applyLabel))
                {
                    ApplyChanges();
                }
                ImGui.SameLine();
                if (ImGui.Button("Revert"))
                {
                    RevertChanges();
                }

                if (disabled) ImGui.EndDisabled();
            }

            // Preview
            DrawAssetPreview(assetPath, importerType);
        }

        private void DrawSubAssetInspector(string assetPath)
        {
            _currentAssetPath = assetPath;
            _assetMeta = null;
            _editedImporter = null;
            _hasChanges = false;

            SubAssetPath.TryParse(assetPath, out var parentPath, out var subType, out var subIndex);

            // Sub-Asset Info
            if (ImGui.CollapsingHeader("Sub-Asset Info", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Text("Type");
                ImGui.SameLine(80);
                ImGui.Text(subType);

                ImGui.Text("Index");
                ImGui.SameLine(80);
                ImGui.Text(subIndex.ToString());

                ImGui.Text("Source");
                ImGui.SameLine(80);
                ImGui.TextWrapped(parentPath);
            }

            // Material sub-asset: 독립 .mat과 동일한 레이아웃으로 readonly 표시
            if (subType == "Material")
            {
                var db = Resources.GetAssetDatabase();
                var mat = db?.Load<Material>(assetPath);
                if (mat != null)
                    DrawReadOnlyMaterialInspector(mat);
            }

            // Preview (importerType은 서브에셋 타입으로 대체)
            DrawAssetPreview(assetPath, subType);
        }

        private void DrawMeshImporterSettings()
        {
            DrawImporterFloat("scale", 1.0f, 0.01f);
            DrawImporterBool("generate_normals", true);
            DrawImporterBool("flip_uvs", true);
            DrawImporterBool("triangulate", true);
            DrawImporterBool("generate_mipmesh", false);
            DrawImporterInt("mipmesh_min_triangles", 500, 50, 5000);
            DrawImporterFloat("mipmesh_target_error", 0.02f, 0.001f);
            DrawImporterFloat("mipmesh_reduction", 0.1f, 0.01f);

            // MipMesh LOD 정보 표시
            bool mipmeshEnabled = false;
            if (_editedImporter != null && _editedImporter.TryGetValue("generate_mipmesh", out var mmVal) && mmVal is bool mm)
                mipmeshEnabled = mm;

            if (mipmeshEnabled && _currentAssetPath != null)
            {
                var mipMesh = Resources.GetAssetDatabase()?.Load<MipMesh>(_currentAssetPath);
                if (mipMesh != null && mipMesh.LodCount > 1)
                {
                    ImGui.Spacing();
                    ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                    bool mipMeshOpen = ImGui.TreeNodeEx($"MipMesh ({mipMesh.LodCount} LODs)", ImGuiTreeNodeFlags.None);
                    ImGui.PopStyleColor();

                    if (mipMeshOpen)
                    {
                        if (ImGui.BeginTable("##MipMeshLODs", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                        {
                            ImGui.TableSetupColumn("LOD", ImGuiTableColumnFlags.WidthFixed, 40);
                            ImGui.TableSetupColumn("Vertices", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Triangles", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableHeadersRow();

                            for (int i = 0; i < mipMesh.LodCount; i++)
                            {
                                var lod = mipMesh.lodMeshes[i];
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.Text($"{i}");
                                ImGui.TableNextColumn();
                                ImGui.Text($"{lod.vertices.Length:N0}");
                                ImGui.TableNextColumn();
                                ImGui.Text($"{lod.indices.Length / 3:N0}");
                            }

                            ImGui.EndTable();
                        }
                        ImGui.TreePop();
                    }
                }
            }

            // Sub-Assets 정보 표시
            DrawSubAssetTable();
        }

        private void DrawSubAssetTable()
        {
            if (_currentAssetPath == null || _assetMeta == null) return;
            if (_assetMeta.subAssets.Count == 0) return;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
            bool subAssetsOpen = ImGui.TreeNodeEx($"Sub-Assets ({_assetMeta.subAssets.Count})", ImGuiTreeNodeFlags.None);
            ImGui.PopStyleColor();

            if (subAssetsOpen)
            {
                if (ImGui.BeginTable("##SubAssets", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
                {
                    ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed, 40);
                    ImGui.TableSetupColumn("GUID", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableHeadersRow();

                    foreach (var sub in _assetMeta.subAssets)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.Text(sub.type);

                        ImGui.TableNextColumn();
                        ImGui.Text(sub.name);

                        ImGui.TableNextColumn();
                        ImGui.Text($"{sub.index}");

                        ImGui.TableNextColumn();
                        // GUID 앞 8자리만 표시, 호버 시 전체 GUID
                        var shortGuid = sub.guid.Length > 8 ? sub.guid[..8] + "..." : sub.guid;
                        ImGui.TextDisabled(shortGuid);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(sub.guid);
                    }

                    ImGui.EndTable();
                }
                ImGui.TreePop();
            }
        }

        private void DrawAnimationClipInfo()
        {
            if (_currentAssetPath == null) return;

            var clip = Resources.GetAssetDatabase()?.Load<AnimationClip>(_currentAssetPath);
            if (clip == null)
            {
                ImGui.TextDisabled("Failed to load AnimationClip");
                return;
            }

            float labelWidth = 100;

            ImGui.Text("Length");
            ImGui.SameLine(labelWidth);
            ImGui.Text($"{clip.length:F3} s");

            int totalFrames = (int)(clip.length * clip.frameRate);
            ImGui.Text("Frames");
            ImGui.SameLine(labelWidth);
            ImGui.Text($"{totalFrames}");

            ImGui.Text("Frame Rate");
            ImGui.SameLine(labelWidth);
            ImGui.Text($"{clip.frameRate:F0} fps");

            ImGui.Text("Wrap Mode");
            ImGui.SameLine(labelWidth);
            ImGui.Text(clip.wrapMode.ToString());

            ImGui.Text("Curves");
            ImGui.SameLine(labelWidth);
            ImGui.Text($"{clip.curves.Count}");

            ImGui.Text("Events");
            ImGui.SameLine(labelWidth);
            ImGui.Text($"{clip.events.Count}");

            // Animated Properties 목록
            if (clip.curves.Count > 0)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.TreeNodeEx($"Animated Properties ({clip.curves.Count})", ImGuiTreeNodeFlags.None))
                {
                    if (ImGui.BeginTable("##AnimProps", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Keys", ImGuiTableColumnFlags.WidthFixed, 50);
                        ImGui.TableSetupColumn("Range", ImGuiTableColumnFlags.WidthFixed, 120);
                        ImGui.TableHeadersRow();

                        foreach (var kvp in clip.curves)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(kvp.Key);
                            ImGui.TableNextColumn();
                            ImGui.Text($"{kvp.Value.length}");
                            ImGui.TableNextColumn();
                            if (kvp.Value.length > 0)
                            {
                                float minVal = float.MaxValue, maxVal = float.MinValue;
                                for (int i = 0; i < kvp.Value.length; i++)
                                {
                                    float v = kvp.Value[i].value;
                                    if (v < minVal) minVal = v;
                                    if (v > maxVal) maxVal = v;
                                }
                                ImGui.TextDisabled($"{minVal:F2} ~ {maxVal:F2}");
                            }
                            else
                            {
                                ImGui.TextDisabled("-");
                            }
                        }

                        ImGui.EndTable();
                    }
                    ImGui.TreePop();
                }
            }

            // Events 목록
            if (clip.events.Count > 0)
            {
                ImGui.Spacing();

                if (ImGui.TreeNodeEx($"Animation Events ({clip.events.Count})", ImGuiTreeNodeFlags.None))
                {
                    if (ImGui.BeginTable("##AnimEvents", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Function", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableHeadersRow();

                        foreach (var evt in clip.events)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text($"{evt.time:F3} s");
                            ImGui.TableNextColumn();
                            ImGui.Text(evt.functionName);
                        }

                        ImGui.EndTable();
                    }
                    ImGui.TreePop();
                }
            }
        }

        private void DrawCompressionFormatPreview()
        {
            if (_editedImporter == null)
                return;
            var tt = _editedImporter.TryGetValue("texture_type", out var ttv2) ? ttv2?.ToString() ?? "Color" : "Color";
            var q = _editedImporter.TryGetValue("quality", out var qv2) ? qv2?.ToString() ?? "High" : "High";
            var sr = _editedImporter.TryGetValue("srgb", out var sv2) && sv2 is true;
            var resolution = TextureCompressionFormatResolver.Resolve(tt ?? "Color", q ?? "High", sr);
            ImGui.TextDisabled($"Format: {resolution.DisplayLabel}");
        }

        private void DrawTextureImporterSettings()
        {
            // Determine texture type
            string textureType = "Color";
            if (_editedImporter != null && _editedImporter.TryGetValue("texture_type", out var ttVal))
                textureType = ttVal?.ToString() ?? "Color";
            bool isHdr = textureType == "HDR";
            bool isPanoramic = textureType == "Panoramic";
            bool isSprite = textureType == "Sprite";

            DrawImporterInt("max_size", (isHdr || isPanoramic) ? 4096 : 2048, 1, 8192);

            if (isHdr)
            {
                DrawImporterCombo("texture_type", new[] { "HDR", "Panoramic" }, "HDR");
                DrawImporterCombo("quality", new[] { "High", "Medium", "Low", "NoCompression" }, "High");
                DrawCompressionFormatPreview();
            }
            else if (isPanoramic)
            {
                DrawImporterCombo("texture_type", new[] { "Color", "ColorWithAlpha", "NormalMap", "Sprite", "Panoramic" }, "Color");

                // Panoramic 전환 시 srgb 자동 갱신 (Panoramic은 linear 강제)
                if (_editedImporter != null)
                {
                    if (_editedImporter.TryGetValue("srgb", out var sv) && sv is true)
                    {
                        _editedImporter["srgb"] = false;
                        _hasChanges = true;
                    }
                }

                DrawImporterInt("face_size", 512, 128, 4096);
                DrawImporterCombo("quality", new[] { "High", "Medium", "Low", "NoCompression" }, "High");
                DrawCompressionFormatPreview();
            }
            else
            {
                var curType = _editedImporter?.TryGetValue("texture_type", out var ttv) == true ? ttv?.ToString() : null;
                bool isNormalMap = curType == "NormalMap";

                DrawImporterCombo("texture_type", new[] { "Color", "ColorWithAlpha", "NormalMap", "Sprite", "Panoramic" }, "Color");

                // NormalMap 전환 시 srgb 자동 갱신 (NormalMap은 linear 강제)
                if (isNormalMap && _editedImporter != null)
                {
                    if (_editedImporter.TryGetValue("srgb", out var sv2) && sv2 is true)
                    {
                        _editedImporter["srgb"] = false;
                        _hasChanges = true;
                    }
                }

                // quality는 NormalMap 포함 모든 LDR 경로에서 노출.
                // NormalMap은 High/Medium/Low에서 동일하게 BC5로 결정되며, NoCompression에서 RGBA8로 빠진다.
                // 이 의미 차이는 DrawCompressionFormatPreview()의 라벨이 설명한다.
                // TODO(Phase 3): quality=NoCompression일 때 파이프라인(RoseCache.StoreTexture)이
                //   Compressonator 경로를 건너뛰도록 연결한다. Phase 2 단독 시점에는 compression 키가 없어
                //   기본값 "BC7"로 fallback되어 라벨과 실제 결과가 불일치할 수 있다.
                DrawImporterCombo("quality", new[] { "High", "Medium", "Low", "NoCompression" }, "High");
                DrawCompressionFormatPreview();
            }

            if (isPanoramic)
            {
                ImGui.BeginDisabled();
                DrawImporterBool("srgb", false);
                ImGui.EndDisabled();
            }
            else if (isSprite)
            {
                // Sprite → srgb 강제 ON, generate_mipmaps 강제 OFF, wrap 기본 Clamp
                if (_editedImporter != null)
                {
                    if (!_editedImporter.TryGetValue("srgb", out var sv3) || sv3 is not true)
                    {
                        _editedImporter["srgb"] = true;
                        _hasChanges = true;
                    }
                    if (_editedImporter.TryGetValue("generate_mipmaps", out var gm) && gm is true)
                    {
                        _editedImporter["generate_mipmaps"] = false;
                        _hasChanges = true;
                    }
                    if (!_editedImporter.TryGetValue("wrap_mode", out _))
                    {
                        _editedImporter["wrap_mode"] = "Clamp";
                        _hasChanges = true;
                    }
                }
                ImGui.BeginDisabled();
                DrawImporterBool("srgb", true);
                ImGui.EndDisabled();
            }
            else
            {
                DrawImporterBool("srgb", !isHdr);
            }

            DrawImporterCombo("filter_mode", new[] { "Bilinear", "Trilinear", "Point" }, isSprite ? "Point" : "Bilinear");
            DrawImporterCombo("wrap_mode", new[] { "Repeat", "Clamp", "Mirror" }, isSprite ? "Clamp" : "Repeat");

            if (isSprite)
            {
                ImGui.BeginDisabled();
                DrawImporterBool("generate_mipmaps", false);
                ImGui.EndDisabled();
            }
            else
            {
                DrawImporterBool("generate_mipmaps", true);
            }

            // Sprite-specific settings
            if (isSprite)
            {
                ImGui.Spacing();
                ImGui.TextColored(new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f), "Sprite");
                DrawImporterCombo("sprite_mode", new[] { "Single", "Multiple" }, "Single");
                DrawImporterFloat("pixels_per_unit", 100f, 1f);

                // Open Sprite Editor button
                if (ImGui.Button("Open Sprite Editor"))
                {
                    OpenSpriteEditor();
                }
            }
        }

        private void OpenSpriteEditor()
        {
            if (_currentAssetPath == null || _assetMeta == null) return;
            var overlay = EditorBridge.GetImGuiOverlay<IronRose.Engine.Editor.ImGuiEditor.ImGuiOverlay>();
            if (overlay == null) return;
            var spriteEditor = overlay.SpriteEditorPanel;
            if (spriteEditor == null) return;

            var db = Resources.GetAssetDatabase();
            if (db == null) return;
            var tex = db.Load<Texture2D>(_currentAssetPath);
            if (tex == null) return;

            spriteEditor.Open(_currentAssetPath, tex, _assetMeta);
        }

        private static void OpenAnimationEditor(Animator animator)
        {
            if (animator.clip == null) return;
            var overlay = EditorBridge.GetImGuiOverlay<IronRose.Engine.Editor.ImGuiEditor.ImGuiOverlay>();
            if (overlay == null) return;
            var animEditor = overlay.AnimationEditorPanel;
            if (animEditor == null) return;

            // Find the .anim file path from AssetDatabase via GUID reverse lookup
            var db = Resources.GetAssetDatabase();
            if (db == null) return;
            var guid = db.FindGuidForAnimationClip(animator.clip);
            if (guid == null) return;
            var animPath = db.GetPathFromGuid(guid);
            if (animPath == null) return;

            animEditor.Open(animPath, animator.clip);
            animEditor.SetContext(animator);
        }

        private void DrawFontImporterSettings()
        {
            DrawImporterInt("font_size", 32, 8, 128);
        }

        // ── Post Process Profile Editor ──

        private void DrawPostProcessProfileEditor(string assetPath)
        {
            var db = Resources.GetAssetDatabase();
            var profile = db?.Load<PostProcessProfile>(assetPath);
            if (profile == null)
            {
                ImGui.TextDisabled("Could not load profile");
                return;
            }

            if (!ImGui.CollapsingHeader("Post Process Profile", ImGuiTreeNodeFlags.DefaultOpen)) return;

            ImGui.Spacing();

            // 현재 스택에 등록된 이펙트 목록 (Bloom, Tonemap 등)
            var stack = RenderSettings.postProcessing;
            if (stack == null)
            {
                ImGui.TextDisabled("No PostProcessStack available");
                return;
            }

            bool changed = false;

            foreach (var effectTemplate in stack.Effects)
            {
                var effectName = effectTemplate.Name;
                var ov = profile.GetOrAddEffect(effectName);

                bool enabled = ov.enabled;
                if (ImGui.Checkbox($"##{effectName}_pp_enabled", ref enabled))
                {
                    ov.enabled = enabled;
                    changed = true;
                }
                ImGui.SameLine();

                if (ImGui.TreeNodeEx(effectName, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    if (!enabled) ImGui.BeginDisabled();

                    foreach (var param in effectTemplate.GetParameters())
                    {
                        string paramName = param.Name;
                        float val = ov.parameters.TryGetValue(paramName, out var v) ? v : 0f;

                        if (param.ValueType == typeof(float))
                        {
                            if (param.Min != param.Max)
                            {
                                if (EditorWidgets.SliderFloatWithInput("PP", paramName, ref val, param.Min, param.Max))
                                {
                                    ov.parameters[paramName] = val;
                                    changed = true;
                                }
                            }
                            else
                            {
                                if (EditorWidgets.DragFloatClickable("PP." + paramName, paramName, ref val, 0.01f, "%.2f"))
                                {
                                    ov.parameters[paramName] = val;
                                    changed = true;
                                }
                            }
                        }
                        else if (param.ValueType == typeof(int))
                        {
                            int ival = (int)val;
                            if (param.Min != param.Max)
                            {
                                if (EditorWidgets.SliderIntWithInput("PP", paramName, ref ival, (int)param.Min, (int)param.Max))
                                {
                                    ov.parameters[paramName] = ival;
                                    changed = true;
                                }
                            }
                            else
                            {
                                if (EditorWidgets.DragIntClickable("PP." + paramName, paramName, ref ival))
                                {
                                    ov.parameters[paramName] = ival;
                                    changed = true;
                                }
                            }
                        }
                        else if (param.ValueType == typeof(bool))
                        {
                            bool bval = val >= 0.5f;
                            string wl = EditorWidgets.BeginPropertyRow(paramName);
                            if (ImGui.Checkbox(wl, ref bval))
                            {
                                ov.parameters[paramName] = bval ? 1f : 0f;
                                changed = true;
                            }
                        }
                    }

                    if (!enabled) ImGui.EndDisabled();
                    ImGui.TreePop();
                }
            }

            if (changed)
            {
                PostProcessProfileImporter.Export(profile, Path.GetFullPath(assetPath));
            }
        }

        // ── Renderer Profile Editor ──

        private float _rpSaveTimer;
        private bool _rpDirty;
        private const float RpSaveDelay = 0.5f;

        private void DrawRendererProfileEditor(string assetPath)
        {
            var db = Resources.GetAssetDatabase();
            var profile = db?.Load<RendererProfile>(assetPath);
            if (profile == null)
            {
                ImGui.TextDisabled("Could not load profile");
                return;
            }

            if (!ImGui.CollapsingHeader("Renderer Profile", ImGuiTreeNodeFlags.DefaultOpen)) return;

            ImGui.Spacing();

            bool isActive = RenderSettings.activeRendererProfileGuid == _assetMeta?.guid;
            if (isActive)
                ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f), "Active Profile");

            bool changed = false;

            // ── FSR Upscaler ──
            if (ImGui.CollapsingHeader("FSR Upscaler", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool fsrEnabled = profile.fsrEnabled;
                string wlFsr = EditorWidgets.BeginPropertyRow("FSR Enabled");
                if (ImGui.Checkbox(wlFsr, ref fsrEnabled))
                {
                    profile.fsrEnabled = fsrEnabled;
                    changed = true;
                }

                if (!fsrEnabled) ImGui.BeginDisabled();

                var modeNames = Enum.GetNames<FsrScaleMode>();
                int current = (int)profile.fsrScaleMode;
                string wlMode = EditorWidgets.BeginPropertyRow("Scale Mode");
                if (ImGui.Combo(wlMode, ref current, modeNames, modeNames.Length))
                {
                    profile.fsrScaleMode = (FsrScaleMode)current;
                    changed = true;
                }

                if (profile.fsrScaleMode == FsrScaleMode.Custom)
                {
                    float cs = profile.fsrCustomScale;
                    if (EditorWidgets.SliderFloatWithInput("RP", "Custom Scale", ref cs, 1.0f, 3.0f))
                    {
                        profile.fsrCustomScale = cs;
                        changed = true;
                    }
                }

                float sharp = profile.fsrSharpness;
                if (EditorWidgets.SliderFloatWithInput("RP", "Sharpness", ref sharp, 0f, 1f))
                {
                    profile.fsrSharpness = sharp;
                    changed = true;
                }

                float jitter = profile.fsrJitterScale;
                if (EditorWidgets.SliderFloatWithInput("RP", "Jitter Scale", ref jitter, 0f, 2f))
                {
                    profile.fsrJitterScale = jitter;
                    changed = true;
                }

                if (!fsrEnabled) ImGui.EndDisabled();
            }

            // ── SSIL / AO ──
            if (ImGui.CollapsingHeader("SSIL / AO", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool ssilEnabled = profile.ssilEnabled;
                string wlSsil = EditorWidgets.BeginPropertyRow("SSIL Enabled");
                if (ImGui.Checkbox(wlSsil, ref ssilEnabled))
                {
                    profile.ssilEnabled = ssilEnabled;
                    changed = true;
                }

                if (!ssilEnabled) ImGui.BeginDisabled();

                float radius = profile.ssilRadius;
                if (EditorWidgets.SliderFloatWithInput("RP", "Radius", ref radius, 0.1f, 5f))
                {
                    profile.ssilRadius = radius;
                    changed = true;
                }

                float falloff = profile.ssilFalloffScale;
                if (EditorWidgets.SliderFloatWithInput("RP", "Falloff Scale", ref falloff, 0.1f, 10f))
                {
                    profile.ssilFalloffScale = falloff;
                    changed = true;
                }

                int slices = profile.ssilSliceCount;
                if (EditorWidgets.SliderIntWithInput("RP", "Slice Count", ref slices, 1, 8))
                {
                    profile.ssilSliceCount = slices;
                    changed = true;
                }

                int steps = profile.ssilStepsPerSlice;
                if (EditorWidgets.SliderIntWithInput("RP", "Steps/Slice", ref steps, 1, 8))
                {
                    profile.ssilStepsPerSlice = steps;
                    changed = true;
                }

                float aoIntensity = profile.ssilAoIntensity;
                if (EditorWidgets.SliderFloatWithInput("RP", "AO Intensity", ref aoIntensity, 0f, 2f))
                {
                    profile.ssilAoIntensity = aoIntensity;
                    changed = true;
                }

                bool indirect = profile.ssilIndirectEnabled;
                string wlIndirect = EditorWidgets.BeginPropertyRow("Indirect Enabled");
                if (ImGui.Checkbox(wlIndirect, ref indirect))
                {
                    profile.ssilIndirectEnabled = indirect;
                    changed = true;
                }

                if (indirect)
                {
                    float boost = profile.ssilIndirectBoost;
                    if (EditorWidgets.SliderFloatWithInput("RP", "Indirect Boost", ref boost, 0f, 2f))
                    {
                        profile.ssilIndirectBoost = boost;
                        changed = true;
                    }

                    float sat = profile.ssilSaturationBoost;
                    if (EditorWidgets.SliderFloatWithInput("RP", "Saturation Boost", ref sat, 0f, 5f))
                    {
                        profile.ssilSaturationBoost = sat;
                        changed = true;
                    }
                }

                if (!ssilEnabled) ImGui.EndDisabled();
            }

            if (changed)
            {
                // 활성 프로파일이면 라이브 반영
                if (isActive)
                    profile.ApplyToRenderSettings();

                // 디바운스 자동 저장
                _rpDirty = true;
                _rpSaveTimer = RpSaveDelay;
            }

            // 디바운스 틱
            if (_rpDirty)
            {
                _rpSaveTimer -= Time.unscaledDeltaTime;
                if (_rpSaveTimer <= 0f)
                {
                    _rpDirty = false;
                    var absolutePath = Path.GetFullPath(assetPath);
                    db?.SuppressNextChange(absolutePath);
                    RendererProfileImporter.Export(profile, absolutePath);
                }
            }
        }

        // ── Material Editor ──

        private void DrawMaterialEditor(string assetPath)
        {
            if (_editedMatTable == null || _matFilePath == null) return;

            if (!ImGui.CollapsingHeader("Material", ImGuiTreeNodeFlags.DefaultOpen)) return;

            ImGui.Spacing();

            // ── Blend Mode ──
            {
                var blendNames = new[] { "Opaque", "AlphaBlend", "Additive" };
                int currentIdx = 0;
                if (_editedMatTable!.TryGetValue("blendMode", out var blendVal))
                {
                    var blendStr = blendVal?.ToString() ?? "Opaque";
                    currentIdx = blendStr switch
                    {
                        "AlphaBlend" => 1,
                        "Additive" => 2,
                        _ => 0,
                    };
                }

                string wl = EditorWidgets.BeginPropertyRow("Blend Mode");
                if (ImGui.Combo(wl, ref currentIdx, blendNames, blendNames.Length))
                {
                    var oldSnap = Toml.FromModel(_editedMatTable);
                    _editedMatTable["blendMode"] = blendNames[currentIdx];
                    SaveMatFile();
                    var newSnap = Toml.FromModel(_editedMatTable);
                    UndoSystem.Record(new MaterialPropertyUndoAction(
                        "Change Material blendMode", _matFilePath!, oldSnap, newSnap));
                }
            }

            ImGui.Spacing();

            // ── Base Surface ──
            DrawMatTextureSlot("mainTextureGuid", "Main Texture");
            DrawMatVec2("textureScaleX", "textureScaleY", "Tiling", 1f, 1f);
            DrawMatVec2("textureOffsetX", "textureOffsetY", "Offset", 0f, 0f);
            // color
            {
                var c = ReadMatColor("color");
                if (EditorWidgets.ColorEdit4("color", ref c))
                {
                    _undoTracker.BeginEdit("Mat.color", Toml.FromModel(_editedMatTable));
                    WriteMatColor("color", c);
                    SaveMatFile();
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (_undoTracker.EndEdit("Mat.color", out var oldSnap))
                    {
                        var newSnap = Toml.FromModel(_editedMatTable);
                        UndoSystem.Record(new MaterialPropertyUndoAction("Change Material color", _matFilePath, (string)oldSnap!, newSnap));
                    }
                    SaveMatFile();
                }
            }

            ImGui.Spacing();

            // ── Normal ──
            DrawMatTextureSlot("normalMapGuid", "Normal Map");
            DrawMatFloatRange("normalMapStrength", 1.0f, -2.0f, 2.0f);

            ImGui.Spacing();

            // ── MRO ──
            DrawMatTextureSlot("MROMapGuid", "MRO Map");
            DrawMatFloat("metallic", 0.0f);
            DrawMatFloat("roughness", 0.5f);
            DrawMatFloat("occlusion", 1.0f);

            ImGui.Spacing();

            // ── Emission ──
            // emission
            {
                var c = ReadMatColor("emission");
                if (EditorWidgets.ColorEdit4("emission", ref c))
                {
                    _undoTracker.BeginEdit("Mat.emission", Toml.FromModel(_editedMatTable));
                    WriteMatColor("emission", c);
                    SaveMatFile();
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (_undoTracker.EndEdit("Mat.emission", out var oldSnap))
                    {
                        var newSnap = Toml.FromModel(_editedMatTable);
                        UndoSystem.Record(new MaterialPropertyUndoAction("Change Material emission", _matFilePath, (string)oldSnap!, newSnap));
                    }
                    SaveMatFile();
                }
            }
        }

        /// <summary>
        /// GLB sub-asset Material을 독립 .mat과 동일한 레이아웃으로 표시하되
        /// 모든 위젯을 disabled(readonly) 상태로 렌더링한다.
        /// </summary>
        private void DrawReadOnlyMaterialInspector(Material mat)
        {
            if (!ImGui.CollapsingHeader("Material", ImGuiTreeNodeFlags.DefaultOpen)) return;

            var db = Resources.GetAssetDatabase();

            ImGui.BeginDisabled();

            ImGui.Spacing();

            // ── Blend Mode ──
            {
                string wl = EditorWidgets.BeginPropertyRow("Blend Mode");
                ImGui.TextUnformatted(mat.blendMode.ToString());
            }

            ImGui.Spacing();

            // ── Base Surface ──
            DrawReadOnlyTextureSlot(db, mat.mainTexture, "Main Texture");
            {
                var s = new System.Numerics.Vector2(mat.textureScale.x, mat.textureScale.y);
                string wl = EditorWidgets.BeginPropertyRow("Tiling");
                ImGui.DragFloat2(wl, ref s, 0.01f);
            }
            {
                var o = new System.Numerics.Vector2(mat.textureOffset.x, mat.textureOffset.y);
                string wl = EditorWidgets.BeginPropertyRow("Offset");
                ImGui.DragFloat2(wl, ref o, 0.01f);
            }

            // color
            {
                var c = mat.color;
                EditorWidgets.ColorEdit4("color", ref c);
            }

            ImGui.Spacing();

            // ── Normal ──
            DrawReadOnlyTextureSlot(db, mat.normalMap, "Normal Map");
            {
                float nms = mat.normalMapStrength;
                string wl = EditorWidgets.BeginPropertyRow("normalMapStrength");
                ImGui.DragFloat(wl, ref nms, 0.01f, -2f, 2f, "%.3f");
            }

            ImGui.Spacing();

            // ── MRO ──
            DrawReadOnlyTextureSlot(db, mat.MROMap, "MRO Map");
            {
                float metallic = mat.metallic;
                string wl = EditorWidgets.BeginPropertyRow("metallic");
                ImGui.DragFloat(wl, ref metallic, 0.01f, 0f, 1f, "%.3f");
            }
            {
                float roughness = mat.roughness;
                string wl = EditorWidgets.BeginPropertyRow("roughness");
                ImGui.DragFloat(wl, ref roughness, 0.01f, 0f, 1f, "%.3f");
            }
            {
                float occlusion = mat.occlusion;
                string wl = EditorWidgets.BeginPropertyRow("occlusion");
                ImGui.DragFloat(wl, ref occlusion, 0.01f, 0f, 1f, "%.3f");
            }

            ImGui.Spacing();

            // ── Emission ──
            {
                var c = mat.emission;
                EditorWidgets.ColorEdit4("emission", ref c);
            }

            ImGui.EndDisabled();
        }

        private static void DrawReadOnlyTextureSlot(IAssetDatabase? db, Texture2D? texture, string label)
        {
            string displayName = "(None)";
            if (texture != null && db != null)
            {
                var guid = db.FindGuidForTexture(texture);
                if (!string.IsNullOrEmpty(guid))
                {
                    var texPath = db.GetPathFromGuid(guid);
                    displayName = texPath != null ? Path.GetFileNameWithoutExtension(texPath) : "(Missing)";
                }
                else
                {
                    displayName = !string.IsNullOrEmpty(texture.name) ? texture.name : "(Embedded)";
                }
            }

            ImGui.Text(label);
            ImGui.SameLine(110);

            float availW = ImGui.GetContentRegionAvail().X;
            ImGui.Button($"{displayName}##{label}", new System.Numerics.Vector2(availW, 0));
        }

        private void DrawMatFloat(string key, float defaultValue)
        {
            if (_editedMatTable == null || _matFilePath == null) return;

            float val = defaultValue;
            if (_editedMatTable.TryGetValue(key, out var raw))
                val = Convert.ToSingle(raw);

            string widgetId = "Mat." + key;
            if (EditorWidgets.DragFloatClickable(widgetId, key, ref val, 0.01f, "%.3f"))
            {
                _undoTracker.BeginEdit(widgetId, Toml.FromModel(_editedMatTable));
                val = Math.Clamp(val, 0f, 1f);
                _editedMatTable[key] = (double)val;
                SaveMatFile();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (_undoTracker.EndEdit(widgetId, out var oldSnap))
                {
                    var newSnap = Toml.FromModel(_editedMatTable);
                    UndoSystem.Record(new MaterialPropertyUndoAction($"Change Material {key}", _matFilePath, (string)oldSnap!, newSnap));
                }
                SaveMatFile();
            }
        }

        private void DrawMatFloatRange(string key, float defaultValue, float min, float max)
        {
            if (_editedMatTable == null || _matFilePath == null) return;

            float val = defaultValue;
            if (_editedMatTable.TryGetValue(key, out var raw))
                val = Convert.ToSingle(raw);

            string widgetId = "Mat." + key;
            if (EditorWidgets.DragFloatClickable(widgetId, key, ref val, 0.01f, "%.3f"))
            {
                _undoTracker.BeginEdit(widgetId, Toml.FromModel(_editedMatTable));
                val = Math.Clamp(val, min, max);
                _editedMatTable[key] = (double)val;
                SaveMatFile();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (_undoTracker.EndEdit(widgetId, out var oldSnap))
                {
                    var newSnap = Toml.FromModel(_editedMatTable);
                    UndoSystem.Record(new MaterialPropertyUndoAction($"Change Material {key}", _matFilePath, (string)oldSnap!, newSnap));
                }
                SaveMatFile();
            }
        }

        private void DrawMatVec2(string keyX, string keyY, string label, float defaultX, float defaultY)
        {
            if (_editedMatTable == null || _matFilePath == null) return;

            float vx = defaultX, vy = defaultY;
            if (_editedMatTable.TryGetValue(keyX, out var rx)) vx = Convert.ToSingle(rx);
            if (_editedMatTable.TryGetValue(keyY, out var ry)) vy = Convert.ToSingle(ry);

            var v = new System.Numerics.Vector2(vx, vy);
            string widgetId = "Mat." + label;
            if (EditorWidgets.DragFloat2Clickable(widgetId, label, ref v, 0.01f))
            {
                _undoTracker.BeginEdit(widgetId, Toml.FromModel(_editedMatTable));
                _editedMatTable[keyX] = (double)v.X;
                _editedMatTable[keyY] = (double)v.Y;
                SaveMatFile();
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (_undoTracker.EndEdit(widgetId, out var oldSnap))
                {
                    var newSnap = Toml.FromModel(_editedMatTable);
                    UndoSystem.Record(new MaterialPropertyUndoAction($"Change Material {label}", _matFilePath, (string)oldSnap!, newSnap));
                }
                SaveMatFile();
            }
        }

        // ── Asset Browser Popup helpers ──

        private void OpenAssetBrowser(string title, string typeFilter, string? currentGuid,
            Action<string> onConfirm)
        {
            _openAssetBrowser = true;
            _assetBrowserSearch = "";
            _assetBrowserFocusSearch = true;
            _assetBrowserTitle = title;
            _assetBrowserTypeFilter = typeFilter;
            _assetBrowserSelectedGuid = currentGuid;
            _assetBrowserOnConfirm = onConfirm;
            _assetBrowserCachedList = null;
        }

        private static string GetTypeFilter(Type? memberType)
        {
            if (memberType == null) return "";
            if (memberType == typeof(Material)) return "material";
            if (memberType == typeof(Mesh) || memberType == typeof(MipMesh)) return "mesh";
            if (memberType == typeof(Texture2D)) return "texture";
            if (memberType == typeof(Sprite)) return "sprite";
            if (memberType == typeof(Font)) return "font";
            if (memberType == typeof(AnimationClip)) return "anim";
            if (memberType == typeof(PostProcessProfile)) return "ppprofile";
            if (memberType == typeof(GameObject)) return "prefab";
            return "";
        }

        private static string? FindCurrentGuid(object? asset)
        {
            if (asset == null) return null;
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            return asset switch
            {
                Mesh mesh      => db.FindGuidForMesh(mesh),
                Material mat   => db.FindGuidForMaterial(mat),
                Texture2D tex  => db.FindGuidForTexture(tex),
                Sprite spr     => db.FindGuidForSprite(spr),
                AnimationClip clip => db.FindGuidForAnimationClip(clip),
                MipMesh mip    => mip.LodCount > 0 ? db.FindGuidForMesh(mip.lodMeshes[0]) : null,
                PostProcessProfile pp => db.FindGuidForPostProcessProfile(pp),
                GameObject go  => db.FindGuidForPrefab(go),
                _              => null,
            };
        }

        private List<(string displayName, string guid, string path)> CollectBrowsableAssets(string typeFilter)
        {
            var results = new List<(string displayName, string guid, string path)>();
            var db = Resources.GetAssetDatabase();
            if (db == null) return results;

            foreach (var mainPath in db.GetAllAssetPaths())
            {
                var ext = Path.GetExtension(mainPath).ToLowerInvariant();

                bool mainMatches = typeFilter switch
                {
                    "texture"    => ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr",
                    "font"       => ext is ".ttf" or ".otf",
                    "material"   => ext is ".mat",
                    "anim"       => ext is ".anim",
                    "ppprofile"  => ext is ".ppprofile",
                    "renderer"   => ext is ".renderer",
                    "sprite"     => false, // sprites are sub-assets only
                    _            => false,
                };

                if (mainMatches)
                {
                    var mainGuid = db.GetGuidFromPath(mainPath);
                    if (!string.IsNullOrEmpty(mainGuid))
                    {
                        var name = Path.GetFileNameWithoutExtension(mainPath);
                        results.Add((name, mainGuid, mainPath));
                    }
                }

                var subs = db.GetSubAssets(mainPath);
                foreach (var sub in subs)
                {
                    bool subMatches = typeFilter switch
                    {
                        "mesh"     => sub.type == "Mesh",
                        "material" => sub.type == "Material",
                        "texture"  => sub.type is "Sprite" or "Texture2D",
                        "sprite"   => sub.type == "Sprite",
                        _          => false,
                    };

                    if (subMatches && !string.IsNullOrEmpty(sub.guid))
                    {
                        var displayName = !string.IsNullOrEmpty(sub.name)
                            ? sub.name
                            : $"{Path.GetFileNameWithoutExtension(mainPath)}#{sub.type}:{sub.index}";
                        results.Add((displayName, sub.guid, mainPath));
                    }
                }
            }

            results.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
            return results;
        }

        private void DrawMatTextureSlot(string key, string label)
        {
            if (_editedMatTable == null || _matFilePath == null) return;

            // 1. Read current GUID (may be absent or empty)
            string guid = "";
            if (_editedMatTable.TryGetValue(key, out var guidVal))
                guid = guidVal?.ToString() ?? "";

            // 2. Resolve GUID to display name
            var db = Resources.GetAssetDatabase();
            string? texturePath = null;
            string displayName = "(None)";

            if (!string.IsNullOrEmpty(guid) && db != null)
            {
                texturePath = db.GetPathFromGuid(guid);
                if (texturePath != null)
                    displayName = Path.GetFileNameWithoutExtension(texturePath);
                else
                    displayName = "(Missing)";
            }

            // 3. Label
            ImGui.Text(label);
            ImGui.SameLine(110);

            // 4. Clickable texture name / drop target button
            float availW = ImGui.GetContentRegionAvail().X;
            float clearBtnW = 20f;
            float browseBtnW = 20f;
            bool hasTexture = !string.IsNullOrEmpty(guid);
            float reserved = browseBtnW + 4f + (hasTexture ? clearBtnW + 4f : 0f);
            float slotW = availW - reserved;

            if (texturePath != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{displayName}##{key}", false, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(slotW, 0)))
                {
                    EditorBridge.PingAsset(texturePath);
                }
                ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(texturePath);
            }
            else
            {
                ImGui.Button($"{displayName}##{key}", new System.Numerics.Vector2(slotW, 0));
            }

            // 5. Drag-drop target
            if (ImGui.BeginDragDropTarget())
            {
                unsafe
                {
                    var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                    if (payload.NativePtr != null)
                    {
                        var droppedPath = ImGuiProjectPanel._draggedAssetPath;
                        if (!string.IsNullOrEmpty(droppedPath))
                        {
                            var ext = Path.GetExtension(droppedPath);
                            if (TextureExtensions.Contains(ext) && db != null)
                            {
                                var newGuid = db.GetGuidFromPath(droppedPath);
                                if (!string.IsNullOrEmpty(newGuid))
                                    SetTextureGuid(key, label, newGuid);
                            }
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // 5.5. Browse button (◎)
            ImGui.SameLine();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
            if (ImGui.Button($"\u25ce##{key}_browse"))
            {
                OpenAssetBrowser($"Select {label}", "texture", guid,
                    newGuid => SetTextureGuid(key, label, newGuid));
            }
            ImGui.PopStyleVar();

            // 6. X clear button
            if (hasTexture)
            {
                ImGui.SameLine();
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
                if (ImGui.Button($"X##{key}_clear"))
                    SetTextureGuid(key, label, "");
                ImGui.PopStyleVar();
            }
        }

        private void SetTextureGuid(string key, string label, string newGuid)
        {
            if (_editedMatTable == null || _matFilePath == null) return;

            var oldToml = Toml.FromModel(_editedMatTable);

            if (string.IsNullOrEmpty(newGuid))
                _editedMatTable.Remove(key);
            else
                _editedMatTable[key] = newGuid;

            var newToml = Toml.FromModel(_editedMatTable);

            string desc = string.IsNullOrEmpty(newGuid) ? $"Clear {label}" : $"Set {label}";
            UndoSystem.Record(new MaterialPropertyUndoAction(desc, _matFilePath, oldToml, newToml));

            SaveMatFile();
        }

        private Color ReadMatColor(string key)
        {
            if (_editedMatTable != null && _editedMatTable.TryGetValue(key, out var cv) && cv is TomlTable ct)
            {
                float r = ct.TryGetValue("r", out var rv) ? Convert.ToSingle(rv) : 0f;
                float g = ct.TryGetValue("g", out var gv) ? Convert.ToSingle(gv) : 0f;
                float b = ct.TryGetValue("b", out var bv) ? Convert.ToSingle(bv) : 0f;
                float a = ct.TryGetValue("a", out var av) ? Convert.ToSingle(av) : 1f;
                return new Color(r, g, b, a);
            }
            return key == "emission" ? Color.black : Color.white;
        }

        private void WriteMatColor(string key, Color c)
        {
            if (_editedMatTable == null) return;
            _editedMatTable[key] = new TomlTable
            {
                ["r"] = (double)c.r,
                ["g"] = (double)c.g,
                ["b"] = (double)c.b,
                ["a"] = (double)c.a,
            };
        }

        private void SaveMatFile()
        {
            if (_editedMatTable == null || _matFilePath == null) return;
            File.WriteAllText(_matFilePath, Toml.FromModel(_editedMatTable));
            var db = Resources.GetAssetDatabase();
            db?.Reimport(_matFilePath);
            ClearPreviewCache();
        }

        // ── Asset Preview ──

        private void DrawAssetPreview(string assetPath, string importerType)
        {
            // 캐시가 유효하면 재생성 없이 바로 그리기
            var dbVersion = Resources.GetAssetDatabase()?.ReimportVersion ?? 0;
            if (_previewCachedPath != assetPath || _previewReimportVersion != dbVersion)
                BuildPreviewCache(assetPath, importerType);

            if (_previewType == PreviewType.None) return;

            ImGui.Spacing();
            if (!ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen)) return;

            switch (_previewType)
            {
                case PreviewType.Texture:
                    DrawTexturePreview();
                    break;
                case PreviewType.Material:
                    DrawMaterialPreview();
                    break;
                case PreviewType.Mesh:
                    DrawMeshPreviewInfo();
                    break;
                case PreviewType.Font:
                    DrawFontPreview();
                    break;
            }
        }

        private void BuildPreviewCache(string assetPath, string importerType)
        {
            ClearPreviewCache();
            _previewCachedPath = assetPath;
            _previewReimportVersion = Resources.GetAssetDatabase()?.ReimportVersion ?? 0;

            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            // 서브에셋 경로인 경우 타입 문자열로 판별
            if (SubAssetPath.IsSubAssetPath(assetPath))
            {
                SubAssetPath.TryParse(assetPath, out _, out var subType, out _);
                switch (subType)
                {
                    case "Texture2D":
                        CacheTexture(db.Load<Texture2D>(assetPath));
                        break;
                    case "Material":
                        CacheMaterial(db.Load<Material>(assetPath));
                        break;
                    case "Mesh":
                        CacheMesh(db.Load<Mesh>(assetPath));
                        break;
                }
                return;
            }

            // 탑-레벨 에셋은 임포터 타입으로 판별
            switch (importerType)
            {
                case "TextureImporter":
                    CacheTexture(db.Load<Texture2D>(assetPath));
                    break;
                case "FontImporter":
                    CacheFont(db.Load<Font>(assetPath));
                    break;
                case "MeshImporter":
                    // 첫 번째 메시 정보 표시
                    CacheMesh(db.Load<Mesh>(assetPath));
                    break;
                case "MaterialImporter":
                    CacheMaterial(db.Load<Material>(assetPath));
                    break;
            }
        }

        private IntPtr BindTexture(Texture2D? tex)
        {
            if (tex == null) return IntPtr.Zero;
            if (tex.TextureView == null)
                tex.UploadToGPU(_device);
            if (tex.TextureView == null) return IntPtr.Zero;
            return _imguiRenderer.GetOrCreateImGuiBinding(tex.TextureView);
        }

        private void CacheTexture(Texture2D? tex)
        {
            if (tex == null) return;
            var id = BindTexture(tex);
            if (id == IntPtr.Zero) return;

            _previewType = PreviewType.Texture;
            _previewTextureId = id;
            _previewTexWidth = tex.width;
            _previewTexHeight = tex.height;
        }

        private void CacheMaterial(Material? mat)
        {
            if (mat == null) return;
            _previewType = PreviewType.Material;
            _previewMatColor = mat.color;
            _previewMatMetallic = mat.metallic;
            _previewMatRoughness = mat.roughness;

            // 3D sphere 미리보기 (PBR 머티리얼 모드)
            _meshPreview ??= new MeshPreviewRenderer(_device);

            var sphere = PrimitiveGenerator.CreateSphere();
            _meshPreview.SetMesh(sphere);

            // 머티리얼 색상 + PBR 파라미터 + 텍스처 적용
            var c = mat.color;
            var colorVec = new System.Numerics.Vector4(c.r, c.g, c.b, c.a);
            TextureView? texView = null;
            if (mat.mainTexture != null)
            {
                if (mat.mainTexture.TextureView == null)
                    mat.mainTexture.UploadToGPU(_device);
                texView = mat.mainTexture.TextureView;
            }
            _meshPreview.SetMaterialOverride(colorVec, mat.metallic, mat.roughness, texView);
            _meshPreview.RenderIfDirty();

            if (_meshPreview.ColorTextureView != null)
                _meshPreviewTextureId = _imguiRenderer.GetOrCreateImGuiBinding(_meshPreview.ColorTextureView);
        }

        private void CacheMesh(Mesh? mesh)
        {
            if (mesh == null) return;
            _previewType = PreviewType.Mesh;
            _previewMeshName = mesh.name;
            _previewMeshVerts = mesh.vertices.Length;
            _previewMeshTris = mesh.indices.Length / 3;
            mesh.RecalculateBounds();
            _previewMeshBoundsSize = mesh.bounds.size;

            // 3D 미리보기 렌더러 초기화 및 메시 설정
            _meshPreview ??= new MeshPreviewRenderer(_device);
            _meshPreview.ClearMaterialOverride();
            _meshPreview.SetMesh(mesh);
            _meshPreview.RenderIfDirty();

            if (_meshPreview.ColorTextureView != null)
                _meshPreviewTextureId = _imguiRenderer.GetOrCreateImGuiBinding(_meshPreview.ColorTextureView);
        }

        private void CacheFont(Font? font)
        {
            if (font == null) return;
            _previewType = PreviewType.Font;
            _previewFontName = font.name;
            _previewFontSize = font.fontSize;
            _previewFontLineHeight = font.lineHeight;

            // atlas 텍스처 썸네일
            if (font.atlasTexture != null)
            {
                var id = BindTexture(font.atlasTexture);
                if (id != IntPtr.Zero)
                {
                    _previewTextureId = id;
                    _previewTexWidth = font.atlasTexture.width;
                    _previewTexHeight = font.atlasTexture.height;
                }
            }
        }

        private void DrawTexturePreview()
        {
            if (_previewTextureId == IntPtr.Zero) return;

            // 패널 너비에 맞춰 비율 유지
            float availW = ImGui.GetContentRegionAvail().X;
            float aspect = (float)_previewTexHeight / _previewTexWidth;
            float displayW = MathF.Min(availW, _previewTexWidth);
            float displayH = displayW * aspect;

            ImGui.Image(_previewTextureId, new System.Numerics.Vector2(displayW, displayH));

            // 압축 후 메모리 크기 계산
            string compression = "BC7";
            bool generateMipmaps = true;
            if (_editedImporter != null)
            {
                if (_editedImporter.TryGetValue("compression", out var cVal))
                    compression = cVal?.ToString() ?? "BC7";
                if (_editedImporter.TryGetValue("generate_mipmaps", out var mVal) && mVal is bool m)
                    generateMipmaps = m;
            }
            long memBytes = CalculateTextureMemorySize(_previewTexWidth, _previewTexHeight, compression, generateMipmaps);
            string memStr = FormatMemorySize(memBytes);

            ImGui.TextDisabled($"{_previewTexWidth} x {_previewTexHeight}");
            ImGui.SameLine();
            ImGui.TextDisabled($"  {memStr}");
            ImGui.SameLine();
            ImGui.TextDisabled($"  {compression}");
        }

        private static long CalculateTextureMemorySize(int width, int height, string compression, bool generateMipmaps)
        {
            long totalBytes = 0;
            int w = width, h = height;
            while (true)
            {
                if (compression is "BC7" or "BC5" or "BC6H")
                {
                    int blocksX = (w + 3) / 4;
                    int blocksY = (h + 3) / 4;
                    totalBytes += blocksX * blocksY * 16;
                }
                else
                {
                    totalBytes += (long)w * h * 4;
                }

                if (!generateMipmaps || (w == 1 && h == 1)) break;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            return totalBytes;
        }

        private static string FormatMemorySize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        private void DrawMaterialPreview()
        {
            // 3D sphere 미리보기
            if (_meshPreviewTextureId != IntPtr.Zero && _meshPreview != null)
            {
                float availW = ImGui.GetContentRegionAvail().X;
                float size = MathF.Min(availW, 256f);

                ImGui.Image(_meshPreviewTextureId, new System.Numerics.Vector2(size, size));

                // 마우스 드래그로 회전 (머티리얼 모드: 오브젝트 회전이므로 횡 방향 반전)
                if (ImGui.IsItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    var delta = ImGui.GetIO().MouseDelta;
                    if (delta.X != 0 || delta.Y != 0)
                    {
                        _meshPreview.UpdateOrbit(delta.X * 0.5f, delta.Y * 0.5f);
                        _meshPreview.RenderIfDirty();
                    }
                }
            }

        }

        private void DrawMeshPreviewInfo()
        {
            // 3D 미리보기
            if (_meshPreviewTextureId != IntPtr.Zero && _meshPreview != null)
            {
                float availW = ImGui.GetContentRegionAvail().X;
                float size = MathF.Min(availW, 256f);
                var imagePos = ImGui.GetCursorScreenPos();

                ImGui.Image(_meshPreviewTextureId, new System.Numerics.Vector2(size, size));

                // 마우스 드래그로 회전
                if (ImGui.IsItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    var delta = ImGui.GetIO().MouseDelta;
                    if (delta.X != 0 || delta.Y != 0)
                    {
                        _meshPreview.UpdateOrbit(delta.X * 0.5f, -delta.Y * 0.5f);
                        _meshPreview.RenderIfDirty();
                    }
                }
            }

            // 메시 정보
            if (!string.IsNullOrEmpty(_previewMeshName))
                ImGui.Text(_previewMeshName);

            ImGui.Text($"Vertices: {_previewMeshVerts:N0}");
            ImGui.Text($"Triangles: {_previewMeshTris:N0}");
            ImGui.Text($"Bounds: {_previewMeshBoundsSize.x:F2} x {_previewMeshBoundsSize.y:F2} x {_previewMeshBoundsSize.z:F2}");
        }

        private void DrawFontPreview()
        {
            if (!string.IsNullOrEmpty(_previewFontName))
                ImGui.Text(_previewFontName);

            ImGui.Text($"Size: {_previewFontSize} pt");
            ImGui.Text($"Line Height: {_previewFontLineHeight:F1} px");

            // atlas 텍스처
            if (_previewTextureId != IntPtr.Zero)
            {
                ImGui.Spacing();
                ImGui.Text("Atlas");
                float availW = ImGui.GetContentRegionAvail().X;
                float aspect = (float)_previewTexHeight / _previewTexWidth;
                float displayW = MathF.Min(availW, _previewTexWidth);
                float displayH = displayW * aspect;

                // 어두운 배경을 그려서 흰색 글리프가 잘 보이도록 함
                var cursorPos = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(
                    cursorPos,
                    new System.Numerics.Vector2(cursorPos.X + displayW, cursorPos.Y + displayH),
                    ImGui.GetColorU32(new System.Numerics.Vector4(0.08f, 0.08f, 0.08f, 1f)));

                ImGui.Image(_previewTextureId, new System.Numerics.Vector2(displayW, displayH));
                ImGui.TextDisabled($"{_previewTexWidth} x {_previewTexHeight}");
            }
        }

        // ── Importer Setting Widgets ──

        private void DrawImporterFloat(string key, float defaultValue, float speed = 0.01f)
        {
            if (_editedImporter == null) return;

            bool isMixed = _mixedImporterKeys.Contains(key);
            float val = defaultValue;
            if (_editedImporter.TryGetValue(key, out var raw))
                val = Convert.ToSingle(raw);

            if (isMixed)
                ImGui.PushStyleColor(ImGuiCol.Text,
                    new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 0.5f));

            if (EditorWidgets.DragFloatClickable("Importer." + key, isMixed ? $"{key}  -" : key, ref val, speed, "%.3f"))
            {
                _editedImporter[key] = (double)val;
                _hasChanges = true;
                _mixedImporterKeys.Remove(key);
            }

            if (isMixed)
                ImGui.PopStyleColor();
        }

        private void DrawImporterInt(string key, int defaultValue, int min, int max)
        {
            if (_editedImporter == null) return;

            bool isMixed = _mixedImporterKeys.Contains(key);
            int val = defaultValue;
            if (_editedImporter.TryGetValue(key, out var raw))
                val = Convert.ToInt32(raw);

            if (isMixed)
                ImGui.PushStyleColor(ImGuiCol.Text,
                    new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 0.5f));

            if (EditorWidgets.DragIntClickable("Importer." + key, isMixed ? $"{key}  -" : key, ref val))
            {
                if (val < min) val = min;
                if (val > max) val = max;
                _editedImporter[key] = (long)val;
                _hasChanges = true;
                _mixedImporterKeys.Remove(key);
            }

            if (isMixed)
                ImGui.PopStyleColor();
        }

        private void DrawImporterBool(string key, bool defaultValue)
        {
            if (_editedImporter == null) return;

            bool isMixed = _mixedImporterKeys.Contains(key);
            bool val = defaultValue;
            if (_editedImporter.TryGetValue(key, out var raw) && raw is bool b)
                val = b;

            if (isMixed)
                ImGui.PushStyleColor(ImGuiCol.CheckMark,
                    new System.Numerics.Vector4(0, 0, 0, 0));

            string wLabel = EditorWidgets.BeginPropertyRow(key);
            if (ImGui.Checkbox(wLabel, ref val))
            {
                _editedImporter[key] = val;
                _hasChanges = true;
                _mixedImporterKeys.Remove(key);
            }

            if (isMixed)
            {
                ImGui.PopStyleColor();
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                float frameH = rectMax.Y - rectMin.Y;
                var dashSize = ImGui.CalcTextSize("-");
                float cx = rectMin.X + (frameH - dashSize.X) * 0.5f;
                float cy = rectMin.Y + (frameH - dashSize.Y) * 0.5f;
                ImGui.GetWindowDrawList().AddText(
                    new System.Numerics.Vector2(cx, cy),
                    ImGui.GetColorU32(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1.0f)),
                    "-");
            }
        }

        private void DrawImporterCombo(string key, string[] options, string defaultValue)
        {
            if (_editedImporter == null) return;

            bool isMixed = _mixedImporterKeys.Contains(key);
            string val = defaultValue;
            if (_editedImporter.TryGetValue(key, out var raw))
                val = raw?.ToString() ?? defaultValue;

            int current = Array.IndexOf(options, val);
            if (current < 0) current = 0;

            string preview = isMixed ? "-" : options[current];
            string wLabel = EditorWidgets.BeginPropertyRow(key);
            float previewW = ImGui.CalcTextSize(preview).X;
            float comboW = ImGui.CalcItemWidth();
            float pad = (comboW - previewW) * 0.5f;
            string centeredPreview = pad > 0 ? new string(' ', Math.Max(1, (int)(pad / ImGui.CalcTextSize(" ").X))) + preview : preview;
            if (ImGui.BeginCombo(wLabel, centeredPreview))
            {
                float popupW = ImGui.GetContentRegionAvail().X;
                for (int idx = 0; idx < options.Length; idx++)
                {
                    bool selected = !isMixed && idx == current;
                    float textW = ImGui.CalcTextSize(options[idx]).X;
                    float offset = (popupW - textW) * 0.5f;
                    if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                    if (ImGui.Selectable(options[idx], selected))
                    {
                        _editedImporter[key] = options[idx];
                        _hasChanges = true;
                        _mixedImporterKeys.Remove(key);
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        // ── Apply / Revert ──

        private void ApplyChanges()
        {
            if (_assetMeta == null || _editedImporter == null) return;

            var db = Resources.GetAssetDatabase();
            foreach (var path in _editingAssetPaths)
            {
                var meta = (path == _currentAssetPath) ? _assetMeta : RoseMetadata.LoadOrCreate(path);
                meta.importer = CloneTomlTable(_editedImporter);
                // PushImportGuard로 OnRoseMetadataSaved의 비동기 reimport를 억제하고
                // 동기 Reimport를 직접 호출하여, 프리뷰 갱신 시점에 텍스처가 확정되도록 한다.
                db?.PushImportGuard();
                meta.Save(path + ".rose");
                db?.PopImportGuard();
                db?.Reimport(path);
            }

            ClearPreviewCache();
            _hasChanges = false;
        }

        private void RevertChanges()
        {
            if (_assetMeta == null) return;
            _editedImporter = CloneTomlTable(_assetMeta.importer);
            _hasChanges = false;
        }

        private static TomlTable CloneTomlTable(TomlTable source)
        {
            var clone = new TomlTable();
            foreach (var kvp in source)
                clone[kvp.Key] = kvp.Value;
            return clone;
        }

        // ── Fields (기존) ──

        private void DrawComponentFields(Component comp, Type type, Component? baseComp = null)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                if (field.Name.StartsWith("_is") || field.Name == "gameObject" || field.Name == "enabled") continue;
                if (AssetNameExtractors.ContainsKey(field.FieldType)) continue;

                bool isPublic = field.IsPublic;
                bool hasSerialize = field.GetCustomAttribute<SerializeFieldAttribute>() != null;
                bool hasHide = field.GetCustomAttribute<HideInInspectorAttribute>() != null;

                if (!isPublic && !hasSerialize) continue;
                if (hasHide) continue;

                var header = field.GetCustomAttribute<HeaderAttribute>();
                if (header != null)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f), header.header);
                }

                // Override 감지
                bool isOverridden = false;
                object? baseVal = null;
                if (baseComp != null)
                {
                    try
                    {
                        var curVal = field.GetValue(comp);
                        baseVal = field.GetValue(baseComp);
                        isOverridden = !OverrideValuesEqual(curVal, baseVal, field.FieldType);
                    }
                    catch { /* 비교 실패 시 무시 */ }
                }

                if (isOverridden)
                    DrawOverrideMarker(comp, field.Name, field.FieldType, baseVal, v => field.SetValue(comp, v));

                var range = field.GetCustomAttribute<RangeAttribute>();
                var tooltip = field.GetCustomAttribute<TooltipAttribute>();
                bool readOnly = field.GetCustomAttribute<ReadOnlyInInspectorAttribute>() != null;
                var intDropdown = field.GetCustomAttribute<IntDropdownAttribute>();

                DrawValue(comp, field.Name, field.FieldType,
                    () => field.GetValue(comp),
                    v => field.SetValue(comp, v),
                    range, tooltip, readOnly, intDropdown);
            }
        }

        // ── Properties (신규) ──

        private void DrawComponentProperties(Component comp, Type type, Component? baseComp = null)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.GetMethod?.IsStatic == true) continue;
                if (SkipPropertyNames.Contains(prop.Name)) continue;
                if (SkipPropertyTypes.Contains(prop.PropertyType)) continue;
                if (prop.GetCustomAttribute<HideInInspectorAttribute>() != null) continue;

                // 지원하는 타입만 표시
                if (!IsSupportedType(prop.PropertyType)) continue;

                // Override 감지
                bool isOverridden = false;
                object? baseVal = null;
                if (baseComp != null)
                {
                    try
                    {
                        var curVal = prop.GetValue(comp);
                        baseVal = prop.GetValue(baseComp);
                        isOverridden = !OverrideValuesEqual(curVal, baseVal, prop.PropertyType);
                    }
                    catch { /* 비교 실패 시 무시 */ }
                }

                if (isOverridden)
                    DrawOverrideMarker(comp, prop.Name, prop.PropertyType, baseVal, v => prop.SetValue(comp, v));

                var range = prop.GetCustomAttribute<RangeAttribute>();
                var tooltip = prop.GetCustomAttribute<TooltipAttribute>();
                bool readOnly = prop.GetCustomAttribute<ReadOnlyInInspectorAttribute>() != null;
                var intDropdown = prop.GetCustomAttribute<IntDropdownAttribute>();

                DrawValue(comp, prop.Name, prop.PropertyType,
                    () => prop.GetValue(comp),
                    v => prop.SetValue(comp, v),
                    range, tooltip, readOnly, intDropdown);
            }
        }

        /// <summary>Override 마커 (◆) + Revert 버튼을 DrawValue 앞에 인라인 표시.</summary>
        private void DrawOverrideMarker(Component comp, string memberName, Type memberType, object? baseVal, Action<object> setter)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1.0f, 0.6f, 0.2f, 1.0f));
            ImGui.Text("\u25c6"); // ◆
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text($"Override: {memberName}");
                if (baseVal != null)
                    ImGui.TextDisabled($"Base: {baseVal}");
                ImGui.TextDisabled("Right-click to revert");
                ImGui.EndTooltip();
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                if (baseVal != null)
                {
                    var oldVal = memberType == typeof(float) ? (object)(float)0 : null;
                    try { oldVal = memberType.IsValueType ? Activator.CreateInstance(memberType) : null; } catch { }
                    // Revert to base value
                    setter(baseVal);
                    UndoSystem.Record(new SetPropertyAction(
                        $"Revert {memberName}", _currentInspectedGoId,
                        comp.GetType().Name, memberName, oldVal, baseVal));
                    SceneManager.GetActiveScene().isDirty = true;
                }
            }

            ImGui.SameLine();
        }

        // ── 에셋 참조 (리플렉션 기반 자동 감지) ──

        private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr"
        };

        // Pingable 에셋 타입 → name 추출 규칙
        private static readonly Dictionary<Type, Func<object, string?>> AssetNameExtractors = new()
        {
            [typeof(Mesh)] = obj => (obj as Mesh)?.name,
            [typeof(Texture2D)] = obj => (obj as Texture2D)?.name,
            [typeof(Sprite)] = obj => {
                var s = obj as Sprite;
                return !string.IsNullOrEmpty(s?.spriteName) ? s.spriteName : s?.texture?.name;
            },
            [typeof(Material)] = obj => (obj as Material)?.name,
            [typeof(Font)] = obj => (obj as Font)?.name,
            [typeof(MipMesh)] = obj => {
                var mm = obj as MipMesh;
                return mm != null && mm.LodCount > 0 ? $"{mm.lodMeshes[0].name} ({mm.LodCount} LODs)" : null;
            },
            [typeof(AnimationClip)] = obj => (obj as AnimationClip)?.name,
            [typeof(PostProcessProfile)] = obj => (obj as PostProcessProfile)?.name,
            [typeof(GameObject)] = obj => (obj as GameObject)?.name,
        };

        private void DrawAssetReferences(Component comp, Type type, bool readOnly = false)
        {
            // Fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (AssetNameExtractors.TryGetValue(field.FieldType, out var extractor))
                {
                    var val = field.GetValue(comp);
                    Action<object?>? setter = readOnly ? null : v => field.SetValue(comp, v);
                    DrawPingableLabel(field.Name, val != null ? extractor(val) : null, val,
                        comp, field.Name, field.FieldType, setter);
                }
            }

            // Properties
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (SkipPropertyNames.Contains(prop.Name)) continue;
                if (AssetNameExtractors.TryGetValue(prop.PropertyType, out var extractor))
                {
                    var val = prop.GetValue(comp);
                    Action<object?>? setter = readOnly ? null : (prop.CanWrite ? v => prop.SetValue(comp, v) : null);
                    DrawPingableLabel(prop.Name, val != null ? extractor(val) : null, val,
                        comp, prop.Name, prop.PropertyType, setter);
                }
            }
        }

        private void DrawPingableLabel(string label, string? assetName, object? asset = null,
            Component? comp = null, string? memberName = null, Type? memberType = null,
            Action<object?>? setter = null)
        {
            string displayName = string.IsNullOrEmpty(assetName) ? "(None)" : assetName;
            ImGui.Text(label);
            ImGui.SameLine();

            // Reserve space for browse button
            float availW = ImGui.GetContentRegionAvail().X;
            var typeFilter = GetTypeFilter(memberType);
            bool hasBrowse = setter != null && !string.IsNullOrEmpty(typeFilter);
            float selectableW = hasBrowse ? availW - 24f : availW;

            var assetPath = FindAssetPath(asset) ?? FindAssetPathByName(assetName);
            if (assetPath != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{displayName}##{label}", false, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(selectableW, 0)))
                    EditorBridge.PingAsset(assetPath);
                ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(assetPath);
            }
            else
            {
                ImGui.Button($"{displayName}##{label}", new System.Numerics.Vector2(selectableW, 0));
            }

            // Drag-drop target for Material / Sprite / AnimationClip / PostProcessProfile / GameObject fields
            if (setter != null && (memberType == typeof(Material) || memberType == typeof(Sprite) || memberType == typeof(AnimationClip) || memberType == typeof(PostProcessProfile) || memberType == typeof(GameObject)))
            {
                if (ImGui.BeginDragDropTarget())
                {
                    unsafe
                    {
                        var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                        if (payload.NativePtr != null)
                        {
                            var droppedPath = ImGuiProjectPanel._draggedAssetPath;
                            if (!string.IsNullOrEmpty(droppedPath))
                            {
                                if (memberType == typeof(Material))
                                {
                                    bool isMaterial = droppedPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
                                    if (!isMaterial && SubAssetPath.TryParse(droppedPath, out _, out var subType, out _))
                                        isMaterial = subType == "Material";

                                    if (isMaterial)
                                    {
                                        var db = Resources.GetAssetDatabase();
                                        var newMat = db?.Load<Material>(droppedPath);
                                        if (newMat != null && comp != null && memberName != null)
                                        {
                                            var oldVal = asset;
                                            setter(newMat);
                                            UndoSystem.Record(new SetPropertyAction(
                                                $"Set {label}",
                                                comp.gameObject.GetInstanceID(),
                                                comp.GetType().FullName!,
                                                memberName,
                                                oldVal, newMat));
                                        }
                                    }
                                }
                                else if (memberType == typeof(Sprite))
                                {
                                    bool isSpriteTexture = false;
                                    if (TextureExtensions.Contains(Path.GetExtension(droppedPath)))
                                    {
                                        var meta = IronRose.AssetPipeline.RoseMetadata.LoadOrCreate(droppedPath);
                                        isSpriteTexture = meta.importer.TryGetValue("texture_type", out var tt)
                                                          && tt?.ToString() == "Sprite";
                                    }
                                    bool isSpriteSubAsset = false;
                                    if (!isSpriteTexture && SubAssetPath.TryParse(droppedPath, out _, out var subType2, out _))
                                        isSpriteSubAsset = subType2 == "Sprite";

                                    if (isSpriteTexture || isSpriteSubAsset)
                                    {
                                        var db = Resources.GetAssetDatabase();
                                        var newSprite = db?.Load<Sprite>(droppedPath);
                                        if (newSprite != null && comp != null && memberName != null)
                                        {
                                            var oldVal = asset;
                                            setter(newSprite);
                                            UndoSystem.Record(new SetPropertyAction(
                                                $"Set {label}",
                                                comp.gameObject.GetInstanceID(),
                                                comp.GetType().FullName!,
                                                memberName,
                                                oldVal, newSprite));
                                        }
                                    }
                                }
                                else if (memberType == typeof(AnimationClip))
                                {
                                    if (droppedPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var db = Resources.GetAssetDatabase();
                                        var newClip = db?.Load<AnimationClip>(droppedPath);
                                        if (newClip != null && comp != null && memberName != null)
                                        {
                                            var oldVal = asset;
                                            setter(newClip);
                                            UndoSystem.Record(new SetPropertyAction(
                                                $"Set {label}",
                                                comp.gameObject.GetInstanceID(),
                                                comp.GetType().FullName!,
                                                memberName,
                                                oldVal, newClip));
                                        }
                                    }
                                }
                                else if (memberType == typeof(PostProcessProfile))
                                {
                                    if (droppedPath.EndsWith(".ppprofile", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var db = Resources.GetAssetDatabase();
                                        var newProfile = db?.Load<PostProcessProfile>(droppedPath);
                                        if (newProfile != null && comp != null && memberName != null)
                                        {
                                            var oldVal = asset;
                                            setter(newProfile);
                                            // PostProcessVolume인 경우 profileGuid도 함께 갱신
                                            if (comp is PostProcessVolume ppv)
                                            {
                                                var guid = db?.GetGuidFromPath(droppedPath);
                                                ppv.profileGuid = guid;
                                            }
                                            UndoSystem.Record(new SetPropertyAction(
                                                $"Set {label}",
                                                comp.gameObject.GetInstanceID(),
                                                comp.GetType().FullName!,
                                                memberName,
                                                oldVal, newProfile));
                                        }
                                    }
                                }
                                else if (memberType == typeof(GameObject))
                                {
                                    if (droppedPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var db = Resources.GetAssetDatabase();
                                        var newPrefab = db?.Load<GameObject>(droppedPath);
                                        if (newPrefab != null && comp != null && memberName != null)
                                        {
                                            var oldVal = asset;
                                            setter(newPrefab);
                                            UndoSystem.Record(new SetPropertyAction(
                                                $"Set {label}",
                                                comp.gameObject.GetInstanceID(),
                                                comp.GetType().FullName!,
                                                memberName,
                                                oldVal, newPrefab));
                                        }
                                    }
                                }
                            }
                        }
                    }
                    ImGui.EndDragDropTarget();
                }
            }

            // Browse button (◎)
            if (hasBrowse)
            {
                ImGui.SameLine();
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
                if (ImGui.Button($"\u25ce##{label}_browse"))
                {
                    var currentGuid = FindCurrentGuid(asset);
                    var capturedAsset = asset;
                    var capturedSetter = setter!;
                    var capturedComp = comp;
                    var capturedMemberName = memberName;
                    var capturedMemberType = memberType!;
                    var capturedLabel = label;

                    OpenAssetBrowser($"Select {label}", typeFilter, currentGuid, newGuid =>
                    {
                        var db = Resources.GetAssetDatabase();
                        if (db == null) return;

                        if (string.IsNullOrEmpty(newGuid))
                        {
                            var oldVal = capturedAsset;
                            capturedSetter(null);
                            if (capturedComp is PostProcessVolume ppvClear)
                                ppvClear.profileGuid = null;
                            if (capturedComp != null && capturedMemberName != null)
                            {
                                UndoSystem.Record(new SetPropertyAction(
                                    $"Clear {capturedLabel}",
                                    capturedComp.gameObject.GetInstanceID(),
                                    capturedComp.GetType().FullName!,
                                    capturedMemberName,
                                    oldVal, null));
                            }
                        }
                        else
                        {
                            object? newAsset = capturedMemberType switch
                            {
                                _ when capturedMemberType == typeof(Material)      => db.LoadByGuid<Material>(newGuid),
                                _ when capturedMemberType == typeof(Mesh)          => db.LoadByGuid<Mesh>(newGuid),
                                _ when capturedMemberType == typeof(MipMesh)       => db.LoadByGuid<MipMesh>(newGuid),
                                _ when capturedMemberType == typeof(Texture2D)     => db.LoadByGuid<Texture2D>(newGuid),
                                _ when capturedMemberType == typeof(Sprite)        => db.LoadByGuid<Sprite>(newGuid),
                                _ when capturedMemberType == typeof(Font)          => db.LoadByGuid<Font>(newGuid),
                                _ when capturedMemberType == typeof(AnimationClip) => db.LoadByGuid<AnimationClip>(newGuid),
                                _ when capturedMemberType == typeof(PostProcessProfile) => db.LoadByGuid<PostProcessProfile>(newGuid),
                                _ when capturedMemberType == typeof(GameObject) => db.LoadByGuid<GameObject>(newGuid),
                                _ => null,
                            };

                            if (newAsset != null)
                            {
                                var oldVal = capturedAsset;
                                capturedSetter(newAsset);
                                // PostProcessVolume인 경우 profileGuid도 함께 갱신
                                if (capturedComp is PostProcessVolume ppv2 && newAsset is PostProcessProfile)
                                    ppv2.profileGuid = newGuid;
                                if (capturedComp != null && capturedMemberName != null)
                                {
                                    UndoSystem.Record(new SetPropertyAction(
                                        $"Set {capturedLabel}",
                                        capturedComp.gameObject.GetInstanceID(),
                                        capturedComp.GetType().FullName!,
                                        capturedMemberName,
                                        oldVal, newAsset));
                                }
                            }
                        }
                    });
                }
                ImGui.PopStyleVar();
            }
        }

        private static string? FindAssetPath(object? asset)
        {
            if (asset == null) return null;
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            switch (asset)
            {
                case Mesh mesh:
                    return db.FindPathForMesh(mesh);
                case Material mat:
                {
                    var guid = db.FindGuidForMaterial(mat);
                    if (guid == null) return null;
                    var path = db.GetPathFromGuid(guid);
                    if (path != null && SubAssetPath.TryParse(path, out var filePath, out _, out _))
                        return filePath;
                    return path;
                }
                case Sprite sprite:
                {
                    var guid = db.FindGuidForSprite(sprite);
                    if (guid == null) return null;
                    var path = db.GetPathFromGuid(guid);
                    if (path != null && SubAssetPath.TryParse(path, out var filePath2, out _, out _))
                        return filePath2;
                    return path;
                }
                case MipMesh mip:
                    if (mip.LodCount > 0)
                        return db.FindPathForMesh(mip.lodMeshes[0]);
                    return null;
                case GameObject go:
                {
                    var guid = db.FindGuidForPrefab(go);
                    return guid != null ? db.GetPathFromGuid(guid) : null;
                }
                default:
                    return null;
            }
        }

        private static string? FindAssetPathByName(string? assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return null;

            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            return db.GetAllAssetPaths()
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == assetName);
        }

        private static bool IsSupportedElementType(Type t)
        {
            return t == typeof(float) || t == typeof(int) || t == typeof(bool)
                || t == typeof(string) || t == typeof(Vector2) || t == typeof(Vector3)
                || t == typeof(Vector4) || t == typeof(Quaternion)
                || t == typeof(Color) || t.IsEnum
                || t == typeof(long) || t == typeof(double) || t == typeof(byte);
        }

        private static bool IsSceneObjectReferenceType(Type t)
        {
            return t == typeof(GameObject) || typeof(Component).IsAssignableFrom(t);
        }

        private static bool IsNestedSerializableType(Type t)
        {
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return false;
            if (IsSupportedElementType(t)) return false;
            if (IsSceneObjectReferenceType(t)) return false;
            if (AssetNameExtractors.ContainsKey(t)) return false;
            return Attribute.IsDefined(t, typeof(SerializableAttribute))
                && (t.IsValueType || t.IsClass);
        }

        private static bool IsSupportedType(Type t)
        {
            if (IsSupportedElementType(t)) return true;
            if (IsSceneObjectReferenceType(t)) return true;
            if (IsNestedSerializableType(t)) return true;
            if (t.IsArray && IsSupportedElementType(t.GetElementType()!)) return true;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)
                && IsSupportedElementType(t.GetGenericArguments()[0])) return true;
            return false;
        }

        // ── 씬 오브젝트 참조 편집기 ──

        private void DrawGameObjectReferenceField(Component owner, string label, string widgetId,
            Func<object?> getter, Action<object> setter)
        {
            var current = getter() as GameObject;
            string displayName = current != null && !current._isDestroyed ? current.name : "(None)";
            string compTypeName = owner.GetType().Name;
            string popupId = $"##sceneref_popup_{widgetId}";

            bool hasLayout = !label.StartsWith("##");
            if (hasLayout)
            {
                float availWidth = ImGui.GetContentRegionAvail().X;
                float labelWidth = availWidth * EditorWidgets.LabelWidthRatio;
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(label);
                ImGui.SameLine(labelWidth);
            }

            // 버튼 영역: name + ◎ 브라우즈
            float totalW = ImGui.GetContentRegionAvail().X;
            float browseW = 24f;
            float nameW = totalW - browseW - ImGui.GetStyle().ItemSpacing.X;

            // 이름 버튼 (클릭 시 해당 GO 선택/핑)
            if (current != null && !current._isDestroyed)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{displayName}##{widgetId}_name", false, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(nameW, 0)))
                    EditorSelection.SelectGameObject(current);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.Button($"{displayName}##{widgetId}_name", new System.Numerics.Vector2(nameW, 0));
            }

            ImGui.SameLine();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
            if (ImGui.Button($"\u25ce##{widgetId}_browse"))
                ImGui.OpenPopup(popupId);
            ImGui.PopStyleVar();

            if (ImGui.BeginPopup(popupId))
            {
                if (ImGui.Selectable("(None)", current == null))
                {
                    var oldVal = getter();
                    setter(null!);
                    UndoSystem.Record(new SetPropertyAction(
                        $"Clear {label}", _currentInspectedGoId,
                        compTypeName, label, oldVal, null));
                }
                foreach (var go in SceneManager.AllGameObjects)
                {
                    if (go._isDestroyed || go._isEditorInternal) continue;
                    bool selected = current != null && current.guid == go.guid;
                    if (ImGui.Selectable($"{go.name}##{go.guid}", selected))
                    {
                        var oldVal = getter();
                        setter(go);
                        UndoSystem.Record(new SetPropertyAction(
                            $"Set {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, go));
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndPopup();
            }
        }

        private void DrawComponentReferenceField(Component owner, string label, string widgetId,
            Type componentType, Func<object?> getter, Action<object> setter)
        {
            var current = getter() as Component;
            string displayName = current != null && !current._isDestroyed
                ? $"{current.gameObject.name}"
                : "(None)";
            string compTypeName = owner.GetType().Name;
            string popupId = $"##compref_popup_{widgetId}";

            bool hasLayout = !label.StartsWith("##");
            if (hasLayout)
            {
                float availWidth = ImGui.GetContentRegionAvail().X;
                float labelWidth = availWidth * EditorWidgets.LabelWidthRatio;
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(label);
                ImGui.SameLine(labelWidth);
            }

            float totalW = ImGui.GetContentRegionAvail().X;
            float browseW = 24f;
            float nameW = totalW - browseW - ImGui.GetStyle().ItemSpacing.X;

            if (current != null && !current._isDestroyed)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.600f, 0.380f, 0.350f, 1f));
                if (ImGui.Selectable($"{displayName}##{widgetId}_name", false, ImGuiSelectableFlags.None,
                        new System.Numerics.Vector2(nameW, 0)))
                    EditorSelection.SelectGameObject(current.gameObject);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.Button($"{displayName}##{widgetId}_name", new System.Numerics.Vector2(nameW, 0));
            }

            ImGui.SameLine();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(2, 0));
            if (ImGui.Button($"\u25ce##{widgetId}_browse"))
                ImGui.OpenPopup(popupId);
            ImGui.PopStyleVar();

            if (ImGui.BeginPopup(popupId))
            {
                if (ImGui.Selectable("(None)", current == null))
                {
                    var oldVal = getter();
                    setter(null!);
                    UndoSystem.Record(new SetPropertyAction(
                        $"Clear {label}", _currentInspectedGoId,
                        compTypeName, label, oldVal, null));
                }
                foreach (var go in SceneManager.AllGameObjects)
                {
                    if (go._isDestroyed || go._isEditorInternal) continue;
                    var comp = go.GetComponent(componentType);
                    if (comp == null) continue;
                    bool selected = current != null && ReferenceEquals(current, comp);
                    if (ImGui.Selectable($"{go.name}##{go.guid}", selected))
                    {
                        var oldVal = getter();
                        setter(comp);
                        UndoSystem.Record(new SetPropertyAction(
                            $"Set {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, comp));
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndPopup();
            }
        }

        // ── 중첩 직렬화 구조체 편집기 ──

        private void DrawNestedSerializableType(Component comp, string label, Type valueType,
            Func<object?> getter, Action<object> setter, string widgetId, int depth = 0)
        {
            if (depth > 5) { ImGui.TextDisabled($"{label}: (too deep)"); return; }

            object? structVal = getter();
            if (structVal == null && !valueType.IsValueType)
            {
                string wLabel = EditorWidgets.BeginPropertyRow(label);
                ImGui.TextDisabled("(null)");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Create##{widgetId}"))
                {
                    structVal = Activator.CreateInstance(valueType);
                    setter(structVal!);
                }
                return;
            }

            if (structVal == null) structVal = Activator.CreateInstance(valueType)!;

            bool open = ImGui.TreeNodeEx($"{label}##{widgetId}", ImGuiTreeNodeFlags.Framed);
            if (!open) return;

            string compTypeName = comp.GetType().Name;
            var fields = valueType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.IsLiteral || field.IsInitOnly) continue;
                bool isPublic = field.IsPublic;
                bool hasSerialize = field.GetCustomAttribute<SerializeFieldAttribute>() != null;
                if (!isPublic && !hasSerialize) continue;
                if (field.GetCustomAttribute<HideInInspectorAttribute>() != null) continue;

                string fieldWidgetId = $"{widgetId}.{field.Name}";
                object capturedStruct = structVal;

                if (IsNestedSerializableType(field.FieldType))
                {
                    DrawNestedSerializableType(comp, field.Name, field.FieldType,
                        () => field.GetValue(capturedStruct),
                        v =>
                        {
                            field.SetValue(capturedStruct, v);
                            setter(capturedStruct);
                        },
                        fieldWidgetId, depth + 1);
                }
                else
                {
                    DrawValue(comp, field.Name, field.FieldType,
                        () => field.GetValue(capturedStruct),
                        v =>
                        {
                            field.SetValue(capturedStruct, v);
                            setter(capturedStruct);
                        },
                        field.GetCustomAttribute<RangeAttribute>(),
                        field.GetCustomAttribute<TooltipAttribute>(),
                        field.GetCustomAttribute<ReadOnlyInInspectorAttribute>() != null,
                        field.GetCustomAttribute<IntDropdownAttribute>());
                }
            }

            ImGui.TreePop();
        }

        // ── 배열/리스트 편집기 ──

        private void DrawArrayOrListValue(Component comp, string label, Type valueType,
            Func<object?> getter, Action<object> setter, string widgetId)
        {
            bool isArray = valueType.IsArray;
            Type elemType = isArray ? valueType.GetElementType()! : valueType.GetGenericArguments()[0];

            object? collection = getter();
            int count = 0;
            if (isArray && collection is Array arr) count = arr.Length;
            else if (collection is System.Collections.IList list) count = list.Count;

            string compTypeName = comp.GetType().Name;
            bool open = ImGui.TreeNodeEx($"{label} [{count}]##{widgetId}", ImGuiTreeNodeFlags.Framed);
            if (!open) return;

            // Size 필드
            int newSize = count;
            if (EditorWidgets.DragIntClickable(widgetId + ".size", "Size", ref newSize, 0.1f))
            {
                newSize = Math.Max(0, newSize);
                if (newSize != count)
                {
                    var oldVal = CloneCollection(collection, isArray, elemType);
                    ResizeCollection(ref collection, elemType, isArray, newSize);
                    setter(collection!);
                    UndoSystem.Record(new SetPropertyAction(
                        $"Resize {label}", _currentInspectedGoId,
                        compTypeName, label, oldVal, CloneCollection(collection, isArray, elemType)));
                }
            }

            // 각 요소 렌더링
            var ilist = collection as System.Collections.IList;
            if (ilist != null)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    int idx = i;
                    string elemWidgetId = $"{widgetId}[{i}]";
                    DrawValue(comp, $"[{i}]", elemType,
                        () => ilist[idx],
                        v =>
                        {
                            ilist[idx] = v;
                            setter(collection!);
                        },
                        null, null);
                }
            }

            // +/- 버튼
            if (ImGui.Button($"+##{widgetId}.add"))
            {
                var oldVal = CloneCollection(collection, isArray, elemType);
                ResizeCollection(ref collection, elemType, isArray, count + 1);
                setter(collection!);
                UndoSystem.Record(new SetPropertyAction(
                    $"Add to {label}", _currentInspectedGoId,
                    compTypeName, label, oldVal, CloneCollection(collection, isArray, elemType)));
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(count == 0);
            if (ImGui.Button($"-##{widgetId}.remove"))
            {
                var oldVal = CloneCollection(collection, isArray, elemType);
                ResizeCollection(ref collection, elemType, isArray, count - 1);
                setter(collection!);
                UndoSystem.Record(new SetPropertyAction(
                    $"Remove from {label}", _currentInspectedGoId,
                    compTypeName, label, oldVal, CloneCollection(collection, isArray, elemType)));
            }
            ImGui.EndDisabled();

            ImGui.TreePop();
        }

        private static void ResizeCollection(ref object? collection, Type elemType, bool isArray, int newSize)
        {
            if (isArray)
            {
                var oldArr = collection as Array;
                var newArr = Array.CreateInstance(elemType, newSize);
                int copyLen = Math.Min(oldArr?.Length ?? 0, newSize);
                if (oldArr != null && copyLen > 0)
                    Array.Copy(oldArr, newArr, copyLen);
                collection = newArr;
            }
            else
            {
                var list = collection as System.Collections.IList;
                if (list == null)
                {
                    var listType = typeof(List<>).MakeGenericType(elemType);
                    list = (System.Collections.IList)Activator.CreateInstance(listType)!;
                }
                while (list.Count > newSize)
                    list.RemoveAt(list.Count - 1);
                while (list.Count < newSize)
                    list.Add(elemType.IsValueType ? Activator.CreateInstance(elemType) : null);
                collection = list;
            }
        }

        private static object? CloneCollection(object? collection, bool isArray, Type elemType)
        {
            if (collection == null) return null;
            if (isArray && collection is Array srcArr)
            {
                var clone = Array.CreateInstance(elemType, srcArr.Length);
                Array.Copy(srcArr, clone, srcArr.Length);
                return clone;
            }
            if (collection is System.Collections.IList srcList)
            {
                var listType = typeof(List<>).MakeGenericType(elemType);
                var clone = (System.Collections.IList)Activator.CreateInstance(listType)!;
                foreach (var item in srcList) clone.Add(item);
                return clone;
            }
            return null;
        }

        // ── 공통 값 편집기 ──

        private void DrawValue(Component comp, string label, Type valueType,
            Func<object?> getter, Action<object> setter,
            RangeAttribute? range, TooltipAttribute? tooltip, bool readOnly = false,
            IntDropdownAttribute? intDropdown = null)
        {
            try
            {
                if (readOnly) ImGui.BeginDisabled();
                object? val = getter();
                string compTypeName = comp.GetType().Name;
                string widgetId = $"{compTypeName}.{label}";

                if (valueType == typeof(float))
                {
                    float f = (float)(val ?? 0f);
                    bool changed;
                    bool sliderDeactivated = false;
                    if (range != null)
                    {
                        changed = EditorWidgets.SliderFloatWithInput(widgetId, label, ref f, range.min, range.max, out sliderDeactivated);
                    }
                    else
                        changed = EditorWidgets.DragFloatClickable(widgetId, label, ref f, 0.01f, "%.2f");
                    if (changed)
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(f);
                    }
                    if ((ImGui.IsItemDeactivatedAfterEdit() || sliderDeactivated) &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(int) && intDropdown != null)
                {
                    int i = (int)(val ?? 0);
                    int current = Array.IndexOf(intDropdown.values, i);
                    if (current < 0) current = 0;
                    string preview = intDropdown.labels[current];
                    // center-align preview text
                    float previewW = ImGui.CalcTextSize(preview).X;
                    float comboW = ImGui.CalcItemWidth();
                    float pad = (comboW - previewW) * 0.5f;
                    string centeredPreview = pad > 0 ? new string(' ', Math.Max(1, (int)(pad / ImGui.CalcTextSize(" ").X))) + preview : preview;
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    if (ImGui.BeginCombo(wLabel, centeredPreview))
                    {
                        float popupW = ImGui.GetContentRegionAvail().X;
                        for (int idx = 0; idx < intDropdown.labels.Length; idx++)
                        {
                            bool selected = idx == current;
                            // center-align each item
                            float textW = ImGui.CalcTextSize(intDropdown.labels[idx]).X;
                            float offset = (popupW - textW) * 0.5f;
                            if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                            if (ImGui.Selectable(intDropdown.labels[idx], selected))
                            {
                                var newVal = intDropdown.values[idx];
                                setter(newVal);
                                UndoSystem.Record(new SetPropertyAction(
                                    $"Change {label}", _currentInspectedGoId,
                                    compTypeName, label, val, newVal));
                            }
                            if (selected) ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                }
                else if (valueType == typeof(int))
                {
                    int i = (int)(val ?? 0);
                    bool changed;
                    bool sliderDeactivated = false;
                    if (range != null)
                    {
                        changed = EditorWidgets.SliderIntWithInput(widgetId, label, ref i, (int)range.min, (int)range.max, out sliderDeactivated);
                    }
                    else
                        changed = EditorWidgets.DragIntClickable(widgetId, label, ref i);
                    if (changed)
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(i);
                    }
                    if ((ImGui.IsItemDeactivatedAfterEdit() || sliderDeactivated) &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(bool))
                {
                    bool b = (bool)(val ?? false);
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    if (ImGui.Checkbox(wLabel, ref b))
                    {
                        setter(b);
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, val, b));
                    }
                }
                else if (valueType == typeof(string))
                {
                    string s = (string)(val ?? "");
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    if (ImGui.InputText(wLabel, ref s, 256))
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(s);
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(Vector2))
                {
                    var v = (Vector2)(val ?? Vector2.zero);
                    var nv = new System.Numerics.Vector2(v.x, v.y);
                    if (EditorWidgets.DragFloat2Clickable(widgetId, label, ref nv, 0.1f))
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(new Vector2(nv.X, nv.Y));
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(Vector3))
                {
                    var v = (Vector3)(val ?? Vector3.zero);
                    var nv = new System.Numerics.Vector3(v.x, v.y, v.z);
                    if (EditorWidgets.DragFloat3Clickable(widgetId, label, ref nv, 0.1f))
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(new Vector3(nv.X, nv.Y, nv.Z));
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(Color))
                {
                    var c = (Color)(val ?? Color.white);
                    // ColorEdit4는 내부적으로 여러 ImGui 아이템을 submit 하므로
                    // ImGui.IsItemDeactivatedAfterEdit()로는 picker 편집 종료를 감지할 수 없다.
                    // out 신호를 직접 사용한다.
                    if (EditorWidgets.ColorEdit4(label, ref c, out bool colorDeactivated))
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(c);
                    }
                    if (colorDeactivated &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType.IsEnum)
                {
                    var names = Enum.GetNames(valueType);
                    int current = Array.IndexOf(names, val?.ToString() ?? "");
                    if (current < 0) current = 0;
                    string preview = names[current];
                    string wLabel = EditorWidgets.BeginPropertyRow(label);
                    float previewW = ImGui.CalcTextSize(preview).X;
                    float comboW = ImGui.CalcItemWidth();
                    float pad = (comboW - previewW) * 0.5f;
                    string centeredPreview = pad > 0 ? new string(' ', Math.Max(1, (int)(pad / ImGui.CalcTextSize(" ").X))) + preview : preview;
                    if (ImGui.BeginCombo(wLabel, centeredPreview))
                    {
                        float popupW = ImGui.GetContentRegionAvail().X;
                        for (int idx = 0; idx < names.Length; idx++)
                        {
                            bool selected = idx == current;
                            float textW = ImGui.CalcTextSize(names[idx]).X;
                            float offset = (popupW - textW) * 0.5f;
                            if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                            if (ImGui.Selectable(names[idx], selected))
                            {
                                var newVal = Enum.Parse(valueType, names[idx]);
                                setter(newVal);
                                UndoSystem.Record(new SetPropertyAction(
                                    $"Change {label}", _currentInspectedGoId,
                                    compTypeName, label, val, newVal));
                            }
                            if (selected) ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                }
                else if (valueType == typeof(Quaternion))
                {
                    var q = (Quaternion)(val ?? Quaternion.identity);
                    var euler = q.eulerAngles;
                    var nv = new System.Numerics.Vector3(euler.x, euler.y, euler.z);
                    if (EditorWidgets.DragFloat3Clickable(widgetId, label, ref nv, 0.5f))
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(Quaternion.Euler(nv.X, nv.Y, nv.Z));
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(Vector4))
                {
                    var v = (Vector4)(val ?? Vector4.zero);
                    var nv = new System.Numerics.Vector4(v.x, v.y, v.z, v.w);
                    if (EditorWidgets.DragFloat4Clickable(widgetId, label, ref nv, 0.1f))
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter(new Vector4(nv.X, nv.Y, nv.Z, nv.W));
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(long))
                {
                    int i = (int)Math.Clamp((long)(val ?? 0L), int.MinValue, int.MaxValue);
                    bool changed = EditorWidgets.DragIntClickable(widgetId, label, ref i);
                    if (changed)
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter((long)i);
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(double))
                {
                    float f = (float)(double)(val ?? 0.0);
                    bool changed;
                    bool sliderDeactivated = false;
                    if (range != null)
                        changed = EditorWidgets.SliderFloatWithInput(widgetId, label, ref f, range.min, range.max, out sliderDeactivated);
                    else
                        changed = EditorWidgets.DragFloatClickable(widgetId, label, ref f, 0.01f, "%.4f");
                    if (changed)
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter((double)f);
                    }
                    if ((ImGui.IsItemDeactivatedAfterEdit() || sliderDeactivated) &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(byte))
                {
                    int i = (byte)(val ?? (byte)0);
                    bool changed = EditorWidgets.DragIntClickable(widgetId, label, ref i);
                    i = Math.Clamp(i, 0, 255);
                    if (changed)
                    {
                        _undoTracker.BeginEdit(widgetId, val);
                        setter((byte)i);
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit() &&
                        _undoTracker.EndEdit(widgetId, out var oldVal))
                    {
                        UndoSystem.Record(new SetPropertyAction(
                            $"Change {label}", _currentInspectedGoId,
                            compTypeName, label, oldVal, getter()));
                    }
                }
                else if (valueType == typeof(GameObject))
                {
                    DrawGameObjectReferenceField(comp, label, widgetId, getter, setter);
                }
                else if (typeof(Component).IsAssignableFrom(valueType))
                {
                    DrawComponentReferenceField(comp, label, widgetId, valueType, getter, setter);
                }
                else if (IsNestedSerializableType(valueType))
                {
                    DrawNestedSerializableType(comp, label, valueType, getter, setter, widgetId);
                }
                else if ((valueType.IsArray && IsSupportedElementType(valueType.GetElementType()!))
                      || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(List<>)
                          && IsSupportedElementType(valueType.GetGenericArguments()[0])))
                {
                    DrawArrayOrListValue(comp, label, valueType, getter, setter, widgetId);
                }
                else
                {
                    EditorWidgets.BeginPropertyRow(label);
                    ImGui.TextDisabled($"{val}");
                }

                if (tooltip != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(tooltip.tooltip);

                if (readOnly) ImGui.EndDisabled();
            }
            catch
            {
                if (readOnly) ImGui.EndDisabled();
                ImGui.TextDisabled($"{label}: (error)");
            }
        }

        // ── Component Paste Values ──

        private void PasteComponentValues(Component target, TomlTable clipboard, int goId)
        {
            if (!clipboard.TryGetValue("fields", out var fieldsVal) || fieldsVal is not TomlTable fields)
                return;

            var type = target.GetType();
            var compTypeName = type.Name;
            var undoActions = new List<IUndoAction>();
            const BindingFlags bindFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var kvp in fields)
            {
                string memberName = kvp.Key;
                object tomlVal = kvp.Value;

                // Skip asset references (TomlTable with _assetGuid)
                if (tomlVal is TomlTable) continue;

                // Try field first
                var fi = type.GetField(memberName, bindFlags);
                if (fi != null && !fi.IsLiteral && !fi.IsInitOnly)
                {
                    var converted = ConvertTomlValue(tomlVal, fi.FieldType);
                    if (converted != null)
                    {
                        try
                        {
                            var oldVal = fi.GetValue(target);
                            fi.SetValue(target, converted);
                            undoActions.Add(new SetPropertyAction(
                                $"Paste {memberName}", goId, compTypeName, memberName, oldVal, converted));
                        }
                        catch { }
                    }
                    continue;
                }

                // Try property
                var pi = type.GetProperty(memberName, bindFlags);
                if (pi != null && pi.CanWrite)
                {
                    var converted = ConvertTomlValue(tomlVal, pi.PropertyType);
                    if (converted != null)
                    {
                        try
                        {
                            var oldVal = pi.GetValue(target);
                            pi.SetValue(target, converted);
                            undoActions.Add(new SetPropertyAction(
                                $"Paste {memberName}", goId, compTypeName, memberName, oldVal, converted));
                        }
                        catch { }
                    }
                }
            }

            if (undoActions.Count > 0)
            {
                UndoSystem.Record(new CompoundUndoAction($"Paste Values to {compTypeName}", undoActions));
                SceneManager.GetActiveScene().isDirty = true;
            }
        }

        private void PasteMultiComponentValues(List<Component> components, TomlTable clipboard)
        {
            if (!clipboard.TryGetValue("fields", out var fieldsVal) || fieldsVal is not TomlTable fields)
                return;

            var type = components[0].GetType();
            var compTypeName = type.Name;
            var allActions = new List<IUndoAction>();
            const BindingFlags bindFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var kvp in fields)
            {
                string memberName = kvp.Key;
                object tomlVal = kvp.Value;

                if (tomlVal is TomlTable) continue;

                var fi = type.GetField(memberName, bindFlags);
                if (fi != null && !fi.IsLiteral && !fi.IsInitOnly)
                {
                    var converted = ConvertTomlValue(tomlVal, fi.FieldType);
                    if (converted != null)
                    {
                        foreach (var comp in components)
                        {
                            try
                            {
                                var oldVal = fi.GetValue(comp);
                                fi.SetValue(comp, converted);
                                allActions.Add(new SetPropertyAction(
                                    $"Paste {memberName}", comp.gameObject.GetInstanceID(),
                                    compTypeName, memberName, oldVal, converted));
                            }
                            catch { }
                        }
                    }
                    continue;
                }

                var pi = type.GetProperty(memberName, bindFlags);
                if (pi != null && pi.CanWrite)
                {
                    var converted = ConvertTomlValue(tomlVal, pi.PropertyType);
                    if (converted != null)
                    {
                        foreach (var comp in components)
                        {
                            try
                            {
                                var oldVal = pi.GetValue(comp);
                                pi.SetValue(comp, converted);
                                allActions.Add(new SetPropertyAction(
                                    $"Paste {memberName}", comp.gameObject.GetInstanceID(),
                                    compTypeName, memberName, oldVal, converted));
                            }
                            catch { }
                        }
                    }
                }
            }

            if (allActions.Count > 0)
            {
                UndoSystem.Record(new CompoundUndoAction($"Paste Values to {compTypeName} (multi)", allActions));
                SceneManager.GetActiveScene().isDirty = true;
            }
        }

        private static object? ConvertTomlValue(object? tomlVal, Type targetType)
        {
            if (tomlVal == null) return null;

            if (targetType == typeof(float))
                return tomlVal switch { double d => (float)d, long l => (float)l, _ => null };
            if (targetType == typeof(int))
                return tomlVal switch { long l => (int)l, double d => (int)d, _ => null };
            if (targetType == typeof(bool) && tomlVal is bool b) return b;
            if (targetType == typeof(string)) return tomlVal.ToString();
            if (targetType == typeof(Vector3) && tomlVal is TomlArray arr3 && arr3.Count >= 3)
                return new Vector3(TomlFloat(arr3[0]), TomlFloat(arr3[1]), TomlFloat(arr3[2]));
            if (targetType == typeof(Color) && tomlVal is TomlArray arr4 && arr4.Count >= 4)
                return new Color(TomlFloat(arr4[0]), TomlFloat(arr4[1]), TomlFloat(arr4[2]), TomlFloat(arr4[3]));
            if (targetType.IsEnum && tomlVal is string s && Enum.TryParse(targetType, s, out var ev))
                return ev;

            return null;
        }

        private static float TomlFloat(object? val)
        {
            return val switch { double d => (float)d, long l => l, float f => f, int i => i, _ => 0f };
        }

        // ── Anchor Preset helpers ──

        private static void ApplyAnchorPresetKeepVisual(RectTransform rt, int goId, RectTransform.AnchorPreset preset)
        {
            var oldMin = rt.anchorMin;
            var oldMax = rt.anchorMax;
            var oldPivot = rt.pivot;
            var oldPos = rt.anchoredPosition;
            var oldSize = rt.sizeDelta;

            rt.SetAnchorPresetKeepVisual(preset, rt.GetParentSize());

            UndoSystem.Record(new CompoundUndoAction("Change Anchor Preset", new List<IUndoAction>
            {
                new SetPropertyAction("AnchorMin", goId, "RectTransform", "anchorMin", oldMin, rt.anchorMin),
                new SetPropertyAction("AnchorMax", goId, "RectTransform", "anchorMax", oldMax, rt.anchorMax),
                new SetPropertyAction("Pivot", goId, "RectTransform", "pivot", oldPivot, rt.pivot),
                new SetPropertyAction("AnchoredPosition", goId, "RectTransform", "anchoredPosition", oldPos, rt.anchoredPosition),
                new SetPropertyAction("SizeDelta", goId, "RectTransform", "sizeDelta", oldSize, rt.sizeDelta),
            }));
        }

        // ── Anchor Preset popup ──

        private static readonly (string label, RectTransform.AnchorPreset preset)[] _anchorPresets = new[]
        {
            ("↖  Top Left",          RectTransform.AnchorPreset.TopLeft),
            ("↑  Top Center",        RectTransform.AnchorPreset.TopCenter),
            ("↗  Top Right",         RectTransform.AnchorPreset.TopRight),
            ("←  Middle Left",       RectTransform.AnchorPreset.MiddleLeft),
            ("◉  Middle Center",     RectTransform.AnchorPreset.MiddleCenter),
            ("→  Middle Right",      RectTransform.AnchorPreset.MiddleRight),
            ("↙  Bottom Left",       RectTransform.AnchorPreset.BottomLeft),
            ("↓  Bottom Center",     RectTransform.AnchorPreset.BottomCenter),
            ("↘  Bottom Right",      RectTransform.AnchorPreset.BottomRight),
            ("↔  Top Stretch",       RectTransform.AnchorPreset.TopStretch),
            ("↔  Middle Stretch",    RectTransform.AnchorPreset.MiddleStretch),
            ("↔  Bottom Stretch",    RectTransform.AnchorPreset.BottomStretch),
            ("↕  Stretch Left",      RectTransform.AnchorPreset.StretchLeft),
            ("↕  Stretch Center",    RectTransform.AnchorPreset.StretchCenter),
            ("↕  Stretch Right",     RectTransform.AnchorPreset.StretchRight),
            ("⬜  Stretch All",       RectTransform.AnchorPreset.StretchAll),
        };

        private void DrawAnchorPresetButton(RectTransform rt, int goId)
        {
            // 현재 프리셋 감지
            string currentLabel = "Custom";
            foreach (var (label, preset) in _anchorPresets)
            {
                var tempRt = new RectTransform();
                tempRt.SetAnchorPreset(preset);
                if (MathF.Abs(rt.anchorMin.x - tempRt.anchorMin.x) < 1e-4f &&
                    MathF.Abs(rt.anchorMin.y - tempRt.anchorMin.y) < 1e-4f &&
                    MathF.Abs(rt.anchorMax.x - tempRt.anchorMax.x) < 1e-4f &&
                    MathF.Abs(rt.anchorMax.y - tempRt.anchorMax.y) < 1e-4f)
                {
                    currentLabel = label;
                    break;
                }
            }

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.BeginCombo("##AnchorPreset", currentLabel))
            {
                // 고정 앵커 (3x3 그리드)
                ImGui.TextDisabled("Fixed Position");
                for (int i = 0; i < 9; i++)
                {
                    var (label, preset) = _anchorPresets[i];
                    if (ImGui.Selectable(label))
                        ApplyAnchorPresetKeepVisual(rt, goId, preset);
                }

                ImGui.Separator();
                ImGui.TextDisabled("Horizontal Stretch");
                for (int i = 9; i < 12; i++)
                {
                    var (label, preset) = _anchorPresets[i];
                    if (ImGui.Selectable(label))
                        ApplyAnchorPresetKeepVisual(rt, goId, preset);
                }

                ImGui.Separator();
                ImGui.TextDisabled("Vertical Stretch");
                for (int i = 12; i < 15; i++)
                {
                    var (label, preset) = _anchorPresets[i];
                    if (ImGui.Selectable(label))
                        ApplyAnchorPresetKeepVisual(rt, goId, preset);
                }

                ImGui.Separator();
                ImGui.TextDisabled("Full Stretch");
                {
                    var (label, preset) = _anchorPresets[15];
                    if (ImGui.Selectable(label))
                        ApplyAnchorPresetKeepVisual(rt, goId, preset);
                }

                ImGui.EndCombo();
            }
        }

    }
}
