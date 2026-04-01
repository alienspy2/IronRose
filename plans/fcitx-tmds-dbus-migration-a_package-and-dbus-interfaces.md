# Phase 1: Tmds.DBus 패키지 추가 및 D-Bus 인터페이스 선언

## 목표
- Tmds.DBus NuGet 패키지를 프로젝트에 추가한다.
- Fcitx4 D-Bus 인터페이스(`IFcitxInputMethod`, `IFcitxInputContext`)를 C# 인터페이스로 선언한다.
- 이 phase 완료 시 기존 코드에 영향 없이 빌드가 성공해야 한다.

## 선행 조건
- 없음 (첫 번째 phase)

## 수정할 파일

### `src/IronRose.Engine/IronRose.Engine.csproj`
- **변경 내용**: `<ItemGroup>` 내에 `Tmds.DBus` 패키지 참조를 추가한다.
- **이유**: Phase 2에서 FcitxPreeditProvider를 Tmds.DBus 기반으로 재작성하기 위한 사전 준비.
- **추가할 라인** (기존 `ImGui.NET` PackageReference 아래에):
  ```xml
  <PackageReference Include="Tmds.DBus" Version="0.91.1" />
  ```
- **기존 내용 참조**: 현재 파일의 `<ItemGroup>` 에는 AssimpNet, SharpGLTF.Core, BCnEncoder.Net, Meshoptimizer.NET, Tomlyn, Silk.NET.Windowing, Silk.NET.Input, Veldrid, SixLabors.ImageSharp, SixLabors.ImageSharp.Drawing, SixLabors.Fonts, YamlDotNet, ImGui.NET 이 있다.

## 생성할 파일

### `src/IronRose.Engine/Editor/ImGui/Ime/FcitxDBusInterfaces.cs`
- **역할**: Fcitx4 D-Bus 서비스의 두 인터페이스를 Tmds.DBus 프록시 생성용 C# 인터페이스로 선언한다. 이 파일 자체는 런타임 로직이 없으며, Tmds.DBus가 리플렉션으로 프록시를 자동 생성할 때 사용된다.
- **네임스페이스**: `IronRose.Engine.Editor.ImGuiEditor.Ime`
- **접근 제한자**: 모든 타입 `internal`

#### 인터페이스 1: `IFcitxInputMethod`
- **D-Bus 서비스**: `org.fcitx.Fcitx`
- **D-Bus 경로**: `/inputmethod`
- **D-Bus 인터페이스**: `org.fcitx.Fcitx.InputMethod`
- **Tmds.DBus 어트리뷰트**: `[DBusInterface("org.fcitx.Fcitx.InputMethod")]`
- **상속**: `IDBusObject`
- **메서드**:
  - `Task<(int icId, bool enable, uint keyval1, uint state1, uint keyval2, uint state2)> CreateICv3Async(string appName, int pid)` -- InputContext 생성. 반환 튜플은 D-Bus 시그니처 `(ibqqqq)` 대응. 실제로는 `icId`만 사용한다.

#### 인터페이스 2: `IFcitxInputContext`
- **D-Bus 서비스**: `org.fcitx.Fcitx`
- **D-Bus 경로**: `/inputcontext_{icId}` (동적)
- **D-Bus 인터페이스**: `org.fcitx.Fcitx.InputContext`
- **Tmds.DBus 어트리뷰트**: `[DBusInterface("org.fcitx.Fcitx.InputContext")]`
- **상속**: `IDBusObject`
- **메서드** (모두 `Task` 반환):
  - `Task EnableICAsync()`
  - `Task CloseICAsync()`
  - `Task FocusInAsync()`
  - `Task FocusOutAsync()`
  - `Task ResetAsync()`
  - `Task CommitPreeditAsync()`
  - `Task SetCursorRectAsync(int x, int y, int w, int h)`
  - `Task SetCapacityAsync(uint caps)` -- **주의: 메서드명이 `SetCapacity`이다 (Fcitx4 레거시 네이밍). `SetCapability`가 아님.**
  - `Task SetSurroundingTextAsync(string text, uint cursor, uint anchor)`
  - `Task<int> ProcessKeyEventAsync(uint keyval, uint keycode, uint state, int type, uint time)` -- 반환값 `int`: Fcitx가 키를 처리했으면 1, 아니면 0
  - `Task DestroyICAsync()`
