# 핫 리로드 디바운스를 Trailing Edge 방식으로 변경 (여러 파일 동시 변경 대응)

## 유저 보고 내용
- 새로 추가한 `ExplosionVfxScript.cs`와 수정한 `BombScript.cs`가 핫 리로드에 반영되지 않음
- 빌드는 성공했지만 에디터에서 핫 리로드가 동작하지 않는 상황

## 원인
`OnLiveCodeChanged()`의 디바운스가 Leading Edge 방식이었다:
1. 첫 번째 FileSystemWatcher 이벤트가 발생하면 즉시 `_reloadRequested = true` 설정
2. 이후 1초간 모든 추가 이벤트를 무시 (디바운싱)
3. 다음 프레임에서 `ProcessReload()` -> `CompileAllLiveCode()` 즉시 실행

문제 시나리오:
- 새 파일 생성 + 기존 파일 수정이 거의 동시에 발생
- 첫 번째 이벤트(Created)에서 `_reloadRequested = true` 설정, 디바운스 타이머 시작
- 다음 프레임(~16ms 후)에서 즉시 `CompileAllLiveCode()` 실행
- 이 시점에 두 번째 파일이 아직 완전히 기록되지 않았을 수 있음 (파일 잠금으로 IOException -> 스킵)
- 또는 두 번째 파일의 Changed 이벤트가 디바운싱으로 무시됨
- 컴파일 실패 후 재시도 메커니즘이 없으므로 핫 리로드가 실패 상태로 남음

## 수정 내용
디바운스를 **Trailing Edge** 방식으로 변경:
- `_lastReloadTime` -> `_lastFileChangeTime`: 마지막 파일 변경 감지 시간
- `DEBOUNCE_SECONDS = 0.5`: 디바운스 대기 시간
- `OnLiveCodeChanged()`: 이벤트마다 `_lastFileChangeTime`을 리셋하고 `_reloadRequested = true` 설정 (이전의 leading edge 필터 제거)
- `ProcessReload()`: `_reloadRequested`가 true여도 `_lastFileChangeTime` 이후 0.5초가 경과하지 않으면 대기. 0.5초 경과 후에만 리로드 실행

수정 후 동작:
1. 파일 A 변경 -> `_lastFileChangeTime = T0`, `_reloadRequested = true`
2. 파일 B 변경 (T0 + 0.1초) -> `_lastFileChangeTime = T0.1` (리셋됨)
3. 매 프레임: `ProcessReload()` -> `elapsed < 0.5` -> 대기
4. T0.1 + 0.5초 후: `elapsed >= 0.5` -> `CompileAllLiveCode()` 실행 -> 모든 파일이 기록 완료된 상태

## 변경된 파일
- `src/IronRose.Engine/LiveCodeManager.cs` -- 디바운스를 trailing edge 방식으로 변경. `_lastReloadTime` -> `_lastFileChangeTime`, `DEBOUNCE_SECONDS` 상수 추가, `OnLiveCodeChanged()` leading edge 필터 제거, `ProcessReload()` trailing edge 대기 로직 추가

## 검증
- 빌드 성공 확인 (0 Error)
- 실제 핫 리로드 동작은 에디터에서 Play 모드 테스트 필요 (유저 확인)
