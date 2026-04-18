# Phase C: CLI 파이프 서버 라이프사이클 & 디스패처

## 목표

- `CliPipeServer.Stop()` 의 Dispose 순서 버그 수정 (C6: Join 완료 전 `_cts.Dispose()` 호출로 ServerLoop 이 disposed token 접근 → `ObjectDisposedException` 위험 제거).
- `CliPipeServer.IsRunning` 플래그의 스레드 간 가시성 보장 (H1: 메인과 ServerLoop 백그라운드 스레드 양쪽 쓰기).
- `CliCommandDispatcher.ExecuteOnMainThread` 의 무조건 5초 블로킹 제거 (C3: 메인 stall 사전 감지 + 재진입 방어 + 타임아웃 task cleanup).
- Phase C 완료 후 에디터 종료/CLI 장기요청 중 종료/메인 stall 중 CLI 호출 시나리오에서 예외 없이 동작해야 한다.

## 선행 조건

- **Phase A 머지 완료**: `RoseEngine.ThreadGuard.IsMainThread` 가 사용 가능해야 한다 (`src/IronRose.Contracts/ThreadGuard.cs`).
- **Phase B 머지 완료**: 충돌 방지 차원. Phase B 는 C 와 파일이 겹치지 않으므로 빌드 상 의존은 없다.
- `CliCommandDispatcher.cs`, `CliPipeServer.cs` 현 상태는 본 문서 작성 시점(2026-04-18)의 main 브랜치 기준이다.

## Worktree 전략

- **단일 worktree**: `feat/phase-c-cli-pipe`.
- 서브 phase C-1, C-2, C-3 이 모두 `CliPipeServer.cs`(C-1, C-2) 와 `CliCommandDispatcher.cs`(C-3) 2개 파일만 건드리므로 분리하면 머지 복잡도만 늘어난다. 3개 서브 phase 를 **하나의 worktree 안에서 순서대로 커밋**한다 (리뷰는 worktree 단위 1회).

## 배경: 현재 코드 구조 (aca-coder가 원본을 열지 않아도 되도록 발췌)

### `CliPipeServer.cs` — 전체 207줄

현재 `CliPipeServer` 은 다음 필드/메서드를 보유:

```csharp
public class CliPipeServer
{
    private CliCommandDispatcher? _dispatcher;
    private Thread? _serverThread;
    private CancellationTokenSource? _cts;
    private readonly string _pipeName;

    public bool IsRunning { get; private set; }       // ← H1 대상

    private const int MAX_MESSAGE_SIZE = 16 * 1024 * 1024;

    public CliPipeServer() { _pipeName = GetPipeName(); }

    public void Start(CliCommandDispatcher dispatcher) { ... }
    public void Stop() { ... }                         // ← C6 대상
    private void ServerLoop() { ... }                  // ← C6 대상 (ODE catch)
    private void HandleClient(NamedPipeServerStream pipe) { ... }
    private static string? ReadMessage(Stream stream) { ... }
    private static void WriteMessage(Stream stream, string message) { ... }
    private static string GetPipeName() { ... }
    private static string SanitizeForPipeName(string name) { ... }
}
```

### `CliCommandDispatcher.cs` — 3177줄, 핵심 블록만 발췌

**파일 상단 (line 51-92)** — using / 네임스페이스 / 필드 / `MainThreadTask`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using IronRose.AssetPipeline;
using IronRose.Engine.Editor;
using RoseEngine;                                   // ThreadGuard 접근 가능
using Tomlyn.Model;

namespace IronRose.Engine.Cli
{
    public partial class CliCommandDispatcher
    {
        private readonly Dictionary<string, Func<string[], string>> _handlers = new();
        private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();
        private readonly CliLogBuffer _logBuffer;

        internal static string? _pendingScreenshotPath;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private class MainThreadTask
        {
            public required Func<string> Execute { get; init; }
            public ManualResetEventSlim Done { get; } = new(false);
            public string? Result { get; set; }
        }

