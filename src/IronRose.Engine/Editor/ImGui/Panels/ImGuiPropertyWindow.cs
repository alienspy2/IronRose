using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using IronRose.Engine.Editor;
using RoseEngine;
using Veldrid;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// 고정 대상을 추적하는 독립 Inspector 창.
    /// EditorSelection 변경에 따라가지 않으며, 대상이 삭제되면 invalid 표시.
    /// </summary>
    internal sealed class ImGuiPropertyWindow : IDisposable
    {
        private enum TargetKind { GameObject, Asset }

        private readonly TargetKind _kind;
        private readonly int _targetGoId;          // TargetKind.GameObject
        private readonly string? _targetAssetPath; // TargetKind.Asset

        private readonly string _windowTitle;
        private bool _isOpen = true;

        private readonly ImGuiInspectorPanel _inspector;

        private static int _nextId;

        // ── 요청 큐 (context menu → Overlay) ──
        private static readonly List<PendingRequest> _pendingRequests = new();

        public bool IsOpen => _isOpen;

        private ImGuiPropertyWindow(TargetKind kind, int goId, string? assetPath,
                                     string displayName, GraphicsDevice device,
                                     VeldridImGuiRenderer renderer)
        {
            _kind = kind;
            _targetGoId = goId;
            _targetAssetPath = assetPath;
            _windowTitle = $"Properties: {displayName}##prop_{_nextId++}";
            _inspector = new ImGuiInspectorPanel(device, renderer);
        }

        // ================================================================
        // Static request API
        // ================================================================

        public static void RequestOpenGameObject(int goId, string displayName)
            => _pendingRequests.Add(new PendingRequest(TargetKind.GameObject, goId, null, displayName));

        public static void RequestOpenAsset(string assetPath)
            => _pendingRequests.Add(new PendingRequest(TargetKind.Asset, 0, assetPath,
                Path.GetFileName(assetPath)));

        /// <summary>대기 중인 요청을 소비하여 새 윈도우 목록을 반환.</summary>
        public static List<ImGuiPropertyWindow> ConsumePendingRequests(
            GraphicsDevice device, VeldridImGuiRenderer renderer)
        {
            if (_pendingRequests.Count == 0) return _emptyList;

            var result = new List<ImGuiPropertyWindow>(_pendingRequests.Count);
            foreach (var req in _pendingRequests)
                result.Add(new ImGuiPropertyWindow(
                    req.Kind, req.GoId, req.AssetPath, req.DisplayName, device, renderer));
            _pendingRequests.Clear();
            return result;
        }

        private static readonly List<ImGuiPropertyWindow> _emptyList = new();

        // ================================================================
        // Drawing
        // ================================================================

        public void Draw()
        {
            if (!_isOpen) return;

            ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 600), ImGuiCond.FirstUseEver);

            if (ImGui.Begin(_windowTitle, ref _isOpen, ImGuiWindowFlags.NoDocking))
            {
                // ── Ctrl+C / X / V — Asset 창 간 복사/잘라내기/붙여넣기 ──
                if (_kind == TargetKind.Asset && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                    HandleAssetClipboardShortcuts();

                switch (_kind)
                {
                    case TargetKind.GameObject:
                        DrawGameObjectTarget();
                        break;
                    case TargetKind.Asset:
                        DrawAssetTarget();
                        break;
                }
            }
            ImGui.End();
        }

        private void DrawGameObjectTarget()
        {
            bool valid = false;
            foreach (var go in SceneManager.AllGameObjects)
            {
                if (!go._isDestroyed && go.GetInstanceID() == _targetGoId)
                {
                    valid = true;
                    break;
                }
            }

            if (!valid)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f),
                    "Invalid — target object has been destroyed.");
                ImGui.BeginDisabled();
                ImGui.TextDisabled("(no properties)");
                ImGui.EndDisabled();
                return;
            }

            _inspector.DrawGameObjectInspector(_targetGoId);
        }

        private void DrawAssetTarget()
        {
            if (_targetAssetPath == null || !File.Exists(_targetAssetPath))
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f),
                    "Invalid — target asset no longer exists.");
                ImGui.BeginDisabled();
                ImGui.TextDisabled("(no properties)");
                ImGui.EndDisabled();
                return;
            }

            _inspector.DrawAssetInspector(_targetAssetPath);
        }

        // ================================================================
        // Asset clipboard shortcuts
        // ================================================================

        private void HandleAssetClipboardShortcuts()
        {
            var io = ImGui.GetIO();
            if (io.WantTextInput) return;
            if (!io.KeyCtrl || io.KeyShift) return;

            if (_targetAssetPath == null) return;

            // Ctrl+C — 복사
            if (ImGui.IsKeyPressed(ImGuiKey.C))
            {
                EditorClipboard.CopyAssets(new[] { _targetAssetPath }, cut: false);
            }
            // Ctrl+X — 잘라내기
            else if (ImGui.IsKeyPressed(ImGuiKey.X))
            {
                EditorClipboard.CopyAssets(new[] { _targetAssetPath }, cut: true);
            }
            // Ctrl+V — 붙여넣기 (이 창의 에셋과 같은 디렉터리에)
            else if (ImGui.IsKeyPressed(ImGuiKey.V))
            {
                if (EditorClipboard.ClipboardKind != EditorClipboard.Kind.Assets)
                    return; // 형태가 다르면 무시

                var dir = Path.GetDirectoryName(_targetAssetPath);
                if (dir != null)
                    EditorClipboard.PasteAssets(dir);
            }
        }

        // ================================================================
        // Cleanup
        // ================================================================

        public void Dispose()
        {
            _inspector.DisposePreviews();
        }

        // ================================================================
        // Internal types
        // ================================================================

        private readonly struct PendingRequest
        {
            public readonly TargetKind Kind;
            public readonly int GoId;
            public readonly string? AssetPath;
            public readonly string DisplayName;

            public PendingRequest(TargetKind kind, int goId, string? assetPath, string displayName)
            {
                Kind = kind;
                GoId = goId;
                AssetPath = assetPath;
                DisplayName = displayName;
            }
        }
    }
}
