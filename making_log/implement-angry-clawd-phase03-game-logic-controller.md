# AngryClawd Phase 03 - AngryClawdGame 게임 로직 구현

## 수행한 작업
- `AngryClawdGame.cs`를 빈 스텁에서 전체 게임 로직으로 전면 재작성
- 스테이지 생성: pile prefab을 stageNum에 따라 1~5개 인스턴스화
- Slingshot 발사: 마우스 드래그로 방향/세기 결정, Rigidbody Impulse 발사
- Cannonball 추적: 타임아웃(8초), 낙하(y < -10) 감지 후 리스폰
- 스테이지 클리어 판정: "Pig" 태그 오브젝트 전멸 확인 후 2초 딜레이 → 다음 스테이지

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` — 빈 스텁 → 전체 게임 컨트롤러 (257줄)

## 주요 결정 사항
- `Object.Destroy` → `RoseEngine.Object.Destroy`로 명시적 한정: ImplicitUsings가 활성화되어 System.Object와 충돌
- `Random.Range` → `RoseEngine.Random.Range`로 명시적 한정: 같은 이유
- UTF-8 BOM 수동 추가: Write 도구가 BOM 없이 저장하므로 printf + cat으로 BOM 삽입
- 명세서의 코드를 그대로 사용하되, 위 두 가지 네임스페이스 충돌만 수정

## 다음 작업자 참고
- MyGame 디렉토리는 git 저장소가 아님. 커밋 불가.
- Phase 02(CannonballScript 등)가 이미 존재해야 빌드 성공함
- 에디터에서 Play 모드로 실제 동작 테스트 필요:
  - shooter GO가 씬에 (-24.5, 0, 0) 위치에 존재하는지 확인
  - pile/cannonball prefab GUID가 유효한지 확인
  - "Pig" 태그가 pig 오브젝트에 설정되어 있는지 확인