        public CliCommandDispatcher(CliLogBuffer logBuffer)
        {
            _logBuffer = logBuffer;
            RegisterHandlers();
        }
```

**`Dispatch` / `ProcessMainThreadQueue` (line 94-133)**:

```csharp
public string Dispatch(string requestLine) { ... }   // 변경 없음

public void ProcessMainThreadQueue()
{
    while (_mainThreadQueue.TryDequeue(out var task))
    {
        try
        {
            task.Result = task.Execute();
        }
        catch (Exception ex)
        {
            task.Result = JsonError(ex.Message);
        }
        finally
        {
            task.Done.Set();
        }
    }
}
```

**`ExecuteOnMainThread` (line 2730-2737)**:

```csharp
private string ExecuteOnMainThread(Func<string> action)
{
    var task = new MainThreadTask { Execute = action };
    _mainThreadQueue.Enqueue(task);
    if (!task.Done.Wait(TimeSpan.FromSeconds(5)))
        return JsonError("Main thread timeout (5s)");
    return task.Result!;
}
```

### 호출자 / 파급 조사 결과

- **`CliPipeServer.IsRunning` 외부 참조**: 코드베이스 전체 grep 결과 **외부 참조 없음**. 내부 `Stop()`의 early-return 체크와 `ServerLoop` 종료 시 대입만 존재. 따라서 property 시그니처를 `public bool IsRunning => ...` 로 바꿔도 호출자 수정 불요. (동일 이름 `IsRunning`이 다른 클래스들 — `ClaudeManager.Session`, `Stopwatch`, `FeedbackPanel._fixSession` — 에도 존재하지만 모두 별개 타입이다.)
- **`CliPipeServer.Stop()` 호출 지점**: `EngineCore.cs:541` 한 군데 (`_cliPipeServer?.Stop();`) 만 존재. 반환값 없음. 수정 불요.
- **`ProcessMainThreadQueue` 호출 지점**: `EngineCore.cs:235` (`Update` 메서드 내부 첫 블록) 한 군데. 수정 불요.
- **`ExecuteOnMainThread` 호출 지점**: `CliCommandDispatcher.cs` 내부 약 100곳. 전부 `return JsonError(...)` 또는 `return JsonOk(...)` 형태의 `Func<string>` 을 전달하고 결과 `string` 을 그대로 반환. 본 Phase 에서 `ExecuteOnMainThread` 의 **시그니처/반환 타입은 유지**하므로 호출자 수정 불요.

---

## 서브 phase C-1: `CliPipeServer.Stop()` 순서 수정 + `ServerLoop` ODE 방어 (C6)

### 수정할 파일

#### `src/IronRose.Engine/Cli/CliPipeServer.cs`

**변경 1**: 파일 상단 상수 추가. `const int MAX_MESSAGE_SIZE` 바로 위에 Join 타임아웃 상수를 새로 추가한다.

- old_string (line 35 근처):

```csharp
        private const int MAX_MESSAGE_SIZE = 16 * 1024 * 1024; // 16MB
```

- new_string:

```csharp
        private const int MAX_MESSAGE_SIZE = 16 * 1024 * 1024; // 16MB

        // Stop() 에서 ServerLoop 종료를 기다리는 최대 시간. 이 시간을 넘기면
        // CancellationTokenSource 를 Dispose 하지 않고 leak 하는 대신 경고 로그를 남긴다.
        // 에디터 종료가 지나치게 느려지지 않으면서도 장기요청 처리 중인 클라이언트를 기다릴
        // 여유를 확보하는 균형점 (마스터 플랜 Phase C-1 확정값).
        private const int ServerJoinTimeoutMs = 5000;
```

**변경 2**: `Stop()` 전체 재작성.

- old_string (line 62-80 전체):

```csharp
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
```

- new_string:

```csharp
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

            IsRunning = false;
            EditorDebug.Log("[CLI] Pipe server stopped");
        }
```

**변경 3**: `ServerLoop` 의 루프 본체를 `ObjectDisposedException` 에 대해 graceful 하게 만든다. 현재는 `_cts!.Token.IsCancellationRequested` 접근과 `WaitForConnectionAsync(_cts.Token)` 접근이 Stop() 과 경합할 수 있으므로, `ODE` 도 정상 종료 시그널로 취급한다.

- old_string (line 82-119 전체):

```csharp
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
```

- new_string:

```csharp
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
            // 실제 백킹 필드 대입은 C-2 에서 volatile 로 전환됨.
            IsRunning = false;
        }
