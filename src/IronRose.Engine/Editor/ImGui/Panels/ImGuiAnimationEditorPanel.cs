using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using IronRose.AssetPipeline;
using IronRose.Engine.Editor;
using RoseEngine;
using Debug = RoseEngine.EditorDebug;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.ImGuiEditor.Panels
{
    /// <summary>
    /// Animation Editor — 도프 시트 / 커브 모드 전환 + 키프레임 편집 + 프리뷰 재생.
    /// </summary>
    public class ImGuiAnimationEditorPanel : IEditorPanel
    {
        private bool _isOpen;
        private bool _isWindowFocused;
        public bool IsOpen { get => _isOpen; set => _isOpen = value; }
        public bool IsWindowFocused => _isWindowFocused;

        // ── Current clip ──
        private AnimationClip? _clip;
        private string? _animPath;
        private bool _isDirty;

        // ── Preview context ──
        private Animator? _contextAnimator;

        // ── Playback ──
        private float _playheadTime;
        private bool _isPlaying;
        private float _previewSpeed = 1f;

        // ── View mode (Unity-style Dope ↔ Curves toggle) ──
        private enum TimelineViewMode { Dopesheet, Curves }
        private TimelineViewMode _viewMode = TimelineViewMode.Curves;

        // ── Timeline view state ──
        private float _zoom = 150f;
        private float _scrollX;
        private const float TRACK_HEIGHT = 24f;
        private float _trackListWidth = 200f;
        private const float TRACK_LIST_WIDTH_MIN = 100f;
        private const float TRACK_LIST_WIDTH_MAX = 500f;
        private const float RULER_HEIGHT = 22f;
        private const float DIAMOND_SIZE = 5f;
        private const float INSPECTOR_HEIGHT = 80f;

        // ── Curves mode state ──
        private float _curveScrollY;
        private float _cachedYMin = -1f;
        private float _cachedYMax = 1f;
        private bool _hasCachedYRange;

        // ── Multi-select (Feature 7) ──
        private HashSet<(string path, int keyIndex)> _selectedKeys = new();
        private readonly HashSet<string> _selectedTrackPaths = new();
        private int _selectedKeyIndex = -1;
        private bool _isBoxSelecting;
        private Vector2 _boxSelectStart;

        // ── Drag state ──
        private bool _isDraggingKey;
        private Dictionary<(string path, int keyIndex), float> _dragOriginalTimes = new();
        private Dictionary<(string path, int keyIndex), float> _dragOriginalValues = new();
        private float _dragAnchorTime;
        private float _dragAnchorValue;
        private bool _isDraggingPlayhead;

        // ── Tangent drag (Curves mode) ──
        private bool _isDraggingTangent;
        private bool _isDraggingInTangent;
        private int _draggingTangentKeyIndex = -1;
        private string? _draggingTangentTrackPath;

        // ── Tangent mode (Feature 6) ──
        private enum TangentMode { Auto, Linear, Constant, Free }

        // ── Copy/Paste (Feature 8) ──
        private List<ClipboardKeyframe> _clipboard = new();

        private struct ClipboardKeyframe
        {
            public string trackPath;
            public float relativeTime;
            public float value;
            public float inTangent;
            public float outTangent;
        }

        // ── Event editing (Feature 9) ──
        private int _selectedEventIndex = -1;

        // ── Double-click focus inspector ──
        private bool _focusInspector;

        // ── Record mode ──
        private bool _isRecording;
        public bool IsRecording => _isRecording && _clip != null && _contextAnimator != null;

        // ── Preview snapshot (원래 상태 저장/복원) ──
        private bool _hasPreviewSnapshot;

        // ── Undo state (Feature 3) ──
        private (Dictionary<string, Keyframe[]> curves, List<AnimationEvent> events, float length)? _undoBeforeState;

        // ── Track tree (cached) ──
        private List<TrackEntry> _trackEntries = new();
        private HashSet<string> _collapsedGroups = new();
        private HashSet<string> _hiddenTracks = new();

        // ── Scene Animator list (cached) ──
        private Animator[] _sceneAnimators = Array.Empty<Animator>();
        private string[] _sceneAnimatorNames = Array.Empty<string>();
        private int _lastAnimatorScanFrame;

        // ── Curve colors ──
        private static readonly uint[] CurveColors = {
            0xFF4444FF, // red
            0xFF44FF44, // green
            0xFFFF4444, // blue
            0xFF44FFFF, // yellow
            0xFFFF44FF, // magenta
            0xFFFFFF44, // cyan
        };

        /// <summary>외부에서 클립을 열 때 호출.</summary>
        public void Open(string animPath, AnimationClip clip)
        {
            // 기존 프리뷰 상태 복원
            RestorePreview();

            _animPath = animPath;
            _clip = clip;
            _isOpen = true;
            _isDirty = false;
            _playheadTime = 0f;
            _isPlaying = false;
            _selectedKeys.Clear();
            _selectedTrackPaths.Clear();
            _selectedKeyIndex = -1;
            _selectedEventIndex = -1;
            _scrollX = 0f;
            _zoom = 150f;

            RebuildTrackEntries();
            Debug.Log($"[AnimationEditor] Opened: {animPath} ({clip.curves.Count} curves)");
        }

        /// <summary>Inspector에서 Animator 연동용.</summary>
        public void SetContext(Animator? animator)
        {
            if (_contextAnimator != animator)
                RestorePreview();

            _contextAnimator = animator;
        }

        public void Draw()
        {
            if (!_isOpen || _clip == null) return;

            // ── Play mode guard: 플레이 모드 진입 시 프리뷰 강제 종료 ──
            if (EditorPlayMode.IsInPlaySession)
            {
                if (_isPlaying || _hasPreviewSnapshot || _isRecording)
                {
                    _isPlaying = false;
                    _isRecording = false;
                    RestorePreview();
                }

                ImGui.SetNextWindowSize(new Vector2(900, 500), ImGuiCond.FirstUseEver);
                string blockedTitle = _isDirty
                    ? $"Animation Editor — {_clip.name}*###AnimEditor"
                    : $"Animation Editor — {_clip.name}###AnimEditor";
                bool blockedOpen = _isOpen;
                var blockedVisible = ImGui.Begin(blockedTitle, ref blockedOpen, ImGuiWindowFlags.MenuBar);
                PanelMaximizer.DrawTabContextMenu("Animation Editor");
                if (blockedVisible)
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "Animation Editor is disabled during Play Mode.");
                }
                ImGui.End();
                _isOpen = blockedOpen;
                return;
            }

            UpdatePreview();

            ImGui.SetNextWindowSize(new Vector2(900, 500), ImGuiCond.FirstUseEver);
            string rec = _isRecording ? " [REC]" : "";
            string dirty = _isDirty ? "*" : "";
            string title = $"Animation Editor — {_clip.name}{rec}{dirty}###AnimEditor";

            bool windowOpen = _isOpen;
            var animVisible = ImGui.Begin(title, ref windowOpen, ImGuiWindowFlags.MenuBar);
            PanelMaximizer.DrawTabContextMenu("Animation Editor");
            if (animVisible)
            {
                _isWindowFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

                // Shortcuts only when window focused
                if (_isWindowFocused)
                    ProcessShortcuts();

                DrawMenuBar();
                DrawToolbar();
                ImGui.Separator();
                DrawTimelineArea();
            }
            ImGui.End();
            if (!windowOpen && _isOpen)
                RestorePreview();
            _isOpen = windowOpen;
        }

        // ================================================================
        // Keyboard shortcuts (Feature 10)
        // ================================================================

        private void ProcessShortcuts()
        {
            if (_clip == null) return;
            var io = ImGui.GetIO();
            bool ctrl = io.KeyCtrl;
            bool shift = io.KeyShift;
            bool noTarget = _contextAnimator == null;

            // Space — Play/Pause
            if (!noTarget && ImGui.IsKeyPressed(ImGuiKey.Space) && !io.WantTextInput)
            {
                _isPlaying = !_isPlaying;
                if (_isPlaying && _contextAnimator != null)
                {
                    _contextAnimator.clip = _clip;
                    _contextAnimator.InvalidateTargets();
                    CapturePreviewIfNeeded();
                }
            }

            // Ctrl+S — Save
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S))
            {
                if (_isDirty) Save();
            }

            // Ctrl+Z — Undo
            if (ctrl && !shift && ImGui.IsKeyPressed(ImGuiKey.Z))
            {
                var desc = UndoSystem.PerformUndo();
                if (desc != null)
                {
                    RebuildTrackEntries();
                    _isDirty = true;
                }
            }

            // Ctrl+Shift+Z — Redo
            if (ctrl && shift && ImGui.IsKeyPressed(ImGuiKey.Z))
            {
                var desc = UndoSystem.PerformRedo();
                if (desc != null)
                {
                    RebuildTrackEntries();
                    _isDirty = true;
                }
            }

            // Delete / Backspace — delete selected keys
            if ((ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.Backspace)) && !io.WantTextInput)
            {
                DeleteSelectedKeys();
            }

            // Ctrl+C — Copy
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C))
                CopySelectedKeys();

            // Ctrl+V — Paste
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V))
                PasteKeys();

            // Ctrl+D — Duplicate
            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D))
            {
                CopySelectedKeys();
                PasteKeys();
            }

            // A — Select all / deselect keyframes
            if (ImGui.IsKeyPressed(ImGuiKey.A) && !ctrl && !io.WantTextInput)
                ToggleSelectAll();

            // Ctrl+A — Select all tracks
            if (ImGui.IsKeyPressed(ImGuiKey.A) && ctrl && !io.WantTextInput)
                SelectAllTracks();

            // K — Add key at playhead
            if (!noTarget && ImGui.IsKeyPressed(ImGuiKey.K) && !io.WantTextInput)
                AddKeyAtPlayhead();

            // Left/Right — Prev/Next keyframe
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) && !io.WantTextInput)
                JumpToPrevKey();
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) && !io.WantTextInput)
                JumpToNextKey();

            // F — Frame / Fit Y-axis (Curves mode)
            if (ImGui.IsKeyPressed(ImGuiKey.F) && !io.WantTextInput)
            {
                if (_viewMode == TimelineViewMode.Curves)
                {
                    _curveScrollY = 0f;
                    _hasCachedYRange = false; // Force recompute
                }
            }
        }

        // ================================================================
        // Menu bar
        // ================================================================

        private void DrawMenuBar()
        {
            if (ImGui.BeginMenuBar())
            {
                if (ImGui.BeginMenu("File"))
                {
                    if (ImGui.MenuItem("Save", "Ctrl+S", false, _isDirty))
                        Save();
                    if (ImGui.MenuItem("Revert", null, false, _isDirty))
                        Revert();
                    ImGui.EndMenu();
                }
                if (ImGui.BeginMenu("Edit"))
                {
                    if (ImGui.MenuItem("Undo", "Ctrl+Z", false, UndoSystem.UndoDescription != null))
                    {
                        UndoSystem.PerformUndo();
                        RebuildTrackEntries();
                        _isDirty = true;
                    }
                    if (ImGui.MenuItem("Redo", "Ctrl+Shift+Z", false, UndoSystem.RedoDescription != null))
                    {
                        UndoSystem.PerformRedo();
                        RebuildTrackEntries();
                        _isDirty = true;
                    }
                    ImGui.Separator();
                    if (ImGui.MenuItem("Copy", "Ctrl+C", false, _selectedKeys.Count > 0))
                        CopySelectedKeys();
                    if (ImGui.MenuItem("Paste", "Ctrl+V", false, _clipboard.Count > 0))
                        PasteKeys();
                    if (ImGui.MenuItem("Select All", "A"))
                        ToggleSelectAll();
                    ImGui.EndMenu();
                }
                ImGui.EndMenuBar();
            }
        }

        // ================================================================
        // Toolbar
        // ================================================================

        private void DrawToolbar()
        {
            // ── Animator selector ──
            ImGui.Text("Target:");
            ImGui.SameLine();
            RefreshSceneAnimators();
            int currentIdx = -1;
            if (_contextAnimator != null)
            {
                currentIdx = Array.IndexOf(_sceneAnimators, _contextAnimator);
                if (currentIdx < 0) { _contextAnimator = null; }
            }

            ImGui.SetNextItemWidth(160);
            string previewLabel = currentIdx >= 0 ? _sceneAnimatorNames[currentIdx] : "(No Animator)";
            if (ImGui.BeginCombo("##AnimatorSelect", previewLabel))
            {
                if (ImGui.Selectable("(None)", currentIdx < 0))
                    _contextAnimator = null;
                for (int i = 0; i < _sceneAnimators.Length; i++)
                {
                    if (ImGui.Selectable(_sceneAnimatorNames[i], i == currentIdx))
                        _contextAnimator = _sceneAnimators[i];
                }
                ImGui.EndCombo();
            }

            bool noTarget = _contextAnimator == null;

            ImGui.SameLine();
            ImGui.Text("|");
            ImGui.SameLine();

            // ── Record ──
            if (noTarget) ImGui.BeginDisabled();
            bool wasRecording = _isRecording;
            if (wasRecording)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.2f, 0.2f, 1f));
            if (ImGui.Button(_isRecording ? "● Stop" : "● Rec"))
            {
                _isRecording = !_isRecording;
                if (_isRecording)
                    CapturePreviewIfNeeded();
            }
            if (wasRecording)
                ImGui.PopStyleColor();
            if (noTarget) ImGui.EndDisabled();

            ImGui.SameLine();

            // ── Play / Pause / Stop ──
            if (noTarget) ImGui.BeginDisabled();
            if (_isPlaying)
            {
                if (ImGui.Button("Pause"))
                {
                    _isPlaying = false;
                }
            }
            else
            {
                if (ImGui.Button("Play"))
                {
                    _isPlaying = true;
                    if (_contextAnimator != null && _clip != null)
                    {
                        _contextAnimator.clip = _clip;
                        _contextAnimator.InvalidateTargets();
                        CapturePreviewIfNeeded();
                    }
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                _isPlaying = false;
                _playheadTime = 0f;
                RestorePreview();
            }
            if (noTarget) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("|<")) JumpToPrevKey();
            ImGui.SameLine();
            if (ImGui.Button(">|")) JumpToNextKey();

            ImGui.SameLine();
            bool hasTrack = _clip!.curves.Count > 0;
            if (noTarget || !hasTrack) ImGui.BeginDisabled();
            if (ImGui.Button("+Key")) AddKeyAtPlayhead();
            if (noTarget || !hasTrack) ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.Text($"Time: {_playheadTime:F2}s");

            // ── Speed control ──
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            ImGui.DragFloat("##Speed", ref _previewSpeed, 0.05f, 0.1f, 5f, "x%.1f");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            float fr = _clip!.frameRate;
            if (noTarget) ImGui.BeginDisabled();
            if (ImGui.DragFloat("##FrameRate", ref fr, 1f, 1f, 120f, "FR: %.0f"))
            {
                _clip.frameRate = Math.Max(1f, fr);
                _isDirty = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var wrapNames = Enum.GetNames(typeof(WrapMode));
            int wrapIdx = (int)_clip.wrapMode;
            if (ImGui.Combo("##WrapMode", ref wrapIdx, wrapNames, wrapNames.Length))
            {
                _clip.wrapMode = (WrapMode)wrapIdx;
                _isDirty = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            float len = _clip.length;
            if (ImGui.DragFloat("##Length", ref len, 0.05f, 0.01f, 600f, "Len: %.2f"))
            {
                _clip.length = MathF.Max(0.01f, len);
                _isDirty = true;
            }
            if (noTarget) ImGui.EndDisabled();

            ImGui.SameLine();
            bool canSave = _isDirty;
            if (!canSave) ImGui.BeginDisabled();
            if (ImGui.Button("Save")) Save();
            if (!canSave) ImGui.EndDisabled();

            // ── Dope / Curves mode toggle ──
            ImGui.SameLine();
            ImGui.Text("|");
            ImGui.SameLine();

            bool isDope = _viewMode == TimelineViewMode.Dopesheet;
            if (isDope) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            if (ImGui.Button("Dope")) _viewMode = TimelineViewMode.Dopesheet;
            if (isDope) ImGui.PopStyleColor();

            ImGui.SameLine();
            bool isCurves = _viewMode == TimelineViewMode.Curves;
            if (isCurves) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            if (ImGui.Button("Curves")) _viewMode = TimelineViewMode.Curves;
            if (isCurves) ImGui.PopStyleColor();
        }

        // ================================================================
        // Timeline area — track list (left) + view (right)
        // ================================================================

        private void DrawTimelineArea()
        {
            var avail = ImGui.GetContentRegionAvail();
            float timelineH = avail.Y;
            if (avail.X < 100 || timelineH < 50) return;

            if (_contextAnimator == null)
            {
                float centerY = timelineH * 0.4f;
                ImGui.Dummy(new Vector2(0, centerY));
                float textWidth = ImGui.CalcTextSize("Select a Target Animator to edit animation.").X;
                ImGui.SetCursorPosX((avail.X - textWidth) * 0.5f);
                ImGui.TextDisabled("Select a Target Animator to edit animation.");
                return;
            }

            // Left: track list
            ImGui.BeginChild("##AnimTrackList", new Vector2(_trackListWidth, timelineH), ImGuiChildFlags.Border);
            DrawTrackList();
            ImGui.EndChild();

            // ── Splitter between track list and timeline view ──
            ImGui.SameLine();
            {
                const float SPLITTER_W = 6f;
                var splitterPos = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton("##AnimSplitter", new Vector2(SPLITTER_W, timelineH));
                bool splitterHovered = ImGui.IsItemHovered();
                bool splitterActive = ImGui.IsItemActive();

                if (splitterHovered || splitterActive)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

                if (splitterActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    _trackListWidth += ImGui.GetIO().MouseDelta.X;
                    _trackListWidth = MathF.Max(TRACK_LIST_WIDTH_MIN, MathF.Min(TRACK_LIST_WIDTH_MAX, _trackListWidth));
                }

                // Visual line
                var drawList = ImGui.GetWindowDrawList();
                float cx = splitterPos.X + SPLITTER_W * 0.5f;
                uint lineColor = (splitterHovered || splitterActive)
                    ? ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1f))
                    : ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
                drawList.AddLine(
                    new Vector2(cx, splitterPos.Y),
                    new Vector2(cx, splitterPos.Y + timelineH),
                    lineColor, 2f);
            }

            ImGui.SameLine();

            // Right: dopesheet or curves
            float rightW = avail.X - _trackListWidth - 14f;
            if (rightW < 50f) rightW = 50f;
            ImGui.BeginChild("##AnimTimelineView", new Vector2(rightW, timelineH), ImGuiChildFlags.Border);
            if (_viewMode == TimelineViewMode.Dopesheet)
                DrawDopesheet();
            else
                DrawCurvesView();
            ImGui.EndChild();
        }

        private bool HasInspectorContent()
        {
            return _selectedKeys.Count > 0 || _selectedEventIndex >= 0;
        }

        // ================================================================
        // Inspector area (Keyframe / Event editing) — Features 1, 6, 9
        // ================================================================

        private void DrawInspectorArea()
        {
            if (!HasInspectorContent()) return;
            if (_clip == null) return;

            ImGui.Separator();

            if (_selectedEventIndex >= 0)
                DrawEventInspector();
            else if (_selectedKeys.Count > 0)
                DrawKeyframeInspector();
        }

        private void DrawKeyframeInspector()
        {
            if (_clip == null || _selectedKeys.Count == 0) return;

            var primary = _selectedKeys.FirstOrDefault();
            if (!_clip.curves.TryGetValue(primary.path, out var curve)) return;
            if (primary.keyIndex < 0 || primary.keyIndex >= curve.length) return;

            var kf = curve[primary.keyIndex];
            bool changed = false;

            ImGui.Text("Keyframe:");
            ImGui.SameLine();
            ImGui.TextDisabled(primary.path);

            // Double-click from dopesheet → focus first editable field
            if (_focusInspector)
            {
                ImGui.SetKeyboardFocusHere();
                _focusInspector = false;
            }

            // Time — special: MoveKey changes index, so use instant undo
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            float time = kf.time;
            bool timeChanged = EditorWidgets.DragFloatClickable("kf_time", "Time", ref time, 0.01f, "%.3f");
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (timeChanged)
            {
                float frameTime = 1f / _clip.frameRate;
                time = MathF.Round(MathF.Max(0f, time) / frameTime) * frameTime;
                if (MathF.Abs(time - kf.time) > 0.0001f)
                {
                    kf.time = time;
                    int newIdx = curve.MoveKey(primary.keyIndex, kf);
                    UpdateSelectionAfterMove(primary.path, primary.keyIndex, newIdx);
                    AutoExpandLength();
                    changed = true;
                }
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Move Keyframe");

            // Value
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            float val = kf.value;
            bool valChanged = EditorWidgets.DragFloatClickable("kf_val", "Value", ref val, 0.01f, "%.3f");
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (valChanged)
            {
                kf.value = val;
                curve[primary.keyIndex] = kf;
                changed = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Edit Keyframe Value");

            // In Tangent
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            float inT = kf.inTangent;
            bool inTChanged = EditorWidgets.DragFloatClickable("kf_inT", "InT", ref inT, 0.01f, "%.2f");
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (inTChanged)
            {
                kf.inTangent = inT;
                curve[primary.keyIndex] = kf;
                changed = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Edit In Tangent");

            // Out Tangent
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            float outT = kf.outTangent;
            bool outTChanged = EditorWidgets.DragFloatClickable("kf_outT", "OutT", ref outT, 0.01f, "%.2f");
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (outTChanged)
            {
                kf.outTangent = outT;
                curve[primary.keyIndex] = kf;
                changed = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Edit Out Tangent");

            // Tangent mode combo
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            var modeNames = Enum.GetNames(typeof(TangentMode));
            int currentMode = DetectTangentMode(curve, primary.keyIndex);
            if (ImGui.Combo("##TangentMode", ref currentMode, modeNames, modeNames.Length))
            {
                BeginUndoAction();
                ApplyTangentMode(curve, primary.keyIndex, (TangentMode)currentMode);
                changed = true;
                EndUndoAction("Change Tangent Mode");
            }

            if (changed)
            {
                _isDirty = true;
                ScrubPreview();
            }
        }

        private void DrawEventInspector()
        {
            if (_clip == null || _selectedEventIndex < 0 || _selectedEventIndex >= _clip.events.Count) return;

            var evt = _clip.events[_selectedEventIndex];
            bool changed = false;

            ImGui.Text("Event:");

            // Time
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            float evtTime = evt.time;
            bool evtTimeChanged = EditorWidgets.DragFloatClickable("evt_time", "Time", ref evtTime, 0.01f, "%.3f");
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (evtTimeChanged)
            {
                evt.time = MathF.Max(0f, evtTime);
                _clip.events[_selectedEventIndex] = evt;
                changed = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Move Event");

            // Function name
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            string funcName = evt.functionName ?? "";
            if (ImGui.InputText("##EvtFunc", ref funcName, 128))
            {
                BeginUndoAction();
                evt.functionName = funcName;
                _clip.events[_selectedEventIndex] = evt;
                changed = true;
                EndUndoAction("Edit Event Function");
            }

            // Float param
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            float fp = evt.floatParameter;
            bool fpChanged = EditorWidgets.DragFloatClickable("evt_fp", "Float", ref fp, 0.01f, "%.2f");
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (fpChanged)
            {
                evt.floatParameter = fp;
                _clip.events[_selectedEventIndex] = evt;
                changed = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Edit Event Float");

            // Int param
            ImGui.SameLine();
            ImGui.SetNextItemWidth(50);
            int ip = evt.intParameter;
            bool ipChanged = ImGui.DragInt("##EvtInt", ref ip, 1);
            if (ImGui.IsItemActivated()) BeginUndoAction();
            if (ipChanged)
            {
                evt.intParameter = ip;
                _clip.events[_selectedEventIndex] = evt;
                changed = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) EndUndoAction("Edit Event Int");

            // Delete button
            ImGui.SameLine();
            if (ImGui.Button("Delete Event"))
            {
                BeginUndoAction();
                _clip.events.RemoveAt(_selectedEventIndex);
                _selectedEventIndex = -1;
                changed = true;
                EndUndoAction("Delete Event");
            }

            if (changed) _isDirty = true;
        }

        // ================================================================
        // Track list (left panel) — Feature 2: current value display
        // ================================================================

        private void DrawTrackList()
        {
            ImGui.Text("Properties");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20);
            if (ImGui.SmallButton("+"))
                ImGui.OpenPopup("##AddProperty");

            DrawAddPropertyPopup();
            ImGui.Separator();

            // Spacer for ruler alignment
            ImGui.Dummy(new Vector2(0, RULER_HEIGHT));

            for (int i = 0; i < _trackEntries.Count; i++)
            {
                var entry = _trackEntries[i];

                // Skip collapsed group children
                if (!entry.isGroupHeader && entry.groupKey != null && _collapsedGroups.Contains(entry.groupKey))
                    continue;

                ImGui.PushID(i);

                if (entry.isGroupHeader)
                {
                    // Foldout header
                    bool collapsed = _collapsedGroups.Contains(entry.groupKey!);
                    string arrow = collapsed ? ">" : "v";
                    if (ImGui.Selectable($"{arrow} {entry.displayName}", false, ImGuiSelectableFlags.None, new Vector2(_trackListWidth - 65, TRACK_HEIGHT)))
                    {
                        if (collapsed)
                            _collapsedGroups.Remove(entry.groupKey!);
                        else
                            _collapsedGroups.Add(entry.groupKey!);
                    }

                    // Right-click context menu for group header
                    if (ImGui.BeginPopupContextItem($"##GroupCtx_{i}"))
                    {
                        if (ImGui.MenuItem("Remove Group"))
                        {
                            BeginUndoAction();
                            string gk = entry.groupKey!;
                            var pathsToRemove = _clip!.curves.Keys
                                .Where(p => p.StartsWith(gk + ".") || p == gk)
                                .ToList();
                            foreach (var p in pathsToRemove)
                            {
                                _clip.curves.Remove(p);
                                var toRemove = _selectedKeys.Where(s => s.path == p).ToList();
                                foreach (var s in toRemove) _selectedKeys.Remove(s);
                                _selectedTrackPaths.Remove(p);
                            }
                            _clip.RecalculateLength();
                            _isDirty = true;
                            _collapsedGroups.Remove(gk);
                            RebuildTrackEntries();
                            EndUndoAction("Remove Group");
                        }
                        ImGui.EndPopup();
                    }
                }
                else
                {
                    bool selected = _selectedTrackPaths.Contains(entry.path);

                    // Visibility toggle for Curves mode
                    if (_viewMode == TimelineViewMode.Curves)
                    {
                        bool hidden = _hiddenTracks.Contains(entry.path);
                        ImGui.PushStyleColor(ImGuiCol.Text, hidden
                            ? ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f))
                            : ImGui.GetColorU32(ImGuiCol.Text));
                        if (ImGui.SmallButton(hidden ? " " : "v"))
                        {
                            if (hidden) _hiddenTracks.Remove(entry.path);
                            else _hiddenTracks.Add(entry.path);
                        }
                        ImGui.PopStyleColor();
                        ImGui.SameLine();
                    }

                    if (ImGui.Selectable(entry.displayName, selected, ImGuiSelectableFlags.None, new Vector2(_trackListWidth - 20, TRACK_HEIGHT)))
                    {
                        var io2 = ImGui.GetIO();
                        if (io2.KeyCtrl)
                        {
                            // Toggle individual track
                            if (_selectedTrackPaths.Contains(entry.path))
                                _selectedTrackPaths.Remove(entry.path);
                            else
                                _selectedTrackPaths.Add(entry.path);
                        }
                        else
                        {
                            // Single select
                            _selectedTrackPaths.Clear();
                            _selectedTrackPaths.Add(entry.path);
                        }
                        _selectedKeys.Clear();
                        _selectedKeyIndex = -1;
                        _selectedEventIndex = -1;
                    }

                    // Right-click context menu (Selectable 직후 배치)
                    if (ImGui.BeginPopupContextItem($"##TrackCtx_{i}"))
                    {
                        if (ImGui.MenuItem("Remove Property"))
                        {
                            BeginUndoAction();
                            _clip!.curves.Remove(entry.path);
                            _clip.RecalculateLength();
                            _isDirty = true;
                            RebuildTrackEntries();
                            // Clean up selection
                            var toRemove = _selectedKeys.Where(s => s.path == entry.path).ToList();
                            foreach (var s in toRemove) _selectedKeys.Remove(s);
                            _selectedTrackPaths.Remove(entry.path);
                            EndUndoAction("Remove Property");
                        }
                        ImGui.EndPopup();
                    }
                }

                ImGui.PopID();
            }
        }

        // ================================================================
        // Dopesheet view (right panel)
        // ================================================================

        private void DrawDopesheet()
        {
            var contentMin = ImGui.GetCursorScreenPos();
            var contentSize = ImGui.GetContentRegionAvail();
            var drawList = ImGui.GetWindowDrawList();
            var io = ImGui.GetIO();

            AutoScrollToPlayhead(contentSize.X);

            ImGui.InvisibleButton("##DopesheetCanvas", contentSize);
            bool hovered = ImGui.IsItemHovered();

            HandleZoomAndPan(hovered, io, contentMin);

            float clipLength = MathF.Max(_clip!.length, 0.1f);

            // Ruler
            DrawRuler(drawList, contentMin, contentSize.X, clipLength);

            // Event markers on ruler
            DrawEventMarkers(drawList, contentMin, contentSize.X, hovered, io);

            // Track rows + keyframe diamonds
            float trackAreaY = contentMin.Y + RULER_HEIGHT;

            int visibleRow = 0;
            for (int i = 0; i < _trackEntries.Count; i++)
            {
                var entry = _trackEntries[i];

                // Skip group headers and collapsed children
                if (entry.isGroupHeader) { visibleRow++; continue; }
                if (entry.groupKey != null && _collapsedGroups.Contains(entry.groupKey)) continue;

                float rowY = trackAreaY + visibleRow * TRACK_HEIGHT;

                // Row background
                if (visibleRow % 2 == 1)
                {
                    drawList.AddRectFilled(
                        new Vector2(contentMin.X, rowY),
                        new Vector2(contentMin.X + contentSize.X, rowY + TRACK_HEIGHT),
                        ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 0.3f)));
                }

                if (_selectedTrackPaths.Contains(entry.path))
                {
                    drawList.AddRectFilled(
                        new Vector2(contentMin.X, rowY),
                        new Vector2(contentMin.X + contentSize.X, rowY + TRACK_HEIGHT),
                        ImGui.GetColorU32(new Vector4(0.2f, 0.4f, 0.7f, 0.2f)));
                }

                if (_clip.curves.TryGetValue(entry.path, out var curve))
                {
                    float centerY = rowY + TRACK_HEIGHT * 0.5f;

                    for (int k = 0; k < curve.length; k++)
                    {
                        var kf = curve[k];
                        float screenX = TimeToScreenX(kf.time, contentMin.X);
                        if (screenX < contentMin.X - DIAMOND_SIZE || screenX > contentMin.X + contentSize.X + DIAMOND_SIZE)
                            continue;

                        bool isSelected = _selectedKeys.Contains((entry.path, k));
                        uint color = isSelected
                            ? ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.3f, 1f))
                            : ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, 1f));

                        DrawDiamond(drawList, new Vector2(screenX, centerY), DIAMOND_SIZE, color, isSelected);

                        // Click to select
                        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            float dx = io.MousePos.X - screenX;
                            float dy = io.MousePos.Y - centerY;
                            if (MathF.Abs(dx) <= DIAMOND_SIZE + 2 && MathF.Abs(dy) <= DIAMOND_SIZE + 2)
                            {
                                HandleKeyframeClick(entry.path, k, kf.time, io);
                            }
                        }
                    }
                }

                visibleRow++;
            }

            // Box select
            HandleBoxSelect(drawList, contentMin, contentSize, trackAreaY, hovered, io);

            // Drag keyframe(s)
            HandleKeyframeDrag(contentMin, io, false);

            // Playhead
            HandlePlayhead(drawList, contentMin, contentSize, trackAreaY, hovered, io);

            // Double-click to add keyframe
            HandleDoubleClickAdd(contentMin, trackAreaY, hovered, io);
        }

        // ================================================================
        // Curves view (right panel) — Feature 5
        // ================================================================

        private void DrawCurvesView()
        {
            var contentMin = ImGui.GetCursorScreenPos();
            var contentSize = ImGui.GetContentRegionAvail();
            var drawList = ImGui.GetWindowDrawList();
            var io = ImGui.GetIO();

            AutoScrollToPlayhead(contentSize.X);

            ImGui.InvisibleButton("##CurvesCanvas", contentSize);
            bool hovered = ImGui.IsItemHovered();

            HandleZoomAndPan(hovered, io, contentMin);

            // Y-axis pan with middle mouse
            if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            {
                _curveScrollY += io.MouseDelta.Y;
            }

            float clipLength = MathF.Max(_clip!.length, 0.1f);

            // Ruler
            DrawRuler(drawList, contentMin, contentSize.X, clipLength);

            // Event markers on ruler
            DrawEventMarkers(drawList, contentMin, contentSize.X, hovered, io);

            float curveAreaY = contentMin.Y + RULER_HEIGHT;
            float curveAreaH = contentSize.Y - RULER_HEIGHT;

            // Clip curve area
            drawList.PushClipRect(
                new Vector2(contentMin.X, curveAreaY),
                new Vector2(contentMin.X + contentSize.X, contentMin.Y + contentSize.Y),
                true);

            // Auto-range Y — freeze during keyframe drag, update on release
            float yMin, yMax;
            if (_isDraggingKey && _hasCachedYRange)
            {
                yMin = _cachedYMin;
                yMax = _cachedYMax;
            }
            else
            {
                ComputeCurveYRange(out yMin, out yMax);
                float yr = yMax - yMin;
                if (yr < 0.01f) { yMin -= 0.5f; yMax += 0.5f; }
                _cachedYMin = yMin;
                _cachedYMax = yMax;
                _hasCachedYRange = true;
            }
            float yRange = yMax - yMin;
            if (yRange < 0.01f) yRange = 1f;

            // Y-axis grid
            DrawYAxisGrid(drawList, contentMin, contentSize, curveAreaY, curveAreaH, yMin, yMax);

            // Draw curves
            int colorIdx = 0;
            var tracksToShow = GetVisibleCurveTracks();
            foreach (var trackPath in tracksToShow)
            {
                if (!_clip.curves.TryGetValue(trackPath, out var curve) || curve.length == 0) continue;

                uint curveColor = CurveColors[colorIdx % CurveColors.Length];
                colorIdx++;

                // Draw spline segments
                DrawHermiteSpline(drawList, curve, contentMin, curveAreaY, curveAreaH,
                    yMin, yMax, clipLength, curveColor, contentSize.X);

                // Draw keyframe circles + tangent handles
                for (int k = 0; k < curve.length; k++)
                {
                    var kf = curve[k];
                    float sx = TimeToScreenX(kf.time, contentMin.X);
                    float sy = ValueToScreenY(kf.value, curveAreaY, curveAreaH, yMin, yMax);

                    if (sx < contentMin.X - 20 || sx > contentMin.X + contentSize.X + 20) continue;

                    bool isSelected = _selectedKeys.Contains((trackPath, k));

                    // Tangent handles (selected keys only)
                    if (isSelected)
                        DrawTangentHandles(drawList, curve, k, contentMin, curveAreaY, curveAreaH,
                            yMin, yMax, curveColor, hovered, io);

                    // Keyframe circle
                    uint kfColor = isSelected
                        ? ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.3f, 1f))
                        : curveColor;
                    drawList.AddCircleFilled(new Vector2(sx, sy), 4f, kfColor);
                    drawList.AddCircle(new Vector2(sx, sy), 4f, 0xFF000000);

                    // Click to select
                    if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        float dx = io.MousePos.X - sx;
                        float dy = io.MousePos.Y - sy;
                        if (dx * dx + dy * dy <= 36f) // radius 6
                        {
                            HandleKeyframeClick(trackPath, k, kf.time, io);
                        }
                    }
                }
            }

            drawList.PopClipRect();

            // Handle tangent drag
            HandleTangentDrag(io, contentMin, curveAreaY, curveAreaH, yMin, yMax);

            // Box select
            HandleBoxSelect(drawList, contentMin, contentSize, curveAreaY, hovered, io);

            // Drag keyframe(s) — 2D in curves mode
            HandleKeyframeDrag(contentMin, io, true, curveAreaY, curveAreaH, yMin, yMax);

            // Playhead
            HandlePlayhead(drawList, contentMin, contentSize, curveAreaY, hovered, io);

            // Double-click to add keyframe
            HandleDoubleClickAddCurves(contentMin, curveAreaY, curveAreaH, yMin, yMax, hovered, io);

            // Right-click context menu for tangent mode
            if (hovered && ImGui.BeginPopupContextItem("##CurvesContext"))
            {
                if (_selectedKeys.Count > 0)
                {
                    if (ImGui.MenuItem("Auto Tangent")) ApplyTangentModeToSelection(TangentMode.Auto);
                    if (ImGui.MenuItem("Linear Tangent")) ApplyTangentModeToSelection(TangentMode.Linear);
                    if (ImGui.MenuItem("Constant Tangent")) ApplyTangentModeToSelection(TangentMode.Constant);
                    if (ImGui.MenuItem("Free Tangent")) ApplyTangentModeToSelection(TangentMode.Free);
                }
                ImGui.EndPopup();
            }
        }

        private List<string> GetVisibleCurveTracks()
        {
            if (_clip == null) return new List<string>();

            // Show selected track(s), or all if none selected
            var selected = _selectedKeys.Select(s => s.path).Distinct().ToList();
            List<string> result;
            if (selected.Count > 0)
                result = selected;
            else if (_selectedTrackPaths.Count > 0)
                result = _selectedTrackPaths.ToList();
            else
                result = _clip.curves.Keys.ToList();

            // Filter out hidden tracks
            if (_hiddenTracks.Count > 0)
                result = result.Where(p => !_hiddenTracks.Contains(p)).ToList();

            return result;
        }

        private void ComputeCurveYRange(out float yMin, out float yMax)
        {
            yMin = float.MaxValue;
            yMax = float.MinValue;

            var tracks = GetVisibleCurveTracks();
            foreach (var path in tracks)
            {
                if (!_clip!.curves.TryGetValue(path, out var curve)) continue;
                for (int i = 0; i < curve.length; i++)
                {
                    float v = curve[i].value;
                    if (v < yMin) yMin = v;
                    if (v > yMax) yMax = v;
                }
            }

            if (yMin == float.MaxValue) { yMin = -1f; yMax = 1f; }

            // Add padding
            float padding = (yMax - yMin) * 0.15f;
            if (padding < 0.1f) padding = 0.5f;
            yMin -= padding;
            yMax += padding;
        }

        private void DrawYAxisGrid(ImDrawListPtr drawList, Vector2 contentMin, Vector2 contentSize,
            float curveAreaY, float curveAreaH, float yMin, float yMax)
        {
            uint gridColor = ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.3f));
            uint textColor = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f));

            float yRange = yMax - yMin;
            float step = CalculateNiceStep(yRange, curveAreaH / 40f);

            float startVal = MathF.Floor(yMin / step) * step;
            for (float v = startVal; v <= yMax; v += step)
            {
                float sy = ValueToScreenY(v, curveAreaY, curveAreaH, yMin, yMax);
                if (sy < curveAreaY || sy > curveAreaY + curveAreaH) continue;

                drawList.AddLine(
                    new Vector2(contentMin.X, sy),
                    new Vector2(contentMin.X + contentSize.X, sy),
                    gridColor);
                drawList.AddText(new Vector2(contentMin.X + 2, sy - 7), textColor, $"{v:F1}");
            }

            // Zero line
            float zeroY = ValueToScreenY(0f, curveAreaY, curveAreaH, yMin, yMax);
            if (zeroY >= curveAreaY && zeroY <= curveAreaY + curveAreaH)
            {
                drawList.AddLine(
                    new Vector2(contentMin.X, zeroY),
                    new Vector2(contentMin.X + contentSize.X, zeroY),
                    ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f)), 1.5f);
            }
        }

        private static float CalculateNiceStep(float range, float maxSteps)
        {
            if (maxSteps <= 0) maxSteps = 1;
            float rawStep = range / maxSteps;
            float magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(rawStep)));
            float normalized = rawStep / magnitude;
            float niceStep;
            if (normalized < 1.5f) niceStep = 1f;
            else if (normalized < 3.5f) niceStep = 2f;
            else if (normalized < 7.5f) niceStep = 5f;
            else niceStep = 10f;
            return niceStep * magnitude;
        }

        private void DrawHermiteSpline(ImDrawListPtr drawList, AnimationCurve curve,
            Vector2 contentMin, float curveAreaY, float curveAreaH,
            float yMin, float yMax, float clipLength, uint color,
            float contentWidth = 0f)
        {
            // LOD: adjust segment count based on pixel span of each pair
            const int MIN_SEGMENTS = 4;
            const int MAX_SEGMENTS = 24;

            for (int i = 0; i < curve.length - 1; i++)
            {
                var k0 = curve[i];
                var k1 = curve[i + 1];
                float dt = k1.time - k0.time;
                if (dt <= 0f) continue;

                // Cull pairs entirely outside viewport
                float sx0 = TimeToScreenX(k0.time, contentMin.X);
                float sx1 = TimeToScreenX(k1.time, contentMin.X);
                float rightEdge = contentMin.X + (contentWidth > 0 ? contentWidth : 9999f);
                if (sx1 < contentMin.X || sx0 > rightEdge) continue;

                // Adaptive segments: ~1 segment per 8 pixels
                float pixelSpan = MathF.Abs(sx1 - sx0);
                int segments = Math.Clamp((int)(pixelSpan / 8f), MIN_SEGMENTS, MAX_SEGMENTS);

                var points = new Vector2[segments + 1];
                for (int s = 0; s <= segments; s++)
                {
                    float t = s / (float)segments;
                    float time = k0.time + t * dt;
                    float value = HermiteInterpolate(k0.value, k0.outTangent * dt, k1.value, k1.inTangent * dt, t);

                    points[s] = new Vector2(
                        TimeToScreenX(time, contentMin.X),
                        ValueToScreenY(value, curveAreaY, curveAreaH, yMin, yMax));
                }

                for (int s = 0; s < segments; s++)
                {
                    drawList.AddLine(points[s], points[s + 1], color, 1.5f);
                }
            }

            // Extend flat lines before first / after last key
            if (curve.length > 0)
            {
                var first = curve[0];
                float firstSx = TimeToScreenX(first.time, contentMin.X);
                float firstSy = ValueToScreenY(first.value, curveAreaY, curveAreaH, yMin, yMax);
                if (firstSx > contentMin.X)
                {
                    drawList.AddLine(new Vector2(contentMin.X, firstSy), new Vector2(firstSx, firstSy), color, 1f);
                }

                var last = curve[curve.length - 1];
                float lastSx = TimeToScreenX(last.time, contentMin.X);
                float lastSy = ValueToScreenY(last.value, curveAreaY, curveAreaH, yMin, yMax);
                float rightEdge = contentMin.X + (contentWidth > 0 ? contentWidth : 800f);
                if (lastSx < rightEdge)
                {
                    drawList.AddLine(new Vector2(lastSx, lastSy), new Vector2(rightEdge, lastSy), color, 1f);
                }
            }
        }

        private static float HermiteInterpolate(float p0, float m0, float p1, float m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        private void DrawTangentHandles(ImDrawListPtr drawList, AnimationCurve curve, int keyIndex,
            Vector2 contentMin, float curveAreaY, float curveAreaH,
            float yMin, float yMax, uint curveColor, bool hovered, ImGuiIOPtr io)
        {
            var kf = curve[keyIndex];
            float sx = TimeToScreenX(kf.time, contentMin.X);
            float sy = ValueToScreenY(kf.value, curveAreaY, curveAreaH, yMin, yMax);

            float handleLenPx = 40f;
            uint handleColor = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.4f, 0.8f));

            // In tangent handle (left)
            if (keyIndex > 0)
            {
                float prevTime = curve[keyIndex - 1].time;
                float timeDiff = kf.time - prevTime;
                float handleTimeDelta = MathF.Min(timeDiff * 0.33f, handleLenPx / _zoom);

                float inX = TimeToScreenX(kf.time - handleTimeDelta, contentMin.X);
                float inVal = kf.value - kf.inTangent * handleTimeDelta;
                float inY = ValueToScreenY(inVal, curveAreaY, curveAreaH, yMin, yMax);

                drawList.AddLine(new Vector2(sx, sy), new Vector2(inX, inY), handleColor, 1.5f);
                drawList.AddCircleFilled(new Vector2(inX, inY), 3f, handleColor);

                // Click to start drag
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_isDraggingTangent)
                {
                    float dx = io.MousePos.X - inX;
                    float dy = io.MousePos.Y - inY;
                    if (dx * dx + dy * dy <= 25f)
                    {
                        var match = _selectedKeys.FirstOrDefault(s => s.keyIndex == keyIndex);
                        if (match.path != null)
                        {
                            _isDraggingTangent = true;
                            _isDraggingInTangent = true;
                            _draggingTangentKeyIndex = keyIndex;
                            _draggingTangentTrackPath = match.path;
                            BeginUndoAction();
                        }
                    }
                }
            }

            // Out tangent handle (right)
            if (keyIndex < curve.length - 1)
            {
                float nextTime = curve[keyIndex + 1].time;
                float timeDiff = nextTime - kf.time;
                float handleTimeDelta = MathF.Min(timeDiff * 0.33f, handleLenPx / _zoom);

                float outX = TimeToScreenX(kf.time + handleTimeDelta, contentMin.X);
                float outVal = kf.value + kf.outTangent * handleTimeDelta;
                float outY = ValueToScreenY(outVal, curveAreaY, curveAreaH, yMin, yMax);

                drawList.AddLine(new Vector2(sx, sy), new Vector2(outX, outY), handleColor, 1.5f);
                drawList.AddCircleFilled(new Vector2(outX, outY), 3f, handleColor);

                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_isDraggingTangent)
                {
                    float dx = io.MousePos.X - outX;
                    float dy = io.MousePos.Y - outY;
                    if (dx * dx + dy * dy <= 25f)
                    {
                        var match = _selectedKeys.FirstOrDefault(s => s.keyIndex == keyIndex);
                        if (match.path != null)
                        {
                            _isDraggingTangent = true;
                            _isDraggingInTangent = false;
                            _draggingTangentKeyIndex = keyIndex;
                            _draggingTangentTrackPath = match.path;
                            BeginUndoAction();
                        }
                    }
                }
            }
        }

        private void HandleTangentDrag(ImGuiIOPtr io, Vector2 contentMin,
            float curveAreaY, float curveAreaH, float yMin, float yMax)
        {
            if (!_isDraggingTangent) return;

            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) &&
                _draggingTangentTrackPath != null &&
                _clip!.curves.TryGetValue(_draggingTangentTrackPath, out var curve) &&
                _draggingTangentKeyIndex >= 0 && _draggingTangentKeyIndex < curve.length)
            {
                var kf = curve[_draggingTangentKeyIndex];
                float kfSx = TimeToScreenX(kf.time, contentMin.X);
                float kfSy = ValueToScreenY(kf.value, curveAreaY, curveAreaH, yMin, yMax);

                float mouseTime = ScreenXToTime(io.MousePos.X, contentMin.X);
                float mouseVal = ScreenYToValue(io.MousePos.Y, curveAreaY, curveAreaH, yMin, yMax);

                float dt = mouseTime - kf.time;
                float dv = mouseVal - kf.value;

                if (_isDraggingInTangent)
                {
                    if (dt < -0.001f)
                        kf.inTangent = dv / dt;
                }
                else
                {
                    if (dt > 0.001f)
                        kf.outTangent = dv / dt;
                }

                curve[_draggingTangentKeyIndex] = kf;
                _isDirty = true;
                ScrubPreview();
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _isDraggingTangent = false;
                EndUndoAction("Edit Tangent");
            }
        }

        // ================================================================
        // Shared rendering helpers
        // ================================================================

        private void HandleZoomAndPan(bool hovered, ImGuiIOPtr io, Vector2 contentMin)
        {
            // Zoom with mouse wheel (X-axis)
            if (hovered)
            {
                float scroll = io.MouseWheel;
                if (scroll != 0)
                {
                    float oldZoom = _zoom;
                    _zoom *= (1f + scroll * 0.1f);
                    _zoom = Math.Clamp(_zoom, 30f, 1000f);

                    float mouseRelX = io.MousePos.X - contentMin.X + _scrollX;
                    _scrollX = mouseRelX * (_zoom / oldZoom) - (io.MousePos.X - contentMin.X);
                    _scrollX = MathF.Max(0f, _scrollX);
                }
            }

            // Pan with middle mouse
            if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            {
                _scrollX -= io.MouseDelta.X;
                _scrollX = MathF.Max(0f, _scrollX);
            }
        }

        private void DrawRuler(ImDrawListPtr drawList, Vector2 contentMin, float width, float clipLength)
        {
            float rulerY = contentMin.Y;
            uint rulerBg = ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.18f, 1f));
            drawList.AddRectFilled(contentMin, new Vector2(contentMin.X + width, rulerY + RULER_HEIGHT), rulerBg);

            float pixelsPerTick = _zoom * 0.1f;
            float interval = 0.1f;
            if (pixelsPerTick < 15f) { interval = 0.5f; pixelsPerTick = _zoom * 0.5f; }
            if (pixelsPerTick < 15f) { interval = 1f; pixelsPerTick = _zoom; }
            if (pixelsPerTick < 15f) { interval = 5f; pixelsPerTick = _zoom * 5f; }

            float startTime = MathF.Floor(_scrollX / _zoom / interval) * interval;
            uint tickColor = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f));
            uint textColor = ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 1f));

            for (float t = startTime; t <= clipLength + interval; t += interval)
            {
                float x = TimeToScreenX(t, contentMin.X);
                if (x < contentMin.X - 50 || x > contentMin.X + width + 50) continue;

                bool isMajor = MathF.Abs(t % (interval * 5f)) < interval * 0.01f || interval >= 1f;
                float tickH = isMajor ? RULER_HEIGHT * 0.7f : RULER_HEIGHT * 0.4f;
                drawList.AddLine(
                    new Vector2(x, rulerY + RULER_HEIGHT - tickH),
                    new Vector2(x, rulerY + RULER_HEIGHT), tickColor);

                if (isMajor)
                {
                    string label = t < 10f ? $"{t:F1}" : $"{t:F0}";
                    drawList.AddText(new Vector2(x + 2, rulerY + 1), textColor, label);
                }
            }
        }

        private void DrawEventMarkers(ImDrawListPtr drawList, Vector2 contentMin, float width,
            bool hovered, ImGuiIOPtr io)
        {
            if (_clip == null) return;

            uint evtColor = ImGui.GetColorU32(new Vector4(0.3f, 0.6f, 1f, 1f));
            uint evtSelectedColor = ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 1f));
            float markerY = contentMin.Y + RULER_HEIGHT - 4;

            for (int i = 0; i < _clip.events.Count; i++)
            {
                var evt = _clip.events[i];
                float x = TimeToScreenX(evt.time, contentMin.X);
                if (x < contentMin.X - 10 || x > contentMin.X + width + 10) continue;

                bool selected = i == _selectedEventIndex;
                uint color = selected ? evtSelectedColor : evtColor;

                // Small upward triangle
                drawList.AddTriangleFilled(
                    new Vector2(x - 4, markerY + 4),
                    new Vector2(x + 4, markerY + 4),
                    new Vector2(x, markerY - 2),
                    color);

                // Click to select event
                if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    float dx = io.MousePos.X - x;
                    float dy = io.MousePos.Y - markerY;
                    if (MathF.Abs(dx) <= 6 && MathF.Abs(dy) <= 6)
                    {
                        _selectedEventIndex = i;
                        _selectedKeys.Clear();
                        _selectedKeyIndex = -1;
                        break;
                    }
                }
            }

            // Double-click on ruler to add event
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                float mouseY = io.MousePos.Y;
                if (mouseY >= contentMin.Y && mouseY <= contentMin.Y + RULER_HEIGHT)
                {
                    float time = ScreenXToTime(io.MousePos.X, contentMin.X);
                    time = MathF.Max(0f, time);

                    // Check we're not clicking an existing event
                    bool hitEvent = false;
                    for (int i = 0; i < _clip.events.Count; i++)
                    {
                        float ex = TimeToScreenX(_clip.events[i].time, contentMin.X);
                        if (MathF.Abs(io.MousePos.X - ex) <= 6) { hitEvent = true; break; }
                    }

                    if (!hitEvent)
                    {
                        BeginUndoAction();
                        _clip.events.Add(new AnimationEvent(time, "NewEvent"));
                        _selectedEventIndex = _clip.events.Count - 1;
                        _selectedKeys.Clear();
                        _isDirty = true;
                        EndUndoAction("Add Event");
                    }
                }
            }
        }

        // ================================================================
        // Playhead
        // ================================================================

        private void HandlePlayhead(ImDrawListPtr drawList, Vector2 contentMin, Vector2 contentSize,
            float trackAreaY, bool hovered, ImGuiIOPtr io)
        {
            float phX = TimeToScreenX(_playheadTime, contentMin.X);

            if (phX >= contentMin.X && phX <= contentMin.X + contentSize.X)
            {
                uint phColor = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.3f, 0.9f));
                drawList.AddLine(
                    new Vector2(phX, contentMin.Y),
                    new Vector2(phX, contentMin.Y + contentSize.Y), phColor, 2f);
                drawList.AddTriangleFilled(
                    new Vector2(phX - 5, contentMin.Y),
                    new Vector2(phX + 5, contentMin.Y),
                    new Vector2(phX, contentMin.Y + 8), phColor);
            }

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                float mouseY = io.MousePos.Y;
                if (mouseY >= contentMin.Y && mouseY <= contentMin.Y + RULER_HEIGHT)
                {
                    // Check we're not hitting an event marker
                    bool hitEvent = false;
                    if (_clip != null)
                    {
                        for (int i = 0; i < _clip.events.Count; i++)
                        {
                            float ex = TimeToScreenX(_clip.events[i].time, contentMin.X);
                            if (MathF.Abs(io.MousePos.X - ex) <= 6) { hitEvent = true; break; }
                        }
                    }

                    if (!hitEvent)
                    {
                        _isDraggingPlayhead = true;
                        _playheadTime = ScreenXToTime(io.MousePos.X, contentMin.X);
                        _playheadTime = MathF.Max(0f, _playheadTime);
                        _isPlaying = false;
                        ScrubPreview();
                    }
                }
            }

            if (_isDraggingPlayhead)
            {
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    _playheadTime = ScreenXToTime(io.MousePos.X, contentMin.X);
                    _playheadTime = Math.Clamp(_playheadTime, 0f, _clip!.length);
                    ScrubPreview();
                }
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    _isDraggingPlayhead = false;
            }
        }

        // ================================================================
        // Keyframe selection (Feature 7: multi-select)
        // ================================================================

        private void HandleKeyframeClick(string path, int keyIndex, float keyTime, ImGuiIOPtr io)
        {
            _selectedEventIndex = -1;

            if (io.KeyShift || io.KeyCtrl)
            {
                // Toggle in multi-select
                var key = (path, keyIndex);
                if (_selectedKeys.Contains(key))
                    _selectedKeys.Remove(key);
                else
                    _selectedKeys.Add(key);
            }
            else
            {
                // Single select
                _selectedKeys.Clear();
                _selectedKeys.Add((path, keyIndex));
            }

            _selectedTrackPaths.Clear();
            _selectedTrackPaths.Add(path);
            _selectedKeyIndex = keyIndex;

            // Start drag
            _isDraggingKey = true;
            _dragOriginalTimes.Clear();
            _dragOriginalValues.Clear();
            foreach (var sel in _selectedKeys)
            {
                if (_clip!.curves.TryGetValue(sel.path, out var c) && sel.keyIndex < c.length)
                {
                    _dragOriginalTimes[sel] = c[sel.keyIndex].time;
                    _dragOriginalValues[sel] = c[sel.keyIndex].value;
                }
            }
            _dragAnchorTime = keyTime;
            if (_clip!.curves.TryGetValue(path, out var curve) && keyIndex < curve.length)
                _dragAnchorValue = curve[keyIndex].value;
        }

        // ================================================================
        // Box select (Feature 7)
        // ================================================================

        private void HandleBoxSelect(ImDrawListPtr drawList, Vector2 contentMin, Vector2 contentSize,
            float trackAreaY, bool hovered, ImGuiIOPtr io)
        {
            // Start box select on click in empty area
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !_isDraggingKey &&
                !_isDraggingPlayhead && !_isDraggingTangent && io.MousePos.Y > contentMin.Y + RULER_HEIGHT)
            {
                // Check if we clicked on a keyframe — if not, start box select
                bool hitKey = false;
                if (_viewMode == TimelineViewMode.Dopesheet)
                {
                    for (int i = 0; i < _trackEntries.Count && !hitKey; i++)
                    {
                        if (!_clip!.curves.TryGetValue(_trackEntries[i].path, out var curve)) continue;
                        float centerY = trackAreaY + i * TRACK_HEIGHT + TRACK_HEIGHT * 0.5f;
                        for (int k = 0; k < curve.length; k++)
                        {
                            float sx = TimeToScreenX(curve[k].time, contentMin.X);
                            if (MathF.Abs(io.MousePos.X - sx) <= DIAMOND_SIZE + 2 &&
                                MathF.Abs(io.MousePos.Y - centerY) <= DIAMOND_SIZE + 2)
                            { hitKey = true; break; }
                        }
                    }
                }

                if (!hitKey)
                {
                    _isBoxSelecting = true;
                    _boxSelectStart = io.MousePos;
                    if (!io.KeyShift) _selectedKeys.Clear();
                }
            }

            if (_isBoxSelecting)
            {
                // Draw selection rect
                var boxEnd = io.MousePos;
                uint boxColor = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.3f));
                uint boxBorder = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.8f));
                drawList.AddRectFilled(_boxSelectStart, boxEnd, boxColor);
                drawList.AddRect(_boxSelectStart, boxEnd, boxBorder);

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    // Find keys inside box
                    float x0 = MathF.Min(_boxSelectStart.X, boxEnd.X);
                    float x1 = MathF.Max(_boxSelectStart.X, boxEnd.X);
                    float y0 = MathF.Min(_boxSelectStart.Y, boxEnd.Y);
                    float y1 = MathF.Max(_boxSelectStart.Y, boxEnd.Y);

                    if (_viewMode == TimelineViewMode.Dopesheet)
                    {
                        for (int i = 0; i < _trackEntries.Count; i++)
                        {
                            if (!_clip!.curves.TryGetValue(_trackEntries[i].path, out var curve)) continue;
                            float centerY = trackAreaY + i * TRACK_HEIGHT + TRACK_HEIGHT * 0.5f;
                            if (centerY < y0 || centerY > y1) continue;

                            for (int k = 0; k < curve.length; k++)
                            {
                                float sx = TimeToScreenX(curve[k].time, contentMin.X);
                                if (sx >= x0 && sx <= x1)
                                    _selectedKeys.Add((_trackEntries[i].path, k));
                            }
                        }
                    }
                    else // Curves
                    {
                        ComputeCurveYRange(out float yMin, out float yMax);
                        float curveAreaH = contentSize.Y - RULER_HEIGHT;

                        foreach (var trackPath in GetVisibleCurveTracks())
                        {
                            if (!_clip!.curves.TryGetValue(trackPath, out var curve)) continue;
                            for (int k = 0; k < curve.length; k++)
                            {
                                float sx = TimeToScreenX(curve[k].time, contentMin.X);
                                float sy = ValueToScreenY(curve[k].value, trackAreaY, curveAreaH, yMin, yMax);
                                if (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1)
                                    _selectedKeys.Add((trackPath, k));
                            }
                        }
                    }

                    _isBoxSelecting = false;
                }
            }
        }

        // ================================================================
        // Keyframe drag (Features 4, 7: snap + multi-drag)
        // ================================================================

        private void HandleKeyframeDrag(Vector2 contentMin, ImGuiIOPtr io, bool curvesMode,
            float curveAreaY = 0, float curveAreaH = 0, float yMin = 0, float yMax = 0)
        {
            if (!_isDraggingKey) return;

            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                float mouseTime = ScreenXToTime(io.MousePos.X, contentMin.X);
                float timeDelta = mouseTime - _dragAnchorTime;

                float valueDelta = 0f;
                if (curvesMode && curveAreaH > 0)
                {
                    float mouseValue = ScreenYToValue(io.MousePos.Y, curveAreaY, curveAreaH, yMin, yMax);
                    valueDelta = mouseValue - _dragAnchorValue;
                }

                // Snap delta to frame grid
                float frameTime = 1f / _clip!.frameRate;

                BeginUndoActionIfNeeded();

                // Move all selected keys by delta
                // We need to process in reverse-sorted order to avoid index shifting issues
                var sortedSel = _dragOriginalTimes.Keys
                    .OrderByDescending(k => _dragOriginalTimes[k])
                    .ToList();

                foreach (var sel in sortedSel)
                {
                    if (!_clip.curves.TryGetValue(sel.path, out var curve)) continue;
                    if (sel.keyIndex >= curve.length) continue;

                    float origTime = _dragOriginalTimes[sel];
                    float newTime = origTime + timeDelta;
                    newTime = MathF.Max(0f, newTime);
                    newTime = MathF.Round(newTime / frameTime) * frameTime; // Snap

                    var kf = curve[sel.keyIndex];
                    kf.time = newTime;

                    if (curvesMode && _dragOriginalValues.ContainsKey(sel))
                    {
                        kf.value = _dragOriginalValues[sel] + valueDelta;
                    }

                    curve[sel.keyIndex] = kf;
                }

                // Tooltip showing current time (and value in curves mode)
                {
                    float tipTime = ScreenXToTime(io.MousePos.X, contentMin.X);
                    float frameT = 1f / _clip!.frameRate;
                    float snapTime = MathF.Round(MathF.Max(0f, tipTime) / frameT) * frameT;
                    var fgDraw = ImGui.GetForegroundDrawList();
                    string tip = curvesMode && curveAreaH > 0
                        ? $"T:{snapTime:F2}  V:{ScreenYToValue(io.MousePos.Y, curveAreaY, curveAreaH, yMin, yMax):F2}"
                        : $"T:{snapTime:F2}";
                    fgDraw.AddText(io.MousePos + new Vector2(12, -16), 0xFFFFFFFF, tip);
                }
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                // Re-sort all affected curves and rebuild selection indices
                bool anyMoved = false;
                var newSelection = new HashSet<(string path, int keyIndex)>();

                foreach (var sel in _dragOriginalTimes.Keys)
                {
                    if (!_clip!.curves.TryGetValue(sel.path, out var curve)) continue;
                    if (sel.keyIndex >= curve.length) continue;

                    float origTime = _dragOriginalTimes[sel];
                    float curTime = curve[sel.keyIndex].time;
                    if (MathF.Abs(curTime - origTime) > 0.001f)
                    { anyMoved = true; break; }

                    if (_dragOriginalValues.TryGetValue(sel, out float origVal))
                    {
                        float curVal = curve[sel.keyIndex].value;
                        if (MathF.Abs(curVal - origVal) > 0.0001f)
                        { anyMoved = true; break; }
                    }
                }

                if (anyMoved)
                {
                    // Re-sort curves and remap selection
                    var affectedPaths = _dragOriginalTimes.Keys.Select(k => k.path).Distinct().ToList();
                    foreach (var path in affectedPaths)
                    {
                        if (!_clip!.curves.TryGetValue(path, out var curve)) continue;
                        // Collect all keys, re-sort
                        var keys = curve.GetKeys();
                        Array.Sort(keys, (a, b) => a.time.CompareTo(b.time));
                        curve.SetKeys(keys);
                    }

                    // Rebuild selection by finding keys at their new times (+ value for curves mode)
                    float dragTimeDelta = ScreenXToTime(io.MousePos.X, contentMin.X) - _dragAnchorTime;
                    foreach (var sel in _dragOriginalTimes.Keys)
                    {
                        if (!_clip!.curves.TryGetValue(sel.path, out var curve)) continue;
                        float origTime = _dragOriginalTimes[sel];
                        float newTime = origTime + dragTimeDelta;
                        float frameTime = 1f / _clip.frameRate;
                        newTime = MathF.Round(MathF.Max(0f, newTime) / frameTime) * frameTime;

                        // Find the key matching newTime (and value if available)
                        int bestIdx = -1;
                        for (int k = 0; k < curve.length; k++)
                        {
                            if (MathF.Abs(curve[k].time - newTime) < 0.001f)
                            {
                                if (bestIdx == -1)
                                    bestIdx = k;

                                // In curves mode, also match by value for disambiguation
                                if (_dragOriginalValues.TryGetValue(sel, out float origVal))
                                {
                                    float dragValueDelta = 0f;
                                    if (curveAreaH > 0)
                                        dragValueDelta = ScreenYToValue(io.MousePos.Y, curveAreaY, curveAreaH, yMin, yMax) - _dragAnchorValue;
                                    float expectedVal = origVal + dragValueDelta;
                                    if (MathF.Abs(curve[k].value - expectedVal) < 0.01f)
                                    {
                                        bestIdx = k;
                                        break;
                                    }
                                }
                                else
                                {
                                    break; // Dopesheet mode: first time match is fine
                                }
                            }
                        }
                        if (bestIdx >= 0)
                            newSelection.Add((sel.path, bestIdx));
                    }

                    _selectedKeys = newSelection;
                    AutoExpandLength();
                    _isDirty = true;
                    EndUndoAction("Move Keyframes");
                }

                _isDraggingKey = false;
                _dragOriginalTimes.Clear();
                _dragOriginalValues.Clear();
            }
        }

        // ================================================================
        // Double-click to add keyframe
        // ================================================================

        private void HandleDoubleClickAdd(Vector2 contentMin, float trackAreaY, bool hovered, ImGuiIOPtr io)
        {
            if (!hovered || !ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) return;

            float mouseY = io.MousePos.Y;
            if (mouseY < trackAreaY) return;

            int trackIdx = (int)((mouseY - trackAreaY) / TRACK_HEIGHT);
            if (trackIdx < 0 || trackIdx >= _trackEntries.Count) return;

            var entry = _trackEntries[trackIdx];
            if (!_clip!.curves.TryGetValue(entry.path, out var curve)) return;

            // Check if double-clicking an existing keyframe → select it instead of adding
            for (int k = 0; k < curve.length; k++)
            {
                float kx = TimeToScreenX(curve[k].time, contentMin.X);
                if (MathF.Abs(kx - io.MousePos.X) <= DIAMOND_SIZE + 2)
                {
                    _selectedKeys.Clear();
                    _selectedKeys.Add((entry.path, k));
                    _selectedTrackPaths.Clear();
                    _selectedTrackPaths.Add(entry.path);
                    _selectedKeyIndex = k;
                    _focusInspector = true;
                    return;
                }
            }

            float time = ScreenXToTime(io.MousePos.X, contentMin.X);
            time = MathF.Max(0f, time);
            float frameTime = 1f / _clip.frameRate;
            time = MathF.Round(time / frameTime) * frameTime;

            BeginUndoAction();
            float value = curve.Evaluate(time);
            int newIdx = curve.AddKey(new Keyframe(time, value));

            _selectedKeys.Clear();
            _selectedKeys.Add((entry.path, newIdx));
            _selectedTrackPaths.Clear();
            _selectedTrackPaths.Add(entry.path);
            _selectedKeyIndex = newIdx;
            AutoExpandLength();
            _isDirty = true;
            EndUndoAction("Add Keyframe");
        }

        private void HandleDoubleClickAddCurves(Vector2 contentMin, float curveAreaY, float curveAreaH,
            float yMin, float yMax, bool hovered, ImGuiIOPtr io)
        {
            if (!hovered || !ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) return;
            if (io.MousePos.Y < curveAreaY) return;

            // Add to the selected track (single selected only)
            string? targetTrack = _selectedTrackPaths.Count == 1 ? _selectedTrackPaths.First() : null;
            if (targetTrack == null)
            {
                var tracks = GetVisibleCurveTracks();
                if (tracks.Count == 1) targetTrack = tracks[0];
            }
            if (targetTrack == null || !_clip!.curves.TryGetValue(targetTrack, out var curve)) return;

            float time = ScreenXToTime(io.MousePos.X, contentMin.X);
            time = MathF.Max(0f, time);
            float frameTime = 1f / _clip.frameRate;
            time = MathF.Round(time / frameTime) * frameTime;

            float value = ScreenYToValue(io.MousePos.Y, curveAreaY, curveAreaH, yMin, yMax);

            BeginUndoAction();
            int newIdx = curve.AddKey(new Keyframe(time, value));
            _selectedKeys.Clear();
            _selectedKeys.Add((targetTrack, newIdx));
            _selectedTrackPaths.Clear();
            _selectedTrackPaths.Add(targetTrack);
            _selectedKeyIndex = newIdx;
            AutoExpandLength();
            _isDirty = true;
            EndUndoAction("Add Keyframe");
        }

        // ================================================================
        // Delete selected keys (multi-select aware)
        // ================================================================

        private void DeleteSelectedKeys()
        {
            if (_selectedKeys.Count == 0 && _selectedEventIndex >= 0)
            {
                // Delete selected event
                if (_clip != null && _selectedEventIndex < _clip.events.Count)
                {
                    BeginUndoAction();
                    _clip.events.RemoveAt(_selectedEventIndex);
                    _selectedEventIndex = -1;
                    _isDirty = true;
                    EndUndoAction("Delete Event");
                }
                return;
            }

            if (_selectedKeys.Count == 0) return;

            BeginUndoAction();

            // Delete in reverse index order per track to keep indices valid
            var byTrack = _selectedKeys.GroupBy(s => s.path)
                .ToDictionary(g => g.Key, g => g.Select(s => s.keyIndex).OrderByDescending(i => i).ToList());

            foreach (var (path, indices) in byTrack)
            {
                if (!_clip!.curves.TryGetValue(path, out var curve)) continue;
                foreach (int idx in indices)
                {
                    if (idx < curve.length)
                        curve.RemoveKey(idx);
                }
            }

            _selectedKeys.Clear();
            _selectedKeyIndex = -1;
            _clip!.RecalculateLength();
            _isDirty = true;
            EndUndoAction("Delete Keyframes");
        }

        // ================================================================
        // Copy/Paste (Feature 8)
        // ================================================================

        private void CopySelectedKeys()
        {
            _clipboard.Clear();
            if (_selectedKeys.Count == 0 || _clip == null) return;

            float minTime = float.MaxValue;
            foreach (var sel in _selectedKeys)
            {
                if (_clip.curves.TryGetValue(sel.path, out var curve) && sel.keyIndex < curve.length)
                {
                    float t = curve[sel.keyIndex].time;
                    if (t < minTime) minTime = t;
                }
            }

            foreach (var sel in _selectedKeys)
            {
                if (!_clip.curves.TryGetValue(sel.path, out var curve) || sel.keyIndex >= curve.length) continue;
                var kf = curve[sel.keyIndex];
                _clipboard.Add(new ClipboardKeyframe
                {
                    trackPath = sel.path,
                    relativeTime = kf.time - minTime,
                    value = kf.value,
                    inTangent = kf.inTangent,
                    outTangent = kf.outTangent,
                });
            }
        }

        private void PasteKeys()
        {
            if (_clipboard.Count == 0 || _clip == null) return;

            BeginUndoAction();
            _selectedKeys.Clear();

            foreach (var ckf in _clipboard)
            {
                if (!_clip.curves.TryGetValue(ckf.trackPath, out var curve)) continue;

                float time = _playheadTime + ckf.relativeTime;
                float frameTime = 1f / _clip.frameRate;
                time = MathF.Round(time / frameTime) * frameTime;

                int newIdx = curve.AddKey(new Keyframe(time, ckf.value, ckf.inTangent, ckf.outTangent));
                _selectedKeys.Add((ckf.trackPath, newIdx));
            }

            AutoExpandLength();
            _isDirty = true;
            EndUndoAction("Paste Keyframes");
        }

        // ================================================================
        // Select all / deselect (Feature 10)
        // ================================================================

        private void ToggleSelectAll()
        {
            if (_clip == null) return;

            if (_selectedKeys.Count > 0)
            {
                _selectedKeys.Clear();
                return;
            }

            foreach (var (path, curve) in _clip.curves)
            {
                for (int k = 0; k < curve.length; k++)
                    _selectedKeys.Add((path, k));
            }
        }

        private void SelectAllTracks()
        {
            if (_clip == null) return;

            if (_selectedTrackPaths.Count == _clip.curves.Count)
            {
                _selectedTrackPaths.Clear();
                return;
            }

            _selectedTrackPaths.Clear();
            foreach (var path in _clip.curves.Keys)
                _selectedTrackPaths.Add(path);
            _selectedKeys.Clear();
        }

        // ================================================================
        // Tangent mode (Feature 6)
        // ================================================================

        private int DetectTangentMode(AnimationCurve curve, int keyIndex)
        {
            // Simple heuristic
            var kf = curve[keyIndex];
            if (kf.inTangent == 0f && kf.outTangent == 0f)
                return (int)TangentMode.Constant;

            // Check if linear (interior keys)
            if (keyIndex > 0 && keyIndex < curve.length - 1)
            {
                var prev = curve[keyIndex - 1];
                var next = curve[keyIndex + 1];
                float dtIn = kf.time - prev.time;
                float dtOut = next.time - kf.time;
                float slopeIn = dtIn != 0f ? (kf.value - prev.value) / dtIn : 0f;
                float slopeOut = dtOut != 0f ? (next.value - kf.value) / dtOut : 0f;
                if (MathF.Abs(kf.inTangent - slopeIn) < 0.01f && MathF.Abs(kf.outTangent - slopeOut) < 0.01f)
                    return (int)TangentMode.Linear;
            }
            // Check edge keys (first / last)
            else if (keyIndex == 0 && curve.length > 1)
            {
                var next = curve[1];
                float dt = next.time - kf.time;
                float slope = dt != 0f ? (next.value - kf.value) / dt : 0f;
                if (MathF.Abs(kf.outTangent - slope) < 0.01f)
                    return (int)TangentMode.Linear;
            }
            else if (keyIndex == curve.length - 1 && curve.length > 1)
            {
                var prev = curve[keyIndex - 1];
                float dt = kf.time - prev.time;
                float slope = dt != 0f ? (kf.value - prev.value) / dt : 0f;
                if (MathF.Abs(kf.inTangent - slope) < 0.01f)
                    return (int)TangentMode.Linear;
            }

            return (int)TangentMode.Free;
        }

        private void ApplyTangentMode(AnimationCurve curve, int keyIndex, TangentMode mode)
        {
            var kf = curve[keyIndex];

            switch (mode)
            {
                case TangentMode.Auto:
                {
                    // Catmull-Rom style
                    float slopeIn = 0f, slopeOut = 0f;
                    if (keyIndex > 0 && keyIndex < curve.length - 1)
                    {
                        var prev = curve[keyIndex - 1];
                        var next = curve[keyIndex + 1];
                        float dt = next.time - prev.time;
                        float slope = dt != 0f ? (next.value - prev.value) / dt : 0f;
                        slopeIn = slope;
                        slopeOut = slope;
                    }
                    else if (keyIndex > 0)
                    {
                        var prev = curve[keyIndex - 1];
                        float dt = kf.time - prev.time;
                        slopeIn = slopeOut = dt != 0f ? (kf.value - prev.value) / dt : 0f;
                    }
                    else if (keyIndex < curve.length - 1)
                    {
                        var next = curve[keyIndex + 1];
                        float dt = next.time - kf.time;
                        slopeIn = slopeOut = dt != 0f ? (next.value - kf.value) / dt : 0f;
                    }
                    kf.inTangent = slopeIn;
                    kf.outTangent = slopeOut;
                    break;
                }
                case TangentMode.Linear:
                {
                    if (keyIndex > 0)
                    {
                        var prev = curve[keyIndex - 1];
                        float dt = kf.time - prev.time;
                        kf.inTangent = dt != 0f ? (kf.value - prev.value) / dt : 0f;
                    }
                    if (keyIndex < curve.length - 1)
                    {
                        var next = curve[keyIndex + 1];
                        float dt = next.time - kf.time;
                        kf.outTangent = dt != 0f ? (next.value - kf.value) / dt : 0f;
                    }
                    break;
                }
                case TangentMode.Constant:
                    kf.inTangent = 0f;
                    kf.outTangent = 0f;
                    break;
                case TangentMode.Free:
                    // Keep as-is
                    break;
            }

            curve[keyIndex] = kf;
        }

        private void ApplyTangentModeToSelection(TangentMode mode)
        {
            if (_clip == null || _selectedKeys.Count == 0) return;

            BeginUndoAction();
            foreach (var sel in _selectedKeys)
            {
                if (!_clip.curves.TryGetValue(sel.path, out var curve)) continue;
                if (sel.keyIndex >= curve.length) continue;
                ApplyTangentMode(curve, sel.keyIndex, mode);
            }
            _isDirty = true;
            EndUndoAction($"Set Tangent {mode}");
        }

        // ================================================================
        // Undo helpers (Feature 3)
        // ================================================================

        private Dictionary<string, Keyframe[]> SnapshotCurves()
        {
            var snap = new Dictionary<string, Keyframe[]>();
            if (_clip == null) return snap;
            foreach (var (path, curve) in _clip.curves)
                snap[path] = curve.GetKeys();
            return snap;
        }

        private List<AnimationEvent> SnapshotEvents()
        {
            if (_clip == null) return new List<AnimationEvent>();
            return new List<AnimationEvent>(_clip.events);
        }

        private void BeginUndoAction()
        {
            if (_clip == null) return;
            _undoBeforeState = (SnapshotCurves(), SnapshotEvents(), _clip.length);
        }

        private void BeginUndoActionIfNeeded()
        {
            if (_undoBeforeState == null)
                BeginUndoAction();
        }

        private void EndUndoAction(string description)
        {
            if (_undoBeforeState == null || _clip == null) return;

            var before = _undoBeforeState.Value;
            var action = new AnimationClipUndoAction(
                description, _clip,
                before.curves, before.events, before.length,
                SnapshotCurves(), SnapshotEvents(), _clip.length);

            UndoSystem.Record(action);
            _undoBeforeState = null;
        }

        // ================================================================
        // Selection helper after MoveKey
        // ================================================================

        private void UpdateSelectionAfterMove(string path, int oldIndex, int newIndex)
        {
            _selectedKeys.Remove((path, oldIndex));
            _selectedKeys.Add((path, newIndex));
            _selectedKeyIndex = newIndex;
        }

        // ================================================================
        // Preview playback
        // ================================================================

        private void UpdatePreview()
        {
            if (!_isPlaying || _clip == null || _contextAnimator == null) return;

            // Time.unscaledDeltaTime는 EditorPlayMode.Playing 시에만 갱신되므로
            // 에디터 프리뷰에서는 ImGui의 deltaTime을 사용
            _playheadTime += ImGui.GetIO().DeltaTime * _previewSpeed;

            float clipLength = _clip.length;
            if (clipLength <= 0f) return;

            switch (_clip.wrapMode)
            {
                case WrapMode.Loop:
                    _playheadTime %= clipLength;
                    break;
                case WrapMode.PingPong:
                    float cycle = _playheadTime % (clipLength * 2f);
                    _playheadTime = cycle <= clipLength ? cycle : clipLength * 2f - cycle;
                    break;
                case WrapMode.Once:
                    if (_playheadTime >= clipLength)
                    {
                        _playheadTime = clipLength;
                        _isPlaying = false;
                    }
                    break;
                case WrapMode.ClampForever:
                    _playheadTime = MathF.Min(_playheadTime, clipLength);
                    break;
            }

            _contextAnimator.clip = _clip;
            _contextAnimator.SampleAt(_playheadTime);
        }

        private void ScrubPreview()
        {
            if (_contextAnimator == null || _clip == null) return;
            CapturePreviewIfNeeded();
            _contextAnimator.clip = _clip;
            _contextAnimator.SampleAt(_playheadTime);
        }

        // ── Record mode helpers ──

        /// <summary>
        /// Record 모드에서 외부(Inspector, Gizmo)로부터 프로퍼티 변경 통지를 받아 키프레임을 자동 생성.
        /// </summary>
        /// <summary>Record undo가 보류 중이면 커밋. 기즈모 릴리스/Inspector 변경 후 호출.</summary>
        public void FlushRecordUndo()
        {
            if (_undoBeforeState != null)
                EndUndoAction("Record Keyframe");
        }

        public void RecordProperty(GameObject go, string componentType, string memberName,
                                   float value, string? subField = null)
        {
            if (!IsRecording || _clip == null) return;

            BeginUndoActionIfNeeded();

            string path = BuildRecordPath(go, componentType, memberName, subField);

            // curve 확보 (없으면 생성)
            if (!_clip.curves.TryGetValue(path, out var curve))
            {
                curve = new AnimationCurve();
                _clip.curves[path] = curve;
                RebuildTrackEntries();
            }

            // 프레임 스냅
            float frameTime = 1f / _clip.frameRate;
            float time = MathF.Round(_playheadTime / frameTime) * frameTime;

            // 기존 키가 같은 시간에 있으면 값만 업데이트
            for (int k = 0; k < curve.length; k++)
            {
                if (MathF.Abs(curve[k].time - time) < 0.001f)
                {
                    var kf = curve[k];
                    kf.value = value;
                    curve[k] = kf;
                    _isDirty = true;
                    return;
                }
            }

            curve.AddKey(new Keyframe(time, value));
            _clip.RecalculateLength();
            _isDirty = true;
        }

        private string BuildRecordPath(GameObject go, string componentType, string memberName, string? subField)
        {
            bool isSelf = (go == _contextAnimator!.gameObject);
            bool isTransform = (componentType == "Transform");

            string propPart = isTransform
                ? (subField != null ? $"{memberName}.{subField}" : memberName)
                : (subField != null ? $"{componentType}.{memberName}.{subField}" : $"{componentType}.{memberName}");

            return isSelf ? propPart : $"{go.name}.{propPart}";
        }

        // ── Preview snapshot helpers ──

        private void CapturePreviewIfNeeded()
        {
            if (_hasPreviewSnapshot || _contextAnimator == null) return;
            _contextAnimator.clip = _clip;
            _contextAnimator.InvalidateTargets();
            _contextAnimator.CapturePreviewSnapshot();
            _hasPreviewSnapshot = true;
        }

        private void RestorePreview()
        {
            if (!_hasPreviewSnapshot || _contextAnimator == null) return;
            _contextAnimator.RestorePreviewSnapshot();
            _hasPreviewSnapshot = false;
        }

        // ── Playhead auto-scroll ──

        /// <summary>재생 중 playhead가 보이는 영역을 벗어나면 스크롤을 따라간다.</summary>
        private void AutoScrollToPlayhead(float contentWidth)
        {
            if (!_isPlaying || contentWidth <= 0f) return;

            float phScreenX = _playheadTime * _zoom - _scrollX;

            if (phScreenX > contentWidth - 20f)
            {
                // playhead가 오른쪽 밖 → 스크롤해서 playhead를 25% 위치로
                _scrollX = _playheadTime * _zoom - contentWidth * 0.25f;
                _scrollX = MathF.Max(0f, _scrollX);
            }
            else if (phScreenX < 0f && _scrollX > 0f)
            {
                // playhead가 왼쪽 밖 (PingPong 등, scrollX > 0인 경우만)
                _scrollX = _playheadTime * _zoom - contentWidth * 0.75f;
                _scrollX = MathF.Max(0f, _scrollX);
            }
        }

        // ================================================================
        // Auto-expand length
        // ================================================================

        private void AutoExpandLength()
        {
            if (_clip == null) return;
            float maxKeyTime = 0f;
            foreach (var curve in _clip.curves.Values)
            {
                if (curve.length > 0)
                {
                    float last = curve[curve.length - 1].time;
                    if (last > maxKeyTime) maxKeyTime = last;
                }
            }
            if (maxKeyTime > _clip.length)
                _clip.length = maxKeyTime;
        }

        // ================================================================
        // Save / Revert
        // ================================================================

        private void Save()
        {
            if (_clip == null || _animPath == null) return;
            AnimationClipImporter.Export(_clip, _animPath);
            _isDirty = false;
            Debug.Log($"[AnimationEditor] Saved: {_animPath}");
        }

        private void Revert()
        {
            if (_animPath == null) return;
            RestorePreview();
            var importer = new AnimationClipImporter();
            var reloaded = importer.Import(_animPath);
            if (reloaded != null)
            {
                _clip = reloaded;
                _isDirty = false;
                _selectedKeys.Clear();
                _selectedTrackPaths.Clear();
                _selectedKeyIndex = -1;
                _selectedEventIndex = -1;
                RebuildTrackEntries();
                Debug.Log($"[AnimationEditor] Reverted: {_animPath}");
            }
        }

        // ================================================================
        // Add key at playhead
        // ================================================================

        private void AddKeyAtPlayhead()
        {
            if (_clip == null) return;

            BeginUndoAction();
            float frameTime = 1f / _clip.frameRate;
            float time = MathF.Round(_playheadTime / frameTime) * frameTime;

            if (_selectedTrackPaths.Count > 0)
            {
                _selectedKeys.Clear();
                foreach (var tp in _selectedTrackPaths)
                {
                    if (_clip.curves.TryGetValue(tp, out var selCurve))
                    {
                        float value = selCurve.Evaluate(time);
                        int newIdx = selCurve.AddKey(new Keyframe(time, value));
                        _selectedKeys.Add((tp, newIdx));
                    }
                }
            }
            else
            {
                _selectedKeys.Clear();
                foreach (var (path, curve) in _clip.curves)
                {
                    float value = curve.Evaluate(time);
                    int idx = curve.AddKey(new Keyframe(time, value));
                    _selectedKeys.Add((path, idx));
                }
            }

            AutoExpandLength();
            _isDirty = true;
            EndUndoAction("Add Keyframe");
        }

        // ================================================================
        // Jump to prev / next keyframe
        // ================================================================

        private void JumpToPrevKey()
        {
            if (_clip == null) return;
            float best = 0f;
            foreach (var curve in _clip.curves.Values)
            {
                for (int i = 0; i < curve.length; i++)
                {
                    float t = curve[i].time;
                    if (t < _playheadTime - 0.001f && t > best)
                        best = t;
                }
            }
            _playheadTime = best;
            ScrubPreview();
        }

        private void JumpToNextKey()
        {
            if (_clip == null) return;
            float best = _clip.length;
            foreach (var curve in _clip.curves.Values)
            {
                for (int i = 0; i < curve.length; i++)
                {
                    float t = curve[i].time;
                    if (t > _playheadTime + 0.001f && t < best)
                        best = t;
                }
            }
            _playheadTime = best;
            ScrubPreview();
        }

        // ================================================================
        // Coordinate conversion
        // ================================================================

        private float TimeToScreenX(float time, float contentMinX)
            => contentMinX + time * _zoom - _scrollX;

        private float ScreenXToTime(float screenX, float contentMinX)
            => (screenX - contentMinX + _scrollX) / _zoom;

        private float ValueToScreenY(float value, float curveAreaY, float curveAreaH, float yMin, float yMax)
        {
            float t = (value - yMin) / (yMax - yMin);
            return curveAreaY + curveAreaH - t * curveAreaH + _curveScrollY;
        }

        private float ScreenYToValue(float screenY, float curveAreaY, float curveAreaH, float yMin, float yMax)
        {
            float t = (curveAreaY + curveAreaH - screenY + _curveScrollY) / curveAreaH;
            return yMin + t * (yMax - yMin);
        }

        // ================================================================
        // Diamond rendering
        // ================================================================

        private static void DrawDiamond(ImDrawListPtr drawList, Vector2 center, float size, uint color, bool filled)
        {
            var top = new Vector2(center.X, center.Y - size);
            var right = new Vector2(center.X + size, center.Y);
            var bottom = new Vector2(center.X, center.Y + size);
            var left = new Vector2(center.X - size, center.Y);

            if (filled)
                drawList.AddQuadFilled(top, right, bottom, left, color);
            else
                drawList.AddQuad(top, right, bottom, left, color, 1.5f);
        }

        // ================================================================
        // Scene Animator scan
        // ================================================================

        private void RefreshSceneAnimators()
        {
            int frame = Time.frameCount;
            if (frame - _lastAnimatorScanFrame < 30 && _sceneAnimators.Length > 0) return;
            _lastAnimatorScanFrame = frame;

            _sceneAnimators = RoseEngine.Object.FindObjectsOfType<Animator>();
            _sceneAnimatorNames = new string[_sceneAnimators.Length];
            for (int i = 0; i < _sceneAnimators.Length; i++)
                _sceneAnimatorNames[i] = _sceneAnimators[i].gameObject.name;
        }

        // ================================================================
        // Track entries
        // ================================================================

        private static readonly string[] GroupableProperties = { "localPosition", "localEulerAngles", "localScale" };
        private static readonly Dictionary<string, string> GroupDisplayNames = new()
        {
            { "localPosition", "Position" },
            { "localEulerAngles", "Rotation" },
            { "localScale", "Scale" }
        };

        private void RebuildTrackEntries()
        {
            _trackEntries.Clear();
            if (_clip == null) return;

            var paths = _clip.curves.Keys.OrderBy(p => p).ToList();
            string? lastGroup = null;

            foreach (var path in paths)
            {
                // Detect groupable property (e.g. "localPosition.x" → group "localPosition")
                string? groupKey = null;
                foreach (var gp in GroupableProperties)
                {
                    if (path.StartsWith(gp + ".") || path.Contains("." + gp + "."))
                    {
                        // Extract full group key including object prefix
                        int idx = path.IndexOf(gp, StringComparison.Ordinal);
                        groupKey = path[..(idx + gp.Length)];
                        break;
                    }
                }

                // Insert group header if entering a new group
                if (groupKey != null && groupKey != lastGroup)
                {
                    string groupDisplay = groupKey;
                    foreach (var (k, v) in GroupDisplayNames)
                        groupDisplay = groupDisplay.Replace(k, v);

                    _trackEntries.Add(new TrackEntry
                    {
                        path = groupKey,
                        displayName = groupDisplay,
                        groupKey = groupKey,
                        isGroupHeader = true
                    });
                    lastGroup = groupKey;
                }

                string display = path;
                foreach (var (k, v) in GroupDisplayNames)
                    display = display.Replace(k, v);

                _trackEntries.Add(new TrackEntry
                {
                    path = path,
                    displayName = groupKey != null ? "  " + display.Split('.').Last() : display,
                    groupKey = groupKey,
                    isGroupHeader = false
                });
            }
        }

        private struct TrackEntry
        {
            public string path;
            public string displayName;
            public string? groupKey;     // e.g. "localPosition", null for non-grouped
            public bool isGroupHeader;   // true = foldout header row (no curve data)
        }

        // ================================================================
        // Add Property popup
        // ================================================================

        private void DrawAddPropertyPopup()
        {
            if (!ImGui.BeginPopup("##AddProperty")) return;

            ImGui.Text("Add Property");
            ImGui.Separator();

            var go = _contextAnimator?.gameObject;
            if (go == null)
            {
                ImGui.TextDisabled("(Select an Animator first)");
                ImGui.EndPopup();
                return;
            }

            var targets = new List<GameObject>();
            CollectDescendants(go, targets);

            foreach (var targetGo in targets)
            {
                string objName = targetGo.name;

                if (ImGui.BeginMenu(objName))
                {
                    if (ImGui.BeginMenu("Transform"))
                    {
                        string prefix = objName;
                        AddPropertyMenuItem($"{prefix}.localPosition.x");
                        AddPropertyMenuItem($"{prefix}.localPosition.y");
                        AddPropertyMenuItem($"{prefix}.localPosition.z");
                        ImGui.Separator();
                        AddPropertyMenuItem($"{prefix}.localEulerAngles.x");
                        AddPropertyMenuItem($"{prefix}.localEulerAngles.y");
                        AddPropertyMenuItem($"{prefix}.localEulerAngles.z");
                        ImGui.Separator();
                        AddPropertyMenuItem($"{prefix}.localScale.x");
                        AddPropertyMenuItem($"{prefix}.localScale.y");
                        AddPropertyMenuItem($"{prefix}.localScale.z");
                        ImGui.EndMenu();
                    }

                    foreach (var comp in targetGo._components)
                    {
                        if (comp is Transform) continue;
                        if (comp is Animator) continue;

                        var compType = comp.GetType();
                        string compName = compType.Name;
                        string pathPrefix = $"{objName}.{compName}";

                        var entries = CollectAnimatableMembers(pathPrefix, compType);
                        if (entries.Count == 0) continue;

                        if (ImGui.BeginMenu(compName))
                        {
                            foreach (var entry in entries)
                                AddPropertyMenuItem(entry);
                            ImGui.EndMenu();
                        }
                    }

                    ImGui.EndMenu();
                }
            }

            ImGui.EndPopup();
        }

        private static void CollectDescendants(GameObject go, List<GameObject> result)
        {
            result.Add(go);
            for (int i = 0; i < go.transform.childCount; i++)
                CollectDescendants(go.transform.GetChild(i).gameObject, result);
        }

        private static List<string> CollectAnimatableMembers(string pathPrefix, Type compType)
        {
            var results = new List<string>();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var fi in compType.GetFields(flags))
            {
                if (fi.DeclaringType == typeof(Component) || fi.DeclaringType == typeof(MonoBehaviour))
                    continue;
                AddPathsForType(results, pathPrefix, fi.Name, fi.FieldType);
            }

            foreach (var pi in compType.GetProperties(flags))
            {
                if (!pi.CanRead || !pi.CanWrite) continue;
                if (pi.DeclaringType == typeof(Component) || pi.DeclaringType == typeof(MonoBehaviour))
                    continue;
                if (pi.GetIndexParameters().Length > 0) continue;
                AddPathsForType(results, pathPrefix, pi.Name, pi.PropertyType);
            }

            return results;
        }

        private static void AddPathsForType(List<string> results, string prefix, string memberName, Type memberType)
        {
            if (memberType == typeof(float) || memberType == typeof(int))
            {
                results.Add($"{prefix}.{memberName}");
            }
            else if (memberType == typeof(RoseEngine.Vector2))
            {
                results.Add($"{prefix}.{memberName}.x");
                results.Add($"{prefix}.{memberName}.y");
            }
            else if (memberType == typeof(RoseEngine.Vector3))
            {
                results.Add($"{prefix}.{memberName}.x");
                results.Add($"{prefix}.{memberName}.y");
                results.Add($"{prefix}.{memberName}.z");
            }
            else if (memberType == typeof(RoseEngine.Vector4) || memberType == typeof(RoseEngine.Quaternion))
            {
                results.Add($"{prefix}.{memberName}.x");
                results.Add($"{prefix}.{memberName}.y");
                results.Add($"{prefix}.{memberName}.z");
                results.Add($"{prefix}.{memberName}.w");
            }
            else if (memberType == typeof(Color))
            {
                results.Add($"{prefix}.{memberName}.r");
                results.Add($"{prefix}.{memberName}.g");
                results.Add($"{prefix}.{memberName}.b");
                results.Add($"{prefix}.{memberName}.a");
            }
        }

        private void AddPropertyMenuItem(string path)
        {
            bool exists = _clip!.curves.ContainsKey(path);
            if (exists) ImGui.BeginDisabled();

            if (ImGui.MenuItem(path))
            {
                BeginUndoAction();
                var curve = new AnimationCurve();
                curve.AddKey(new Keyframe(0f, 0f));
                _clip.SetCurve(path, curve);
                AutoExpandLength();
                _isDirty = true;
                RebuildTrackEntries();
                EndUndoAction("Add Property");
            }

            if (exists) ImGui.EndDisabled();
        }
    }
}
