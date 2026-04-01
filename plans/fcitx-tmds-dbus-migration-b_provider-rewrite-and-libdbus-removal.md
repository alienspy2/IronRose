# Phase 2: FcitxPreeditProvider 전면 재작성 및 LibDBus 삭제

## 목표
- `FcitxPreeditProvider.cs`를 Tmds.DBus 기반으로 전면 재작성한다.
- `LibDBus.cs`를 삭제한다.
- `IImePreeditProvider` 인터페이스 계약을 100% 유지한다.
- `UpdateFormattedPreedit`, `ForwardKey` 시그널 처리를 추가한다.
- `SetCapacity` (올바른 Fcitx4 메서드명) 호출로 수정한다.

## 선행 조건
- Phase 1 완료 (Tmds.DBus 패키지 추가 + FcitxDBusInterfaces.cs 생성)
- `src/IronRose.Engine/Editor/ImGui/Ime/FcitxDBusInterfaces.cs` 파일이 존재해야 함

## 삭제할 파일

### `src/IronRose.Engine/Editor/ImGui/Ime/LibDBus.cs`
- **이유**: raw P/Invoke 바인딩이 Tmds.DBus로 대체되므로 더 이상 필요 없다.
- **영향 확인 완료**: `LibDBus`를 참조하는 파일은 `FcitxPreeditProvider.cs`뿐이다 (이 phase에서 전면 재작성). `LibDBus.cs`에 정의된 `DBusError`, `DBusMessageIter`, `DBusBusType`, `DBusDispatchStatus`, `DBusHandlerResult`, `DBusHandleMessageFunction`, `DBusType` 등의 타입도 `FcitxPreeditProvider.cs`에서만 사용된다.

## 생성할 파일 (전면 재작성)

### `src/IronRose.Engine/Editor/ImGui/Ime/FcitxPreeditProvider.cs`
- **역할**: Fcitx4 D-Bus 인터페이스를 통한 IME preedit provider. Tmds.DBus `Connection` + 프록시 기반.
- **네임스페이스**: `IronRose.Engine.Editor.ImGuiEditor.Ime`
- **클래스**: `internal sealed class FcitxPreeditProvider : IImePreeditProvider`

#### using 문
```csharp
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using ImGuiNET;
using Tmds.DBus;
using Debug = RoseEngine.EditorDebug;
```

**주의**: 로깅은 반드시 `RoseEngine.EditorDebug`를 사용한다 (`Debug = RoseEngine.EditorDebug` 에일리어스). 에디터/엔진 코드이므로 `Debug.Log`가 아닌 `EditorDebug.Log`이다.

#### 상수
```csharp
private const string FCITX_SERVICE = "org.fcitx.Fcitx";
private const string FCITX_IM_PATH = "/inputmethod";
private const uint CAPACITY_PREEDIT = 0x2;  // Fcitx CAPACITY_PREEDIT 플래그
private const double RECONNECT_INTERVAL_SEC = 5.0;
```

#### 내부 이벤트 타입 (시그널 큐용)
```csharp
private enum ImeEventType { CommitString, UpdatePreedit, ForwardKey }

private readonly record struct ImeEvent(
    ImeEventType Type,
    string? Text = null,
    int CursorPos = 0,
    uint KeyVal = 0,
    uint KeyState = 0,
    int KeyType = 0);
```

#### 필드
```csharp
// Tmds.DBus 연결 및 프록시
private Connection? _connection;
private IFcitxInputContext? _icProxy;
private int _icId = -1;
private bool _connected;
private bool _disposed;
private double _lastReconnectAttempt;

// 시그널 구독 해제용 IDisposable
private IDisposable? _commitStringWatch;
private IDisposable? _updatePreeditWatch;
private IDisposable? _updateFormattedPreeditWatch;
private IDisposable? _forwardKeyWatch;

// 스레드 안전 이벤트 큐 (시그널 핸들러 -> Update)
private readonly ConcurrentQueue<ImeEvent> _eventQueue = new();

// 비동기 키 처리
private volatile bool _hasPendingKey;
private volatile bool _pendingKeyCompleted;
private bool _pendingKeyHandled;
private uint _pendingKeyVal, _pendingScanCode, _pendingState;
```