```

**구현 힌트**:
- `Thread.Join(int ms)` 은 `bool` 을 반환 — true 면 스레드가 종료됨, false 면 타임아웃. 이 반환값으로 Dispose 여부를 결정.
- `_serverThread` 가 null 인 케이스(= `Start` 가 호출된 적 없음)는 `Stop()` 맨 앞 `if (!IsRunning) return;` 에서 이미 걸러진다 — `Start()` 이후엔 `_serverThread` 도 항상 non-null. `?? true` 는 형식적 null-safety.
- 기존 `catch (OperationCanceledException)` 은 유지한다. `WaitForConnectionAsync(token)` 이 cancel 되면 OCE, token 이 dispose 되면 ODE — 둘 다 break.

### 검증 (C-1 커밋 후)

- [ ] `dotnet build` 성공.
- [ ] 에디터 시작/종료 반복 (10회) — 크래시/예외 없음.
- [ ] Join 타임아웃을 유도하려면 ServerLoop 내부에 `Thread.Sleep(TimeSpan.FromSeconds(20))` 을 임시 삽입 → Stop 이 5초 warning 로그를 남기고 진행하는지 확인. (검증 후 원복.)

---

## 서브 phase C-2: `IsRunning` 플래그 volatile 전환 (H1)

### 수정할 파일

#### `src/IronRose.Engine/Cli/CliPipeServer.cs`

**변경 4**: `IsRunning` auto-property 를 volatile 백킹 필드 + readonly property + 내부 setter 메서드로 교체.

- old_string (line 33):

```csharp
        public bool IsRunning { get; private set; }
```

- new_string:

```csharp
        // (H1) Start(메인) → ServerLoop(백그라운드) → Stop(메인) 세 스레드 경로에서
        // 쓰기가 발생하므로 가시성 확보를 위해 volatile 로 선언한다. 읽기는 public
        // property 로 노출, 쓰기는 SetRunning() 로 래핑하여 호출 지점을 명시적으로
        // 남긴다.
        private volatile bool _isRunning;
        public bool IsRunning => _isRunning;

        private void SetRunning(bool value) => _isRunning = value;
```

**변경 5**: `Start()` 내부의 `IsRunning = true` 를 `SetRunning(true)` 로 치환.

- old_string (line 46 부근, `Start` 메서드 안):

```csharp
            _dispatcher = dispatcher;
            _cts = new CancellationTokenSource();
            IsRunning = true;
```

- new_string:

```csharp
            _dispatcher = dispatcher;
            _cts = new CancellationTokenSource();
            SetRunning(true);
```

**변경 6**: `Stop()` 내부의 `IsRunning = false` 치환. (C-1 에서 이미 수정한 `Stop()` 의 끝부분에만 존재.)

- old_string:

```csharp
            IsRunning = false;
            EditorDebug.Log("[CLI] Pipe server stopped");
        }
```

- new_string:

```csharp
            SetRunning(false);
            EditorDebug.Log("[CLI] Pipe server stopped");
        }
```

**변경 7**: `ServerLoop()` 끝부분의 `IsRunning = false` 치환.

- old_string (C-1 에서 수정된 `ServerLoop()` 의 끝부분. 주석까지 포함해 유일한 매칭을 보장):

```csharp
            // IsRunning 은 Stop() 에서도 false 로 설정하지만, ServerLoop 이 자력으로
            // 종료되는 케이스(예외 폭주 등)를 위해 여기서도 false 로 마킹한다.
            // 실제 백킹 필드 대입은 C-2 에서 volatile 로 전환됨.
            IsRunning = false;
        }
```

- new_string:

```csharp
            // IsRunning 은 Stop() 에서도 false 로 설정하지만, ServerLoop 이 자력으로
            // 종료되는 케이스(예외 폭주 등)를 위해 여기서도 false 로 마킹한다.
            SetRunning(false);
        }
