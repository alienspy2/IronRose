using System;
using System.Collections.Generic;
using ImGuiNET;
using IronRose.Engine.Editor.ImGuiEditor.Panels;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// 패널 탭 우클릭 → Maximize / Restore 기능.
    /// 각 패널의 ImGui.Begin() 직후에 DrawTabContextMenu()를 호출하면
    /// 탭 우클릭 시 Maximize/Restore 메뉴가 표시된다.
    /// </summary>
    internal static class PanelMaximizer
    {
        private static bool _isMaximized;
        private static string? _maximizedPanelName;
        private static readonly Dictionary<string, bool> _savedOpenStates = new();
        private static readonly Dictionary<string, IEditorPanel> _panels = new();

        public static bool IsMaximized => _isMaximized;

        public static void Register(string name, IEditorPanel panel)
        {
            _panels[name] = panel;
        }

        /// <summary>
        /// 각 패널의 ImGui.Begin() 직후 호출.
        /// 탭 우클릭 시 Maximize/Restore 컨텍스트 메뉴를 표시한다.
        /// <paramref name="extraItems"/>가 전달되면 Maximize/Restore 아래에 구분선과 함께 추가 항목을 렌더링한다.
        /// </summary>
        public static void DrawTabContextMenu(string panelName, Action? extraItems = null)
        {
            if (ImGui.BeginPopupContextItem($"##tabctx_{panelName}"))
            {
                if (_isMaximized && _maximizedPanelName == panelName)
                {
                    if (ImGui.MenuItem("Restore"))
                        Restore();
                }
                else if (!_isMaximized)
                {
                    if (ImGui.MenuItem("Maximize"))
                        Maximize(panelName);
                }

                if (extraItems != null)
                {
                    ImGui.Separator();
                    extraItems();
                }

                ImGui.EndPopup();
            }
        }

        /// <summary>
        /// 최대화된 패널이 닫히면 자동으로 복원한다.
        /// ImGuiOverlay의 매 프레임 업데이트에서 호출.
        /// </summary>
        public static void CheckAutoRestore()
        {
            if (!_isMaximized || _maximizedPanelName == null) return;

            if (_panels.TryGetValue(_maximizedPanelName, out var panel) && !panel.IsOpen)
                Restore();
        }

        private static void Maximize(string panelName)
        {
            _savedOpenStates.Clear();
            foreach (var (name, panel) in _panels)
            {
                _savedOpenStates[name] = panel.IsOpen;
                if (name != panelName)
                    panel.IsOpen = false;
            }
            _isMaximized = true;
            _maximizedPanelName = panelName;
        }

        private static void Restore()
        {
            foreach (var (name, panel) in _panels)
            {
                if (_savedOpenStates.TryGetValue(name, out bool wasOpen))
                    panel.IsOpen = wasOpen;
            }
            _isMaximized = false;
            _maximizedPanelName = null;
            _savedOpenStates.Clear();
        }
    }
}