#### 프로퍼티 (IImePreeditProvider 구현)
```csharp
public string? PreeditText { get; private set; }
public int PreeditCursorPos { get; private set; }
public bool IsActive => _connected && _icId >= 0;
```

#### `Connect()` 메서드
- **시그니처**: `public void Connect()`
- **동작**:
  1. `Connection.Session`으로 세션 버스 주소를 얻는다.
  2. `new Connection(address)`로 Connection 객체를 생성한다.
  3. `_connection.ConnectAsync()`를 호출하여 D-Bus에 연결한다.
  4. `_connection.CreateProxy<IFcitxInputMethod>(FCITX_SERVICE, FCITX_IM_PATH)`로 InputMethod 프록시를 얻는다.
  5. `imProxy.CreateICv3Async("IronRose", pid)`를 호출하여 InputContext를 생성한다. `pid`는 `Environment.ProcessId`를 사용한다.
  6. 반환값에서 `icId`를 추출한다.
  7. IC 경로를 `$"/inputcontext_{icId}"`로 구성한다. **주의: 경로 구분자가 `/inputcontext_{id}`이다 (슬래시 아님, 언더스코어). 기존 코드는 `$"/inputcontext/{_icId}"`를 사용하고 있었으나, 이 부분은 기존 동작과 동일하게 유지한다.** 기존 코드의 경로 형식을 그대로 따른다: `$"/inputcontext/{_icId}"`.
  8. `_connection.CreateProxy<IFcitxInputContext>(FCITX_SERVICE, icPath)`로 IC 프록시를 얻는다.
  9. 시그널 Watch를 등록한다 (아래 상세).
  10. `_icProxy.SetCapacityAsync(CAPACITY_PREEDIT)`를 호출한다. **반드시 `SetCapacityAsync`이다 (Fcitx4 레거시 네이밍). `SetCapabilityAsync`가 아님.**
  11. `_icProxy.FocusInAsync()`를 호출한다.
  12. `_connected = true`로 설정한다.
- **동기/비동기 조화**: `Connect()`는 동기 메서드이다. 내부의 모든 비동기 호출은 `Task.Run(async () => { ... }).GetAwaiter().GetResult()`로 감싼다. 초기화는 한 번만 일어나므로 동기 블록이 허용된다.
- **실패 시**: 예외를 그대로 throw한다. 팩토리(`ImePreeditProviderFactory`)에서 catch하여 `NullPreeditProvider`로 폴백한다.
- **로그**: 각 단계마다 `Debug.Log("[IME-DIAG] ...")` 진단 로그를 남긴다 (기존 패턴 유지).

#### 시그널 Watch 등록
`Connect()` 내에서 다음 4개 시그널을 구독한다. 모두 비동기이므로 `Task.Run` 블록 안에서 await한다.

1. **CommitString**: `_commitStringWatch = await _icProxy.WatchCommitStringAsync(OnCommitString);`
   - `OnCommitString(string text)`: `_eventQueue.Enqueue(new ImeEvent(ImeEventType.CommitString, Text: text))`

2. **UpdatePreedit**: `_updatePreeditWatch = await _icProxy.WatchUpdatePreeditAsync(OnUpdatePreedit);`
   - `OnUpdatePreedit((string str, int cursorPos) data)`: `_eventQueue.Enqueue(new ImeEvent(ImeEventType.UpdatePreedit, Text: data.str, CursorPos: data.cursorPos))`

3. **UpdateFormattedPreedit**: `_updateFormattedPreeditWatch = await _icProxy.WatchUpdateFormattedPreeditAsync(OnUpdateFormattedPreedit);`
   - `OnUpdateFormattedPreedit(((string text, int attr)[] preedit, int cursorPos) data)`:
     - `a(si)` 배열의 각 요소에서 `.text` 부분만 이어붙여 단일 문자열을 만든다: `string.Concat(data.preedit.Select(p => p.text))` 또는 수동 `StringBuilder`/`string.Join`.
     - `_eventQueue.Enqueue(new ImeEvent(ImeEventType.UpdatePreedit, Text: combinedText, CursorPos: data.cursorPos))`
     - **이벤트 타입은 `UpdatePreedit`로 통일**한다 (소비 측에서 구분할 필요 없음).

