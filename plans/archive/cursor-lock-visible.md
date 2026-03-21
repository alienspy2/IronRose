# Cursor.lockState / Cursor.visible API 구현

## 배경

Unity 호환 API로 `Cursor.lockState`와 `Cursor.visible`을 제공하여, 1인칭/3인칭 카메라 제어 등에서 커서를 잠그거나 숨기는 기능이 필요하다. IronRose 엔진은 Silk.NET 2.23.0 + Veldrid 기반이며, Silk.NET의 `ICursor.CursorMode`(Normal/Hidden/Disabled)와 `IMouse.IsConfined` 프로퍼티를 활용할 수 있다.

## 목표

- Unity `Cursor.lockState` (None, Locked, Confined) API 호환
- Unity `Cursor.visible` (bool) API 호환
- 에디터 Play 모드에서만 커서 잠금 적용, Edit 모드에서는 항상 정상 커서
- Standalone 빌드에서도 동일하게 동작
- ESC 키로 커서 잠금 임시 해제 (에디터 디버깅 편의)
- Play 모드 진입/종료 시 커서 상태 자동 저장/복원
- Input 시스템과 연동 (Locked 모드에서 `Input.GetAxis("Mouse X/Y")` 델타 정상 동작)

## 현재 상태

### 입력 시스템 (`src/IronRose.Engine/RoseEngine/Input.cs`)
- Silk.NET `IInputContext` 기반, `IMouse` 이벤트로 마우스 위치/델타/버튼 처리
- `_mouseDelta`는 `_latestMousePosition - _prevMousePosition`으로 계산 (절대 좌표 차이)
- `Input.GetAxis("Mouse X")` / `Input.GetAxis("Mouse Y")`로 델타 반환
- `ImGuiWantsMouse` / `ImGuiWantsKeyboard`로 에디터 UI 입력 차단
- `GameViewActive` / `GameViewMinX,Y` 등으로 에디터 Game View 좌표 리매핑

### 에디터 Play 모드 (`src/IronRose.Engine/Editor/EditorPlayMode.cs`)
- `PlayModeState` enum: Edit, Playing, Paused
- `EnterPlayMode()`, `StopPlayMode()`, `PausePlayMode()`, `ResumePlayMode()`
- Play 모드 전환 시 씬 상태 저장/복원, 물리 리셋 등 처리

### EngineCore (`src/IronRose.Engine/EngineCore.cs`)
- `InitInput()`에서 `_window.CreateInput()` -> `Input.Initialize(inputContext)`
- `_inputContext`와 `_window`를 private 필드로 보유
- `ProcessEngineKeys()`에서 F11(에디터 토글), F12(스크린샷) 처리
- `UpdateImGuiInputState()`에서 ImGui 입력 소비 상태 동기화

### Silk.NET 커서 API (2.23.0)
- `IMouse.Cursor` -> `ICursor` 인터페이스
  - `ICursor.CursorMode`: `Normal`, `Hidden`, `Disabled` (Disabled = 커서 숨김 + relative mouse mode)
- `IMouse.IsConfined`: bool (커서를 윈도우 영역에 제한)
- `IMouse.Position`: `Vector2` (커서 절대 위치, get/set)

### 커서 관련 기존 코드
- `Cursor` 클래스가 아직 존재하지 않음 (신규 생성 필요)
- SDL2 API는 직접 사용하지 않음 (Silk.NET이 SDL2/GLFW를 추상화)

## 설계

### 개요

1. `RoseEngine.Cursor` 정적 클래스 신규 생성 (Unity API 호환)
2. `RoseEngine.CursorLockMode` enum 신규 생성
3. Silk.NET `ICursor.CursorMode` + `IMouse.IsConfined`를 래핑하여 구현
4. `Input` 클래스에 Locked 모드 시 델타 처리 보정 로직 추가
5. `EditorPlayMode`에 커서 상태 저장/복원 로직 추가
6. `EngineCore`에 ESC 키 커서 잠금 해제 처리 추가

### 상세 설계

#### 1. CursorLockMode enum

**파일**: `src/IronRose.Engine/RoseEngine/CursorLockMode.cs` (신규)

```csharp
namespace RoseEngine
{
    public enum CursorLockMode
    {
        None,       // 기본 상태, 커서 자유 이동
        Locked,     // 커서 화면 중앙 고정 + 숨김, 마우스 델타만 반환
        Confined,   // 커서가 윈도우 영역에 제한됨
    }
}
```

#### 2. Cursor 정적 클래스

**파일**: `src/IronRose.Engine/RoseEngine/Cursor.cs` (신규)

