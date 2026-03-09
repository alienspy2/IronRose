# Cursor ESC 잠금 해제 및 Game View 클릭 재진입 버그 수정

## 유저 보고 내용 (2차)
- Play 모드에서 `Cursor.lockState = CursorLockMode.Locked` 상태일 때, ESC 키를 누르면 커서 잠금이 풀려야 하는데 실제 OS 커서가 풀리지 않음
- Game View 클릭으로 재진입도 안 됨
- 자동 테스트에서 `EscapeRelease()` 호출 로그는 찍히지만 실제 마우스 커서가 풀리지 않음

## 원인 (2개)

### 원인 1: ESC override 시 visible=false면 커서가 Hidden으로 설정됨
- `ApplyState()`의 ESC override 분기에서 `cursor.CursorMode = _visible ? CursorMode.Normal : CursorMode.Hidden` 설정
- FPS 게임 등에서 `Cursor.visible = false`로 설정한 경우, ESC를 눌러도 `CursorMode.Hidden`이 적용됨
- `Disabled`(relative mouse mode)에서 `Hidden`으로 바뀌어도 커서가 보이지 않으므로 유저는 "풀리지 않았다"고 인지
- Unity 동작: ESC override 시 visible 상태와 무관하게 커서를 Normal로 보여줌

### 원인 2: Game View 재진입 시 ImGuiWantsMouse 차단
- ESC override 후 커서가 Normal이 되면 ImGui가 마우스를 캡처하여 `ImGuiWantsMouse = true`
- `Input.GetMouseButtonDown(0)`은 `!ImGuiWantsMouse` 조건이 있어 항상 false 반환
- 따라서 Game View를 클릭해도 `ReacquireLock()`이 호출되지 않음

## 수정 내용

### 수정 1: Cursor.cs ApplyState() ESC override 분기
- `_visible` 상태와 무관하게 항상 `CursorMode.Normal` 적용
- ESC override의 목적 자체가 "유저가 에디터 UI를 조작할 수 있도록 커서를 완전히 해제"하는 것

### 수정 2: Input.cs에 GetMouseButtonDownRaw 추가
- ImGui 차단을 무시하는 `GetMouseButtonDownRaw(int button)` internal 메서드 추가
- 기존 `GetKeyDownRaw` 패턴과 동일

### 수정 3: EngineCore.cs 재진입 조건 변경
- `Input.GetMouseButtonDown(0)` -> `Input.GetMouseButtonDownRaw(0)` 변경
- ESC override 상태에서 ImGui 마우스 캡처를 무시하고 Game View 클릭을 감지

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Cursor.cs` -- ApplyState() ESC override 분기에서 항상 CursorMode.Normal 적용
- `src/IronRose.Engine/RoseEngine/Input.cs` -- GetMouseButtonDownRaw() internal 메서드 추가
- `src/IronRose.Engine/EngineCore.cs` -- 재진입 조건에서 GetMouseButtonDownRaw 사용

## 검증
- dotnet build 성공 (오류 0개)
- 유저에게 실제 실행 테스트 요청 필요
