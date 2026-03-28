# OnCollisionEnter 콜백이 물리 엔진에서 호출되지 않는 버그 수정

## 유저 보고 내용
- 포탄(Cannonball)에 맞으면 블럭(Block)이 터져야 하는데, 동작하지 않음
- AngryClawd 게임에서 CannonballScript.OnCollisionEnter가 호출되지 않아 블럭/pig/bomb이 파괴되지 않는 현상

## 원인
- BepuPhysics의 `NarrowPhaseCallbacks`에서 접촉이 발생해도, **충돌 이벤트를 MonoBehaviour.OnCollisionEnter/Stay/Exit로 디스패치하는 코드가 전혀 구현되어 있지 않았음**
- `NarrowPhaseCallbacks.ConfigureContactManifold`에서 material properties만 설정하고, 접촉 쌍을 수집하여 콜백을 호출하는 로직이 누락되어 있었음
- 즉, 물리 시뮬레이션은 정상 동작하지만 (충돌 응답으로 물체가 밀림), 게임 스크립트에 충돌 이벤트가 전달되지 않아 충돌 기반 로직이 모두 동작하지 않았음

## 수정 내용

### 1. ContactEventCollector 추가 (PhysicsWorld3D.cs)
- `NarrowPhaseCallbacks`에서 멀티스레드 안전하게 접촉 쌍을 수집하는 `ContactEventCollector` 클래스 추가
- 이전 프레임과 현재 프레임의 접촉 쌍을 비교하여 Enter/Stay/Exit 이벤트를 분류하는 `Flush()` 메서드 구현
- CollidableReference를 정수 ID로 변환 (Dynamic body는 양수, Static body는 음수)

### 2. NarrowPhaseCallbacks 수정 (PhysicsWorld3D.cs)
- `ContactEventCollector` 참조를 받도록 생성자 추가
- `ConfigureContactManifold`에서 접촉 수(manifold.Count > 0)가 있을 때 `RecordContact()` 호출

### 3. PhysicsWorld3D에 이벤트 조회 API 추가
- `EnteredPairs`, `StayingPairs`, `ExitedPairs` — Step 후 각 카테고리의 접촉 쌍 조회
- `GetUserDataByContactId()` — 정수 ID로 UserData(Rigidbody/Collider) 조회
- `GetVelocityByContactId()` — 정수 ID로 body의 linear velocity 조회 (relativeVelocity 계산용)

### 4. PhysicsManager에 충돌 콜백 디스패치 추가
- `DispatchCollisionEvents3D()` — Step 후 Enter/Stay/Exit 이벤트를 순회하며 MonoBehaviour 콜백 호출
- `DispatchCollisionCallback3D()` — UserData에서 GameObject를 찾고, 상대 속도를 계산하여 Collision 객체 생성, 양쪽 GO에 콜백 fire
- `FireCollisionOnGameObject()` — 해당 GO의 모든 MonoBehaviour에 대해 OnCollisionEnter/Stay/Exit 호출 (enabled + _hasAwoken 체크)
- 파괴된 GO/Component에 대한 안전 검사 포함

## 변경된 파일
- `src/IronRose.Physics/PhysicsWorld3D.cs` — ContactEventCollector 클래스 추가, NarrowPhaseCallbacks에 collector 참조 및 접촉 기록 로직 추가, PhysicsWorld3D에 이벤트 결과 필드/메서드 추가, Initialize/Reset에서 collector 연결/초기화
- `src/IronRose.Engine/Physics/PhysicsManager.cs` — DispatchCollisionEvents3D, DispatchCollisionCallback3D, ResolveGameObject, FireCollisionOnGameObject 메서드 추가, FixedUpdate에서 Step 후 디스패치 호출

## 검증
- IronRose 엔진 빌드 성공 (0 Error)
- MyGame LiveCode 빌드 성공 (0 Error, 0 Warning)
- 유저 실행 테스트 필요: 에디터에서 Play 모드 진입 후 포탄을 발사하여 블럭이 파괴되는지 확인
