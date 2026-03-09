# CharacterController 시스템

## 구조
- `CollisionFlags.cs` — Move() 반환값 열거형 (None, Sides, Above, Below)
- `ControllerColliderHit.cs` — 충돌 콜백 전달용 데이터 클래스
- `CharacterController.cs` — Collider를 상속하는 키네마틱 캐릭터 컨트롤러 컴포넌트
- `MonoBehaviour.cs` — OnControllerColliderHit 콜백 정의
- `PhysicsWorld3D.cs` — SweepCapsule/OverlapCapsule API, UserData 매핑 (Phase 1에서 추가)
- `PhysicsManager.cs` — FixedUpdate에서 CC의 static body pose 동기화 + CC+RB 경고

## 핵심 동작
- CharacterController는 Collider를 상속하므로 `_allColliders`에 자동 등록됨
- Rigidbody 없이 사용하며, RegisterAsStatic으로 캡슐 형상 static body 등록 + SetStaticUserData로 자기 자신 등록
- Move()는 sweep 기반 충돌 감지 이동 (Phase 3+4+5 구현 완료):
  0. enableOverlapRecovery=true이면 PerformOverlapRecovery() 호출 (겹침 보정)
  1. motion 크기 < minMoveDistance이면 조기 리턴
  2. 최대 3회 slide iteration으로 충돌 감지 + 슬라이딩
  3. sweepRadius = scaledRadius - skinWidth로 sweep, 충돌 시 skinWidth만큼 뒤에서 멈춤
  4. 법선 dot(up) 기반 CollisionFlags 판정: >0.7=Below, <-0.7=Above, 그 외=Sides
  5. Slope: 바닥 충돌이면서 경사각 > slopeLimit → 수평 방향으로 슬라이딩
  6. Step: Sides 충돌 + isGrounded + stepOffset 이내 → 위-앞-아래 3단 sweep으로 계단 오르기
  7. 일반 슬라이딩: 법선 방향 성분 제거 (remainingMotion - dot(remaining, normal) * normal)
  8. 완료 후 static body pose를 SetStaticPose로 동기화
- SimpleMove()는 XZ speed * deltaTime + Physics.gravity 기반 수직 속도 누적 → Move() 호출
  - isGrounded 시 _simpleMoveVerticalSpeed를 -0.5f로 리셋 (바닥 접착)
- 충돌 시 같은 GameObject의 MonoBehaviour들에 OnControllerColliderHit 콜백 발송
  - GetUserData로 CollidableReference에서 Collider 또는 Rigidbody 추출
  - UserData가 없으면 콜백 생략
- PerformOverlapRecovery(): OverlapCapsule로 겹침 감지 후 위 방향으로 skinWidth만큼 밀어내기 (최대 4회)

## PhysicsManager 통합 (Phase 6)
- PushTransformsToPhysics() 마지막에 SyncCharacterControllerPoses() 호출
  - _allColliders를 순회하여 CC인 경우 SyncStaticPose() 호출
  - SyncStaticPose()는 CC의 internal 메서드로 protected GetWorldPosition/GetWorldRotation 접근
- EnsureStaticColliders()에서 CC + Rigidbody 조합 감지 시 LogWarning 출력

## PhysicsWorld3D Sweep/UserData 인프라 (Phase 1)
- `SweepHit` 구조체: T(0~1), Normal, HitPosition, CollidableReference
- `ClosestHitHandler`: ISweepHitHandler 구현, 가장 가까운 충돌만 수집, 자기 Body/Static 제외 가능
- `OverlapCollectHandler`: ISweepHitHandler 구현, 겹치는 collidable 수집
- `SweepCapsule()`: sweepDuration=1, velocity=direction*maxDistance 방식으로 Simulation.Sweep 호출
- `OverlapCapsule()`: 매우 짧은 거리 sweep으로 겹침 검출 (간단한 구현)
- `SetStaticPose()`: static body 위치/회전 직접 설정
- UserData 매핑: `_bodyUserData`/`_staticUserData` Dictionary<int, object>
  - Collider 서브클래스의 RegisterAsStatic에서 `SetStaticUserData(handle, this)` 호출
  - Rigidbody의 RegisterWithPhysics에서 `SetBodyUserData(handle, this)` 호출
  - `GetUserData(CollidableReference)`: Static/Dynamic 구분하여 매핑된 오브젝트 반환
  - RemoveBody/RemoveStatic/Reset에서 자동 정리

