# Phase 46a: Named Pipe 서버 + 기본 명령 + EngineCore 통합

## 목표
- 엔진에 Named Pipe 서버(`CliPipeServer`)를 백그라운드 스레드로 추가한다.
- CLI 명령 디스패처(`CliCommandDispatcher`)와 로그 링 버퍼(`CliLogBuffer`)를 구현한다.
- `EngineCore.cs`를 수정하여 CLI 서버를 초기화/업데이트/종료한다.
- `ping`, `scene.info`, `scene.list` 세 가지 기본 명령이 동작한다.
- 이 phase 완료 시 Named Pipe에 수동 연결하여 기본 명령에 대한 JSON 응답을 받을 수 있다.

## 선행 조건
- 없음 (Phase 46의 첫 번째 sub-phase)

## 코딩 규칙
- C# 파일은 UTF-8 with BOM 인코딩
- 파일 경로는 항상 `Path.Combine()` 사용
- 네이밍: PascalCase(클래스/메서드), camelCase(필드/변수), UPPER_CASE(상수)
- 디버깅 로그는 `RoseEngine.EditorDebug.Log()` / `EditorDebug.LogWarning()` / `EditorDebug.LogError()` 사용
- `using RoseEngine;` 추가하여 `EditorDebug` 접근

## 생성할 파일

### `src/IronRose.Engine/Cli/CliLogBuffer.cs`

- **역할**: CLI에서 조회할 수 있도록 최근 로그 엔트리를 링 버퍼에 저장한다.
- **네임스페이스**: `IronRose.Engine.Cli`
- **클래스**: `CliLogBuffer`
- **주요 멤버**:
  - `private readonly LogEntry[] _buffer` -- 링 버퍼 배열
  - `private int _head` -- 다음 쓰기 위치
  - `private int _count` -- 현재 저장된 항목 수
  - `private readonly object _lock = new()` -- 스레드 안전용 락
  - `public const int MAX_SIZE = 1000` -- 링 버퍼 최대 크기
  - `public void Push(LogEntry entry)` -- 로그 추가 (lock 내부에서 `_buffer[_head] = entry; _head = (_head + 1) % MAX_SIZE; _count = Math.Min(_count + 1, MAX_SIZE);`)
  - `public List<LogEntry> GetRecent(int count)` -- 최근 N개 로그를 시간순(오래된 것 먼저)으로 반환 (lock 내부)
- **의존**: `RoseEngine` (LogEntry 타입: `record LogEntry(LogLevel Level, LogSource Source, string Message, DateTime Timestamp)`)
- **구현 힌트**:
  - `LogEntry`는 `RoseEngine` 네임스페이스의 record 타입이다. `using RoseEngine;` 필요.
  - `GetRecent(int count)` 구현: `count = Math.Min(count, _count);` 후, `_head`에서 `count`만큼 역산하여 오래된 것부터 순서대로 `List<LogEntry>`에 담아 반환. 인덱스 계산: `int start = (_head - _count + MAX_SIZE) % MAX_SIZE;` 기준으로 `start`부터 `_count`개 중 마지막 `count`개를 추출.

- **파일 헤더**:
```csharp
// ------------------------------------------------------------
// @file    CliLogBuffer.cs
// @brief   CLI에서 조회할 수 있도록 최근 로그 엔트리를 링 버퍼에 저장한다.
// @deps    RoseEngine/LogEntry
// @exports
//   class CliLogBuffer
//     Push(LogEntry): void           -- 로그 추가 (EditorDebug.LogSink에서 호출)
//     GetRecent(int count): List<LogEntry>  -- 최근 N개 로그 반환
//     MAX_SIZE: int                  -- 링 버퍼 최대 크기 (1000)
// @note    스레드 안전 (lock 기반). 여러 스레드에서 Push가 호출될 수 있다.
// ------------------------------------------------------------
```

---

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

- **역할**: CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다. 메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
- **네임스페이스**: `IronRose.Engine.Cli`
- **클래스**: `CliCommandDispatcher`

