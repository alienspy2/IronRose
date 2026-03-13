# 플레이모드 물리 상태 리셋 수정

## 유저 보고 내용
- 플레이모드에 들어갔다가 종료하면 Rigidbody가 원래 위치로 돌아가지 않는 버그
- Unity 기준: 플레이모드 진입 시 씬 백업, 종료 시 복원하여 모든 오브젝트가 원래 위치/회전/스케일로 복원되어야 함

## 원인
정적 분석과 자동화 테스트(physics_test.scene)로 확인한 결과, 씬 직렬화/역직렬화 경로 자체는 올바르게 동작:
- `EnterPlayMode()` -> `SceneSerializer.SaveToString()` -- localPosition/localRotation/localScale 백업
- `StopPlayMode()` -> `SceneSerializer.LoadFromString()` -> `SceneManager.Clear()` + 씬 재구축 -- 원래 값 복원

그러나 두 가지 물리 상태 관련 누락이 발견됨:

1. **`_fixedAccumulator` 리셋 누락**: `EngineCore._fixedAccumulator`가 Play/Stop 시 리셋되지 않아, 이전 Play 세션의 잔여 물리 시간이 다음 세션에 누적될 수 있음. 이로 인해 다음 Play 진입 시 첫 프레임에서 예상보다 많은 물리 스텝이 실행되어 오브젝트가 비정상적으로 이동할 수 있음.

2. **Play 진입 시 물리 월드 미리셋**: `EnterPlayMode()` 시 물리 월드(BepuPhysics Simulation)를 리셋하지 않아, 이전 세션에서 남은 body handle이나 물리 상태가 새 세션에 영향을 줄 수 있음.

## 수정 내용

### EditorPlayMode.cs
- `EnterPlayMode()`에 `PhysicsManager.Instance?.Reset()` 추가 -- Play 진입 시 물리 월드를 깨끗하게 초기화
- `EnterPlayMode()`과 `StopPlayMode()`에 `OnResetFixedAccumulator?.Invoke()` 호출 추가 -- EngineCore의 `_fixedAccumulator`를 리셋
- `OnResetFixedAccumulator` 콜백 프로퍼티 추가 (EngineCore가 등록)

### EngineCore.cs
- `Initialize()`에서 `EditorPlayMode.OnResetFixedAccumulator = () => _fixedAccumulator = 0;` 등록
- EditorPlayMode이 EngineCore 인스턴스에 직접 접근할 수 없으므로 콜백 패턴 사용

## 변경된 파일
- `src/IronRose.Engine/Editor/EditorPlayMode.cs` -- 물리 월드 리셋, _fixedAccumulator 리셋 콜백 추가
- `src/IronRose.Engine/EngineCore.cs` -- _fixedAccumulator 리셋 콜백 등록

## 검증
- `dotnet build` -- 빌드 성공 (경고 0, 오류 0)
- 자동화 테스트(physics_test.scene, play_mode enter -> wait 3s -> stop -> quit)로 물리 Reset 로그 확인
- 씬 복원 시 Transform 값이 원래 값으로 정확히 복원됨을 진단 로그로 확인
- 유저 환경에서의 최종 검증 필요 (Rigidbody가 자유 낙하하는 씬에서 확인)

## 후속 수정 (fix-playmode-prefab-rigidbody-restore.md)
- 이 수정만으로는 프리팹 기반 Rigidbody 씬에서 문제가 해결되지 않았음
- 프리팹 템플릿 GO의 Rigidbody가 물리 시뮬레이션에 참여하여 원본 Transform이 오염되는 별도의 근본 원인이 있었음
- `PhysicsManager.IsActiveBody()`에서 `_isEditorInternal` GO를 제외하는 수정으로 해결