```

**구현 힌트**:
- `volatile bool` 은 .NET 메모리 모델상 read/write 에 acquire/release 시맨틱이 적용되어 다른 스레드가 최신값을 보는 것이 보장된다. `bool` 대입 자체는 atomic 이므로 `Interlocked.Exchange` 까지는 불필요 (플래그가 false 로 두 번 대입되어도 문제없음).
- `SetRunning` 은 `private` 이므로 JIT 에서 인라인될 수 있다. 성능 영향 없음.
- `public bool IsRunning` 시그니처 자체(`get` only 로 보임)는 외부에서 동일하게 읽힌다. 외부 호출자 없음 (위 "호출자 파급 조사" 참조).

### 검증 (C-2 커밋 후)

- [ ] `dotnet build` 성공 — volatile 필드는 `bool` 타입에 완전 호환.
- [ ] `CliPipeServer.cs` 에 남은 `IsRunning = ` 대입 (property setter) 이 **0개**. `grep -n "IsRunning = " src/IronRose.Engine/Cli/CliPipeServer.cs` 로 확인.
- [ ] 에디터 시작/종료 1회 정상.

---

## 서브 phase C-3: `ExecuteOnMainThread` 개선 (C3)

### 개선 사항 요약

1. **메인 stall 감지**: `ProcessMainThreadQueue` 진입 시 현재 시각을 `_lastDrainTicks` 에 기록. `ExecuteOnMainThread` 진입 시 마지막 드레인으로부터 `StallThresholdMs`(2초) 이상 지났으면 즉시 `busy` 에러 반환 (큐에 넣지 않음).
2. **재진입 방어**: `ThreadGuard.IsMainThread == true` 이면 `EditorDebug.LogError` 로 설계 오류를 surface 하고, **action 을 직접 동기 실행**하여 데드락 회피.
3. **타임아웃 시 task cleanup**: `MainThreadTask.IsCancelled` 플래그를 추가. `ExecuteOnMainThread` 가 타임아웃하면 플래그를 true 로 set → `ProcessMainThreadQueue` 에서 해당 task 를 skip.
4. **상수 중앙화**: `TimeoutMs = 5000`, `StallThresholdMs = 2000`.

### 수정할 파일

#### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

**변경 8**: `MainThreadTask` 정의에 `IsCancelled` 플래그 추가.

- old_string (line 81-86):

```csharp
        private class MainThreadTask
        {
            public required Func<string> Execute { get; init; }
            public ManualResetEventSlim Done { get; } = new(false);
            public string? Result { get; set; }
        }
```

- new_string:

```csharp
        private class MainThreadTask
        {
            public required Func<string> Execute { get; init; }
            public ManualResetEventSlim Done { get; } = new(false);
            public string? Result { get; set; }

            // (C3) 백그라운드가 타임아웃 후 큐에 남은 task 를 메인이 실행하지 않도록
            // 표시한다. Volatile.Read/Write 로 접근하여 가시성 확보.
            private int _cancelled;  // 0=alive, 1=cancelled
            public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;
            public void MarkCancelled() => Volatile.Write(ref _cancelled, 1);
        }
```

**변경 9**: 파일 상단 필드 블록(`_mainThreadQueue` 선언 바로 아래)에 `_lastDrainTicks` 와 관련 상수를 추가한다.

- old_string (line 69-74):

```csharp
        private readonly Dictionary<string, Func<string[], string>> _handlers = new();
        private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();
        private readonly CliLogBuffer _logBuffer;

        /// <summary>CLI에서 요청한 스크린샷 경로. EngineCore.Update()에서 소비.</summary>
        internal static string? _pendingScreenshotPath;
```

- new_string:

```csharp
        private readonly Dictionary<string, Func<string[], string>> _handlers = new();
        private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();
        private readonly CliLogBuffer _logBuffer;

        /// <summary>CLI에서 요청한 스크린샷 경로. EngineCore.Update()에서 소비.</summary>
        internal static string? _pendingScreenshotPath;

        // (C3) ExecuteOnMainThread 설정
        //   TimeoutMs          : 큐에 넣은 task 가 완료되지 않을 때 포기하는 시간. 5초 유지.
        //   StallThresholdMs   : 메인이 ProcessMainThreadQueue 를 마지막으로 돈 뒤 이 시간을
        //                        넘었으면 메인이 stall 됐다고 판단, 큐에 넣지 않고 즉시 busy.
        //   _lastDrainTicks    : ProcessMainThreadQueue 가 마지막으로 "완주"한 시각(UtcNow.Ticks).
        //                        다중 스레드 읽기/쓰기이므로 Volatile 로 접근.
        private const int TimeoutMs = 5000;
        private const int StallThresholdMs = 2000;
        private long _lastDrainTicks;