- **주요 멤버**:

  **필드**:
  - `private readonly Dictionary<string, Func<string[], string>> _handlers = new()` -- 명령 핸들러 맵
  - `private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new()` -- 메인 스레드 실행 큐
  - `private readonly CliLogBuffer _logBuffer` -- 로그 버퍼 참조
  - `private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }` -- JSON 직렬화 옵션

  **내부 클래스**:
  ```csharp
  private class MainThreadTask
  {
      public required Func<string> Execute { get; init; }
      public ManualResetEventSlim Done { get; } = new(false);
      public string? Result { get; set; }
  }
  ```

  **생성자**:
  - `public CliCommandDispatcher(CliLogBuffer logBuffer)` -- 로그 버퍼를 주입받고, `RegisterHandlers()` 호출

  **공개 메서드**:
  - `public string Dispatch(string requestLine)` -- 요청 평문을 파싱하여 핸들러 호출, JSON 응답 반환
  - `public void ProcessMainThreadQueue()` -- EngineCore.Update()에서 호출. 큐에 쌓인 MainThreadTask를 순차 실행하고 Done 시그널.

  **비공개 메서드**:
  - `private void RegisterHandlers()` -- `ping`, `scene.info`, `scene.list` 핸들러 등록
  - `private string ExecuteOnMainThread(Func<string> action)` -- MainThreadTask 생성, 큐에 넣고, `task.Done.Wait(TimeSpan.FromSeconds(5))` 대기. 타임아웃 시 `JsonError("Main thread timeout (5s)")` 반환.
  - `private static string[] ParseArgs(string requestLine)` -- 따옴표 인식 토크나이저. 쌍따옴표 내부 공백은 분리하지 않음.
  - `private static string JsonOk(object data)` -- `JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions)` 반환
  - `private static string JsonError(string message)` -- `JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions)` 반환

- **의존**:
  - `System.Text.Json` (JsonSerializer, JsonNamingPolicy) -- .NET 10 기본 포함, NuGet 불필요
  - `System.Collections.Concurrent` (ConcurrentQueue)
  - `System.Threading` (ManualResetEventSlim)
  - `RoseEngine` (SceneManager, EditorDebug, LogEntry)
  - `IronRose.Engine` (ProjectContext)

- **구현 힌트**:

  **ParseArgs 구현** (따옴표 인식 토크나이저):
  ```csharp
  private static string[] ParseArgs(string requestLine)
  {
      var args = new List<string>();
      var current = new System.Text.StringBuilder();
      bool inQuotes = false;

      for (int i = 0; i < requestLine.Length; i++)
      {
          char c = requestLine[i];
          if (c == '"')
          {
              inQuotes = !inQuotes;
              continue;
          }
          if (c == ' ' && !inQuotes)
          {
              if (current.Length > 0)
              {
                  args.Add(current.ToString());
                  current.Clear();
              }
              continue;
          }
          current.Append(c);
      }
      if (current.Length > 0)
          args.Add(current.ToString());

      return args.ToArray();
  }
  ```

  **Dispatch 구현**:
  ```csharp
  public string Dispatch(string requestLine)
  {
      try
      {
          var tokens = ParseArgs(requestLine.Trim());
          if (tokens.Length == 0)
              return JsonError("Empty command");

          var command = tokens[0].ToLowerInvariant();
          var args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

          if (_handlers.TryGetValue(command, out var handler))
              return handler(args);

          return JsonError($"Unknown command: {command}");
      }
      catch (Exception ex)
      {
          return JsonError($"Dispatch error: {ex.Message}");
      }
  }
  ```

  **핸들러 등록** (이 phase에서는 `ping`, `scene.info`, `scene.list` 3개만):

  1. **`ping`** -- 백그라운드 스레드에서 직접 실행 (메인 스레드 불필요):
  ```csharp
  _handlers["ping"] = args => JsonOk(new { pong = true, project = ProjectContext.ProjectName });
  ```

  2. **`scene.info`** -- 메인 스레드 필요 (`SceneManager` 접근):
  ```csharp
  _handlers["scene.info"] = args => ExecuteOnMainThread(() =>
  {
      var scene = SceneManager.GetActiveScene();
      return JsonOk(new
      {
          name = scene.name,
          path = scene.path ?? "",
          isDirty = scene.isDirty,
          gameObjectCount = SceneManager.AllGameObjects.Count
      });
  });
  ```
  - `SceneManager.GetActiveScene()` 반환 타입: `Scene` (필드: `name`, `path`, `isDirty`)
  - `SceneManager.AllGameObjects` 반환 타입: `IReadOnlyList<GameObject>`

  3. **`scene.list`** -- 메인 스레드 필요:
  ```csharp
  _handlers["scene.list"] = args => ExecuteOnMainThread(() =>
  {
      var gos = SceneManager.AllGameObjects;
      var list = new List<object>();
      foreach (var go in gos)
      {
          if (go._isDestroyed) continue;
          list.Add(new
          {
              id = go.GetInstanceID(),
              name = go.name,
              active = go.activeSelf,
              parentId = go.transform.parent?.gameObject.GetInstanceID()
          });
      }
      return JsonOk(new { gameObjects = list });
  });
  ```
  - `go._isDestroyed`는 `public` 필드이다 (같은 프로젝트 내 접근 가능).
  - `go.GetInstanceID()` 반환 타입: `int`
  - `go.activeSelf` 반환 타입: `bool`
  - `go.transform.parent` 반환 타입: `Transform?`

  **ProcessMainThreadQueue 구현**:
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