4. **ForwardKey**: `_forwardKeyWatch = await _icProxy.WatchForwardKeyAsync(OnForwardKey);`
   - `OnForwardKey((uint keyval, uint state, int type) data)`: `_eventQueue.Enqueue(new ImeEvent(ImeEventType.ForwardKey, KeyVal: data.keyval, KeyState: data.state, KeyType: data.type))`

- **onError 콜백**: 모든 Watch의 `onError` 파라미터에 에러 핸들러를 전달한다. 에러 발생 시 `_connected = false`로 설정하고 로그를 남긴다.

#### `Update()` 메서드
- **시그니처**: `public void Update()`
- **동작**:
  1. `if (_disposed) return;`
  2. 연결 끊김 시 재연결 시도: `if (!_connected) { TryReconnect(); return; }`
  3. **이벤트 큐 처리**: `while (_eventQueue.TryDequeue(out var evt))` 루프로 모든 대기 이벤트를 처리:
     - `ImeEventType.CommitString`:
       - `ImGui.GetIO()`로 IO 객체를 얻는다.
       - `foreach (var ch in evt.Text!) io.AddInputCharacter(ch);` -- 완성된 문자열의 각 문자를 ImGui에 전달.
       - `PreeditText = null; PreeditCursorPos = 0;` -- preedit 초기화.
     - `ImeEventType.UpdatePreedit`:
       - `PreeditText = string.IsNullOrEmpty(evt.Text) ? null : evt.Text;`
       - `PreeditCursorPos = evt.CursorPos;`
     - `ImeEventType.ForwardKey`:
       - Fcitx가 처리하지 않고 되돌려 보낸 키를 ImGui IO에 전달한다.
       - `evt.KeyType`: 0 = press, 1 = release.
       - XKB keysym(`evt.KeyVal`)을 ImGuiKey로 변환한다. 아래 `MapXkbKeysymToImGuiKey()` 참조.
       - `var imKey = MapXkbKeysymToImGuiKey(evt.KeyVal);`
       - `if (imKey != ImGuiKey.None)` -> `ImGui.GetIO().AddKeyEvent(imKey, evt.KeyType == 0);`
       - Unicode 문자 범위(0x20~0x7E)에 해당하는 keysym이면 press 시 `io.AddInputCharacter((char)evt.KeyVal);` 추가 호출.
  4. **Tmds.DBus는 메시지 폴링 불필요** -- 내부 비동기 I/O 루프가 시그널을 자동으로 수신하여 Watch 핸들러를 호출한다. 기존의 `dbus_connection_read_write` + `dbus_connection_dispatch` 루프는 제거한다.
  5. **연결 끊김 감지**: 이벤트 큐 처리 중 예외가 발생하면 `_connected = false`로 설정한다. 다만 `ConcurrentQueue` 자체는 예외를 던지지 않으므로, 연결 끊김은 주로 시그널 Watch의 `onError` 콜백에서 감지된다.

#### `MapXkbKeysymToImGuiKey()` 메서드
- **시그니처**: `private static ImGuiKey MapXkbKeysymToImGuiKey(uint keysym)`
- **역할**: XKB keysym을 ImGuiKey로 변환한다. ForwardKey 이벤트 처리에 사용.
- **구현**: switch 표현식으로 매핑. 기존 `ImGuiInputHandler.MapKey()`의 XKB 버전이다.
- **매핑 테이블** (XkbKeyvalHelper.cs의 keysym 상수 참조):
  ```
  0xff09 => ImGuiKey.Tab
  0xff51 => ImGuiKey.LeftArrow
  0xff52 => ImGuiKey.UpArrow
  0xff53 => ImGuiKey.RightArrow
  0xff54 => ImGuiKey.DownArrow
  0xff55 => ImGuiKey.PageUp
  0xff56 => ImGuiKey.PageDown
  0xff50 => ImGuiKey.Home
  0xff57 => ImGuiKey.End
  0xff63 => ImGuiKey.Insert
  0xffff => ImGuiKey.Delete
  0xff08 => ImGuiKey.Backspace
  0x0020 => ImGuiKey.Space
  0xff0d => ImGuiKey.Enter
  0xff8d => ImGuiKey.KeypadEnter
  0xff1b => ImGuiKey.Escape
  0x0061~0x007a => ImGuiKey.A ~ ImGuiKey.Z  (소문자 a-z)
  0x0030~0x0039 => ImGuiKey.0 ~ ImGuiKey.9
  _ => ImGuiKey.None
  ```
