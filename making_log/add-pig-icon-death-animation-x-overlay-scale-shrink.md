# HUD 돼지 아이콘 죽음 애니메이션 추가

## 유저 보고 내용
- 돼지가 죽었을 때 HUD의 돼지 아이콘이 즉시 사라지는 대신, 죽음 연출이 필요하다.
- 죽은 돼지 아이콘에 "X" 표시, 0.5초 대기, scale 축소 애니메이션, 완료 후 제거.

## 원인
- 기존 `UpdateIconRow()`는 count가 변경되면 모든 아이콘을 삭제하고 새로 생성하는 방식이어서 애니메이션 적용이 불가능했다.

## 수정 내용
- `UpdateUI()`에서 돼지 count가 줄어드는 경우를 감지하여 `AnimatePigIconDeath()`를 호출하도록 변경
- `AnimatePigIconDeath()`: 살아있는 아이콘 중 뒤쪽부터 죽은 수만큼:
  - 아이콘 색상을 빨간색 틴트 (1, 0.3, 0.3, 1)로 변경
  - "X" 텍스트 오버레이 자식 GO 추가 (빨간색, fontSize = ICON_SIZE * 0.8)
  - 코루틴 시작
- `PigIconDeathCoroutine()` 코루틴:
  - 0.5초 대기 (WaitForSeconds)
  - 0.3초간 localScale을 1에서 0으로 Lerp 축소 애니메이션
  - 완료 시 GO 삭제 + `pigIconGOs` 리스트에서 제거
  - `RepositionPigIcons()`로 남은 아이콘 중앙 재정렬
- `dyingPigIconCount` 필드로 애니메이션 중인 아이콘 수를 추적하여, dying 아이콘과 alive 아이콘을 구분
- `ClearStage()`, `OnRestartClicked()`에서 `dyingPigIconCount` 리셋 추가
- 초기화/스테이지 전환(lastPigCount == -1)이나 count 증가 시에는 기존 전체 재생성 방식 유지

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` -- 돼지 아이콘 죽음 애니메이션 로직 추가 (AnimatePigIconDeath, PigIconDeathCoroutine, RepositionPigIcons)

## 검증
- LiveCode 빌드 성공 확인
- 실제 동작은 에디터에서 Play 모드로 테스트 필요
