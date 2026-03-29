# LiveCode 핫리로드 실패 - AngryClawdGame.cs 카메라 필드 선언 누락

## 유저 보고 내용
- 에디터 실행 중 LiveCode 스크립트(AngryClawdGame.cs 등)를 수정했는데 핫리로드가 작동하지 않음
- 로그 파일(ironrose_*.log)에 핫리로드/컴파일 관련 로그가 전혀 없음
- `[Diag]` 태그가 포함된 새 코드가 반영되지 않음

## 원인
### 직접 원인: LiveCode 컴파일 에러
`AngryClawdGame.cs`의 `UpdateCameraZoom()` 메서드에서 3개의 클래스 필드를 사용하지만 선언이 누락됨:
- `cameraYInitialized` (bool)
- `cameraFixedY` (float)
- `cameraYVelocity` (float)

Roslyn 런타임 컴파일러가 `CS0103` 에러를 반복 발생시키며, 핫리로드가 실패하고 있었음.

이 필드들은 `change-camera-y-fixed-projectile-at-lower-third.md` 작업에서 카메라 Y 로직이 변경될 때 필드 선언이 누락된 것으로 추정.

### 부차 원인: 컴파일 에러가 사용자에게 보이지 않음
- `ScriptCompiler`가 `EditorDebug.LogError()`로 컴파일 에러를 출력하는데, 이 로그는 `editor_*.log`에만 기록됨
- 사용자가 확인한 `ironrose_*.log`(`Debug.Log` 출력 대상)에는 컴파일 에러가 기록되지 않음
- 따라서 사용자는 핫리로드 실패 원인을 알 수 없었음

## 수정 내용
### 1. AngryClawdGame.cs (MyGame/LiveCode/AngryClawd/)
- 누락된 3개 필드 선언 추가: `cameraYVelocity`, `cameraYInitialized`, `cameraFixedY`
- (이 수정은 사용자 측에서 이미 적용됨)

### 2. LiveCodeManager.cs (src/IronRose.Engine/)
- `[HotReload:DIAG]` 진단 로그 5개 제거 (이전 디버깅 세션에서 남겨진 것)
- `OnLiveCodeChanged()`에 `[Scripting]` 카테고리의 정상 로그 1개 추가 (파일 변경 감지 기록용)

## 변경된 파일
- `src/IronRose.Engine/LiveCodeManager.cs` -- [HotReload:DIAG] 진단 로그 제거, [Scripting] 정상 로그 추가

## 검증
- dotnet build 성공 확인 (0 Error)
- 실제 핫리로드 동작은 유저 확인 필요 (에디터에서 LiveCode 수정 후 반영되는지)

## 참고: 잠재적 개선 사항
1. **컴파일 에러를 사용자 로그에도 기록**: `ScriptCompiler`의 에러 로그가 `EditorDebug.LogError()`만 사용하여 `editor_*.log`에만 기록됨. `Debug.LogError()` 또는 별도 메커니즘으로 `ironrose_*.log`에도 기록하면 사용자가 컴파일 에러를 쉽게 확인 가능.
2. **ExecuteReload()에서 컴파일 실패 시 MigrateEditorComponents() 스킵**: 현재 코드는 `CompileAllLiveCode()` 실패 후에도 `MigrateEditorComponents()`를 호출함. 컴파일 성공 여부를 반환하여 실패 시 마이그레이션을 건너뛰어야 함.
