# CharacterController 구현 설계

## 배경
- IronRose 엔진은 Unity 호환 API를 제공하는 게임 엔진이다
- 캐릭터 이동을 위한 CharacterController가 아직 구현되어 있지 않다
- Unity의 CharacterController는 3D 게임에서 가장 빈번하게 사용되는 핵심 컴포넌트 중 하나이다
- 기존 Rigidbody 기반 물리 시스템과 독립적으로 동작하는 키네마틱 캐릭터 컨트롤러가 필요하다

## 목표
- Unity CharacterController와 완전히 동일한 public API 제공
- BepuPhysics v2.4.0의 Sweep API를 활용한 충돌 감지
- 슬로프 처리, 스텝 오프셋, 중력 등 Unity와 동일한 물리 동작
- Inspector에서 프로퍼티 편집 가능
- 씬 직렬화/역직렬화 지원

## 현재 상태

### 물리 시스템 구조
```
IronRose.Physics (PhysicsWorld3D.cs)
  - BepuPhysics v2.4.0 Simulation 래핑
  - Dynamic/Static/Kinematic body 추가/제거
  - Pose/Velocity 조회/설정
  - Sweep API 미구현 (BepuPhysics의 Simulation.Sweep 사용 가능)

IronRose.Engine (PhysicsManager.cs)
  - PhysicsWorld3D/2D 관리
  - FixedUpdate 루프: EnsureStaticColliders → EnsureRigidbodies → Push → Step → Pull
  - CharacterController 관련 코드 없음
```

### Collider 계층
```
Component
  └─ Collider (abstract)
       ├─ BoxCollider
       ├─ SphereCollider
       ├─ CapsuleCollider
       └─ CylinderCollider
```
- Collider는 `_allColliders` 전역 리스트로 관리됨
- Rigidbody 없으면 `RegisterAsStatic()`으로 static body 자동 등록
- `center` 프로퍼티와 `isTrigger` 프로퍼티 보유

### 직렬화 시스템
- SceneSerializer가 컴포넌트를 TOML 형식으로 직렬/역직렬화
- 특수 컴포넌트(Camera, MeshFilter 등)는 switch-case로 처리
- 나머지는 `DeserializeComponentGeneric()` 리플렉션 기반 범용 처리
- CharacterController는 public 프로퍼티만 잘 정의하면 범용 직렬화로 자동 지원

### Inspector 시스템
- `ImGuiInspectorPanel`에서 `Collider` 타입이면 "Edit Collider" 버튼 표시
- 프로퍼티/필드는 리플렉션 기반으로 자동 렌더링
- `[SerializeField]`, `[Range]`, `[Header]` 등 어트리뷰트 지원
- DragFloatClickable 헬퍼 사용 필수

### MonoBehaviour 콜백
- `OnCollisionEnter/Stay/Exit`, `OnTriggerEnter/Stay/Exit` 정의됨
- `OnControllerColliderHit` 콜백은 아직 미정의

## 설계

### 개요

CharacterController는 **Collider를 상속**하며, BepuPhysics의 **Simulation.Sweep**을 사용하여 캡슐 형상을 이동 방향으로 sweep하고, 충돌 응답(슬라이딩, 스텝 오프셋, 슬로프 제한)을 자체적으로 계산하는 키네마틱 컨트롤러이다.

핵심 동작 원리:
1. `Move(motion)` 호출 시 캡슐 sweep으로 이동 경로상 충돌 검출
2. 충돌 법선을 기반으로 슬라이딩 벡터 계산 (최대 3회 반복)
3. 바닥 접촉 시 `isGrounded` 갱신, 경사면 각도에 따라 슬로프 슬라이딩
4. `stepOffset` 이하 높이 장애물은 자동으로 올라감
5. 충돌마다 `OnControllerColliderHit` 콜백 발송

