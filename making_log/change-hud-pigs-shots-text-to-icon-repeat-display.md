# HUD의 pigs/shots 텍스트를 아이콘 반복 표시로 변경

## 유저 보고 내용
- UI에서 "Pigs: 1", "Shots: 5" 같은 텍스트 표시를 돼지/포탄 아이콘 반복 표시로 변경해야 한다.
- 남은 돼지 갯수만큼 돼지 아이콘, 남은 발사 횟수만큼 포탄 아이콘을 반복 표시해야 한다.

## 수정 내용
- `AngryClawdGame.cs`의 `UpdateUI()` 메서드에서 UIText.text를 설정하던 방식을 아이콘 UIImage GO 동적 생성 방식으로 변경
- 아이콘 스프라이트는 `Resources.GetAssetDatabase().LoadByGuid<Sprite>()`로 sub_asset GUID를 사용하여 로드
- `PigCountText`와 `ShotCountText` GO의 UIText는 빈 문자열로 설정하고, 해당 GO 아래에 자식 UIImage GO를 동적 생성
- 아이콘 갯수가 변경될 때만(lastPigCount/lastShotCount 캐시 비교) 기존 아이콘을 삭제하고 재생성하여 성능 최적화
- `ClearStage()`, `OnRestartClicked()`에서 캐시를 리셋하여 스테이지 전환/재시작 시 아이콘이 올바르게 갱신되도록 함

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` -- UIText 텍스트 표시를 UIImage 아이콘 반복 표시로 변경

## 주요 구현 상세
- 아이콘 크기: 32x32px, 간격: 4px
- pig_icon sprite GUID: `0fdb7e87-a870-4400-8188-e74c1052eada` (sub_asset)
- shot_icon sprite GUID: `9dd19a5c-e442-4a21-9495-019e1a370497` (sub_asset)
- `UpdateIconRow()` 메서드: 부모 GO 중심 기준으로 아이콘을 가로로 균등 배치
- 아이콘 GO는 RectTransform + UIImage 컴포넌트를 가지며, 부모 GO의 자식으로 생성

## 검증
- MyGame LiveCode 빌드 성공 확인
- 실제 동작은 에디터에서 Play 모드로 테스트 필요
