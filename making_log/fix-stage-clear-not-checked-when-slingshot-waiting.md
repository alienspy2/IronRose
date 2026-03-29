# Slingshot 대기 상태에서 스테이지 클리어 체크 안 되는 버그 수정

## 유저 보고 내용
- Slingshot(새총)이 대기 중(waiting) 상태일 때도 스테이지 클리어 조건을 체크해야 하는데, 현재는 체크하지 않고 있음

## 원인
- `AngryClawdGame.CheckStageClear()` 메서드의 첫 번째 가드 조건 `if (!cannonballFired) return;`이 문제
- cannonball이 발사되지 않은 상태(대기 중)에서는 스테이지 클리어 조건 체크를 건너뛰고 즉시 반환함
- 이로 인해 다음 시나리오에서 클리어가 감지되지 않음:
  1. cannonball 발사 후 폭탄 연쇄 반응으로 pig가 점진적으로 파괴됨
  2. cannonball은 타임아웃/낙하로 제거되어 HandleCannonballDone() 호출
  3. SpawnCannonball()에서 cannonballFired = false로 리셋
  4. 새 cannonball이 대기(waiting) 상태이므로 CheckStageClear()에서 !cannonballFired 가드로 return
  5. pig가 0마리임에도 스테이지 클리어 미감지

## 수정 내용
- `CheckStageClear()` 메서드에서 `if (!cannonballFired) return;` 가드 조건을 제거
- cannonball의 발사 여부와 무관하게 pig 수가 0이면 스테이지 클리어를 감지하도록 변경

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` -- CheckStageClear()에서 `!cannonballFired` 가드 제거

## 검증
- 정적 분석으로 원인 특정: 코드 흐름 추적으로 cannonballFired=false 상태에서 CheckStageClear가 조기 반환되는 것을 확인
- dotnet build 성공 확인