- **구현 힌트**: 알파벳은 `keysym >= 0x0061 && keysym <= 0x007a` 범위 체크 후 `ImGuiKey.A + (int)(keysym - 0x0061)`로 변환. 숫자도 동일 패턴.

#### `ProcessKeyEventAsync()` 메서드
- **시그니처**: `public void ProcessKeyEventAsync(uint keyval, uint scancode, uint state, bool isRelease)`
- **동작**:
  1. `if (!_connected || _icId < 0) return;`
  2. `if (_hasPendingKey) return;` -- 이전 요청이 아직 완료되지 않음
  3. 인자 준비:
     - `int type = isRelease ? 1 : 0;` (Fcitx4: 0=press, 1=release)
     - `uint time = 0;`
  4. `_pendingKeyVal = keyval; _pendingScanCode = scancode; _pendingState = state; _hasPendingKey = true;`
  5. `_icProxy!.ProcessKeyEventAsync(keyval, scancode, state, type, time).ContinueWith(OnKeyEventResult);`
  6. fire-and-forget 패턴: 결과는 `ContinueWith`에서 큐잉된다.
- **`OnKeyEventResult(Task<int> task)` 콜백**:
  - `if (task.IsFaulted)`: 에러 로그 + `_hasPendingKey = false;` (pending 해제)
  - `if (task.IsCompletedSuccessfully)`: `_pendingKeyHandled = task.Result != 0; _pendingKeyCompleted = true;`
  - **주의**: 이 콜백은 ThreadPool 스레드에서 실행된다. `_pendingKeyCompleted`와 `_hasPendingKey`는 `volatile`이므로 가시성이 보장된다.

#### `HasPendingKeyEvent()` / `ConsumePendingKeyResult()`
- 기존과 동일한 로직:
  ```csharp
  public bool HasPendingKeyEvent() => _hasPendingKey && _pendingKeyCompleted;

  public (bool handled, uint keyval, uint scancode, uint state) ConsumePendingKeyResult()
  {
      if (!_hasPendingKey || !_pendingKeyCompleted)
          return (false, 0, 0, 0);
      var result = (_pendingKeyHandled, _pendingKeyVal, _pendingScanCode, _pendingState);
      _hasPendingKey = false;
      _pendingKeyCompleted = false;
      return result;
  }
  ```

#### `SetFocusState()` 메서드
- **시그니처**: `public void SetFocusState(bool focused)`
- **동작**:
  1. `if (!_connected || _icId < 0) return;`
  2. `focused`이면 `_icProxy!.FocusInAsync()`, 아니면 `_icProxy!.FocusOutAsync()` 호출.
  3. fire-and-forget: `.ContinueWith(t => { if (t.IsFaulted) Debug.Log(...); })` 패턴.
  4. 진단 로그 남기기 (기존 패턴 유지).

#### `SetCursorRect()` 메서드
- **시그니처**: `public void SetCursorRect(int x, int y, int width, int height)`
- **동작**:
  1. `if (!_connected || _icId < 0) return;`
  2. `_icProxy!.SetCursorRectAsync(x, y, width, height)` 호출 (fire-and-forget).

#### `TryReconnect()` 메서드
- **시그니처**: `private void TryReconnect()`
- **동작**: 기존과 동일한 5초 간격 재연결 로직.
  1. `var now = ImGui.GetTime();`
  2. `if (now - _lastReconnectAttempt < RECONNECT_INTERVAL_SEC) return;`
  3. `_lastReconnectAttempt = now;`
  4. `try { Connect(); } catch { /* 재연결 실패 -- 다음 시도까지 대기 */ }`

#### `Dispose()` 메서드
- **시그니처**: `public void Dispose()`
- **동작**:
  1. `if (_disposed) return; _disposed = true;`
  2. 시그널 Watch 해제: `_commitStringWatch?.Dispose(); _updatePreeditWatch?.Dispose(); _updateFormattedPreeditWatch?.Dispose(); _forwardKeyWatch?.Dispose();`
  3. FocusOut 호출 (정리): `if (_connected && _icId >= 0)` -> `try { _icProxy!.FocusOutAsync().GetAwaiter().GetResult(); } catch { }`
  4. Connection 해제: `_connection?.Dispose();`
  5. 상태 초기화: `_connection = null; _icProxy = null; _connected = false; _icId = -1;`