```
                    CharacterController.Move(motion)
                           │
                    ┌──────▼──────┐
                    │ Capsule     │
                    │ Sweep Test  │ ◄── PhysicsWorld3D.SweepCapsule()
                    └──────┬──────┘
                           │ hit?
                    ┌──Yes─┤──No──┐
                    ▼      │      ▼
              Collision    │   Apply full
              Response     │   motion
                    │      │
            ┌───────┴───────┐
            ▼               ▼
     Slope Check      Step Check
     (slopeLimit)    (stepOffset)
            │               │
            ▼               ▼
     Slide along      Step up &
     surface          re-sweep
            │               │
            └───────┬───────┘
                    ▼
            Update Transform
            Update isGrounded
            Fire OnControllerColliderHit
```

### 상세 설계

#### 1. 새로운 타입/열거형

**CollisionFlags (RoseEngine 네임스페이스)**
```csharp
// 파일: src/IronRose.Engine/RoseEngine/CollisionFlags.cs
namespace RoseEngine
{
    [Flags]
    public enum CollisionFlags
    {
        None = 0,
        Sides = 1,
        Above = 2,
        Below = 4,
    }
}
```

**ControllerColliderHit (RoseEngine 네임스페이스)**
```csharp
// 파일: src/IronRose.Engine/RoseEngine/ControllerColliderHit.cs
namespace RoseEngine
{
    public class ControllerColliderHit
    {
        public CharacterController controller { get; internal set; } = null!;
        public Collider collider { get; internal set; } = null!;
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform { get; internal set; } = null!;
        public Rigidbody? rigidbody { get; internal set; }
        public Vector3 point { get; internal set; }
        public Vector3 normal { get; internal set; }
        public Vector3 moveDirection { get; internal set; }
        public float moveLength { get; internal set; }
    }
}
```

#### 2. CharacterController 컴포넌트

**파일: `src/IronRose.Engine/RoseEngine/CharacterController.cs`**

```csharp
namespace RoseEngine
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Character Controller")]
    public class CharacterController : Collider
    {
        // ── Unity 호환 프로퍼티 ──

        [Header("Shape")]
        public float height { get; set; } = 2.0f;
        public float radius { get; set; } = 0.5f;
        // center는 Collider에서 상속

        [Header("Movement")]
        public float slopeLimit { get; set; } = 45f;
        public float stepOffset { get; set; } = 0.3f;
        public float skinWidth { get; set; } = 0.08f;
        public float minMoveDistance { get; set; } = 0.001f;

        [Header("Detection")]
        public bool detectCollisions { get; set; } = true;
        public bool enableOverlapRecovery { get; set; } = true;

        // ── 읽기 전용 상태 ──
        public bool isGrounded { get; private set; }
        public CollisionFlags collisionFlags { get; private set; }
        public Vector3 velocity { get; private set; }

        // ── 내부 상태 ──
        private Vector3 _lastMotion;
        private Vector3 _simpleMoveVelocity;  // SimpleMove용 내부 수직 속도
        private float _simpleMoveVerticalSpeed;
        private StaticHandle? _kinematicHandle; // Bepu static body (다른 물체가 CC와 충돌하도록)

        // ── API 메서드 ──

        /// <summary>충돌 감지하며 motion만큼 이동. 중력 미적용.</summary>
        public CollisionFlags Move(Vector3 motion);

        /// <summary>중력 자동 적용 이동. speed는 XZ 평면 속도.</summary>
        public bool SimpleMove(Vector3 speed);

        // ── 내부 메서드 ──
        internal override void RegisterAsStatic(PhysicsManager mgr);
        internal override void OnAddedToGameObject();
        internal override void OnComponentDestroy();

        // Gizmo
        public override void OnDrawGizmosSelected();
    }
}
```

#### 3. Move() 알고리즘 상세

