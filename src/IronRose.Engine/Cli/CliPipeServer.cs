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

        public bool IsRunning { get; private set; }

        private const int MAX_MESSAGE_SIZE = 16 * 1024 * 1024; // 16MB

        public CliPipeServer()
        {
            _pipeName = GetPipeName();
        }

        public void Start(CliCommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _cts = new CancellationTokenSource();
            IsRunning = true;

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

            _serverThread?.Join(TimeSpan.FromSeconds(3));
            _cts?.Dispose();
            IsRunning = false;
            EditorDebug.Log("[CLI] Pipe server stopped");
        }

        private void ServerLoop()
        {
            while (!_cts!.Token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1, // maxNumberOfServerInstances
                        PipeTransmissionMode.Byte);

                    // WaitForConnectionAsync + CancellationToken으로 종료 가능하게
                    pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();

                    if (_cts.Token.IsCancellationRequested)
                        break;

                    HandleClient(pipe);
                }
                catch (OperationCanceledException)
                {
                    // 정상 종료
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

            IsRunning = false;
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