```

**변경 10**: `ProcessMainThreadQueue` 를 업데이트. (a) 진입 시 stall 타이머를 갱신하고, (b) Cancelled task 는 skip 하고 Dispose 까지 수행.

- old_string (line 116-133):

```csharp
        public void ProcessMainThreadQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var task))
            {
                try
                {
                    task.Result = task.Execute();
                }
                catch (Exception ex)
                {
                    task.Result = JsonError(ex.Message);
                }
                finally
                {
                    task.Done.Set();
                }
            }
        }
```

- new_string:

```csharp
        public void ProcessMainThreadQueue()
        {
            // (C3) 루프 진입 시각을 기록 — ExecuteOnMainThread 가 "메인이 최근에 돌았는가"
            // 를 판정하는 기준. 이 메서드는 EngineCore.Update() 에서 매 프레임 호출되므로
            // 정상 동작 중에는 수 ms 간격으로 갱신된다.
            Volatile.Write(ref _lastDrainTicks, DateTime.UtcNow.Ticks);

            while (_mainThreadQueue.TryDequeue(out var task))
            {
                // (C3) 백그라운드가 이미 타임아웃하여 task 를 버린 경우 실행하지 않는다.
                // Dispose 만 수행하여 핸들 누수를 방지한다.
                if (task.IsCancelled)
                {
                    try { task.Done.Dispose(); } catch { }
                    continue;
                }

                try
                {
                    task.Result = task.Execute();
                }
                catch (Exception ex)
                {
                    task.Result = JsonError(ex.Message);
                }
                finally
                {
                    task.Done.Set();
                }
            }
        }
```

**변경 11**: `ExecuteOnMainThread` 전체 재작성.

- old_string (line 2730-2737):

```csharp
        private string ExecuteOnMainThread(Func<string> action)
        {
            var task = new MainThreadTask { Execute = action };
            _mainThreadQueue.Enqueue(task);
            if (!task.Done.Wait(TimeSpan.FromSeconds(5)))
                return JsonError("Main thread timeout (5s)");
            return task.Result!;
        }
```

- new_string:

```csharp
        private string ExecuteOnMainThread(Func<string> action)
        {
            // (C3) 재진입 방어:
            // ExecuteOnMainThread 는 본래 "백그라운드(CLI 파이프 스레드)에서 메인을 호출"
            // 하기 위한 브리지다. 호출자가 이미 메인 스레드라면 큐에 넣고 기다리는 건
            // 자기 자신을 기다리는 확정 데드락이 된다. 이 경로가 발생했다면 설계 오류이므로
            // LogError 로 surface 하되, 프로세스를 멈추지 않기 위해 action 을 직접 동기
            // 실행한다. ThreadGuard.CheckMainThread 는 "메인이어야 한다" 를 검증하는
            // 방향이므로 여기서는 사용하지 않고 IsMainThread 만 확인한다.
            if (ThreadGuard.IsMainThread)
            {
                EditorDebug.LogError(
                    "[CLI] ExecuteOnMainThread called from main thread — recursive main-thread execution. " +
                    "Running action inline to avoid self-deadlock; fix the calling code.");
                try
                {
                    return action();
                }
                catch (Exception ex)
                {
                    return JsonError(ex.Message);
                }
            }

            // (C3) 메인 stall 사전 감지:
            // ProcessMainThreadQueue 가 마지막으로 돈 시각이 StallThresholdMs 보다 오래됐으면
            // 큐에 넣어봤자 타임아웃까지 블로킹만 하므로 즉시 busy 를 반환한다.
            // _lastDrainTicks == 0 인 초기 상태는 "아직 한 번도 돌지 않음" 으로, stall
            // 판정에서 제외한다 (엔진 부팅 직후 CLI 요청 대응).
            long lastTicks = Volatile.Read(ref _lastDrainTicks);
            if (lastTicks != 0)
            {
                long elapsedMs = (DateTime.UtcNow.Ticks - lastTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs > StallThresholdMs)
                {
                    return JsonError(
                        $"Main thread busy (no drain for {elapsedMs}ms > {StallThresholdMs}ms)");
                }
            }

            var task = new MainThreadTask { Execute = action };
            _mainThreadQueue.Enqueue(task);

            if (!task.Done.Wait(TimeoutMs))
            {
                // (C3) 타임아웃 후 task 가 큐에 남아 있다가 나중에 엉뚱한 시점에 실행되는
                // 걸 막는다. MarkCancelled 는 thread-safe. 큐에서 제거는 불가능하므로
                // ProcessMainThreadQueue 가 dequeue 후 IsCancelled 를 보고 skip 한다.
                task.MarkCancelled();
                return JsonError($"Main thread timeout ({TimeoutMs}ms)");
            }

            return task.Result!;
        }
