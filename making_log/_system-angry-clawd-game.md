# AngryClawd 게임 시스템

## 구조
- `AngryClawdGame.cs` (SimpleGameBase) — 메인 컨트롤러. 스테이지/입력/판정 관리
- `PileScript.cs` (MonoBehaviour) — pile(구조물 묶음) 동작
- `BlockScript.cs` (MonoBehaviour) — 개별 블록 물리/체력
- `PigScript.cs` (MonoBehaviour) — 적(pig) 체력/파괴
- `BombScript.cs` (MonoBehaviour) — 폭탄 폭발 로직
- `CannonballScript.cs` (MonoBehaviour) — 포탄 충돌 처리
- `SimpleGameBase.cs` (MonoBehaviour) — 빈 베이스 클래스 (Start/Update 가상 메서드)

## 핵심 동작
1. **Start**: SetupStage(1) → pile prefab 인스턴스화, SpawnCannonball() → shooter 위치에 cannonball 생성
2. **Update 루프**:
   - stageClearing 중이면 딜레이 대기 후 NextStage()
   - HandleAiming(): 마우스 드래그 시작/끝 감지
   - Fire(): 드래그 벡터 → Impulse 발사 (Y축 부호 반전 주의)
   - TrackCannonball(): 타임아웃/낙하 → 리스폰
   - CheckStageClear(): "Pig" 태그 오브젝트 0개 → 클리어
3. **스테이지 진행**: currentStage++ → SetupStage() → SpawnCannonball()

## 주의사항
- **네임스페이스 충돌**: ImplicitUsings로 System 네임스페이스가 자동 포함됨. `Object.Destroy`는 반드시 `RoseEngine.Object.Destroy`, `Random.Range`는 `RoseEngine.Random.Range`로 한정해야 함.
- **마우스 좌표계**: IronRose의 Input.mousePosition은 좌상단 원점, Y 아래로 증가 (Silk.NET 기반). 드래그 delta의 Y를 부호 반전해야 월드 Y(위=양수)와 매핑됨.
- **fake null 패턴**: `currentCannonball == null`은 Unity와 동일하게 Destroy 후 fake null을 감지함.
- **prefab GUID**: pile=`da096309-223c-488c-b39a-c5f62ba55fe0`, cannonball=`0804bc30-df11-4fd2-89a5-5265d7180ff2`
- **shooter GO**: 씬에 "shooter" 이름의 GO가 (-24.5, 0, 0)에 존재해야 함. 없으면 cannonball 스폰 실패.

## 사용하는 외부 라이브러리
- 없음 (RoseEngine 내장 API만 사용)
