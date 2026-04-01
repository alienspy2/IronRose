using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ImGuiNET;
using RoseEngine;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    public class ImGuiConsolePanel : IEditorPanel
    {
        private bool _isOpen = true;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }

        private const int MaxEntries = 500;
        private readonly List<LogEntry> _entries = new();
        private readonly List<LogEntry> _drainBuffer = new();
        private bool _autoScroll = true;
        private bool _clearOnPlay = true;
        private bool _showInfo = true;
        private bool _showWarning = true;
        private bool _showError = true;

        // Track play mode state to detect transitions
        private PlayModeState _prevPlayModeState = PlayModeState.Edit;

        // Selection state (indices refer to visible/filtered items)
        private int _selectionAnchor = -1;
        private int _selectionEnd = -1;
        private bool _isDragging;

        // Cached visible entries for selection mapping
        private readonly List<int> _visibleIndices = new();

        public void Draw()
        {
            if (!IsOpen) return;

            // 빌드 시작 시 콘솔 전체 클리어 (빌드 에러 포함)
            if (EditorBridge.ConsumeBuildStarted())
            {
                _entries.Clear();
                _selectionAnchor = -1;
                _selectionEnd = -1;
            }

            // Drain new logs from EditorBridge
            _drainBuffer.Clear();
            EditorBridge.DrainLogs(_drainBuffer);
            if (_drainBuffer.Count > 0)
            {
                _entries.AddRange(_drainBuffer);
                // Adjust selection anchors when entries are trimmed from the front
                int removed = 0;
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveAt(0);
                    removed++;
                }

                if (removed > 0)
                {
                    _selectionAnchor = -1;
                    _selectionEnd = -1;
                }
            }

            // Detect Play mode entry and clear if enabled
            var currentPlayState = EditorPlayMode.State;
            if (_clearOnPlay && _prevPlayModeState == PlayModeState.Edit && currentPlayState == PlayModeState.Playing)
            {
                _entries.Clear();
                _selectionAnchor = -1;
                _selectionEnd = -1;
            }
            _prevPlayModeState = currentPlayState;

            var consoleVisible = ImGui.Begin("Console", ref _isOpen);
            PanelMaximizer.DrawTabContextMenu("Console");
            if (consoleVisible)
            {
                // Toolbar
                if (ImGui.Button("Clear"))
                {
                    _entries.RemoveAll(e => !e.IsBuildError);
                    _selectionAnchor = -1;
                    _selectionEnd = -1;
                }

                ImGui.SameLine();
                ImGui.Checkbox("Auto-scroll", ref _autoScroll);

                ImGui.SameLine();
                ImGui.Checkbox("Clear on Play", ref _clearOnPlay);

                ImGui.SameLine();
                ImGui.Spacing();
                ImGui.SameLine();

                // Filter toggles with colors
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.200f, 0.420f, 0.520f, 1f));
                ImGui.Checkbox("Info", ref _showInfo);
                ImGui.PopStyleColor();

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.620f, 0.500f, 0.180f, 1f));
                ImGui.Checkbox("Warn", ref _showWarning);
                ImGui.PopStyleColor();

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.700f, 0.220f, 0.250f, 1f));
                ImGui.Checkbox("Error", ref _showError);
                ImGui.PopStyleColor();

                ImGui.Separator();

                // Log list
                if (ImGui.BeginChild("LogRegion", System.Numerics.Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
                {
                    // Build visible indices list
                    _visibleIndices.Clear();
                    for (int i = 0; i < _entries.Count; i++)
                    {
                        if (ShouldShow(_entries[i].Level))
                            _visibleIndices.Add(i);
                    }

                    // Handle Ctrl+C / Ctrl+A when LogRegion child is focused
                    bool logRegionFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
                    if (logRegionFocused)
                    {
                        var io = ImGui.GetIO();
                        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C))
                            CopySelectionToClipboard();
                        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A))
                        {
                            // Select all visible entries
                            if (_visibleIndices.Count > 0)
                            {
                                _selectionAnchor = 0;
                                _selectionEnd = _visibleIndices.Count - 1;
                            }
                        }
                    }

                    // Track drag state for range selection
                    if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                        _isDragging = false;

                    for (int vi = 0; vi < _visibleIndices.Count; vi++)
                    {
                        var entry = _entries[_visibleIndices[vi]];

                        var color = entry.Level switch
                        {
                            LogLevel.Warning => new Vector4(0.620f, 0.500f, 0.180f, 1f),
                            LogLevel.Error => new Vector4(0.700f, 0.220f, 0.250f, 1f),
                            _ => new Vector4(0.200f, 0.420f, 0.520f, 1f),
                        };

                        var prefix = entry.Level switch
                        {
                            LogLevel.Warning => "[W]",
                            LogLevel.Error => "[E]",
                            _ => "[I]",
                        };

                        bool isSelected = IsVisibleIndexSelected(vi);
                        string text = $"{prefix} [{entry.Timestamp:HH:mm:ss}] {entry.Message}";

                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        // Use Selectable with SpanAllColumns for full-row click area
                        if (ImGui.Selectable($"{text}##log_{vi}", isSelected,
                            ImGuiSelectableFlags.AllowOverlap | ImGuiSelectableFlags.AllowDoubleClick,
                            System.Numerics.Vector2.Zero))
                        {
                            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                OpenSourceInEditor(entry);
                            else
                                HandleItemClick(vi);
                        }

                        // Drag selection: if mouse is held and hovering an item, extend selection
                        if (_isDragging && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                        {
                            _selectionEnd = vi;
                        }

                        // Detect drag start on mouse down over this item
                        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            _isDragging = true;
                        }

                        ImGui.PopStyleColor();
                    }

                    if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                        ImGui.SetScrollHereY(1.0f);
                }
                ImGui.EndChild();
            }
            ImGui.End();
        }

        private void HandleItemClick(int visibleIndex)
        {
            var io = ImGui.GetIO();
            if (io.KeyShift && _selectionAnchor >= 0)
            {
                // Shift+click: extend selection range from anchor
                _selectionEnd = visibleIndex;
            }
            else
            {
                // Normal click: set new anchor
                _selectionAnchor = visibleIndex;
                _selectionEnd = visibleIndex;
            }
        }

        private bool IsVisibleIndexSelected(int visibleIndex)
        {
            if (_selectionAnchor < 0 || _selectionEnd < 0) return false;
            int lo = Math.Min(_selectionAnchor, _selectionEnd);
            int hi = Math.Max(_selectionAnchor, _selectionEnd);
            return visibleIndex >= lo && visibleIndex <= hi;
        }

        private void CopySelectionToClipboard()
        {
            if (_selectionAnchor < 0 || _selectionEnd < 0) return;

            int lo = Math.Min(_selectionAnchor, _selectionEnd);
            int hi = Math.Max(_selectionAnchor, _selectionEnd);

            // Clamp to valid range
            if (lo >= _visibleIndices.Count) return;
            hi = Math.Min(hi, _visibleIndices.Count - 1);

            var sb = new StringBuilder();
            for (int vi = lo; vi <= hi; vi++)
            {
                var entry = _entries[_visibleIndices[vi]];
                var prefix = entry.Level switch
                {
                    LogLevel.Warning => "[W]",
                    LogLevel.Error => "[E]",
                    _ => "[I]",
                };
                if (sb.Length > 0) sb.AppendLine();
                sb.Append($"{prefix} [{entry.Timestamp:HH:mm:ss}] {entry.Message}");
                if (!string.IsNullOrEmpty(entry.StackInfo))
                {
                    sb.AppendLine();
                    sb.Append(entry.StackInfo.TrimEnd());
                }
            }

            SystemClipboard.SetText(sb.ToString());
        }

        private static readonly Regex StackFileRegex = new(@"in\s+(.+):line\s+(\d+)", RegexOptions.Compiled);

        private static void OpenSourceInEditor(LogEntry entry)
        {
            string? filePath = entry.CallerFilePath;
            int line = entry.CallerLine;

            // Fallback: 캡처 시 CallerFilePath가 없으면 스택트레이스에서 파싱
            if (string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(entry.StackInfo))
            {
                var matches = StackFileRegex.Matches(entry.StackInfo);
                foreach (Match m in matches)
                {
                    string f = m.Groups[1].Value;
                    string fileName = Path.GetFileName(f);
                    // Skip known logging infrastructure
                    if (fileName is "Debug.cs" or "EditorDebug.cs")
                        continue;
                    if (File.Exists(f))
                    {
                        filePath = f;
                        if (int.TryParse(m.Groups[2].Value, out int parsed))
                            line = parsed;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            string lineStr = line.ToString();
            string editor = ProjectSettings.ExternalScriptEditor;
            string args = editor switch
            {
                "code" or "code-insiders" => $"--goto \"{filePath}\":{lineStr}",
                "rider" or "rider64" => $"--line {lineStr} \"{filePath}\"",
                _ => $"\"{filePath}\"",
            };

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = editor,
                    Arguments = args,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                RoseEngine.Debug.LogError($"[Console] Failed to open editor '{editor}': {ex.Message}");
            }
        }

        private bool ShouldShow(LogLevel level) => level switch
        {
            LogLevel.Info => _showInfo,
            LogLevel.Warning => _showWarning,
            LogLevel.Error => _showError,
            _ => true,
        };
    }
}