```csharp
using Silk.NET.Input;

namespace RoseEngine
{
    public static class Cursor
    {
        // 사용자가 스크립트에서 설정한 논리적 상태
        private static CursorLockMode _lockState = CursorLockMode.None;
        private static bool _visible = true;

        // ESC로 임시 해제된 상태
        private static bool _escapeOverride = false;

        // Silk.NET 마우스 참조 (Initialize 시 설정)
        private static IMouse? _mouse;

        /// <summary>커서 잠금 모드. Unity Cursor.lockState 호환.</summary>
        public static CursorLockMode lockState
        {
            get => _lockState;
            set
            {
                _lockState = value;
                _escapeOverride = false; // 새 lockState 설정 시 ESC 오버라이드 해제
                ApplyState();
            }
        }

        /// <summary>커서 표시 여부. Unity Cursor.visible 호환.</summary>
        public static bool visible
        {
            get => _visible;
            set
            {
                _visible = value;
                ApplyState();
            }
        }

        /// <summary>ESC로 임시 해제된 상태인지 여부 (읽기 전용).</summary>
        public static bool isEscapeOverridden => _escapeOverride;

        /// <summary>현재 실제로 Locked 상태가 적용 중인지 (ESC 해제 아닌 상태).</summary>
        internal static bool IsEffectivelyLocked =>
            _lockState == CursorLockMode.Locked && !_escapeOverride && IsLockAllowed;

        /// <summary>EngineCore.InitInput()에서 호출. Silk.NET IMouse 참조 저장.</summary>
        internal static void Initialize(IMouse mouse)
        {
            _mouse = mouse;
        }

        /// <summary>ESC 키로 커서 잠금 임시 해제.</summary>
        internal static void EscapeRelease()
        {
            if (_lockState == CursorLockMode.Locked && !_escapeOverride)
            {
                _escapeOverride = true;
                ApplyState();
                Debug.Log("[Cursor] Escape override: cursor unlocked temporarily");
            }
        }

        /// <summary>Game View 클릭으로 Locked 상태 재진입.</summary>
        internal static void ReacquireLock()
        {
            if (_lockState == CursorLockMode.Locked && _escapeOverride)
            {
                _escapeOverride = false;
                ApplyState();
                Debug.Log("[Cursor] Lock reacquired");
            }
        }

        /// <summary>Play 모드 종료 시 강제 리셋.</summary>
        internal static void ResetToDefault()
        {
            _lockState = CursorLockMode.None;
            _visible = true;
            _escapeOverride = false;
            ApplyState();
        }

        /// <summary>현재 커서 잠금이 허용되는 상태인지 판단.</summary>
        private static bool IsLockAllowed
        {
            get
            {
                // Standalone(HeadlessEditor)에서는 Playing 상태일 때 허용
                // 에디터에서는 Playing 상태이고 Game View에 포커스가 있을 때 허용
                // (간단히 Playing 상태이면 허용하고, ImGui 입력 소비 시에는 적용하지 않음)
                return IronRose.Engine.Editor.EditorPlayMode.State
                    == IronRose.Engine.Editor.PlayModeState.Playing;
            }
        }

        /// <summary>논리적 상태 + 에디터 상태를 종합하여 Silk.NET 커서 모드 적용.</summary>
        internal static void ApplyState()
        {
            if (_mouse == null) return;

            var cursor = _mouse.Cursor;

            if (!IsLockAllowed)
            {
                // Play 모드가 아니면 항상 정상 커서
                cursor.CursorMode = CursorMode.Normal;
                return;
            }

            if (_escapeOverride)
            {
                // ESC로 임시 해제 중
                cursor.CursorMode = _visible ? CursorMode.Normal : CursorMode.Hidden;
                _mouse.IsConfined = false;
                return;
            }

            switch (_lockState)
            {
                case CursorLockMode.None:
                    cursor.CursorMode = _visible ? CursorMode.Normal : CursorMode.Hidden;
                    _mouse.IsConfined = false;
                    break;

                case CursorLockMode.Locked:
                    // Disabled = 커서 숨김 + relative mouse mode (SDL_SetRelativeMouseMode)
                    cursor.CursorMode = CursorMode.Disabled;
                    _mouse.IsConfined = false;
                    break;

                case CursorLockMode.Confined:
                    cursor.CursorMode = _visible ? CursorMode.Normal : CursorMode.Hidden;
                    _mouse.IsConfined = true;
                    break;
            }
        }
    }
}
```

**핵심 매핑**:

| Unity CursorLockMode | Silk.NET CursorMode | IMouse.IsConfined |
|---|---|---|
| None + visible=true | Normal | false |
| None + visible=false | Hidden | false |
| Locked | Disabled | false |
| Confined + visible=true | Normal | true |
| Confined + visible=false | Hidden | true |

