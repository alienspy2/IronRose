# Fcitx4 IME: raw libdbus P/Invoke에서 Tmds.DBus로 전환

## 배경

현재 FcitxPreeditProvider는 libdbus-1.so P/Invoke로 D-Bus 세션 버스와 직접 통신한다.
이 방식에는 다음 문제가 있다:

1. **코드 복잡성**: LibDBus.cs에 236줄의 P/Invoke 바인딩, FcitxPreeditProvider.cs에 514줄의 수동 메시지 파싱/조립 로직
2. **버그**: `SetCapability`로 호출하지만 실제 Fcitx4 D-Bus 메서드명은 `SetCapacity` (Fcitx4 레거시 네이밍)
3. **미처리 시그널**: `UpdateFormattedPreedit` (타입: `a(si), i`) 시그널을 처리하지 않음. capability로 preedit를 요청하면 Fcitx는 이 포맷으로 보낼 수 있음
4. **메모리 안전성**: IntPtr 기반 수동 메모리 관리, 누수 가능성

Tmds.DBus는 .NET용 managed D-Bus 라이브러리로, 인터페이스 선언만으로 D-Bus 프록시를 자동 생성하며 시그널 Watch도 IDisposable 기반으로 안전하게 처리된다.

## 목표

1. LibDBus.cs (raw P/Invoke 바인딩) 삭제
2. FcitxPreeditProvider를 Tmds.DBus 기반으로 전면 재작성
3. `SetCapacity` (올바른 메서드명) 호출
4. `UpdateFormattedPreedit` 시그널 처리 추가
5. `CommitString`, `ForwardKey` 시그널 처리
6. 기존 IImePreeditProvider 인터페이스 계약 유지
7. 프레임 블로킹 없는 비동기 키 처리 패턴 유지

## 현재 상태

### 관련 파일 구조

```
src/IronRose.Engine/Editor/ImGui/Ime/
  IImePreeditProvider.cs       -- 인터페이스 (유지)
  FcitxPreeditProvider.cs      -- 핵심 구현 (전면 재작성)
  LibDBus.cs                   -- P/Invoke 바인딩 (삭제)
  ImGuiPreeditOverlay.cs       -- preedit 렌더링 (유지)
  ImePreeditProviderFactory.cs -- 팩토리 (수정)
  NullPreeditProvider.cs       -- null 구현 (유지)
  XkbKeyvalHelper.cs           -- scancode->keysym 변환 (유지)
  LibX11.cs                    -- X11 P/Invoke (유지)
```

### 현재 FcitxPreeditProvider 동작 흐름

1. `Connect()`: `dbus_bus_get(Session)` -> `CreateICv3` 동기 호출 -> IC 생성 -> 시그널 필터 등록 -> `SetCapability` -> `FocusIn`
2. `Update()`: `dbus_connection_read_write(0)` + `dbus_connection_dispatch()` 반복으로 메시지 폴링 -> 필터 콜백에서 시그널 처리 -> `CheckPendingKeyResponse()`
3. `ProcessKeyEventAsync()`: `dbus_connection_send_with_reply()` (비동기) -> pendingCall 저장
4. `CheckPendingKeyResponse()`: `dbus_pending_call_get_completed()` 폴링 -> 결과 소비
5. 시그널 처리: `CommitString` -> ImGui IO에 문자 전달 + preedit 초기화, `UpdatePreedit` -> PreeditText 갱신

### IImePreeditProvider 인터페이스 (유지)

```csharp
internal interface IImePreeditProvider : IDisposable
{
    string? PreeditText { get; }
    int PreeditCursorPos { get; }
    bool IsActive { get; }
    void Update();
    void SetFocusState(bool focused);
    void SetCursorRect(int x, int y, int width, int height);
    void ProcessKeyEventAsync(uint keyval, uint scancode, uint state, bool isRelease);
    bool HasPendingKeyEvent();
    (bool handled, uint keyval, uint scancode, uint state) ConsumePendingKeyResult();
}
```

## 설계

### 개요

Tmds.DBus의 `Connection` + 인터페이스 프록시 패턴을 사용하여 Fcitx4 D-Bus 통신을 구현한다.

핵심 변경:
- Fcitx4 D-Bus 인터페이스를 C# 인터페이스로 선언 (`IFcitxInputMethod`, `IFcitxInputContext`)
- `FcitxPreeditProvider`를 Tmds.DBus Connection 기반으로 재작성
- 시그널은 `WatchXxxAsync()` 메서드로 자동 구독
- `ProcessKeyEvent`는 `Task<int>`를 fire-and-forget + 결과 큐잉

### 상세 설계

#### 1. NuGet 패키지 추가

`IronRose.Engine.csproj`에 추가:
```xml
<PackageReference Include="Tmds.DBus" Version="0.91.1" />
```

Linux 전용이므로 조건부 컴파일은 불필요 (팩토리에서 플랫폼 분기, 런타임에 Windows에서는 FcitxPreeditProvider 자체가 인스턴스화되지 않음).

