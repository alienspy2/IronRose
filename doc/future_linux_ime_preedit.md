# Linux IME Preedit (조합중 문자 표시) 구현 조사 기록

> 상태: 미완성 — 롤백됨 (2026-04-01)
> 관련 커밋: `8c41347`~`c480c2f` (18개 커밋, `ffecc1c`로 롤백)

## 목표

Linux에서 한글 등 CJK 입력 시 **조합중인 문자(preedit text)**를 ImGui InputText 위에 실시간 표시한다.
현재는 완성된 문자만 입력되고, 조합 과정은 보이지 않는다.

## 환경

- IME: Fcitx 4.2.9.9
- D-Bus 서비스: `org.fcitx.Fcitx` (세션 버스)
- 윈도우: Silk.NET (GLFW 기반)
- 렌더링: ImGui (cimgui 바인딩)

## Fcitx4 D-Bus 인터페이스 (introspect 결과)

### org.fcitx.Fcitx.InputMethod (경로: /inputmethod)

| 메서드 | 시그니처 | 설명 |
|--------|----------|------|
| CreateICv3 | `(s appname, i pid) → (i icId, b enable, u keyval1, u state1, u keyval2, u state2)` | InputContext 생성 |

### org.fcitx.Fcitx.InputContext (경로: /inputcontext_{icId})

**주의: 경로 구분자는 슬래시가 아닌 언더스코어 (`/inputcontext_1`, NOT `/inputcontext/1`)**

#### 메서드

| 메서드 | 시그니처 | 설명 |
|--------|----------|------|
| FocusIn | `() → ()` | 포커스 획득 알림 |
| FocusOut | `() → ()` | 포커스 해제 알림 |
| Reset | `() → ()` | IC 리셋 |
| SetCapacity | `(u caps) → ()` | capability 플래그 설정. **주의: "Capacity" (Fcitx4 레거시 네이밍, SetCapability 아님)** |
| SetCursorRect | `(i x, i y, i w, i h) → ()` | 커서 위치 전달 (후보창 위치용) |
| ProcessKeyEvent | `(u keyval, u keycode, u state, i type, u time) → (i ret)` | 키 이벤트 전달. ret=1이면 handled |
| DestroyIC | `() → ()` | IC 파괴 |
| SetSurroundingText | `(s text, u cursor, u anchor) → ()` | 주변 텍스트 설정 |

#### 시그널

| 시그널 | 시그니처 | 설명 |
|--------|----------|------|
| CommitString | `(s str)` | 확정된 문자열 |
| UpdatePreedit | `(s str, i cursorpos)` | 단순 preedit 텍스트 |
| UpdateFormattedPreedit | `(a(si) str, i cursorpos)` | 포맷된 preedit. 각 `(text, attr)` 쌍의 text를 이어붙이면 전체 preedit |
| ForwardKey | `(u keyval, u state, i type)` | Fcitx가 처리하지 않고 되돌려 보내는 키 |
| EnableIM / CloseIM | `()` | IME 활성화/비활성화 알림 |
| UpdateClientSideUI | `(s auxup, s auxdown, s preedit, s candidateword, s imname, i cursorpos)` | 전체 UI 업데이트 |

#### SetCapacity 플래그

| 값 | 의미 |
|----|------|
| 0x2 | CAPACITY_PREEDIT — 클라이언트가 preedit을 직접 표시할 수 있음을 알림 |

## 시도한 구현 방식

### 1차: raw libdbus P/Invoke (커밋 8c41347~f89d42d)

- `LibDBus.cs`: libdbus-1.so P/Invoke 바인딩 (~236줄)
- `FcitxPreeditProvider.cs`: 수동 메시지 조립/파싱 (~514줄)
- `ImGuiPreeditOverlay.cs`: ForegroundDrawList로 preedit 렌더링
- `XkbKeyvalHelper.cs`: scancode → X11 keycode → keysym 변환 (Unknown 키 처리)

**결과**: 연결은 되지만 동작하지 않음. `SetCapability`로 호출하는 버그 (실제 메서드명은 `SetCapacity`).

### 2차: Tmds.DBus 라이브러리 (커밋 6e2707a~c480c2f)

- `FcitxDBusInterfaces.cs`: `[DBusInterface]` 어트리뷰트로 C# 인터페이스 선언
- `FcitxPreeditProvider.cs`: Tmds.DBus Connection + 프록시 패턴으로 재작성 (~244줄)
- `LibDBus.cs` 삭제

