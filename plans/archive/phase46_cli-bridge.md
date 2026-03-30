# Phase 46: IronRose Editor CLI 브릿지

## 배경
- Claude Code에서 IronRose Editor(GUI)를 직접 조작/조회할 수 있는 경로가 없다. 현재 자동화 수단은 `TestCommandRunner`(JSON 파일 기반)뿐이며, 이는 실시간 양방향 통신이 불가능하다.
- Claude Code가 Bash 명령으로 에디터 상태를 조회하거나 오브젝트를 수정할 수 있으면, AI 어시스턴트 기반 게임 개발 워크플로우가 크게 개선된다.
- Named Pipe(로컬 IPC)를 사용하여 네트워크 없이 안전하고 빠른 양방향 통신을 구현한다.

## 목표
1. IronRose Editor 내부에 Named Pipe 서버를 백그라운드 스레드로 운영한다.
2. Python CLI 래퍼(`ironrose-cli`)를 통해 Claude Code가 Bash로 에디터와 통신한다.
3. 래퍼는 명령을 해석하지 않고 문자열만 중계하여, 엔진에 새 명령을 추가해도 래퍼 수정이 불필요하도록 한다.
4. 초기 명령 세트로 씬 조회, 오브젝트 조회/수정, 로그 조회, Play 모드 제어 등을 지원한다.

## 현재 상태

### 기존 자동화 수단
- `src/IronRose.Engine/Automation/TestCommandRunner.cs` : JSON 파일(`.claude/test_commands.json`)을 로드하여 순차 실행. 단방향(파일 -> 엔진)이며 실시간 응답 불가.

### EditorBridge 패턴
- `src/IronRose.Engine/Editor/EditorBridge.cs` : `ConcurrentQueue` 기반으로 Engine <-> Editor 간 스냅샷/커맨드/로그를 전달. 이 패턴을 Named Pipe 서버에서도 활용한다.
- `EditorCommand` 추상 클래스(`EditorCommand.cs`) : `SetFieldCommand`, `SetActiveCommand`, `SetRenderSettingsCommand`, `PauseCommand` 등 이미 다양한 커맨드가 정의되어 있다.

### SceneSnapshot
- `SceneSnapshot.Capture()` : 현재 씬의 모든 GameObject/Component/Field를 스냅샷으로 캡처. CLI에서 씬 상태 조회에 직접 활용 가능.

### EditorSelection
- `EditorSelection.Select(int?)`, `SelectedGameObject` 등 : 에디터 선택 상태 관리.

### EditorPlayMode
- `EnterPlayMode()`, `StopPlayMode()`, `PausePlayMode()`, `ResumePlayMode()` : Play 모드 제어 API.

### SceneSerializer
- `SceneSerializer.Save(path)`, `SceneSerializer.Load(path)` : 씬 파일 저장/로드.

### EngineCore 초기화/종료 흐름
```
Initialize(window) ->
  ProjectContext.Initialize()
  EditorState.Load()
  InitApplication()       // Application 경로, PlayerPrefs 초기화
  InitInput()
  InitGraphics()
  ...
  InitEditor()            // ImGui 오버레이 초기화
  ...

Update(deltaTime) ->
  EditorBridge.ProcessCommands()  // 커맨드 큐 소비
  ...

Shutdown() ->
  PlayerPrefs.Shutdown()
  ...
```

### Named Pipe 관련
- 현재 코드베이스에 Named Pipe 사용 코드 없음. `System.IO.Pipes`는 .NET 표준 라이브러리로 추가 NuGet 패키지 불필요.

## 설계

### 개요

```
Claude Code ──Bash──> ironrose-cli (Python) ──Named Pipe──> CliPipeServer (C# 백그라운드 스레드)
                                                                    |
                                                            CliCommandDispatcher
                                                                    |
                                                  EngineCore 메인 스레드에서 실행
                                                                    |
                                                            JSON 응답 반환
                                                                    |
Claude Code <──stdout── ironrose-cli <──Named Pipe── CliPipeServer
```

