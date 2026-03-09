using System;
using System.Collections.Generic;
using System.Numerics;
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
        private bool _showInfo = true;
        private bool _showWarning = true;
        private bool _showError = true;

        public void Draw()
        {
            if (!IsOpen) return;

            // Drain new logs from EditorBridge
            _drainBuffer.Clear();
            EditorBridge.DrainLogs(_drainBuffer);
            if (_drainBuffer.Count > 0)
            {
                _entries.AddRange(_drainBuffer);
                while (_entries.Count > MaxEntries)
                    _entries.RemoveAt(0);
            }

            if (ImGui.Begin("Console", ref _isOpen))
            {
                // Toolbar
                if (ImGui.Button("Clear"))
                    _entries.Clear();

                ImGui.SameLine();
                ImGui.Checkbox("Auto-scroll", ref _autoScroll);

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
                    foreach (var entry in _entries)
                    {
                        if (!ShouldShow(entry.Level)) continue;

                        var color = entry.Level switch
                        {
                            LogLevel.Warning => new Vector4(0.620f, 0.500f, 0.180f, 1f), // dark yellow
                            LogLevel.Error => new Vector4(0.700f, 0.220f, 0.250f, 1f),   // dark red
                            _ => new Vector4(0.200f, 0.420f, 0.520f, 1f),                        // dark teal
                        };

                        var prefix = entry.Level switch
                        {
                            LogLevel.Warning => "[W]",
                            LogLevel.Error => "[E]",
                            _ => "[I]",
                        };

                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        ImGui.TextUnformatted($"{prefix} [{entry.Timestamp:HH:mm:ss}] {entry.Message}");
                        ImGui.PopStyleColor();
                    }

                    if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                        ImGui.SetScrollHereY(1.0f);
                }
                ImGui.EndChild();
            }
            ImGui.End();
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