#### 파일 상단 주석
기존 FcitxPreeditProvider.cs와 동일한 `// @file`, `// @brief`, `// @deps`, `// @exports`, `// @note` 패턴을 따른다. `@deps`는 `IImePreeditProvider, FcitxDBusInterfaces, Tmds.DBus`로 변경한다. `@note`에는 "Tmds.DBus 기반. 시그널은 ConcurrentQueue를 통해 메인 스레드에서 처리됨."을 포함한다.

## 수정하지 않는 파일 (확인)
- `IImePreeditProvider.cs` -- 인터페이스 변경 없음
- `ImePreeditProviderFactory.cs` -- `new FcitxPreeditProvider()` + `provider.Connect()` 호출 패턴이 동일하므로 변경 불필요
- `ImGuiPreeditOverlay.cs` -- `IImePreeditProvider` 인터페이스를 통해 접근하므로 변경 불필요
- `NullPreeditProvider.cs` -- 변경 없음
- `XkbKeyvalHelper.cs` -- 변경 없음
- `LibX11.cs` -- 변경 없음
- `FcitxDBusInterfaces.cs` -- Phase 1에서 생성, 변경 없음

## 검증 기준
- [ ] `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 성공
- [ ] `LibDBus.cs` 파일이 삭제됨
- [ ] `FcitxPreeditProvider.cs`에 `LibDBus`, `IntPtr _connection`, `dbus_` 등의 P/Invoke 관련 코드가 없음
- [ ] `FcitxPreeditProvider.cs`에 `Tmds.DBus.Connection`, `IFcitxInputContext`, `ConcurrentQueue` 등이 사용됨
- [ ] `SetCapacityAsync` (Fcitx4 레거시 네이밍)가 사용됨 (`SetCapabilityAsync`가 아님)
- [ ] `IImePreeditProvider` 인터페이스의 모든 멤버가 구현됨
- [ ] 진단 로그에 `EditorDebug.Log` (= `Debug.Log` 에일리어스)가 사용됨 (`RoseEngine.Debug.Log`가 아님)

## 참고
- **Tmds.DBus Connection 생성 패턴**: `var address = Address.Session; var connection = new Connection(address); await connection.ConnectAsync();` 또는 `Connection.Session`을 사용. `Address.Session`은 `DBUS_SESSION_BUS_ADDRESS` 환경변수에서 주소를 읽는다.
- **프록시 생성 패턴**: `var proxy = connection.CreateProxy<IFcitxInputMethod>(FCITX_SERVICE, new ObjectPath(FCITX_IM_PATH));`
- **ObjectPath**: Tmds.DBus의 `ObjectPath` 타입으로 D-Bus 경로를 래핑한다. `new ObjectPath("/inputmethod")`, `new ObjectPath($"/inputcontext/{icId}")`.
- **fire-and-forget 패턴**: `SetFocusState`, `SetCursorRect` 등은 응답을 기다리지 않는다. 기존 코드에서는 `send_with_reply_and_block`으로 동기 호출했으나, Tmds.DBus에서는 Task를 fire-and-forget으로 처리하면 된다. 오류 로깅만 ContinueWith로 달아둔다.
- **ConcurrentQueue 선택 이유**: Tmds.DBus의 시그널 핸들러는 ThreadPool 스레드에서 호출된다. `Update()`는 메인(렌더) 스레드에서 호출된다. `ConcurrentQueue<ImeEvent>`로 스레드 안전하게 데이터를 전달한다.
- **기존 `SetCapability` 버그 수정**: 기존 코드에서는 `CallVoidMethod("SetCapability", ...)`로 호출하고 있었으나, Fcitx4 D-Bus 메서드명은 `SetCapacity`이다. Phase 1에서 인터페이스를 `SetCapacityAsync`로 선언했으므로, 이 phase에서 자연스럽게 수정된다.
- **기존 IC 경로**: 기존 코드는 `$"/inputcontext/{_icId}"`를 사용한다 (슬래시 구분). 이 패턴을 그대로 유지한다.