#### 2. 새 파일: FcitxDBusInterfaces.cs

Fcitx4 D-Bus 인터페이스를 Tmds.DBus 인터페이스로 선언한다.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace IronRose.Engine.Editor.ImGuiEditor.Ime
{
    // org.fcitx.Fcitx.InputMethod (path: /inputmethod)
    [DBusInterface("org.fcitx.Fcitx.InputMethod")]
    internal interface IFcitxInputMethod : IDBusObject
    {
        // CreateICv3(appname:s, pid:i) -> (icid:i, enable:b, keyval1:u, state1:u, keyval2:u, state2:u)
        Task<(int icId, bool enable, uint keyval1, uint state1, uint keyval2, uint state2)>
            CreateICv3Async(string appName, int pid);
    }

    // org.fcitx.Fcitx.InputContext (path: /inputcontext_{id})
    [DBusInterface("org.fcitx.Fcitx.InputContext")]
    internal interface IFcitxInputContext : IDBusObject
    {
        Task EnableICAsync();
        Task CloseICAsync();
        Task FocusInAsync();
        Task FocusOutAsync();
        Task ResetAsync();
        Task CommitPreeditAsync();
        Task SetCursorRectAsync(int x, int y, int w, int h);
        Task SetCapacityAsync(uint caps);  // Fcitx4 레거시: "Capacity" (not "Capability")
        Task SetSurroundingTextAsync(string text, uint cursor, uint anchor);
        Task<int> ProcessKeyEventAsync(uint keyval, uint keycode, uint state, int type, uint time);
        Task DestroyICAsync();

        // Signals
        Task<IDisposable> WatchCommitStringAsync(Action<string> handler, Action<Exception>? onError = null);
        Task<IDisposable> WatchUpdatePreeditAsync(Action<(string str, int cursorPos)> handler, Action<Exception>? onError = null);
        Task<IDisposable> WatchUpdateFormattedPreeditAsync(Action<((string text, int attr)[] preedit, int cursorPos)> handler, Action<Exception>? onError = null);
        Task<IDisposable> WatchForwardKeyAsync(Action<(uint keyval, uint state, int type)> handler, Action<Exception>? onError = null);
        Task<IDisposable> WatchEnableIMAsync(Action handler, Action<Exception>? onError = null);
        Task<IDisposable> WatchCloseIMAsync(Action handler, Action<Exception>? onError = null);
    }
}
```

**주의: `UpdateFormattedPreedit`의 `a(si)` 타입 매핑**

Tmds.DBus는 D-Bus `a(si)` 타입을 `(string, int)[]`로 매핑한다. 각 요소는 `(텍스트 조각, 속성 플래그)` 쌍이다. preedit 텍스트를 구성하려면 모든 조각의 텍스트를 이어붙이면 된다.

#### 3. FcitxPreeditProvider 전면 재작성

핵심 설계 결정:

**a) 비동기 초기화와 동기 인터페이스의 조화**

`IImePreeditProvider.Connect()`는 void 동기 메서드다. Tmds.DBus의 `Connection.ConnectAsync()`는 비동기다.
해결: `Connect()` 내에서 `Task.Run(...).GetAwaiter().GetResult()`로 동기 블록한다. 초기화는 한 번만 일어나므로 허용 가능.

**b) 시그널 수신: Tmds.DBus의 내부 비동기 루프**

Tmds.DBus는 내부적으로 비동기 I/O 루프를 운영하며, `WatchXxxAsync()`로 등록한 핸들러를 ThreadPool에서 호출한다.
시그널 핸들러에서 직접 `PreeditText`/`PreeditCursorPos`를 갱신하되, `Update()`에서 읽으므로 thread-safety가 필요하다.

해결: 시그널 핸들러에서 받은 데이터를 `ConcurrentQueue`에 넣고, `Update()`에서 dequeue하여 메인 스레드에서 처리한다.

**c) ProcessKeyEvent 비동기 처리**

현재 패턴을 유지한다:
- `ProcessKeyEventAsync()`: `Task<int>`를 fire-and-forget으로 시작. ContinueWith로 결과를 큐잉.
- `HasPendingKeyEvent()` / `ConsumePendingKeyResult()`: 큐에서 결과 확인/소비.

**d) Update() 메서드**

Tmds.DBus는 메시지 폴링이 불필요하다 (내부 비동기 루프가 처리). `Update()`는:
1. 시그널 이벤트 큐를 처리 (CommitString, UpdatePreedit, UpdateFormattedPreedit, ForwardKey)
2. 비동기 키 응답 큐를 확인
3. 연결 끊김 감지 + 재연결

**e) 재연결 로직**

Tmds.DBus Connection이 끊기면 `ObjectDisposedException` 등이 발생한다.
`_connected` 플래그와 타이머로 재연결을 시도한다 (기존과 동일한 5초 간격).

#### 새 FcitxPreeditProvider 구조 (주요 필드/메서드)

```csharp
internal sealed class FcitxPreeditProvider : IImePreeditProvider
{
    // Tmds.DBus
    private Connection? _connection;
    private IFcitxInputContext? _icProxy;
    private int _icId = -1;
    private bool _connected;
    private bool _disposed;
    private double _lastReconnectAttempt;

