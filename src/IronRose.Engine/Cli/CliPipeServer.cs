// ------------------------------------------------------------
// @file    CliPipeServer.cs
// @brief   Named Pipe 서버. 백그라운드 스레드에서 CLI 클라이언트 요청을 수신한다.
//          요청을 CliCommandDispatcher에 위임하고 응답을 반환한다.
// @deps    System.IO.Pipes, IronRose.Engine.Cli/CliCommandDispatcher, RoseEngine/EditorDebug
// @exports
//   class CliPipeServer
//     Start(CliCommandDispatcher): void    -- 서버 시작
//     Stop(): void                          -- 서버 종료
//     IsRunning: bool                       -- 서버 동작 중 여부
// @note    하나의 클라이언트 연결을 순차 처리한다 (동시 연결 미지원).
//          메시지 프레임: [4 bytes little-endian length][N bytes UTF-8 string].
//          최대 메시지 크기: 16MB.
//          Linux: /tmp/CoreFxPipe_{pipeName} (Unix Domain Socket)
//          Windows: \\.\pipe\{pipeName}
//          IsRunning 은 volatile bool 백킹으로 가시성 보장 (Phase C-2, H1).
//          Stop() 은 Join 성공 후에만 CancellationTokenSource 를 Dispose 한다
//          (Phase C-1, C6). Join 타임아웃 시 CTS 를 leak 하고 경고 로그.
//          ServerLoop 은 ObjectDisposedException 을 graceful break 로 취급.
// ------------------------------------------------------------
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public class CliPipeServer
    {
        private CliCommandDispatcher? _dispatcher;
        private Thread? _serverThread;
        private CancellationTokenSource? _cts;
        private readonly string _pipeName;

        // (H1) Start(메인) → ServerLoop(백그라운드) → Stop(메인) 세 스레드 경로에서
        // 쓰기가 발생하므로 가시성 확보를 위해 volatile 로 선언한다. 읽기는 public
        // property 로 노출, 쓰기는 SetRunning() 로 래핑하여 호출 지점을 명시적으로
        // 남긴다.
        private volatile bool _isRunning;
        public bool IsRunning => _isRunning;

        private void SetRunning(bool value) => _isRunning = value;

        private const int MAX_MESSAGE_SIZE = 16 * 1024 * 1024; // 16MB

        // Stop() 에서 ServerLoop 종료를 기다리는 최대 시간. 이 시간을 넘기면
        // CancellationTokenSource 를 Dispose 하지 않고 leak 하는 대신 경고 로그를 남긴다.
        // 에디터 종료가 지나치게 느려지지 않으면서도 장기요청 처리 중인 클라이언트를 기다릴
        // 여유를 확보하는 균형점 (마스터 플랜 Phase C-1 확정값).
        private const int ServerJoinTimeoutMs = 5000;

        public CliPipeServer()
        {
            _pipeName = GetPipeName();
        }

        public void Start(CliCommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _cts = new CancellationTokenSource();
            SetRunning(true);

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "CliPipeServer"
            };
            _serverThread.Start();

            // Linux: /tmp/CoreFxPipe_{pipeName}, Windows: \\.\pipe\{pipeName}
            var fullPath = OperatingSystem.IsLinux()
                ? $"/tmp/CoreFxPipe_{_pipeName}"
                : $@"\\.\pipe\{_pipeName}";
            EditorDebug.Log($"[CLI] Pipe server started: {fullPath}");
        }

        public void Stop()
        {
            if (!IsRunning) return;

            EditorDebug.Log("[CLI] Pipe server stopping...");
            _cts?.Cancel();

            // Linux에서 소켓 파일 삭제하여 WaitForConnectionAsync 블로킹 해제
            if (OperatingSystem.IsLinux())
            {
                var socketPath = $"/tmp/CoreFxPipe_{_pipeName}";
                try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
            }

            // (C6) Join 성공 후에만 CancellationTokenSource 를 Dispose 한다.
            // Join 타임아웃 시 Dispose 를 건너뛰어 ServerLoop 이 disposed token 에
            // 접근해 ObjectDisposedException 이 발생하는 것을 방지한다. 소유권을
            // ServerLoop 쪽에 이전하는 셈이며, 해당 스레드가 결국 종료되면 GC 가 수거한다.
            bool joined = _serverThread?.Join(ServerJoinTimeoutMs) ?? true;
            if (joined)
            {
                _cts?.Dispose();
                _cts = null;
            }
            else
            {
                EditorDebug.LogWarning(
                    $"[CLI] Pipe server thread did not terminate within {ServerJoinTimeoutMs}ms; " +
                    "leaking CancellationTokenSource to avoid ObjectDisposedException in ServerLoop.");
            }

            SetRunning(false);
            EditorDebug.Log("[CLI] Pipe server stopped");
        }

        private void ServerLoop()
        {
            while (true)
            {
                // (C6) Stop() 과의 경합으로 _cts 가 null/disposed 상태가 될 수 있으므로
                // 매 루프에서 snapshot 을 잡고 ODE 를 정상 종료로 취급한다.
                var cts = _cts;
                if (cts == null) break;

                CancellationToken token;
                try
                {
                    token = cts.Token;
                    if (token.IsCancellationRequested) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1, // maxNumberOfServerInstances
                        PipeTransmissionMode.Byte);

                    // WaitForConnectionAsync + CancellationToken으로 종료 가능하게
                    pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();

                    if (token.IsCancellationRequested)
                        break;

                    HandleClient(pipe);
                }
                catch (OperationCanceledException)
                {
                    // 정상 종료
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // (C6) Stop() 이 _cts.Dispose() 를 먼저 호출하면 token 접근 시 ODE.
                    // 이 경로는 Join 이 성공한 뒤에만 발생할 수 있으므로 정상 종료로 본다.
                    break;
                }
                catch (Exception ex)
                {
                    EditorDebug.LogWarning($"[CLI] Pipe server error: {ex.Message}");
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { }
                }
            }

            // IsRunning 은 Stop() 에서도 false 로 설정하지만, ServerLoop 이 자력으로
            // 종료되는 케이스(예외 폭주 등)를 위해 여기서도 false 로 마킹한다.
            SetRunning(false);
        }

        private void HandleClient(NamedPipeServerStream pipe)
        {
            try
            {
                while (pipe.IsConnected && !_cts!.Token.IsCancellationRequested)
                {
                    var request = ReadMessage(pipe);
                    if (request == null)
                        break; // 클라이언트 연결 종료

                    var response = _dispatcher!.Dispatch(request);
                    WriteMessage(pipe, response);
                }
            }
            catch (IOException)
            {
                // 클라이언트가 연결을 끊음 -- 정상 동작
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[CLI] Client handling error: {ex.Message}");
            }
            finally
            {
                try { pipe.Disconnect(); } catch { }
            }
        }

        private static string? ReadMessage(Stream stream)
        {
            // 4바이트 길이 접두사 읽기
            var lengthBuf = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int n = stream.Read(lengthBuf, bytesRead, 4 - bytesRead);
                if (n == 0) return null; // 스트림 끝
                bytesRead += n;
            }

            int length = BitConverter.ToInt32(lengthBuf, 0); // little-endian (기본)
            if (length <= 0 || length > MAX_MESSAGE_SIZE)
                return null;

            // 메시지 본문 읽기
            var messageBuf = new byte[length];
            bytesRead = 0;
            while (bytesRead < length)
            {
                int n = stream.Read(messageBuf, bytesRead, length - bytesRead);
                if (n == 0) return null;
                bytesRead += n;
            }

            return Encoding.UTF8.GetString(messageBuf);
        }

        private static void WriteMessage(Stream stream, string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            var lengthBuf = BitConverter.GetBytes(bytes.Length); // little-endian (기본)
            stream.Write(lengthBuf, 0, 4);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static string GetPipeName()
        {
            var projectName = ProjectContext.ProjectName;
            if (string.IsNullOrEmpty(projectName))
                projectName = "default";
            var safeName = SanitizeForPipeName(projectName);
            return $"ironrose-cli-{safeName}";
        }

        private static string SanitizeForPipeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : "default";
        }
    }
}