```
Move(Vector3 motion):
    1. motion 크기가 minMoveDistance 미만이면 조기 리턴 (CollisionFlags.None)
    2. collisionFlags = None, isGrounded = false
    3. 현재 위치 = transform.position + center
    4. remainingMotion = motion
    5. 최대 3회 반복 (Slide iteration):
       a. PhysicsWorld3D.SweepCapsule(position, radius-skinWidth, halfHeight, direction, distance)
       b. hit 없으면: position += remainingMotion, break
       c. hit 있으면:
          - position += direction * (hitDistance - skinWidth)
          - hitNormal 기반 collisionFlags 갱신:
            · dot(hitNormal, up) > 0.7 → Below (바닥)
            · dot(hitNormal, up) < -0.7 → Above (천장)
            · 그 외 → Sides
          - isGrounded 갱신: Below 플래그 설정 시 true
          - OnControllerColliderHit 콜백 발송
          - Slope 처리:
            · 바닥 충돌 시 경사각 = acos(dot(hitNormal, up))
            · 경사각 > slopeLimit → 경사면 아래로 미끄러짐 벡터 적용
          - Step 처리:
            · Sides 충돌 && 충돌 높이가 position.y 기준 stepOffset 이내:
            · 위로 stepOffset만큼 sweep → 전방 sweep → 아래로 sweep
            · 성공 시 계단 위로 이동
          - Slide 벡터: remainingMotion = remainingMotion - dot(remainingMotion, hitNormal) * hitNormal
    6. transform.position = position - center
    7. velocity = (최종위치 - 시작위치) / Time.deltaTime
    8. return collisionFlags
```

#### 4. SimpleMove() 알고리즘

```
SimpleMove(Vector3 speed):
    1. XZ 이동 = speed * Time.deltaTime
    2. 수직 속도 += gravity * Time.deltaTime
    3. isGrounded 상태면 수직 속도 = 0 (최소값 클램프)
    4. motion = (XZ이동.x, 수직속도 * deltaTime, XZ이동.z)
    5. Move(motion) 호출
    6. return isGrounded
```

#### 5. PhysicsWorld3D Sweep API 추가

**파일: `src/IronRose.Physics/PhysicsWorld3D.cs`**

```csharp
// ── Sweep 결과 구조체 ──
public struct SweepHit
{
    public float T;              // 0~1 normalized hit time
    public Vector3 Normal;       // 충돌 법선 (world space)
    public Vector3 HitPosition;  // 충돌 지점
    public CollidableReference Collidable; // 충돌한 오브젝트 참조
}

// ── Sweep 메서드 ──

/// <summary>캡슐을 direction 방향으로 sweep하여 최초 충돌을 반환.</summary>
public bool SweepCapsule(
    Vector3 position,      // 캡슐 중심
    Quaternion orientation, // 캡슐 방향
    float radius,
    float halfLength,      // 반구 제외 원통 부분의 절반 길이
    Vector3 direction,     // 이동 방향 (정규화)
    float maxDistance,     // 최대 이동 거리
    out SweepHit hit,
    int selfBodyHandle = -1  // 자기 자신 제외용
);

/// <summary>캡슐을 sweep하여 모든 충돌을 반환 (최대 maxHits개).</summary>
public int SweepCapsuleAll(
    Vector3 position,
    Quaternion orientation,
    float radius,
    float halfLength,
    Vector3 direction,
    float maxDistance,
    Span<SweepHit> results,
    int selfBodyHandle = -1
);

/// <summary>지정 위치에서 캡슐과 겹치는 collidable 검출 (overlap recovery용).</summary>
public int OverlapCapsule(
    Vector3 position,
    Quaternion orientation,
    float radius,
    float halfLength,
    Span<CollidableReference> results,
    int selfBodyHandle = -1
);
```

**구현 방식**: BepuPhysics의 `Simulation.Sweep<TShape, TSweepHitHandler>()` 사용
- `TShape` = `Capsule`
- `TSweepHitHandler` = 커스텀 구조체 `ClosestHitHandler` (ISweepHitHandler 구현)
- BodyVelocity로 sweep 방향/거리 표현: `Linear = direction * (maxDistance / sweepDuration)`

