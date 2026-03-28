# 플레이모드 중 핫 리로드 보류 및 Stop 시 수행

## 유저 보고 내용
- 플레이모드 중에 LiveCode를 수정하면 핫 리로드가 시도되지만 제대로 동작하지 않음
- 플레이모드 중에는 리로드를 보류하고, 플레이모드 종료(play.stop) 시 수행하도록 요청

## 원인
- `LiveCodeManager.ProcessReload()`가 매 프레임 무조건 실행되어, 플레이모드 중에도 Roslyn 재컴파일 + 컴포넌트 마이그레이션이 즉시 수행됨
- 플레이모드 중 컴포넌트 타입이 교체되면 씬 스냅샷과 불일치가 발생하여 Stop 시 복원이 깨질 수 있음
- 플레이모드 중 `IHotReloadable` 상태 저장/복원도 불완전할 수 있음

## 수정 내용

### LiveCodeManager.cs
- `_pendingReloadAfterPlayStop` 플래그 추가
- `ProcessReload()`에서 `EditorPlayMode.IsInPlaySession`이 true이면 실제 리로드를 보류하고 플래그만 설정
- `FlushPendingReload()` 메서드 추가: 보류 중인 리로드를 수행
- `ExecuteReload()` private 메서드로 실제 리로드 로직 추출
- 플레이모드 중에는 파일 변경 감지는 계속하되(`_reloadRequested` 설정), 리로드 실행은 보류

### EditorPlayMode.cs
- `OnAfterStopPlayMode` 콜백 추가 (기존 `OnResetFixedAccumulator`와 동일한 패턴)
- `StopPlayMode()` 마지막에 `OnAfterStopPlayMode?.Invoke()` 호출 (씬 복원 완료 후)

### EngineCore.cs
- `InitLiveCode()`에서 `EditorPlayMode.OnAfterStopPlayMode`에 `LiveCodeManager.FlushPendingReload()` 콜백 등록
- `ProcessReload()` 호출 주석을 실제 동작에 맞게 업데이트

## 변경된 파일
- `src/IronRose.Engine/LiveCodeManager.cs` -- 플레이모드 중 리로드 보류 및 FlushPendingReload 추가
- `src/IronRose.Engine/Editor/EditorPlayMode.cs` -- OnAfterStopPlayMode 콜백 추가
- `src/IronRose.Engine/EngineCore.cs` -- 콜백 등록 및 주석 업데이트

## 동작 흐름
1. 플레이모드 진입
2. LiveCode 파일 수정 -> FileSystemWatcher가 감지 -> `_reloadRequested = true`
3. `ProcessReload()` 호출 -> `IsInPlaySession == true` -> `_pendingReloadAfterPlayStop = true`, 리로드 보류
4. 플레이모드 중 추가 파일 수정이 있어도 pending 플래그만 유지
5. play.stop -> `StopPlayMode()` -> 씬 복원 -> `OnAfterStopPlayMode` -> `FlushPendingReload()` -> 리로드 수행

## 검증
- 빌드 성공 확인 (0 Error, 기존 경고만 존재)
- 실제 플레이모드 진입/파일 수정/Stop 시나리오는 유저 확인 필요