**발견된 문제들**:

1. **internal 인터페이스 접근 불가**: Tmds.DBus는 런타임에 동적 프록시를 Emit하는데, `internal` 인터페이스는 접근 불가 → `public`으로 변경 필요
2. **IC 경로 오류**: `/inputcontext/19` (슬래시) → `/inputcontext_19` (언더스코어)로 수정 필요
3. **GLFW XIM 충돌 (근본 원인)**: GLFW가 자체적으로 XIM을 통해 Fcitx IC를 생성하고 있어, 우리 D-Bus IC는 별도 IC(영문 모드 고정)가 됨. 한영 전환은 GLFW IC에만 적용되어 우리 IC에서는 모든 키가 `handled=False` 반환.

## 근본 문제: GLFW XIM 충돌

GLFW는 X11 환경에서 `XMODIFIERS` 환경변수를 보고 XIM(X Input Method)을 통해 Fcitx에 연결한다.
이때 GLFW가 자체 InputContext를 생성하고, 한영 전환/조합 등을 이 IC에서 처리한다.

우리가 D-Bus로 별도 IC를 만들면:
- GLFW IC: 한글 모드 전환 가능, 조합 처리
- 우리 IC: 항상 영문 모드, 모든 키 handled=False

### 시도한 해결: XMODIFIERS 비활성화

`Environment.SetEnvironmentVariable("XMODIFIERS", "@im=null")` 을 윈도우 생성 전에 설정하여
GLFW의 XIM 연결을 끊고, 우리 IC에서 모든 입력을 처리하도록 시도.

**결과**: 한글 입력 자체가 안 됨. 추가 조사 필요.

## 해결 방향 (미검증)

### 방향 1: GLFW의 XIM preedit 콜백 활용

GLFW 내부적으로 XIM preedit callback을 설정할 수 있다.
`glfwSetPreeditCallback` (GLFW 3.4+) 또는 커스텀 패치를 통해
GLFW가 받는 preedit 텍스트를 직접 가져오는 방식.

- 장점: 별도 D-Bus IC 불필요, GLFW의 기존 XIM 연결 활용
- 단점: Silk.NET이 이 콜백을 노출하는지 확인 필요

### 방향 2: GLFW IC의 preedit 시그널 모니터링

GLFW가 생성한 IC의 D-Bus 경로를 알아내어 해당 IC의 UpdateFormattedPreedit 시그널만 구독.
키 전달은 GLFW에 맡기고, preedit 표시만 우리가 담당.

- 장점: 키 처리 로직 불필요
- 단점: GLFW IC 경로를 알아내기 어려움 (GLFW는 D-Bus가 아닌 XIM 프로토콜 사용 가능)

### 방향 3: XIM을 완전히 비활성화하고 우리 IC에서 전담

`XMODIFIERS=@im=null` + `OnKeyChar` 완전 차단 + 우리 IC에서 모든 키/문자 처리.

- 장점: 완전한 제어
- 단점: 한영 전환 키 처리, 키 버퍼링, 문자 매핑 등 복잡한 로직 필요.
  XMODIFIERS 비활성화 시 한글 입력이 아예 안 되는 현상 확인됨 — 원인 추가 조사 필요.

### 방향 4: SDL 참고 구현

SDL은 `SDL_fcitx.c`에서 Fcitx4 D-Bus IC를 직접 관리한다.
SDL은 XIM을 사용하지 않고 자체 D-Bus 통신으로 모든 것을 처리.
참고: https://github.com/libsdl-org/SDL/blob/main/src/core/linux/SDL_fcitx.c

- 장점: 실전 검증된 구현
- 단점: GLFW에서 SDL 방식을 적용하려면 GLFW의 XIM을 먼저 비활성화해야 함

## 참고 자료

- Tmds.DBus 모델링 가이드: https://github.com/tmds/Tmds.DBus/blob/main/docs/modelling.md
- Fcitx.Client API: https://lazka.github.io/pgi-docs/Fcitx-1.0/classes/Client.html
- SDL Fcitx 구현: https://github.com/libsdl-org/SDL/blob/main/src/core/linux/SDL_fcitx.c

## 검색어 추천

- `GLFW preedit callback IME composition`
- `glfwSetPreeditCallback GLFW 3.4`
- `Silk.NET GLFW IME preedit support`
- `SDL Fcitx D-Bus preedit implementation`
- `GLFW XIM disable custom IME client`
- `fcitx4 dbus ProcessKeyEvent always returns 0`