    // 시그널 구독 해제용
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

    // ImeEvent discriminated union
    private enum ImeEventType { CommitString, UpdatePreedit, ForwardKey }
    private readonly record struct ImeEvent(
        ImeEventType Type,
        string? Text,
        int CursorPos,
        uint KeyVal, uint KeyState, int KeyType);
}
```

#### 4. 시그널별 처리 상세

**CommitString(str:s)**
- 이벤트 큐에 넣음
- `Update()`에서 dequeue -> `ImGui.GetIO().AddInputCharacter()` 호출 + `PreeditText = null`

**UpdatePreedit(str:s, cursorpos:i)**
- 이벤트 큐에 넣음
- `Update()`에서 dequeue -> `PreeditText`, `PreeditCursorPos` 갱신

**UpdateFormattedPreedit(str:a(si), cursorpos:i)**
- `a(si)` 배열의 각 요소에서 텍스트 부분만 이어붙여 단일 문자열로 합성
- 이벤트 큐에 UpdatePreedit와 동일하게 넣음

**ForwardKey(keyval:u, state:u, type:i)**
- 이벤트 큐에 넣음
- `Update()`에서 dequeue -> ImGui IO에 키 이벤트 전달 (Fcitx가 처리하지 않고 되돌려 보낸 키)

#### 5. 파일 변경 목록

| 파일 | 변경 |
|------|------|
| `IronRose.Engine.csproj` | `Tmds.DBus` 0.91.1 패키지 참조 추가 |
| `Ime/FcitxDBusInterfaces.cs` | 새 파일. `IFcitxInputMethod`, `IFcitxInputContext` 인터페이스 선언 |
| `Ime/FcitxPreeditProvider.cs` | 전면 재작성. Tmds.DBus 기반으로 변경 |
| `Ime/LibDBus.cs` | 삭제 |
| `Ime/ImePreeditProviderFactory.cs` | `Connect()` 호출 방식은 동일하므로 변경 없음 (팩토리 자체는 유지) |
| `Ime/IImePreeditProvider.cs` | 변경 없음 |
| `Ime/ImGuiPreeditOverlay.cs` | 변경 없음 |
| `Ime/NullPreeditProvider.cs` | 변경 없음 |

### 영향 범위

- **직접 수정**: 3개 파일 (csproj, FcitxPreeditProvider.cs, 새 FcitxDBusInterfaces.cs) + 1개 삭제 (LibDBus.cs)
- **간접 영향 없음**: IImePreeditProvider 인터페이스가 유지되므로 ImGuiInputHandler, ImGuiPreeditOverlay, ImGuiOverlay 등 소비자에게 영향 없음
- **런타임 의존성**: Tmds.DBus는 managed 구현이므로 libdbus-1.so 시스템 의존성이 제거됨 (다만 D-Bus 데몬 자체는 필요)

### ForwardKey 시그널 처리 설계

ForwardKey는 Fcitx가 키를 처리하지 않고 애플리케이션에 되돌려 보내는 시그널이다.
현재 코드에서는 처리하지 않지만, 이번에 추가한다.

ForwardKey 이벤트를 받으면:
1. `keyval`을 ImGuiKey로 변환 (XkbKeyvalHelper 또는 매핑 테이블)
2. `type` (0=press, 1=release)에 따라 ImGui IO에 키 이벤트 전달

단, ImGuiInputHandler가 이 변환 로직을 이미 갖고 있으므로, FcitxPreeditProvider에서 직접 처리하기보다는 ForwardKey 이벤트를 IImePreeditProvider 인터페이스를 통해 노출하는 것을 검토한다.

**결정**: ForwardKey 처리는 FcitxPreeditProvider 내부에서 직접 ImGui IO에 전달한다 (XKB keysym -> ImGuiKey 변환 테이블 사용). 인터페이스 변경을 최소화하기 위함.

## 대안 검토

### Tmds.DBus.Protocol (저수준 API) vs Tmds.DBus (고수준 인터페이스 기반)

- `Tmds.DBus.Protocol`: 메시지를 수동으로 조립/파싱. 현재 libdbus P/Invoke와 복잡도가 비슷.
- `Tmds.DBus` (고수준): 인터페이스 선언만으로 프록시 자동 생성. 코드 대폭 간소화.
- **선택**: `Tmds.DBus` (고수준). 코드 간소화가 핵심 목표.

### Connection 수명 관리

- **Option A**: Connection을 Connect/Dispose에서만 생성/해제. 재연결 시 새 Connection 생성.
- **Option B**: Connection pool 사용.
- **선택**: Option A. Fcitx IME 연결은 에디터 수명 동안 하나만 필요.

## 미결 사항

없음. 요구사항이 명확하며, Tmds.DBus의 API 패턴도 잘 정의되어 있다.