- **시그널 Watch 메서드** (Tmds.DBus 시그널 구독 패턴):
  - `Task<IDisposable> WatchCommitStringAsync(Action<string> handler, Action<Exception>? onError = null)` -- 확정된 문자열 수신
  - `Task<IDisposable> WatchUpdatePreeditAsync(Action<(string str, int cursorPos)> handler, Action<Exception>? onError = null)` -- 단순 preedit 텍스트 수신
  - `Task<IDisposable> WatchUpdateFormattedPreeditAsync(Action<((string text, int attr)[] preedit, int cursorPos)> handler, Action<Exception>? onError = null)` -- 포맷된 preedit 수신. D-Bus 타입 `a(si), i`. 각 `(text, attr)` 쌍의 text를 이어붙이면 전체 preedit 텍스트.
  - `Task<IDisposable> WatchForwardKeyAsync(Action<(uint keyval, uint state, int type)> handler, Action<Exception>? onError = null)` -- Fcitx가 처리하지 않고 되돌려 보내는 키
  - `Task<IDisposable> WatchEnableIMAsync(Action handler, Action<Exception>? onError = null)` -- IME 활성화 알림 (현재 사용하지 않지만 인터페이스 완전성을 위해 선언)
  - `Task<IDisposable> WatchCloseIMAsync(Action handler, Action<Exception>? onError = null)` -- IME 비활성화 알림 (동상)

- **구현 힌트**:
  - Tmds.DBus의 `[DBusInterface]` 어트리뷰트는 `Tmds.DBus` 네임스페이스에 있다.
  - `IDBusObject`도 `Tmds.DBus` 네임스페이스에 있다.
  - using 문: `using System; using System.Collections.Generic; using System.Threading.Tasks; using Tmds.DBus;`
  - `Action<Exception>?` 파라미터에 `= null` 기본값을 지정한다.
  - 이 인터페이스들은 구현체를 직접 작성하지 않는다. Tmds.DBus가 `Connection.CreateProxy<T>(serviceName, objectPath)` 호출 시 동적 프록시를 생성한다.

- **파일 상단 주석 형식**: 기존 파일들과 동일한 `// @file`, `// @brief`, `// @deps`, `// @exports`, `// @note` 패턴을 따른다.

## NuGet 패키지
- `Tmds.DBus` 0.91.1 -- .NET managed D-Bus 클라이언트 라이브러리. 인터페이스 선언으로 D-Bus 프록시를 자동 생성하며, 시그널 구독을 `IDisposable` 기반으로 안전하게 처리한다.

## 검증 기준
- [ ] `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 성공
- [ ] `Tmds.DBus` 패키지가 정상적으로 복원됨
- [ ] `FcitxDBusInterfaces.cs`가 컴파일 오류 없이 빌드됨
- [ ] 기존 `FcitxPreeditProvider.cs`와 `LibDBus.cs`는 변경 없이 그대로 유지됨 (아직 사용 중)

## 참고
- 네임스페이스는 반드시 `IronRose.Engine.Editor.ImGuiEditor.Ime`을 사용할 것 (기존 파일들과 동일).
- 이 phase에서는 기존 코드를 전혀 변경하지 않는다. 새 인터페이스 파일과 패키지 참조 추가만 수행한다.
- `EnableIMAsync` / `CloseIMAsync` 시그널 Watch는 현재 구현에서 사용하지 않지만, Fcitx4 인터페이스 완전성을 위해 선언해 둔다. Phase 2에서 구독 여부는 선택적이다.