`CursorMode.Disabled`는 Silk.NET에서 SDL2의 `SDL_SetRelativeMouseMode(SDL_TRUE)`를 호출하며, 이는 커서를 숨기고 상대 마우스 이동 모드를 활성화한다.

#### 3. Input 클래스 수정

**파일**: `src/IronRose.Engine/RoseEngine/Input.cs`

Locked 모드에서는 `CursorMode.Disabled` (relative mouse mode)가 활성화되면 Silk.NET이 자동으로 마우스 이벤트를 델타 기반으로 변환한다. 따라서 기존 `_mouseDelta = _latestMousePosition - _prevMousePosition` 로직이 그대로 정상 동작한다.

단, Locked 모드 진입/해제 시 첫 프레임에 큰 점프가 발생할 수 있으므로 이를 방지하는 로직을 추가한다.

**변경 사항**:

```csharp
// 기존 필드에 추가
private static bool _skipNextDelta = false;

/// <summary>다음 프레임의 델타를 무시 (커서 모드 전환 시 점프 방지).</summary>
internal static void SkipNextDelta() => _skipNextDelta = true;
```

`Update()` 메서드의 델타 계산 부분 수정:

```csharp
// 마우스 위치 및 델타
if (_skipNextDelta)
{
    _mouseDelta = Vector2.zero;
    _prevMousePosition = _latestMousePosition;
    _skipNextDelta = false;
}
else
{
    _mouseDelta = _latestMousePosition - _prevMousePosition;
    _prevMousePosition = _latestMousePosition;
}
_mousePosition = _latestMousePosition;
```

#### 4. EditorPlayMode 수정

**파일**: `src/IronRose.Engine/Editor/EditorPlayMode.cs`

**EnterPlayMode()** 끝에 추가:
```csharp
// 커서 상태는 스크립트가 설정하므로 기본값으로 시작
Cursor.ResetToDefault();
```

**StopPlayMode()** 에서 씬 복원 후 추가:
```csharp
// Play 모드 종료 시 커서를 기본 상태로 복원
Cursor.ResetToDefault();
```

**PausePlayMode()** 끝에 추가:
```csharp
// 일시정지 시 커서 잠금 해제 (에디터 조작 가능하도록)
Cursor.ApplyState(); // IsLockAllowed가 Paused에서는 false
```

참고: 현재 `IsLockAllowed`는 `Playing` 상태만 허용하므로 `Paused` 시 자동으로 커서가 풀린다. `ResumePlayMode()` 후에는 `ApplyState()`를 호출하여 스크립트가 설정한 lockState를 재적용한다.

**ResumePlayMode()** 끝에 추가:
```csharp
// Resume 시 스크립트가 설정한 커서 상태 재적용
Cursor.ApplyState();
```

#### 5. EngineCore 수정

**파일**: `src/IronRose.Engine/EngineCore.cs`

**InitInput()** 수정:
```csharp
private void InitInput()
{
    _inputContext = _window!.CreateInput();
    Input.Initialize(_inputContext);

    // Cursor API 초기화: 첫 번째 마우스 디바이스 참조 전달
    if (_inputContext.Mice.Count > 0)
        Cursor.Initialize(_inputContext.Mice[0]);
}
```

**ProcessEngineKeys()** 에 ESC 처리 추가 (**에디터 빌드에서만 동작**):
```csharp
private void ProcessEngineKeys()
{
    // ESC: 커서 잠금 임시 해제 (에디터 Play 모드에서 Locked 상태일 때만)
    // Standalone 빌드에서는 ESC로 커서 해제하지 않음 — 게임 스크립트가 직접 제어
    if (_isEditorMode && Input.GetKeyDownRaw(KeyCode.Escape) && Cursor.IsEffectivelyLocked)
    {
        Cursor.EscapeRelease();
        Input.SkipNextDelta();
    }

    // ... 기존 F11, F12 코드 ...
}
```

**UpdateImGuiInputState()** 에 Game View 클릭으로 커서 잠금 재진입 로직 추가:
```csharp
// 기존 코드 끝에 추가
// Game View 클릭으로 커서 잠금 재진입
if (Cursor.isEscapeOverridden
    && EditorPlayMode.State == PlayModeState.Playing
    && Input.GetMouseButtonDown(0))
{
    bool clickedGameView = false;

    if (_imguiOverlay != null && _imguiOverlay.IsVisible)
    {
        // 에디터: Game View 이미지 영역 클릭 시
        clickedGameView = _imguiOverlay.IsGameViewImageHovered;
    }
    else
    {
        // Standalone 또는 에디터 숨김: 윈도우 어디든 클릭 시
        clickedGameView = true;
    }

    if (clickedGameView)
    {
        Cursor.ReacquireLock();
        Input.SkipNextDelta();
    }
}
```