- **파일 헤더**:
```csharp
// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, IronRose.Engine/ProjectContext
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string  -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void        -- 메인 스레드에서 호출하여 대기 중 명령 실행
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다 (타임아웃 5초).
// ------------------------------------------------------------
```

---

### `src/IronRose.Engine/Cli/CliPipeServer.cs`

- **역할**: Named Pipe 서버를 백그라운드 스레드에서 운영한다. 클라이언트 연결을 수신하고, 요청을 `CliCommandDispatcher`에 위임하여 응답을 반환한다.
- **네임스페이스**: `IronRose.Engine.Cli`
- **클래스**: `CliPipeServer`

- **주요 멤버**:

  **필드**:
  - `private CliCommandDispatcher? _dispatcher`
  - `private Thread? _serverThread`
  - `private CancellationTokenSource? _cts`
  - `private readonly string _pipeName`
  - `public bool IsRunning { get; private set; }`
  - `private const int MAX_MESSAGE_SIZE = 16 * 1024 * 1024` -- 16MB

  **생성자**:
  - `public CliPipeServer()` -- `_pipeName = GetPipeName();` 호출

  **공개 메서드**:
  - `public void Start(CliCommandDispatcher dispatcher)` -- 디스패처 저장, 백그라운드 스레드 시작
  - `public void Stop()` -- CancellationToken 취소, 파이프 닫기, 스레드 Join(3초)

  **비공개 메서드**:
  - `private void ServerLoop()` -- 메인 서버 루프. `while (!_cts.Token.IsCancellationRequested)` 내에서 파이프 생성 -> 연결 대기 -> 요청 처리 -> 연결 종료 -> 반복.
  - `private void HandleClient(NamedPipeServerStream pipe)` -- 연결된 클라이언트와 메시지 교환 루프. 연결이 끊어질 때까지 반복.
  - `private static string? ReadMessage(Stream stream)` -- 4바이트 little-endian 길이 + N바이트 UTF-8 문자열 읽기. 스트림 끝이면 null 반환.
  - `private static void WriteMessage(Stream stream, string message)` -- 4바이트 little-endian 길이 + N바이트 UTF-8 문자열 쓰기.
  - `private static string GetPipeName()` -- 파이프 이름 결정
  - `private static string SanitizeForPipeName(string name)` -- 파일명에 안전하지 않은 문자 제거

