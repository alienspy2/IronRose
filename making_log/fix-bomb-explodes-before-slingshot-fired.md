# 슬링샷 발사 전 폭탄이 즉시 폭발하는 버그 수정

## 유저 보고 내용
- 게임 시작 직후 폭탄(Bomb)이 바로 터져버리는 경우가 있음
- 슬링샷을 쏘기 전까지는 폭탄이 터지지 않아야 함

## 원인
- `BombScript.OnCollisionEnter`에서 `collision.relativeVelocity.magnitude >= TRIGGER_SPEED(2.0f)` 조건만으로 폭발을 판정하고 있었음
- 게임 시작 직후 물리 시뮬레이션이 시작되면서 블록들이 중력에 의해 서로 부딪히고, 이 때 블록-폭탄 간 충돌 속도가 TRIGGER_SPEED를 넘으면 슬링샷 발사 전에도 폭탄이 폭발했음
- 슬링샷 발사 여부와 무관하게 충돌 속도만으로 폭발을 판정하는 것이 근본 원인

## 수정 내용
- `AngryClawdGame`에 `public static bool HasFired` 프로퍼티를 추가하여 슬링샷 발사 상태를 전역으로 노출
- `Fire()` 호출 시 `HasFired = true` 설정
- `Start()`, `ClearStage()`, `OnRestartClicked()` 에서 `HasFired = false`로 리셋
- `BombScript.OnCollisionEnter`에서 `AngryClawdGame.HasFired`가 false이면 충돌 폭발을 무시하도록 가드 추가
- `Explode()` 직접 호출(CannonballScript 경유)은 발사 후에만 일어나므로 별도 가드 불필요

## 변경된 파일
- `Scripts/AngryClawd/AngryClawdGame.cs` -- `HasFired` static 프로퍼티 추가, Fire/Start/ClearStage/OnRestartClicked에서 상태 관리
- `Scripts/AngryClawd/BombScript.cs` -- OnCollisionEnter에 `!AngryClawdGame.HasFired` 가드 추가, 헤더 주석 보완

## 검증
- 정적 분석으로 원인 특정. 빌드 성공 확인 (0 Warning, 0 Error).
- 유저에게 실행 테스트 검증 요청 필요.
