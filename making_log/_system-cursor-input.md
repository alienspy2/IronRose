# Cursor / Input 시스템

## 구조

### 주요 클래스/파일
- `RoseEngine/Cursor.cs` -- 커서 잠금/표시 상태 관리 정적 클래스 (Unity Cursor API 호환)
- `RoseEngine/CursorLockMode.cs` -- 커서 모드 enum (None, Locked, Confined)
- `RoseEngine/Input.cs` -- 키보드/마우스 입력 프레임 단위 관리 정적 클래스 (Unity Input API 호환)
- `EngineCore.cs` -- Cursor/Input 초기화 및 ESC/재진입 로직 호출
- `Editor/EditorPlayMode.cs` -- Play/Stop/Pause/Resume 시 커서 상태 자동 관리

### 의존 관계
```
EngineCore
  -> Cursor.Initialize(IMouse)        : InitInput()
  -> Cursor.EscapeRelease()           : ProcessEngineKeys() (에디터 전용)
  -> Cursor.ReacquireLock()           : UpdateImGuiInputState() (Game View 클릭)
  -> Input.SkipNextDelta()            : 커서 모드 전환 시

EditorPlayMode
  -> Cursor.ResetToDefault()          : EnterPlayMode(), StopPlayMode()
  -> Cursor.ApplyState()              : PausePlayMode(), ResumePlayMode()

Cursor
  -> EditorPlayMode.State             : IsLockAllowed 판단
  -> IMouse.Cursor.CursorMode         : Silk.NET 커서 모드 적용
```

## 핵심 동작

### 커서 잠금 흐름
1. 스크립트에서 `Cursor.lockState = CursorLockMode.Locked` 설정
2. `ApplyState()`가 호출되어 `IsLockAllowed` 확인 (Playing 상태만 허용)
3. Silk.NET `ICursor.CursorMode = Disabled` 적용 (SDL2 relative mouse mode)
4. ESC 키 -> `EscapeRelease()` -> 임시로 Normal 모드, `_escapeOverride = true`
5. Game View 클릭 -> `ReacquireLock()` -> Disabled 모드 재적용, `_escapeOverride = false`

### 델타 점프 방지
- 커서 모드 전환 시 `Input.SkipNextDelta()` 호출
- 다음 프레임 `Update()`에서 `_mouseDelta = Vector2.zero`로 처리

### 매핑 테이블
| Unity CursorLockMode | Silk.NET CursorMode | 비고 |
|---|---|---|
| None + visible=true | Normal | |
| None + visible=false | Hidden | |
| Locked | Disabled | relative mouse mode |
| Confined | Normal/Hidden | 윈도우 제한 미구현 (Silk.NET 제약) |

## 주의사항
- `Confined` 모드는 Silk.NET 2.23.0에 `IMouse.IsConfined`가 없어 윈도우 영역 제한 미구현
- ESC 임시 해제는 에디터(`!HeadlessEditor`)에서만 동작. Standalone에서는 스크립트가 직접 제어
- `Cursor.lockState` setter에서 `_escapeOverride = false`로 리셋됨 (스크립트가 다시 설정하면 ESC 해제 상태 초기화)
- Play 모드가 아닌 상태에서는 `ApplyState()`가 항상 `CursorMode.Normal`로 강제
- **ESC override 시 `_visible` 상태와 무관하게 항상 `CursorMode.Normal` 적용** (visible=false일 때 Hidden이 되면 유저가 커서 해제를 인지 불가)
- **Game View 재진입 클릭 감지는 `Input.GetMouseButtonDownRaw(0)` 사용** (ESC override 후 ImGui가 마우스를 캡처하므로 일반 GetMouseButtonDown은 false 반환)

## 사용하는 외부 라이브러리
- **Silk.NET.Input 2.23.0**: `IMouse`, `ICursor`, `CursorMode` enum (Normal/Hidden/Disabled)
