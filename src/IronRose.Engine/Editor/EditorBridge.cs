using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    public static class EditorBridge
    {
        // Engine → Editor (스냅샷)
        private static readonly ConcurrentQueue<SceneSnapshot> _snapshots = new();

        // Editor → Engine (커맨드)
        private static readonly ConcurrentQueue<EditorCommand> _commands = new();

        // Debug.Log → Editor
        private static readonly ConcurrentQueue<LogEntry> _logs = new();

        public static bool IsEditorConnected { get; set; }
        public static bool IsEditorWindowVisible { get; set; }

        private static volatile bool _toggleWindowRequested;

        public static void RequestToggleWindow() => _toggleWindowRequested = true;

        public static bool ConsumeToggleRequest()
        {
            if (!_toggleWindowRequested) return false;
            _toggleWindowRequested = false;
            return true;
        }

        // ── 엔진 측 (메인 스레드에서 호출) ──

        /// <summary>Update() 끝에서 호출 — 씬 스냅샷 생성</summary>
        public static void PushSnapshot()
        {
            if (!IsEditorConnected) return;
            var snapshot = SceneSnapshot.Capture();
            // 큐를 1개만 유지 (에디터가 느려도 최신 스냅샷만 사용)
            while (_snapshots.TryDequeue(out _)) { }
            _snapshots.Enqueue(snapshot);
        }

        /// <summary>Update() 시작에서 호출 — 에디터 커맨드 처리</summary>
        public static void ProcessCommands()
        {
            if (!IsEditorConnected) return;
            while (_commands.TryDequeue(out var cmd))
            {
                try
                {
                    cmd.Execute();
                }
                catch (Exception ex)
                {
                    EditorDebug.LogError($"[EditorBridge] Command failed: {ex.Message}");
                }
            }
        }

        // ── 에디터 측 (에디터 스레드에서 호출) ──

        public static SceneSnapshot? ConsumeSnapshot()
        {
            _snapshots.TryDequeue(out var snapshot);
            return snapshot;
        }

        public static void EnqueueCommand(EditorCommand cmd) => _commands.Enqueue(cmd);

        // ── Script Build 상태 ──
        private static volatile bool _buildStarted;
        private static volatile bool _buildInProgress;
        private static readonly System.Diagnostics.Stopwatch _buildStopwatch = new();
        private const float BUILD_MODAL_MIN_DURATION = 0.5f;

        public static void NotifyBuildStarted()
        {
            _buildStarted = true;
            _buildInProgress = true;
            _buildStopwatch.Restart();
        }

        public static void NotifyBuildFinished() => _buildInProgress = false;

        public static bool ConsumeBuildStarted()
        {
            if (!_buildStarted) return false;
            _buildStarted = false;
            return true;
        }

        /// <summary>빌드 모달을 표시해야 하는지 여부. 빌드 중이거나 최소 표시 시간이 지나지 않았으면 true.</summary>
        public static bool ShouldShowBuildModal =>
            _buildInProgress ||
            (_buildStopwatch.IsRunning && _buildStopwatch.Elapsed.TotalSeconds < BUILD_MODAL_MIN_DURATION);

        public static float BuildElapsed => (float)_buildStopwatch.Elapsed.TotalSeconds;

        public static void DrainLogs(List<LogEntry> buffer)
        {
            while (_logs.TryDequeue(out var entry))
                buffer.Add(entry);
        }

        // ── Project 패널 Ping ──
        private static volatile string? _pingAssetPath;
        public static void PingAsset(string assetPath) => _pingAssetPath = assetPath;
        public static string? ConsumePingAssetPath()
        {
            var path = _pingAssetPath;
            _pingAssetPath = null;
            return path;
        }

        // ── ImGui Overlay 참조 ──
        private static object? _imguiOverlay;
        public static void SetImGuiOverlay(object overlay) => _imguiOverlay = overlay;
        public static T? GetImGuiOverlay<T>() where T : class => _imguiOverlay as T;

        // ── Debug.Log 연동 ──
        public static void PushLog(LogEntry entry) => _logs.Enqueue(entry);
    }
}