```csharp
// ISweepHitHandler 구현 예시
internal struct ClosestHitHandler : ISweepHitHandler
{
    public float ClosestT;
    public Vector3 ClosestNormal;
    public Vector3 ClosestHitPosition;
    public CollidableReference ClosestCollidable;
    public bool HasHit;
    public int ExcludeBodyHandle;  // 자기 자신 제외

    public bool AllowTest(CollidableReference collidable)
    {
        // 자기 자신의 kinematic body 제외
        if (collidable.Mobility == CollidableMobility.Dynamic
            && collidable.BodyHandle.Value == ExcludeBodyHandle)
            return false;
        return true;
    }

    public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

    public void OnHit(ref float maximumT, float t, Vector3 hitLocation,
                      Vector3 hitNormal, CollidableReference collidable)
    {
        if (t < ClosestT)
        {
            ClosestT = t;
            ClosestNormal = hitNormal;
            ClosestHitPosition = hitLocation;
            ClosestCollidable = collidable;
            HasHit = true;
            maximumT = t; // 이후 더 먼 충돌 무시
        }
    }

    public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
    {
        // 시작 시점에서 이미 겹친 경우
        HasHit = true;
        ClosestT = 0;
        ClosestCollidable = collidable;
        maximumT = 0;
    }
}
```

#### 6. PhysicsManager 통합

**파일: `src/IronRose.Engine/Physics/PhysicsManager.cs`**

CharacterController는 일반 Rigidbody와 달리 PhysicsManager의 시뮬레이션 루프에 참여하지 않는다. 대신:

- CharacterController가 `OnAddedToGameObject()` 시 `_allColliders`에 등록 (Collider 상속이므로 자동)
- Rigidbody가 없으므로 `EnsureStaticColliders()`에서 static body로 등록됨 (다른 dynamic body가 CC와 충돌 가능하도록)
- `Move()`/`SimpleMove()` 호출 시 자체적으로 sweep 수행 후 transform 갱신, 이후 static body pose도 동기화

**PhysicsManager에 추가할 것**:
```csharp
// CharacterController의 static body pose를 transform과 동기화
// EnsureStaticColliders() 이후, PushTransformsToPhysics()에서 호출
private void SyncCharacterControllerPoses()
{
    foreach (var col in Collider._allColliders)
    {
        if (col is CharacterController cc && cc._kinematicHandle != null)
        {
            // CC의 static body 위치를 현재 transform에 동기화
            _world3D.SetStaticPose(cc._kinematicHandle.Value, ...);
        }
    }
}
```

**PhysicsWorld3D에 SetStaticPose 추가**:
```csharp
public void SetStaticPose(StaticHandle handle, Vector3 position, Quaternion rotation)
{
    _simulation.Statics[handle].Pose = new RigidPose(position, rotation);
}
```

#### 7. OnControllerColliderHit 콜백 발송

**MonoBehaviour에 추가**:
```csharp
// 파일: src/IronRose.Engine/RoseEngine/MonoBehaviour.cs
public virtual void OnControllerColliderHit(ControllerColliderHit hit) { }
```

**CharacterController.Move() 내부에서 충돌 발생 시**:
```csharp
private void FireControllerColliderHit(ControllerColliderHit hit)
{
    foreach (var mb in gameObject.GetComponents<MonoBehaviour>())
    {
        if (mb.enabled && mb._hasAwoken)
        {
            try { mb.OnControllerColliderHit(hit); }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in OnControllerColliderHit of {mb.GetType().Name}: {ex.Message}");
            }
        }
    }
}
```

#### 8. CollidableReference에서 GameObject 역매핑

Sweep 결과의 `CollidableReference`에서 충돌한 Collider/GameObject를 찾기 위한 매핑이 필요하다.

**PhysicsWorld3D에 핸들-컴포넌트 매핑 추가**:
```csharp
// 파일: src/IronRose.Physics/PhysicsWorld3D.cs

// Body/Static handle → 사용자 데이터 매핑
private readonly Dictionary<int, object> _bodyUserData = new();    // BodyHandle.Value → Component
private readonly Dictionary<int, object> _staticUserData = new();  // StaticHandle.Value → Component

public void SetBodyUserData(BodyHandle handle, object data) => _bodyUserData[handle.Value] = data;
public void SetStaticUserData(StaticHandle handle, object data) => _staticUserData[handle.Value] = data;

public object? GetUserData(CollidableReference collidable)
{
    if (collidable.Mobility == CollidableMobility.Static)
    {
        _staticUserData.TryGetValue(collidable.StaticHandle.Value, out var data);
        return data;
    }
    else
    {
        _bodyUserData.TryGetValue(collidable.BodyHandle.Value, out var data);
        return data;
    }
}
```

