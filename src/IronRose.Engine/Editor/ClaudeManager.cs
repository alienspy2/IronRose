// ------------------------------------------------------------
// @file    ClaudeManager.cs
// @brief   Claude CLI 연동 호출의 단일 진입점. 현재는 aca-fix 스트리밍 호출만 지원한다.
//          claude -p --verbose --output-format stream-json 프로세스를 실행하고
//          stdout stream-json 라인을 파싱하여 ClaudeSession 버퍼에 텍스트를 축적한다.
// @deps    IronRose.Engine/EditorPreferences, RoseEngine/EditorDebug,
//          System.Diagnostics.Process, System.Text.Json.JsonDocument
// @exports
//   sealed class ClaudeSession : IDisposable
//     IsRunning: bool                                       — 백그라운드 워커 실행 중 여부
//     AppendOutput(string text): void (internal)            — 워커에서 출력 누적 (thread-safe)
//     SnapshotOutput(out bool dirtyConsumed): string?       — UI 스레드용 출력 스냅샷
//     Stop(): void                                          — 프로세스 트리 종료
//     Dispose(): void                                       — Stop 후 Process 정리
//   static class ClaudeManager
//     IsEnabled: bool                                       — EditorPreferences.EnableClaudeUsage 프록시
//     StartFix(string prompt, string workDir): ClaudeSession?  — aca-fix 세션 시작
// @note    명령 실행은 "claude -p --verbose --output-format stream-json"만 허용한다.
//          다른 인자 경로를 추가하지 말 것 (add-editor-preferences plan 섹션 2-1).
//          IsEnabled == false 일 때 StartFix는 null을 반환하고 경고를 로깅한다.
//          ClaudeSession thread-safety: UI 스레드는 SnapshotOutput만, 워커 스레드는
//          AppendOutput만 호출한다. Stop은 양쪽 모두에서 호출 가능.
//          Kill(entireProcessTree: true) 이후 stdout ReadLine이 IOException을 던질 수 있으므로
//          워커 전체를 try/catch로 감싸 에러 메시지를 AppendOutput에 남긴다.
// ------------------------------------------------------------
using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// Claude CLI 호출의 단일 세션. 출력 버퍼와 프로세스 핸들을 소유한다.
    /// UI 스레드는 SnapshotOutput, IsRunning 을, 워커 스레드는 AppendOutput 을 호출한다.
    /// </summary>
    public sealed class ClaudeSession : IDisposable
    {
        internal Process? Process { get; set; }
        public volatile bool IsRunning;

        private readonly StringBuilder _output = new();
        private readonly object _lock = new();
        private bool _outputDirty;

        internal void AppendOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                _output.Append(text);
                _outputDirty = true;
            }
        }

        /// <summary>
        /// UI 스레드가 호출. dirty 플래그를 소비하고 누적 출력 문자열을 반환한다.
        /// dirty == false 이면 null을 반환할 수 있다.
        /// </summary>
        public string? SnapshotOutput(out bool dirtyConsumed)
        {
            lock (_lock)
            {
                dirtyConsumed = _outputDirty;
                if (!_outputDirty) return null;
                _outputDirty = false;
                return _output.ToString();
            }
        }

        /// <summary>내부적으로만 사용: 누적 출력이 비어있는지 검사 후 비어있으면 text 를 추가한다.</summary>
        internal void AppendIfEmpty(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                if (_output.Length == 0)
                {
                    _output.Append(text);
                    _outputDirty = true;
                }
            }
        }

        /// <summary>내부: 버퍼를 비우고 dirty 플래그를 리셋한다. Clear 버튼 처리에서 사용.</summary>
        internal void ClearBuffer()
        {
            lock (_lock)
            {
                _output.Clear();
                _outputDirty = false;
            }
        }

        /// <summary>
        /// 프로세스 트리를 강제 종료한다. 이미 종료되었거나 존재하지 않으면 무시한다.
        /// </summary>
        public void Stop()
        {
            var proc = Process;
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        }

        public void Dispose()
        {
            Stop();
            try { Process?.Dispose(); } catch { /* ignore */ }
            Process = null;
        }
    }

    /// <summary>
    /// Claude CLI 연동 진입점.
    /// EditorPreferences.EnableClaudeUsage 로 게이트되며, false 일 때 호출을 거부한다.
    /// </summary>
    public static class ClaudeManager
    {
        public static bool IsEnabled => EditorPreferences.EnableClaudeUsage;

        /// <summary>
        /// aca-fix 에이전트를 claude CLI 로 스트리밍 실행한다.
        /// IsEnabled == false 이면 null 을 반환하고 경고 로그만 남긴다.
        /// </summary>
        public static ClaudeSession? StartFix(string prompt, string workDir)
        {
            if (!IsEnabled)
            {
                EditorDebug.LogWarning("[ClaudeManager] Disabled by preferences.");
                return null;
            }

            var session = new ClaudeSession { IsRunning = true };

            Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "claude",
                        Arguments = "-p --verbose --output-format stream-json",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workDir,
                    };

                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        session.AppendOutput("[Error] Failed to start claude process.\n");
                        return;
                    }
                    session.Process = proc;

                    // stdin 으로 프롬프트 전달 후 닫기
                    proc.StandardInput.Write(prompt);
                    proc.StandardInput.Close();

                    // stderr 를 별도 태스크로 수집
                    string stderr = "";
                    var stderrTask = Task.Run(() =>
                    {
                        try { stderr = proc.StandardError.ReadToEnd(); }
                        catch { /* ignore */ }
                    });

                    // stdout 을 한 줄씩 읽으며 스트리밍 파싱
                    string? line;
                    while ((line = proc.StandardOutput.ReadLine()) != null)
                    {
                        ProcessStreamLine(session, line);
                    }

                    proc.WaitForExit();
                    stderrTask.Wait(TimeSpan.FromSeconds(5));

                    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                    {
                        session.AppendOutput($"\n[stderr] {stderr}");
                    }
                }
                catch (Exception ex)
                {
                    session.AppendOutput($"\n[Error] {ex.Message}");
                }
                finally
                {
                    session.IsRunning = false;
                    // Process 자체는 session.Dispose()에서 정리한다.
                }
            });

            return session;
        }

        /// <summary>stream-json 라인을 파싱하여 텍스트 청크를 세션 출력에 추가한다.</summary>
        private static void ProcessStreamLine(ClaudeSession session, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                var type = typeEl.GetString();

                switch (type)
                {
                    // Anthropic streaming: 토큰 단위 텍스트 델타
                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var deltaText))
                        {
                            session.AppendOutput(deltaText.GetString() ?? "");
                        }
                        break;

                    // Claude Code: 어시스턴트 메시지 (content 배열)
                    case "assistant":
                        if (root.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var bt) &&
                                    bt.GetString() == "text" &&
                                    block.TryGetProperty("text", out var blockText))
                                {
                                    session.AppendOutput(blockText.GetString() ?? "");
                                }
                            }
                        }
                        break;

                    // 최종 결과: 스트리밍으로 누적된 출력이 비어있을 때만 result 문자열 사용
                    case "result":
                        if (root.TryGetProperty("result", out var result) &&
                            result.ValueKind == JsonValueKind.String)
                        {
                            session.AppendIfEmpty(result.GetString() ?? "");
                        }
                        break;
                }
            }
            catch
            {
                // JSON이 아닌 라인은 그대로 표시
                session.AppendOutput(line);
                session.AppendOutput("\n");
            }
        }
    }
}
