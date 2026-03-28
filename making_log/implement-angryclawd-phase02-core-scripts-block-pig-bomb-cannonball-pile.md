# AngryClawd Phase 02: 핵심 스크립트 구현

## 수행한 작업
- BlockScript, PigScript, BombScript, CannonballScript 4개 새 스크립트 생성
- PileScript를 빈 클래스에서 큐브 더미 동적 생성 로직으로 전면 재작성
- AngryClawdGame.cs의 Object/Random 네임스페이스 모호성 빌드 에러 수정 (기존 코드 문제)

## 변경된 파일
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/BlockScript.cs` (새 파일) — 블록 간 고속 충돌 시 파괴 처리
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/PigScript.cs` (새 파일) — pig 사망 충돌 판정
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/BombScript.cs` (새 파일) — 폭탄 폭발 처리 (범위 내 오브젝트 삭제)
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/CannonballScript.cs` (새 파일) — cannonball 충돌 시 태그별 판정 분기
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/PileScript.cs` (전면 재작성) — 큐브 더미 동적 생성 (블록/pig/bomb 배치)
- `/home/alienspy/git/MyGame/LiveCode/AngryClawd/AngryClawdGame.cs` (모호성 수정) — Object/Random을 RoseEngine.Object/RoseEngine.Random으로 정규화

## 주요 결정 사항
- `ImplicitUsings`가 활성화되어 있어 `System` 네임스페이스가 자동 포함되므로 `Object`와 `Random`이 `RoseEngine.Object`/`System.Object` 및 `RoseEngine.Random`/`System.Random` 간 모호성 발생. 모든 호출을 `RoseEngine.Object.Destroy()`, `RoseEngine.Random.Range()` 등으로 정규화하여 해결함.
- AngryClawdGame.cs는 Phase 02 명세에서 수정 대상이 아니었으나, 이미 Phase 03 코드가 구현되어 있었고 같은 모호성 에러가 발생하여 빌드 성공을 위해 함께 수정함.

## 다음 작업자 참고
- AngryClawdGame.cs는 Phase 03에서 구현 예정이었으나 이미 구현이 완료된 상태임.
- MyGame 디렉토리는 git 저장소가 아니므로 파일 변경을 직접 커밋할 수 없음.
- 향후 LiveCode 프로젝트에서 RoseEngine 타입을 사용할 때 `ImplicitUsings` 때문에 `Object`, `Random` 등 System과 이름이 겹치는 타입은 항상 `RoseEngine.` 접두사를 붙여야 함.