## 클래스 간 의존 관계
```
Component -> Collider -> CharacterController
                          |-- uses CollisionFlags (반환값)
                          |-- uses ControllerColliderHit (콜백 데이터)
                          |-- uses Physics.gravity, Time.deltaTime (SimpleMove)
                          +-- uses PhysicsManager / PhysicsWorld3D (물리 등록/sweep/overlap)
MonoBehaviour -- OnControllerColliderHit(ControllerColliderHit) 콜백

PhysicsManager
  |-- SyncCharacterControllerPoses() -> CharacterController.SyncStaticPose()
  +-- EnsureStaticColliders() -> CC+RB 경고 감지

PhysicsWorld3D
  |-- SweepHit (결과 구조체)
  |-- ClosestHitHandler (ISweepHitHandler, 내부)
  |-- OverlapCollectHandler (ISweepHitHandler, 내부)
  +-- UserData 매핑 <- Collider.RegisterAsStatic, Rigidbody.RegisterWithPhysics에서 등록
```

## 구현 상태 (Phase 6 완료 - 전체 완료)
- [x] Phase 1: Sweep API + UserData 매핑 (PhysicsWorld3D)
- [x] Phase 2: 타입 정의 + 기본 골격 (stub)
- [x] Phase 3: Move() 핵심 로직 (sweep, 슬라이딩, 충돌 플래그)
- [x] Phase 4: 슬로프 + 스텝 처리
- [x] Phase 5: SimpleMove() + Overlap Recovery
- [x] Phase 6: PhysicsManager 통합 + CC+RB 경고

## 주의사항
- CharacterController가 있는 GameObject에는 Rigidbody를 추가하면 안 됨 (Unity 규칙) — PhysicsManager에서 경고 출력
- `_kinematicHandle`과 `_staticHandle`은 현재 동일값 — Move() 완료 후 SetStaticPose로 동기화
- Move() 완료 후 static body pose를 SetStaticPose로 갱신하므로, 다음 물리 스텝에서 올바른 위치 사용
- Rigidbody는 UserData로 자기 자신(Rigidbody)을 설정, Collider는 자기 자신(Collider)을 설정 — GetUserData 결과의 타입 체크로 구분
- FireControllerColliderHit에서 UserData가 null이거나 알 수 없는 타입이면 콜백 생략 (silent fail)
- Move()의 sweep 충돌 시 dynamic body가 sleep 상태이면 WakeBody()로 깨움 — 이 없으면 OnControllerColliderHit에서 힘을 가해도 body가 반응하지 않음
- leftoverMotion 계산 시 반드시 safeDistance(실제 position 이동량)를 사용해야 함 — usedDist(hit.T * remainingDist)를 사용하면 skinWidth만큼 매 iteration 모션이 소실되어 벽 비비기 후 속도 저하 발생 (fix-cc-wall-slide-speed-loss 참조)
- 이전 프레임 접촉 법선 보존: `_prevContactCount`/`_prevContactNormal0`/`_prevContactNormal1` 필드로 벽(Sides) 법선을 프레임 간 유지. Move() 시작 시 벽 방향 모션 성분을 사전 제거하여 jitter 방지 (fix-cc-wall-jitter-v2 참조)
  - **벽(Sides) 법선만 보존**: 바닥/천장 법선을 보존하면 다음 프레임에서 중력 모션이 소거되어 isGrounded 판정이 매 2프레임 주기로 진동함
  - **법선 유지 조건**: 현재 프레임에서 벽 충돌이 없고 원래 motion이 벽을 향하고 있으면, 벽 법선 반대 방향으로 probe sweep(skinWidth*3 거리)을 수행하여 벽 존재 여부를 확인. 벽이 있으면 유지, 없으면 클리어. 원래 motion이 벽을 향하지 않으면 즉시 리셋. (이전에는 probe 없이 무조건 유지했으나, 벽 경계를 넘은 후에도 법선이 유지되어 허공 슬라이딩 버그 발생 — fix-cc-wall-slide-beyond-boundary 참조)
  - **Dynamic body 법선은 수집하지 않음**: dynamic body(Rigidbody)는 이동하므로 이전 프레임 법선이 다음 프레임에서 유효하지 않음. 수집하면 대상이 밀려 떠난 후에도 "보이지 않는 벽"이 생기는 버그 발생 (fix-cc-ghost-wall-after-pushing-dynamic 참조)
- Overlap Recovery는 현재 단순히 위 방향으로 밀어내는 방식 — 옆벽 끼임에는 효과 제한적
- OverlapCollectHandler는 Span을 필드로 가질 수 없어 배열 버퍼 + 복사 방식 사용
- SyncCharacterControllerPoses()는 매 FixedUpdate마다 _allColliders 순회 — CC가 많으면 별도 리스트 관리 고려
- 설계 문서: `plans/character-controller.md`

## 사용하는 외부 라이브러리
- BepuPhysics v2.4.0 — Simulation.Sweep, ISweepHitHandler, Capsule shape, CollidableReference, StaticHandle/BodyHandle