기존 Rigidbody/Collider의 `RegisterAsStatic()`, `RegisterWithPhysics()` 에서 등록 후 user data 설정:
```csharp
// 예: Collider.RegisterAsStatic()
mgr.World3D.SetStaticUserData(_staticHandle.Value, this);

// 예: Rigidbody.RegisterWithPhysics()
mgr.World3D.SetBodyUserData(bodyHandle.Value, this);
```

#### 9. Inspector/Gizmo 지원

**Inspector**: CharacterController의 프로퍼티는 모두 public이므로 범용 리플렉션 Inspector에서 자동 렌더링된다. Collider를 상속하므로 "Edit Collider" 버튼도 자동 표시.

**Gizmo** (`OnDrawGizmosSelected`):
```csharp
public override void OnDrawGizmosSelected()
{
    Gizmos.color = new Color(0.5f, 1f, 0.5f, 1f);
    Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
    // skinWidth를 포함한 외곽선
    Gizmos.DrawWireCapsule(center, radius + skinWidth, height);
    // 실제 충돌 캡슐
    Gizmos.color = new Color(0.5f, 1f, 0.5f, 0.5f);
    Gizmos.DrawWireCapsule(center, radius, height);
}
```

#### 10. 씬 직렬화

CharacterController는 범용 리플렉션 직렬화(`DeserializeComponentGeneric`)로 자동 처리된다. public 프로퍼티가 자동으로 TOML에 저장/복원됨:
- height, radius, center, slopeLimit, stepOffset, skinWidth, minMoveDistance
- detectCollisions, enableOverlapRecovery

### 영향 범위

| 파일 | 변경 내용 |
|------|-----------|
| **신규** `src/IronRose.Engine/RoseEngine/CharacterController.cs` | CharacterController 컴포넌트 |
| **신규** `src/IronRose.Engine/RoseEngine/CollisionFlags.cs` | CollisionFlags 열거형 |
| **신규** `src/IronRose.Engine/RoseEngine/ControllerColliderHit.cs` | ControllerColliderHit 클래스 |
| `src/IronRose.Physics/PhysicsWorld3D.cs` | SweepCapsule, OverlapCapsule, SetStaticPose, UserData 매핑 추가 |
| `src/IronRose.Engine/Physics/PhysicsManager.cs` | CharacterController static body pose 동기화 |
| `src/IronRose.Engine/RoseEngine/MonoBehaviour.cs` | OnControllerColliderHit 콜백 추가 |
| `src/IronRose.Engine/RoseEngine/Collider.cs` | CharacterController 인식을 위한 조건 분기 (필요시) |
| `src/IronRose.Engine/RoseEngine/Rigidbody.cs` | RegisterWithPhysics에서 SetBodyUserData 호출 추가 |
| 기존 Collider 서브클래스들 (BoxCollider, SphereCollider, CapsuleCollider, CylinderCollider) | RegisterAsStatic에서 SetStaticUserData 호출 추가 |

### 기존 기능에 미치는 영향

- **Collider 시스템**: CharacterController가 Collider를 상속하므로 `_allColliders`에 자동 등록됨. Rigidbody가 없으면 static body로 등록되는 기존 로직이 그대로 적용됨.
- **Rigidbody 시스템**: CharacterController가 있는 GameObject에는 Rigidbody를 추가하지 않는 것이 원칙. (Unity와 동일)
- **씬 직렬화**: 범용 직렬화에 의해 자동 지원. 추가 코드 불필요.
- **Inspector**: Collider 상속이므로 기존 Inspector 로직으로 자동 지원.
- **UserData 매핑**: 기존 Rigidbody/Collider에 SetUserData 호출을 추가해야 하지만, 동작에는 영향 없음.

## 구현 단계