```

### 구현 힌트

- `ThreadGuard.IsMainThread` 는 Phase A 에서 도입되어 이미 사용 가능 (`src/IronRose.Contracts/ThreadGuard.cs`). `CliCommandDispatcher.cs` 는 이미 line 62 에 `using RoseEngine;` 이 있다.
- `DateTime.UtcNow.Ticks` 는 `long` 반환, `TimeSpan.TicksPerMillisecond` 는 10000 (상수). `elapsedMs` 계산에서 오버플로 걱정 없음.
- `_lastDrainTicks` 의 **초기값 0 은 "아직 드레인된 적 없음"** 을 의미한다. 엔진 부팅 직후엔 정상 CLI 처리가 가능해야 하므로 stall 판정을 건너뛴다 (위 코드의 `if (lastTicks != 0)` 가드).
- `Volatile.Read/Write` 는 `Interlocked` 와 달리 atomic 연산이 아니다. 하지만 `_lastDrainTicks` 는 단일 writer(메인) + 다중 reader(CLI 서버 스레드)이며 "대략적인 최신값"이면 충분하므로 OK.
- `ManualResetEventSlim.Dispose` 는 멱등이 아니므로 `task.Done.Dispose()` 를 한 번만 호출한다. Cancelled 경로에선 `Set()` 을 호출하지 않는다 (호출자는 이미 포기했음).
- `task.Done.Wait(int ms)` 의 int 오버로드는 `TimeoutMs = 5000` 과 바로 호환 (`TimeSpan` 래핑 불필요).
- 재진입 분기의 `EditorDebug.LogError` 호출은 매우 드물게(= 설계 버그일 때만) 발생하므로 로그 쿨다운은 불필요.

### 주의: stall 감지의 false positive 가능성

- 에디터가 **방금 막 시작**한 상황: `_lastDrainTicks == 0` 이므로 정상 처리됨 (가드됨).
- 에디터가 프리즈 상태 직후 CLI 호출: 정상. 즉시 busy 반환 → 클라이언트는 재시도 가능.
- `EngineCore.Update` 가 **일시적으로 느려진** 상황 (예: 프리임 한 번에 3초 소요): 첫 호출은 busy 로 반환될 수 있으나, 다음 드레인 완료 직후에는 정상화된다. **허용 가능한 false positive**.
- 플레이모드 진입/종료 시점의 일시 블로킹은 보통 1초 이내이므로 StallThresholdMs=2000 에 걸리지 않는다.

### 검증 (C-3 커밋 후)

- [ ] `dotnet build` 성공.
- [ ] `rose-cli ping` → `{"pong": true, ...}` 정상 응답 (메인 스레드 경유하지 않는 명령이므로 영향 없어야 함).
- [ ] `rose-cli scene.info` (메인 경유) → 정상 응답.
- [ ] 에디터에서 플레이모드 진입 중 `rose-cli scene.info` 호출 → `Main thread busy (...)` 또는 정상 응답 중 하나. 5초 이상 블로킹 없음.
- [ ] `ProcessMainThreadQueue` 에 로그를 임시 삽입해, 타임아웃된 task 가 `IsCancelled == true` 로 skip 되는 경로가 실제로 실행되는지 검증 (확인 후 로그 원복).

---

## 전체 검증 (세 서브 phase 커밋 후 통합)

### 빌드
- [ ] `dotnet build IronRose.sln` 성공, 새 warning 0 개.

### 스모크 테스트
- [ ] 에디터 시작 → 즉시 종료 반복 (10회). `ObjectDisposedException`/크래시 없음. Join warning 로그도 없음 (정상 케이스).
- [ ] CLI 클라이언트가 장기 요청 중 에디터 종료: 5초 이내 종료. Join warning 이 있거나 없거나 둘 다 허용.
- [ ] 플레이모드 진입 직전/직후의 일시 블로킹 중 `rose-cli scene.info` 호출 → `busy` 응답 또는 정상 응답. 하지만 **5초 내 응답 반환**은 반드시 보장.
- [ ] `CliCommandDispatcher.cs` 와 `CliPipeServer.cs` 의 외부 API 시그니처 변경 없음 (외부 호출자 검증 불필요).

### 머지 전 체크리스트
- [ ] `Stop()` 이 Join 성공 전에 `_cts.Dispose()` 를 호출하지 않는다 (코드 리뷰로 확인).
- [ ] `IsRunning` 의 백킹 필드가 `volatile bool` 이다.
- [ ] `ExecuteOnMainThread` 가 메인 stall 감지 시 즉시 busy 반환, 재진입 시 inline 실행 후 LogError, 타임아웃 시 task 를 MarkCancelled 한다.
- [ ] `ProcessMainThreadQueue` 가 Cancelled task 를 skip 하고 Done 을 Dispose 한다.
- [ ] `ServerLoop` 이 `ObjectDisposedException` 을 catch 하여 graceful break 한다.

---

## 참고 (미결 사항 및 후속 제안)

- **`CliExecuteOptions` 를 통한 timeout 설정**: 마스터 플랜은 per-call 설정 가능성을 언급했으나, 본 Phase 에서는 상수 `TimeoutMs = 5000` 유지. 추후 CLI 명령별 타임아웃이 필요해지면 옵션 구조체 도입을 별도 phase 로 분리.
- **stall 임계값 튜닝**: `StallThresholdMs = 2000` 은 경험값. 에디터 정상 동작 시 `ProcessMainThreadQueue` 간격은 수 ms 이내이므로 여유가 크다. 실사용 중 false positive 가 잦으면 3000~4000 으로 완화 검토.
- **`SetRunning` 대신 `Interlocked.Exchange` 검토**: `volatile bool` 로 충분하나, 만약 향후 "마지막 쓰기 순서"를 확정해야 하는 로직이 추가되면 `Interlocked.Exchange<int>` + 0/1 플래그로 전환 고려.
- **C-3 의 재진입 분기**: 현재 코드베이스에서 메인이 CLI 를 호출하는 경로는 없다 (grep 로 `CliCommandDispatcher.Dispatch` 의 호출자는 `CliPipeServer.HandleClient` 하나뿐). 본 방어 코드는 **향후 에디터 UI 에서 CLI 명령을 재사용**하는 경로가 추가될 때를 위한 예방책이다.
- **ServerJoinTimeoutMs 고정값 5초**: 마스터 플랜 본문은 10초까지 완화 가능성을 언급했으나, "에디터 종료가 너무 느려지지 않도록" 5초 + warning 로그로 확정 (본 Phase 지시사항 참조).

## 참고 파일

- 마스터 계획: [`plans/threading-safety-fix-master.md`](threading-safety-fix-master.md) §Phase C (line 265-306).
- 정적 분석: [`plans/static-analysis-threading-race-deadlock.md`](static-analysis-threading-race-deadlock.md) C3(line 125), C6(line 212), H1(line 251).
- Phase A 산출물: `src/IronRose.Contracts/ThreadGuard.cs` (머지 완료).
- 대상 파일: `src/IronRose.Engine/Cli/CliPipeServer.cs`, `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`.
- 호출자: `src/IronRose.Engine/EngineCore.cs` (`_cliPipeServer.Start/Stop`, `_cliDispatcher.ProcessMainThreadQueue`) — 수정 불필요.
