# AngryClawd 게임 시스템

## 구조
- `AngryClawdGame.cs` (SimpleGameBase) — 메인 컨트롤러. 스테이지/입력/판정 관리
- `PileScript.cs` (MonoBehaviour) — pile(구조물 묶음) 동작
- `BlockScript.cs` (MonoBehaviour) — 개별 블록 물리/체력
- `PigScript.cs` (MonoBehaviour) — 적(pig) 체력/파괴
- `BombScript.cs` (MonoBehaviour) — 폭탄 폭발 로직
- `CannonballScript.cs` (MonoBehaviour) — 포탄 충돌 처리
- `ExplosionVfxScript.cs` (MonoBehaviour) — 폭발 VFX 스피어 (빨간색, scale 축소 후 자동 제거)
- `DebrisVfxScript.cs` (MonoBehaviour) — 블록 파괴 시 작은 큐브 부스러기 VFX (물리 없이 흩뿌려지며 scale 축소 후 자동 제거). BlockScript.SpawnDebris()에서 호출.
- `SimpleGameBase.cs` (MonoBehaviour) — 빈 베이스 클래스 (Start/Update 가상 메서드)

## 핵심 동작
1. **Start**: SetupStage(1) → pile prefab 인스턴스화, SpawnCannonball() → shooter 위치에 cannonball 생성
2. **Update 루프**:
   - stageClearing 중이면 UpdateCameraZoom() + 딜레이 대기 후 NextStage()
   - HandleAiming(): 마우스 드래그 시작/끝 감지
   - Fire(): 드래그 벡터 → Impulse 발사 (Y축 부호 반전 주의)
   - TrackCannonball(): 타임아웃/낙하 → 리스폰
   - CheckStageClear(): "Pig" 태그 오브젝트 0개 → 클리어
   - UpdateCameraZoom(): 카메라 자동 줌 (아래 참조)
3. **스테이지 진행**: currentStage++ → SetupStage() → SpawnCannonball()
4. **카메라 자동 줌**: shooter와 모든 pile 자식 블록의 X 바운딩 박스를 계산하고, perspective FOV 기반으로 필요한 Z 거리를 역산하여 SmoothDamp로 부드럽게 이동. X는 바운딩 중심에 맞추고, **Y는 지면(Y=0)이 화면 하단 1/3에 오도록 FOV 기반으로 고정** (포탄을 Y 방향으로 추적하지 않음).

## 주의사항
- **네임스페이스 충돌**: ImplicitUsings로 System 네임스페이스가 자동 포함됨. `Object.Destroy`는 반드시 `RoseEngine.Object.Destroy`, `Random.Range`는 `RoseEngine.Random.Range`로 한정해야 함.
- **마우스 좌표계**: IronRose의 Input.mousePosition은 좌상단 원점, Y 아래로 증가 (Silk.NET 기반). 드래그 delta의 Y를 부호 반전해야 월드 Y(위=양수)와 매핑됨.
- **fake null 패턴**: `currentCannonball == null`은 Unity와 동일하게 Destroy 후 fake null을 감지함.
- **Physics.OverlapSphere 주의**: 범위 내 오브젝트를 파괴할 때 반드시 태그 기반 필터링 필요. 태그 없이 전체 파괴하면 Cannonball, Floor/Ground 등 의도하지 않은 오브젝트까지 파괴됨.
- **prefab GUID**: pile=`da096309-223c-488c-b39a-c5f62ba55fe0`, cannonball=`0804bc30-df11-4fd2-89a5-5265d7180ff2`
- **shooter GO**: 씬에 "shooter" 이름의 GO가 (-24.5, 0, 0)에 존재해야 함. 없으면 cannonball 스폰 실패.

## 충돌 이벤트 디스패치 (OnCollisionEnter 등)
- BepuPhysics의 NarrowPhaseCallbacks에서 접촉 쌍을 ContactEventCollector로 수집
- PhysicsWorld3D.Step() 후 이전/현재 프레임 비교로 Enter/Stay/Exit 분류
- PhysicsManager.DispatchCollisionEvents3D()에서 MonoBehaviour 콜백 호출
- UserData 매핑: Rigidbody는 bodyHandle -> this, Collider(static)는 staticHandle -> this
- relativeVelocity는 양쪽 body의 linear velocity 차이로 계산

## 사용하는 외부 라이브러리
- 없음 (RoseEngine 내장 API만 사용)