#### 6. Standalone 빌드 지원

Standalone에서는 `HeadlessEditor = true`이고, `EditorPlayMode.EnterPlayMode()`가 호출되어 항상 `Playing` 상태이다. 따라서:
- `IsLockAllowed`가 true가 되어 스크립트에서 `Cursor.lockState = CursorLockMode.Locked` 설정 시 바로 적용
- ESC 키로 임시 해제 **불가** — Standalone에서는 게임 스크립트가 직접 커서 상태를 제어해야 함
- 별도 수정 불필요

### 영향 범위

| 파일 | 변경 유형 | 설명 |
|---|---|---|
| `src/IronRose.Engine/RoseEngine/CursorLockMode.cs` | **신규** | enum 정의 |
| `src/IronRose.Engine/RoseEngine/Cursor.cs` | **신규** | Cursor 정적 클래스 |
| `src/IronRose.Engine/RoseEngine/Input.cs` | **수정** | `_skipNextDelta` 필드 및 `SkipNextDelta()` 추가, `Update()` 델타 계산 보정 |
| `src/IronRose.Engine/Editor/EditorPlayMode.cs` | **수정** | Play/Stop/Pause/Resume 시 커서 상태 관리 호출 추가 |
| `src/IronRose.Engine/EngineCore.cs` | **수정** | `InitInput()`에 Cursor 초기화, `ProcessEngineKeys()`에 ESC 처리, `UpdateImGuiInputState()`에 재진입 로직 |

### 기존 기능에 미치는 영향

- **Input.GetAxis("Mouse X/Y")**: Locked 모드에서 Silk.NET의 relative mouse mode가 활성화되면 기존 델타 계산이 그대로 동작. 변경 없음.
- **ImGui 오버레이**: `IsLockAllowed`가 Play 상태가 아니면 false를 반환하므로, Edit 모드에서 ImGui 조작에 영향 없음.
- **에디터 Scene View 카메라**: Scene View의 우클릭 카메라 조작은 에디터 자체 로직이므로 `Cursor` API와 독립적. 영향 없음.
- **Game View 좌표 리매핑**: Locked 모드에서는 마우스 절대 위치가 의미 없으므로 리매핑도 무의미하나, 델타는 정상 동작.

## 구현 단계

- [ ] 1단계: `CursorLockMode.cs` enum 생성
- [ ] 2단계: `Cursor.cs` 정적 클래스 생성
- [ ] 3단계: `Input.cs`에 `_skipNextDelta` / `SkipNextDelta()` 추가 및 `Update()` 수정
- [ ] 4단계: `EngineCore.InitInput()`에 `Cursor.Initialize()` 호출 추가
- [ ] 5단계: `EngineCore.ProcessEngineKeys()`에 ESC 커서 해제 처리 추가
- [ ] 6단계: `EngineCore.UpdateImGuiInputState()`에 Game View 클릭 재진입 로직 추가
- [ ] 7단계: `EditorPlayMode`의 Enter/Stop/Pause/Resume에 커서 상태 관리 호출 추가
- [ ] 8단계: 빌드 확인 (`dotnet build`)
- [ ] 9단계: 에디터에서 테스트 (LiveCode 스크립트로 `Cursor.lockState = CursorLockMode.Locked` 설정)
- [ ] 10단계: Standalone에서 테스트

## 대안 검토

### 대안 1: SDL2 API 직접 호출
- `SDL_SetRelativeMouseMode`, `SDL_ShowCursor` 등을 P/Invoke로 직접 호출
- **불채택 이유**: Silk.NET이 이미 SDL2/GLFW를 추상화하고 있으며, `ICursor.CursorMode`와 `IMouse.IsConfined`로 필요한 기능을 모두 제공. 직접 호출 시 Silk.NET 내부 상태와 불일치 위험.

### 대안 2: 커서 상태를 매 프레임 강제 적용
- `Update()` 루프에서 매 프레임 `ApplyState()` 호출
- **불채택 이유**: 상태 변경 시점에만 적용하는 것이 효율적. 단, Play 모드 상태 변화 시에는 `ApplyState()`를 호출하여 동기화.

### 대안 3: Confined 모드에서 윈도우 대신 Game View 영역에 제한
- 에디터의 Game View 패널 영역 내로 커서를 제한
- **불채택 이유**: Silk.NET의 `IsConfined`는 윈도우 단위로만 동작. Game View 영역 제한은 별도의 매 프레임 `WarpMouse` 로직이 필요하며 복잡도 대비 이점이 적음. 에디터에서 Confined는 윈도우 제한으로 동작하고, Standalone에서는 윈도우 = 게임 영역이므로 자연스러움.

## 미결 사항

없음. 설계에 필요한 모든 정보가 확보되었다.