- **의존**:
  - `System.IO.Pipes` (NamedPipeServerStream) -- .NET 표준 라이브러리, NuGet 불필요
  - `System.Threading` (Thread, CancellationTokenSource)
  - `RoseEngine` (EditorDebug)
  - `IronRose.Engine` (ProjectContext)

- **구현 힌트**:

  **GetPipeName 구현**:
  ```csharp
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
      var sb = new System.Text.StringBuilder();
      foreach (char c in name)
      {
          if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
              sb.Append(c);
      }
      return sb.Length > 0 ? sb.ToString() : "default";
  }
  ```
  - 주의: Linux에서 .NET의 `NamedPipeServerStream`은 실제로 `/tmp/CoreFxPipe_{pipeName}` 경로의 Unix Domain Socket을 사용한다. Python 래퍼(Phase 46b)가 이 규칙을 따라야 하므로, `Start()` 시 로그에 전체 파이프 경로를 출력한다.

  **Start 구현**:
  ```csharp
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
  ```

  **ServerLoop 구현**:
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
  - `WaitForConnectionAsync`를 사용하는 이유: `WaitForConnection()`은 `CancellationToken`을 받지 않아 `Stop()` 시 블로킹됨. Async 버전을 `GetAwaiter().GetResult()`로 동기 호출하되 CancellationToken으로 취소 가능하게 한다.
  - `maxNumberOfServerInstances = 1`: 동시 연결을 지원하지 않는다.

  **HandleClient 구현**:
  ```csharp
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
          // 클라이언트가 연결을 끊음 — 정상 동작
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
  ```

  **ReadMessage / WriteMessage 구현** (길이 접두사 프레임):
  ```csharp
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

      return System.Text.Encoding.UTF8.GetString(messageBuf);
  }

  private static void WriteMessage(Stream stream, string message)
  {
      var bytes = System.Text.Encoding.UTF8.GetBytes(message);
      var lengthBuf = BitConverter.GetBytes(bytes.Length); // little-endian (기본)
      stream.Write(lengthBuf, 0, 4);
      stream.Write(bytes, 0, bytes.Length);
      stream.Flush();
  }
  ```

  **Stop 구현**:
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

- **파일 헤더**:
```csharp
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
```

---

## 수정할 파일

### `src/IronRose.Engine/EngineCore.cs`

**변경 내용 1: using 추가**

파일 상단의 using 블록에 다음을 추가:
```csharp
using IronRose.Engine.Cli;
```
- 기존 using 목록 (`using IronRose.Engine.Automation;` 부근)에 추가한다.

**변경 내용 2: 필드 추가**

기존 필드 영역 (`private TestCommandRunner? _testCommandRunner;` 부근, 84번 줄 근처)에 추가:
```csharp
// CLI 브릿지 (Phase 46)
private CliPipeServer? _cliPipeServer;
private CliCommandDispatcher? _cliDispatcher;
private CliLogBuffer? _cliLogBuffer;
```

**변경 내용 3: Initialize() 수정 -- LogSink 연결 변경**

`Initialize()` 메서드 시작 부분의 기존 코드 (128~130번 줄):
```csharp
// EditorDebug + Debug 양쪽의 LogSink를 EditorBridge에 연결
RoseEngine.EditorDebug.LogSink = entry => EditorBridge.PushLog(entry);
RoseEngine.Debug.LogSink = entry => EditorBridge.PushLog(entry);
```

이것을 다음으로 변경:
```csharp
// CLI 로그 버퍼 생성 (LogSink 연결 전에 생성)
_cliLogBuffer = new CliLogBuffer();

// EditorDebug + Debug 양쪽의 LogSink를 EditorBridge + CLI 로그 버퍼에 연결
RoseEngine.EditorDebug.LogSink = entry => { EditorBridge.PushLog(entry); _cliLogBuffer.Push(entry); };
RoseEngine.Debug.LogSink = entry => { EditorBridge.PushLog(entry); _cliLogBuffer.Push(entry); };
```

- **이유**: CLI 로그 버퍼가 기존 EditorBridge 로그 경로와 병행하여 로그를 수집해야 한다.