1. **CliPipeServer** (C#): 백그라운드 스레드에서 Named Pipe 서버 운영. 클라이언트 연결 수신 -> 요청 읽기 -> `CliCommandDispatcher`에 위임 -> 응답 반환.
2. **CliCommandDispatcher** (C#): 문자열 명령을 파싱하여 메인 스레드에서 실행하고 결과를 JSON으로 반환.
3. **ironrose-cli** (Python): Named Pipe에 연결, CLI 인자를 그대로 전송, 응답을 stdout으로 출력.

### 상세 설계

#### 1. Named Pipe 프로토콜

**파이프 이름 규칙**:
- Linux: `/tmp/ironrose-cli-{ProjectName}.pipe`
  - `ProjectName`은 `ProjectContext.ProjectName`에서 가져온다. 빈 문자열이면 `"default"`.
  - 파일명에 안전하지 않은 문자는 제거한다.
- Windows: `\\.\pipe\ironrose-cli-{ProjectName}`

**메시지 프레임 포맷** (길이 접두사 방식):
```
[4 bytes: little-endian uint32 메시지 길이(N)] [N bytes: UTF-8 문자열]
```

- 요청/응답 모두 동일한 프레임 포맷을 사용한다.
- 길이 접두사를 사용하여 메시지 경계를 명확히 한다 (줄바꿈 구분자의 한계 회피).
- 최대 메시지 크기: 16MB (16 * 1024 * 1024 bytes). 초과 시 에러 응답.

**요청 포맷** (평문):
```
scene.list
go.get 42
go.set_field 42 Transform position 1,2,3
go.find "Main Camera"
```
- 첫 토큰이 명령, 나머지가 인자.
- 공백이 포함된 인자는 쌍따옴표로 감싼다: `go.find "Main Camera"`
- **래퍼**: 셸이 따옴표를 벗기므로, 래퍼가 각 인자에 공백이 포함되어 있으면 따옴표를 다시 씌워서 전송한다.
- **C# 서버**: 따옴표 인식 파싱 (쌍따옴표 내부의 공백은 분리하지 않음).

**응답 포맷** (JSON):
```json
{
  "ok": true,
  "data": { ... }
}
```

에러 응답:
```json
{
  "ok": false,
  "error": "Unknown command: foo.bar"
}
```

**인코딩**: UTF-8 (BOM 없음, 와이어 프로토콜용).

#### 2. 초기 명령 세트

| 명령 | 설명 | 인자 | 응답 data |
|------|------|------|-----------|
| `ping` | 연결 테스트 | 없음 | `{ "pong": true, "project": "MyGame" }` |
| `scene.info` | 현재 씬 정보 | 없음 | `{ "name": "...", "path": "...", "isDirty": bool, "gameObjectCount": int }` |
| `scene.list` | 전체 GameObject 목록 | 없음 | `{ "gameObjects": [{ "id": int, "name": "...", "active": bool, "parentId": int? }] }` |
| `scene.save` | 현재 씬 저장 | `[path]` (선택) | `{ "saved": true, "path": "..." }` |
| `scene.load` | 씬 파일 로드 | `<path>` | `{ "loaded": true }` |
| `go.get` | 특정 GO 상세 정보 | `<id\|name>` | `{ "id": int, "name": "...", "active": bool, "components": [...] }` |
| `go.set_active` | GO 활성/비활성 | `<id> <true\|false>` | `{ "ok": true }` |
| `go.set_field` | 컴포넌트 필드 수정 | `<id> <component> <field> <value>` | `{ "ok": true }` |
| `go.find` | 이름으로 GO 검색 | `<name>` | `{ "gameObjects": [{ "id": int, "name": "..." }] }` |
| `select` | 에디터 선택 변경 | `<id>` | `{ "ok": true }` |
| `play.enter` | Play 모드 진입 | 없음 | `{ "state": "Playing" }` |
| `play.stop` | Play 모드 종료 | 없음 | `{ "state": "Edit" }` |
| `play.pause` | 일시정지 | 없음 | `{ "state": "Paused" }` |
| `play.resume` | 재개 | 없음 | `{ "state": "Playing" }` |
| `play.state` | 현재 Play 상태 | 없음 | `{ "state": "Edit" \| "Playing" \| "Paused" }` |
| `log.recent` | 최근 로그 조회 | `[count]` (기본 50) | `{ "logs": [{ "level": "...", "message": "...", "timestamp": "..." }] }` |

#### 3. C# 서버 측 아키텍처

##### 3-1. CliPipeServer 클래스

**파일**: `src/IronRose.Engine/Cli/CliPipeServer.cs`
**네임스페이스**: `IronRose.Engine.Cli`

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
//          메시지 프레임: [4 bytes length][N bytes UTF-8 string].
//          최대 메시지 크기: 16MB.
// ------------------------------------------------------------
```

핵심 동작:
- `Start()` 시 백그라운드 스레드를 시작. `NamedPipeServerStream`을 생성하고 `WaitForConnection()` 루프 진행.
- 클라이언트가 연결되면 요청 메시지(평문)를 읽고, `CliCommandDispatcher.Dispatch(requestLine)` 호출.
- 응답 메시지를 같은 프레임 포맷으로 전송.
- 클라이언트 연결 종료 후 다시 `WaitForConnection()` 대기.
- `Stop()` 호출 시 `CancellationToken`으로 루프를 종료하고 파이프를 닫는다.
- 예외 발생 시 로그 출력 후 재시작 (서버 죽지 않음).

파이프 이름 결정:
```csharp
private static string GetPipeName()
{
    var projectName = ProjectContext.ProjectName;
    if (string.IsNullOrEmpty(projectName))
        projectName = "default";
    var safeName = SanitizeForPipeName(projectName);
    return $"ironrose-cli-{safeName}";
}
```

Linux에서는 `NamedPipeServerStream`이 `/tmp/CoreFxPipe_{pipeName}` 경로를 사용한다 (.NET 런타임 규칙). Python 래퍼도 이 규칙을 따라야 한다.

##### 3-2. CliCommandDispatcher 클래스

**파일**: `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`
**네임스페이스**: `IronRose.Engine.Cli`

```csharp
// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, IronRose.Engine.Editor/*
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string  -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void        -- 메인 스레드에서 호출하여 대기 중 명령 실행
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다.
// ------------------------------------------------------------
```

핵심 동작:
- 명령 핸들러를 `Dictionary<string, Func<string[], string>>` 형태로 등록. `string[]`은 명령 뒤의 인자 배열.
- 요청 평문을 따옴표 인식 파싱으로 분리: 첫 토큰 = 명령, 나머지 = 인자. 쌍따옴표 내부의 공백은 분리하지 않는다.
- 백그라운드 스레드에서 직접 처리 가능한 명령 (예: `ping`)은 즉시 실행.
- SceneManager, EditorPlayMode 등 메인 스레드 접근이 필요한 명령은 `MainThreadTask`로 감싸서 `ConcurrentQueue`에 넣고, `ManualResetEventSlim`으로 완료를 대기.
- 타임아웃: 5초 내에 메인 스레드 응답이 없으면 타임아웃 에러 반환.

메인 스레드 동기화 패턴:
```csharp
private class MainThreadTask
{
    public Func<string> Execute { get; init; }
    public ManualResetEventSlim Done { get; } = new(false);
    public string? Result { get; set; }
}

private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();

// 백그라운드 스레드에서 호출
private string ExecuteOnMainThread(Func<string> action)
{
    var task = new MainThreadTask { Execute = action };
    _mainThreadQueue.Enqueue(task);
    if (!task.Done.Wait(TimeSpan.FromSeconds(5)))
        return JsonError("Main thread timeout");
    return task.Result!;
}

// EngineCore.Update()에서 호출
public void ProcessMainThreadQueue()
{
    while (_mainThreadQueue.TryDequeue(out var task))
    {
        try { task.Result = task.Execute(); }
        catch (Exception ex) { task.Result = JsonError(ex.Message); }
        finally { task.Done.Set(); }
    }
}
```

##### 3-3. CliLogBuffer 클래스

**파일**: `src/IronRose.Engine/Cli/CliLogBuffer.cs`
**네임스페이스**: `IronRose.Engine.Cli`

```csharp
// ------------------------------------------------------------
// @file    CliLogBuffer.cs
// @brief   CLI에서 조회할 수 있도록 최근 로그 엔트리를 링 버퍼에 저장한다.
// @deps    RoseEngine/LogEntry
// @exports
//   class CliLogBuffer
//     Push(LogEntry): void           -- 로그 추가 (EditorDebug.LogSink에서 호출)
//     GetRecent(int count): List<LogEntry>  -- 최근 N개 로그 반환
//     MaxSize: int                   -- 링 버퍼 최대 크기 (기본 1000)
// @note    스레드 안전 (lock 기반).
// ------------------------------------------------------------
```

- 기존 `EditorBridge.PushLog()` 경로에 추가로 CLI 로그 버퍼에도 기록한다.
- `EngineCore.Initialize()`에서 `Debug.LogSink`에 `CliLogBuffer.Push`를 추가 연결.

##### 3-4. 디렉토리 구조

```
src/IronRose.Engine/
  Cli/
    CliPipeServer.cs
    CliCommandDispatcher.cs
    CliLogBuffer.cs
```

#### 4. EngineCore 통합

**`EngineCore.cs` 변경**:

필드 추가:
```csharp
private CliPipeServer? _cliPipeServer;
private CliCommandDispatcher? _cliDispatcher;
```

`Initialize()` 끝 부분 (InitEditor 이후):
```csharp
// CLI 브릿지 시작 (프로젝트 로드 상태와 무관하게 항상 시작)
_cliDispatcher = new CliCommandDispatcher();
_cliPipeServer = new CliPipeServer();
_cliPipeServer.Start(_cliDispatcher);
```

`Update()` 시작 부분 (`EditorBridge.ProcessCommands()` 직후):
```csharp
// CLI 명령 큐 처리 (메인 스레드)
_cliDispatcher?.ProcessMainThreadQueue();
```

`Shutdown()` 시작 부분 (PlayerPrefs.Shutdown 전):
```csharp
_cliPipeServer?.Stop();
```

LogSink 연결 (`Initialize()` 초반, 기존 LogSink 설정 부근):
```csharp
// 기존 로그 싱크 유지 + CLI 로그 버퍼 추가
var cliLogBuffer = new CliLogBuffer();
_cliDispatcher = new CliCommandDispatcher(cliLogBuffer);

RoseEngine.EditorDebug.LogSink = entry => { EditorBridge.PushLog(entry); cliLogBuffer.Push(entry); };
RoseEngine.Debug.LogSink = entry => { EditorBridge.PushLog(entry); cliLogBuffer.Push(entry); };
```

참고: LogSink 연결 위치가 `_cliDispatcher` 생성보다 앞서므로, 실제 구현 시 CliLogBuffer를 먼저 생성하고 CliCommandDispatcher에 주입하는 순서를 조정해야 한다.

#### 5. Python CLI 래퍼

**파일**: `tools/ironrose-cli/ironrose_cli.py`
**의존성**: Python 3.8+ 표준 라이브러리만 사용 (외부 패키지 없음)

```
tools/
  ironrose-cli/
    ironrose_cli.py      # 메인 스크립트
    README.md            # 사용법 (선택)
```

**사용법**:
```bash
# 단일 명령 실행
python tools/ironrose-cli/ironrose_cli.py ping
python tools/ironrose-cli/ironrose_cli.py scene.list
python tools/ironrose-cli/ironrose_cli.py go.get 42
python tools/ironrose-cli/ironrose_cli.py go.set_field 42 Transform position 1,2,3
python tools/ironrose-cli/ironrose_cli.py play.enter

# 프로젝트 지정 (다른 프로젝트의 에디터에 연결)
python tools/ironrose-cli/ironrose_cli.py --project MyGame ping
```

**내부 동작**:
1. CLI 인자 파싱: `--project` 옵션만 래퍼가 소비. 나머지 인자 중 공백이 포함된 것은 쌍따옴표로 감싸고, 공백 join하여 평문 요청 문자열 생성.
2. Named Pipe 연결:
   - Linux: `/tmp/CoreFxPipe_ironrose-cli-{project}` 파일을 `socket.AF_UNIX` (Unix Domain Socket)로 열기.
     - 주의: .NET의 `NamedPipeServerStream`은 Linux에서 Unix Domain Socket으로 구현된다. Python에서는 `socket.AF_UNIX`로 연결한다.
   - Windows: `open(r'\\.\pipe\ironrose-cli-{project}', 'r+b')` 로 열기.
3. 길이 접두사 프레임으로 평문 요청 전송.
4. 응답 프레임 수신 (JSON).
5. JSON 파싱 후 `ok` 필드 확인:
   - `ok=true`: `data`를 JSON pretty-print로 stdout에 출력. exit code 0.
   - `ok=false`: `error`를 stderr에 출력. exit code 1.
6. 연결 타임아웃: 3초.

#### 6. 초기 명령 핸들러 상세

**`ping`**:
```csharp
handlers["ping"] = args => JsonOk(new { pong = true, project = ProjectContext.ProjectName });
```

**`scene.info`** (메인 스레드):
```csharp
handlers["scene.info"] = args => ExecuteOnMainThread(() =>
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

**`scene.list`** (메인 스레드):
```csharp
handlers["scene.list"] = args => ExecuteOnMainThread(() =>
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

**`go.get`** (메인 스레드):
- `SceneSnapshot`의 `GameObjectSnapshot.From(go)` 로직을 재활용하여 상세 정보 반환.
- `args[0]`이 숫자면 ID로 검색, 아니면 이름으로 첫 매칭 GO를 찾는다.

**`go.set_field`** (메인 스레드):
- 기존 `SetFieldCommand`와 동일한 로직. `EditorBridge.EnqueueCommand()`는 사용하지 않고 직접 실행 (이미 메인 스레드이므로).

**`go.set_active`** (메인 스레드):
- 기존 `SetActiveCommand`와 동일한 로직.

**`play.*`** (메인 스레드):
- `EditorPlayMode.EnterPlayMode()` / `StopPlayMode()` / `PausePlayMode()` / `ResumePlayMode()` 호출.

**`log.recent`** (스레드 안전, 직접 실행):
- `CliLogBuffer.GetRecent(count)` 호출.

#### 7. JSON 직렬화

- `System.Text.Json`을 사용한다 (이미 `TestCommandRunner.cs`에서 사용 중).
- 응답 생성 시 `JsonSerializer.Serialize()` 사용.
- 요청은 평문이므로 따옴표 인식 토크나이저로 파싱 (쌍따옴표 내부 공백 보존).
- `JsonSerializerOptions`에 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` 설정.

### 영향 범위

| 파일 | 변경 유형 | Phase |
|------|-----------|-------|
| `src/IronRose.Engine/Cli/CliPipeServer.cs` | **신규** | 46a |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | **신규** | 46a |
| `src/IronRose.Engine/Cli/CliLogBuffer.cs` | **신규** | 46a |
| `src/IronRose.Engine/EngineCore.cs` | **수정** - CLI 서버 초기화/업데이트/종료, LogSink 연결 | 46a |
| `tools/ironrose-cli/ironrose_cli.py` | **신규** | 46b |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | **수정** - 추가 명령 핸들러 | 46c |

### 기존 기능에 미치는 영향
- `EngineCore.Update()`에 `_cliDispatcher.ProcessMainThreadQueue()` 호출이 추가되지만, 큐가 비어 있으면 즉시 반환하므로 성능 영향 무시 가능.
- `LogSink` 연결이 변경되지만, 기존 `EditorBridge.PushLog()` 호출은 유지된다 (추가 호출만 발생).
- Named Pipe 서버는 별도 스레드에서 동작하므로 메인 루프에 영향 없음.
- 프로젝트 미로드 상태(Startup Panel)에서도 CLI 서버는 동작한다 (ping, log 등 기본 명령 사용 가능).

## 구현 단계 (Phase 분할)

### Phase 46a: Named Pipe 서버 + 기본 명령 + EngineCore 통합

**목표**: 엔진에 Named Pipe 서버를 추가하고, `ping`, `scene.info`, `scene.list` 명령을 구현한다.

**작업**:
- [ ] `Cli/` 디렉토리 생성
- [ ] `CliPipeServer.cs` 구현
  - Named Pipe 서버 생성/시작/종료
  - 메시지 프레임 읽기/쓰기 (길이 접두사)
  - 백그라운드 스레드 루프
  - CancellationToken 기반 종료
- [ ] `CliLogBuffer.cs` 구현
  - 링 버퍼 (최대 1000개)
  - 스레드 안전 (lock)
- [ ] `CliCommandDispatcher.cs` 구현
  - JSON 요청 파싱
  - 명령 핸들러 등록 구조
  - 메인 스레드 큐 + ManualResetEventSlim 동기화
  - `ping`, `scene.info`, `scene.list` 핸들러
- [ ] `EngineCore.cs` 수정
  - 필드 추가 (`_cliPipeServer`, `_cliDispatcher`, CLI LogBuffer)
  - `Initialize()`에서 CLI 서버 시작 + LogSink 연결
  - `Update()`에서 메인 스레드 큐 처리
  - `Shutdown()`에서 CLI 서버 정지
- [ ] `dotnet build` 성공 확인

**검증 기준**:
- 엔진 시작 시 Named Pipe 서버가 자동으로 시작된다.
- 엔진 종료 시 Named Pipe 서버가 정상 종료된다.
- Named Pipe에 수동 연결하여 `ping` 명령에 응답을 받을 수 있다.

### Phase 46b: Python CLI 래퍼

**목표**: Python CLI 래퍼를 구현하여 Claude Code에서 Bash로 에디터를 조작할 수 있도록 한다.

**선행**: Phase 46a

**작업**:
- [ ] `tools/ironrose-cli/` 디렉토리 생성
- [ ] `ironrose_cli.py` 구현
  - CLI 인자 파싱 (argparse)
  - Named Pipe 연결 (Linux: Unix Domain Socket, Windows: 파일 열기)
  - 메시지 프레임 읽기/쓰기
  - JSON 요청 구성 및 전송
  - JSON 응답 수신 및 출력
  - 에러 처리 (연결 실패, 타임아웃 등)
  - `--project` 옵션으로 프로젝트 이름 지정
- [ ] 동작 확인: `python ironrose_cli.py ping`으로 엔진 응답 수신

**검증 기준**:
- `python tools/ironrose-cli/ironrose_cli.py ping` 실행 시 `{ "pong": true, "project": "..." }` 출력.
- `python tools/ironrose-cli/ironrose_cli.py scene.list` 실행 시 현재 씬의 GO 목록 출력.
- 에디터가 실행 중이 아닐 때 실행하면 연결 실패 메시지 출력 (exit code 1).

### Phase 46c: 추가 명령 세트

**목표**: 나머지 명령 핸들러를 추가하여 실질적인 에디터 조작을 가능하게 한다.

**선행**: Phase 46b

**작업**:
- [ ] `go.get` 핸들러 구현
- [ ] `go.set_active` 핸들러 구현
- [ ] `go.set_field` 핸들러 구현
- [ ] `go.find` 핸들러 구현
- [ ] `select` 핸들러 구현
- [ ] `play.enter`, `play.stop`, `play.pause`, `play.resume`, `play.state` 핸들러 구현
- [ ] `scene.save`, `scene.load` 핸들러 구현
- [ ] `log.recent` 핸들러 구현
- [ ] `dotnet build` 성공 확인
- [ ] 전체 명령 동작 확인

**검증 기준**:
- 모든 초기 명령 세트가 CLI에서 동작한다.
- `go.set_field`로 Transform position을 변경하면 에디터 화면에 반영된다.
- `play.enter` -> `play.state` -> `play.stop` 시퀀스가 정상 동작한다.

## 의존 관계

```
Phase 46a (Named Pipe 서버 + 기본 명령 + EngineCore 통합)
    |
    v
Phase 46b (Python CLI 래퍼)
    |
    v
Phase 46c (추가 명령 세트)
```

## 대안 검토

### IPC 메커니즘

| 방식 | 장점 | 단점 | 결정 |
|------|------|------|------|
| Named Pipe | 로컬 전용, 빠름, .NET 표준 라이브러리, 설정 불필요 | Windows/Linux 동작 미세 차이 | **채택** |
| TCP Socket | 범용, 크로스 플랫폼 동일 동작 | 포트 충돌 가능, 방화벽 문제, 보안 | 미채택 |
| Unix Domain Socket | Linux에서 가장 자연스러움 | Windows 지원 제한적 | 미채택 (Named Pipe가 Linux에서 UDS로 구현됨) |
| HTTP REST | 범용, 디버깅 용이 (curl 등) | 무거움, 추가 라이브러리 필요 | 미채택 |
| 파일 기반 (기존 TestCommandRunner 방식) | 단순 | 실시간 양방향 통신 불가, 폴링 필요 | 미채택 |

### 래퍼 언어

| 언어 | 장점 | 단점 | 결정 |
|------|------|------|------|
| Python | Claude Code가 Bash로 쉽게 호출, 설치 보편적, 외부 의존성 불필요 | 별도 런타임 필요 | **채택** |
| C# 콘솔 앱 | 타입 안전, 엔진과 동일 언어 | 빌드 필요, 실행 파일 크기 큼 | 미채택 |
| Bash 스크립트 | 추가 런타임 불필요 | Named Pipe 바이너리 프로토콜 처리 어려움 | 미채택 |

### 메시지 프레임 포맷

| 방식 | 장점 | 단점 | 결정 |
|------|------|------|------|
| 길이 접두사 (4바이트 LE) | 메시지 경계 명확, 바이너리 데이터 포함 가능 | 구현 약간 복잡 | **채택** |
| 줄바꿈 구분 (`\n`) | 단순 | 응답 JSON 내부 줄바꿈과 충돌, 메시지 경계 모호 | 미채택 |
| 고정 크기 헤더 + JSON | HTTP 유사, 확장성 좋음 | 과도한 설계 | 미채택 |

## 미결 사항

없음. 모든 주요 결정사항이 확정되었다.