### Phase 1: 기반 인프라 (Sweep API + UserData 매핑)
- [ ] `PhysicsWorld3D`에 `SweepHit` 구조체 정의
- [ ] `PhysicsWorld3D`에 `ClosestHitHandler` (ISweepHitHandler) 구현
- [ ] `PhysicsWorld3D.SweepCapsule()` 구현
- [ ] `PhysicsWorld3D.OverlapCapsule()` 구현
- [ ] `PhysicsWorld3D.SetStaticPose()` 구현
- [ ] `PhysicsWorld3D`에 UserData 매핑 (`_bodyUserData`, `_staticUserData`) 추가
- [ ] 기존 Collider 서브클래스의 `RegisterAsStatic()`에 `SetStaticUserData()` 호출 추가
- [ ] 기존 `Rigidbody.RegisterWithPhysics()`에 `SetBodyUserData()` 호출 추가
- [ ] `PhysicsWorld3D.RemoveBody()`, `RemoveStatic()`에서 UserData 정리 추가
- [ ] 빌드 확인

### Phase 2: 타입 정의 + CharacterController 기본 골격
- [ ] `CollisionFlags.cs` 생성
- [ ] `ControllerColliderHit.cs` 생성
- [ ] `CharacterController.cs` 생성 (프로퍼티만, Move/SimpleMove는 stub)
- [ ] `MonoBehaviour.cs`에 `OnControllerColliderHit` 콜백 추가
- [ ] 빌드 확인

### Phase 3: Move() 핵심 로직
- [ ] 캡슐 sweep 기반 충돌 감지 구현
- [ ] 슬라이딩 벡터 계산 (최대 3회 반복)
- [ ] `collisionFlags` 갱신 로직
- [ ] `isGrounded` 갱신 로직
- [ ] `skinWidth` 적용
- [ ] `minMoveDistance` 적용
- [ ] `velocity` 계산
- [ ] `OnControllerColliderHit` 콜백 발송
- [ ] 빌드 및 기본 이동 테스트

### Phase 4: 슬로프 + 스텝 처리
- [ ] 슬로프 각도 계산 및 `slopeLimit` 적용
- [ ] 경사면 미끄러짐 구현
- [ ] `stepOffset` 계단 오르기 구현 (위 sweep → 전방 sweep → 아래 sweep)
- [ ] 빌드 및 경사면/계단 테스트

### Phase 5: SimpleMove + Overlap Recovery
- [ ] `SimpleMove()` 구현 (내부 중력 누적 + Move 호출)
- [ ] `enableOverlapRecovery` 구현 (겹침 보정)
- [ ] 빌드 및 테스트

### Phase 6: PhysicsManager 통합 + 최종 정리
- [ ] `PhysicsManager`에 CharacterController static body pose 동기화 추가
- [ ] CharacterController가 있으면 Rigidbody 추가 시 경고 로그
- [ ] Gizmo 렌더링 구현
- [ ] 전체 빌드 및 통합 테스트
- [ ] Progress.md 업데이트

## 대안 검토

### 대안 1: BepuPhysics의 CharacterControllers 데모 활용
BepuPhysics 리포지토리의 `Demos/Characters/` 폴더에 캐릭터 컨트롤러 데모 구현이 있다. 이를 직접 포팅하는 방안.

**기각 이유**: 데모 코드는 BepuPhysics의 dynamic body 기반으로 동작하며, Unity CharacterController의 키네마틱 방식과 근본적으로 다르다. Unity API 호환성을 유지하려면 sweep 기반 직접 구현이 더 적합하다.

### 대안 2: Kinematic Body + Contact 수집 방식
CharacterController를 kinematic body로 등록하고, NarrowPhaseCallbacks에서 충돌 이벤트를 수집하여 위치를 보정하는 방안.

**기각 이유**: 이 방식은 시뮬레이션 스텝 이후에야 충돌을 알 수 있어 1프레임 지연이 발생한다. Unity CharacterController의 Move()는 호출 즉시 충돌 응답이 완료되어야 하므로 sweep 방식이 필수적이다.

### 대안 3: Collider 상속 대신 독립 컴포넌트
CharacterController를 Collider가 아닌 Component를 직접 상속하는 방안.

**기각 이유**: Unity에서 CharacterController는 Collider를 상속한다. 기존 `_allColliders` 관리 로직, static body 자동 등록, Inspector "Edit Collider" 버튼 등을 재활용할 수 있어 Collider 상속이 유리하다.

## 미결 사항

없음. 모든 사항이 확인되었다.