**변경 내용 4: Initialize() 끝부분 -- CLI 서버 시작**

`Initialize()` 메서드 끝부분, 기존 코드 (189~194번 줄):
```csharp
// 자동화 테스트 명령 파일 로드
_testCommandRunner = TestCommandRunner.TryLoad();

// 에셋 캐시 워밍업 시작
if (ProjectContext.IsProjectLoaded)
    _warmupManager!.Start();
```

이것의 **앞에** 다음을 삽입:
```csharp
// CLI 브릿지 시작 (프로젝트 로드 상태와 무관하게 항상 시작)
_cliDispatcher = new CliCommandDispatcher(_cliLogBuffer!);
_cliPipeServer = new CliPipeServer();
_cliPipeServer.Start(_cliDispatcher);
```

- **삽입 위치**: `_testCommandRunner = TestCommandRunner.TryLoad();` 바로 **위**.
- **이유**: CLI 서버는 프로젝트 미로드 상태(Startup Panel)에서도 동작해야 한다. InitEditor() 이후에 시작하여 에디터 초기화 완료를 보장한다.

**변경 내용 5: Update() -- 메인 스레드 큐 처리**

`Update()` 메서드의 기존 코드 (197~199번 줄):
```csharp
public void Update(double deltaTime)
{
    EditorBridge.ProcessCommands();
```

`EditorBridge.ProcessCommands();` 바로 **뒤에** 다음을 추가:
```csharp
    // CLI 명령 큐 처리 (메인 스레드)
    _cliDispatcher?.ProcessMainThreadQueue();
```

- **이유**: `CliCommandDispatcher`의 `ExecuteOnMainThread()`가 큐에 넣은 작업을 매 프레임 메인 스레드에서 실행한다.

**변경 내용 6: Shutdown() -- CLI 서버 정지**

`Shutdown()` 메서드의 기존 코드 (478~480번 줄):
```csharp
public void Shutdown()
{
    RoseEngine.EditorDebug.Log("[Engine] EngineCore shutting down...");
```

`EditorDebug.Log(...)` 바로 **뒤, `PlayerPrefs.Shutdown()` 앞에** 다음을 추가:
```csharp
    _cliPipeServer?.Stop();
```

- **이유**: 파이프 서버를 다른 서브시스템보다 먼저 종료하여 클라이언트에게 정상 종료 신호를 준다.

---

## NuGet 패키지
- 없음. `System.IO.Pipes`와 `System.Text.Json`은 .NET 10 기본 포함.

## 검증 기준
- [ ] `dotnet build` 성공 (IronRose.Engine 프로젝트)
- [ ] 엔진 시작 시 콘솔/로그에 `[CLI] Pipe server started: /tmp/CoreFxPipe_ironrose-cli-{ProjectName}` 메시지가 출력된다.
- [ ] 엔진 종료 시 `[CLI] Pipe server stopped` 메시지가 출력된다.
- [ ] 프로젝트 미로드 상태(Startup Panel)에서도 CLI 서버가 시작된다.
- [ ] Named Pipe에 수동 연결하여 `ping` 명령에 JSON 응답을 받을 수 있다.

## 참고
- `NamedPipeServerStream`의 Linux 동작: .NET 런타임이 `/tmp/CoreFxPipe_{pipeName}` 경로에 Unix Domain Socket을 생성한다. 이 경로는 .NET 런타임 내부 규칙이며, Phase 46b의 Python 래퍼가 이 규칙에 맞춰 연결해야 한다.
- `_cliLogBuffer`는 `Initialize()` 최상단에서 생성되어야 한다 (LogSink 람다에서 참조하므로).
- `MainThreadTask`의 `Done.Wait(5초)` 타임아웃: 메인 스레드가 블로킹된 상황(모달 대화상자 등)에서 CLI 요청이 무한 대기하는 것을 방지한다.
- `go._isDestroyed` 필드가 `internal`이면 같은 프로젝트 내에서 접근 가능하다. 만약 접근 불가하면 `go.GetInstanceID() == 0` 또는 null 체크로 대체한다.
