# Physics.Raycast / Physics2D.Raycast 구현

## 배경

IronRose 엔진은 Unity 호환 `Physics.Raycast()`, `Physics.RaycastAll()`, `Physics2D.Raycast()` 등의 API를 제공하지만, 현재 모두 스텁(항상 false/빈 배열 반환) 상태이다. 3D 물리는 BepuPhysics v2.4.0, 2D 물리는 Aether.Physics2D v2.2.0을 사용하며, 두 백엔드 모두 RayCast API를 제공하므로 이를 연결해야 한다.

## 목표

1. `Physics.Raycast()` -- BepuPhysics `Simulation.RayCast`를 사용한 3D 레이캐스트
2. `Physics.RaycastAll()` -- 모든 히트를 수집하는 3D 레이캐스트
3. `Physics.OverlapSphere()` / `Physics.CheckSphere()` -- 구 오버랩 쿼리
4. `Physics2D.Raycast()` -- Aether.Physics2D `World.RayCast`를 사용한 2D 레이캐스트
5. `Physics2D.OverlapCircle()` -- 2D 원형 오버랩 쿼리
6. 기존 Unity API 시그니처와 완전 호환
7. 레이캐스트 결과에서 `RaycastHit.collider`, `RaycastHit.gameObject` 등 올바르게 반환

## 현재 상태

### PhysicsStatic.cs (스텁 API)
- `Physics.Raycast(origin, direction, out hit, maxDistance)` -- `return false`
- `Physics.RaycastAll(origin, direction, maxDistance)` -- `return Array.Empty<RaycastHit>()`
- `Physics.OverlapSphere(position, radius)` -- `return Array.Empty<Collider>()`
- `Physics.CheckSphere(position, radius)` -- `return false`
- `Physics2D.Raycast(origin, direction, distance)` -- `return default`
- `Physics2D.OverlapCircle(point, radius)` -- `return Array.Empty<Collider2D>()`

### PhysicsWorld3D.cs (BepuPhysics 래퍼)
- `Simulation.Sweep`를 사용하는 `SweepCapsule`, `OverlapCapsule` 메서드가 이미 구현되어 있음
- `ISweepHitHandler` 기반의 `ClosestHitHandler`, `OverlapCollectHandler` 패턴이 존재
- `UserData` 매핑: `Dictionary<int, object>`로 `BodyHandle.Value`/`StaticHandle.Value` -> Collider/Rigidbody 매핑
- `GetUserData(CollidableReference)` 메서드로 핸들에서 사용자 객체 조회 가능

### PhysicsWorld2D.cs (Aether.Physics2D 래퍼)
- 현재 RayCast/QueryAABB 메서드 없음
- Body에 대한 UserData 매핑 없음 (하지만 Aether Body/Fixture에 `Tag` 필드 존재)

### UserData 현황
- **3D**: Collider 서브클래스의 `RegisterAsStatic()`에서 `SetStaticUserData(handle, this)` 호출 (Collider 저장)
- **3D**: Rigidbody의 `RegisterWithPhysics()`에서 `SetBodyUserData(handle, this)` 호출 (Rigidbody 저장)
- **2D**: UserData 매핑 없음 -- `Body.Tag`/`Fixture.Tag`를 활용 필요

## 설계

### 개요

1. **PhysicsWorld3D**에 `RayCast`, `RayCastAll`, `OverlapSphere` 메서드를 추가한다.
   - BepuPhysics의 `Simulation.RayCast<T>(origin, direction, maximumT, hitHandler)` 사용
   - `IRayHitHandler` 인터페이스를 구현하는 handler struct 작성

2. **PhysicsWorld2D**에 `RayCast`, `RayCastAll`, `OverlapCircle` 메서드를 추가한다.
   - Aether.Physics2D의 `World.RayCast(callback, point1, point2)` 사용
   - 2D UserData 매핑을 위해 `Body.Tag`에 Collider2D/Rigidbody2D를 저장

3. **PhysicsStatic.cs**의 스텁을 실제 구현으로 교체한다.
   - `PhysicsManager.Instance`를 통해 World3D/World2D에 접근
   - 백엔드 결과를 `RaycastHit`/`RaycastHit2D` 구조체로 변환

### 상세 설계

#### 1. PhysicsWorld3D에 RayCast 메서드 추가

##### 1-1. IRayHitHandler 구현체

```csharp
// 가장 가까운 히트만 수집 (Physics.Raycast용)
internal struct ClosestRayHitHandler : IRayHitHandler
{
    public float ClosestT;
    public Vector3 ClosestNormal;
    public CollidableReference ClosestCollidable;
    public bool HasHit;

    public bool AllowTest(CollidableReference collidable) => true;
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    public void OnRayHit(ref RayData ray, ref float maximumT, float t,
                         in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        if (t < ClosestT)
        {
            ClosestT = t;
            ClosestNormal = normal;
            ClosestCollidable = collidable;
            HasHit = true;
            maximumT = t; // 이후 더 먼 후보를 제외
        }
    }
}

// 모든 히트 수집 (Physics.RaycastAll용)
internal struct AllRayHitHandler : IRayHitHandler
{
    public List<(float T, Vector3 Normal, CollidableReference Collidable)> Hits;

    public bool AllowTest(CollidableReference collidable) => true;
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    public void OnRayHit(ref RayData ray, ref float maximumT, float t,
                         in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        Hits.Add((t, normal, collidable));
    }
}
```

##### 1-2. PhysicsWorld3D 새 메서드

```csharp
/// <summary>가장 가까운 충돌을 반환하는 레이캐스트.</summary>
public bool RayCast(Vector3 origin, Vector3 direction, float maxDistance,
                    out float t, out Vector3 normal, out CollidableReference collidable)

/// <summary>모든 충돌을 반환하는 레이캐스트.</summary>
public List<(float T, Vector3 Normal, CollidableReference Collidable)> RayCastAll(
    Vector3 origin, Vector3 direction, float maxDistance)

/// <summary>구 오버랩 쿼리 -- Sweep을 사용한 근사 구현.</summary>
public int OverlapSphere(Vector3 center, float radius,
                         Span<CollidableReference> results)
```

**RayCast 구현 핵심**:
- BepuPhysics `Simulation.RayCast<T>(in origin, in direction, maximumT, ref handler)` 호출
- `direction`은 정규화 필요. `maximumT = maxDistance / direction.Length()` 또는 direction을 정규화한 뒤 `maximumT = maxDistance`
- 히트 위치 계산: `hitPoint = origin + direction * t`

**OverlapSphere 구현**:
- BepuPhysics에는 직접적인 sphere overlap 쿼리가 없으므로, 기존 `SweepCapsule` 패턴과 동일하게 `Simulation.Sweep`을 사용
- 반지름 `radius`인 Sphere를 `center`에 놓고, 매우 짧은 거리(0.001f)로 sweep하여 t=0에서의 겹침을 검출
- 기존 `OverlapCapsule`이 동일한 접근 방식을 사용하고 있음

#### 2. PhysicsWorld2D에 RayCast 메서드 추가

##### 2-1. 2D UserData 매핑

Aether.Physics2D의 `Body.Tag`를 활용하여 Collider2D 참조를 저장한다.

**변경 대상**: `BoxCollider2D.RegisterAsStatic()`, `CircleCollider2D.RegisterAsStatic()`, `Rigidbody2D.RegisterWithPhysics()`

```csharp
// BoxCollider2D.RegisterAsStatic 예시
_staticBody = mgr.World2D.CreateStaticBody(pos.x + offset.x, pos.y + offset.y);
_staticBody.Tag = this;  // <-- 추가
mgr.World2D.AttachRectangle(_staticBody, size.x, size.y, 1f);

// Rigidbody2D.RegisterWithPhysics 예시
aetherBody = mgr.World2D.CreateDynamicBody(pos.x, pos.y);
aetherBody.Tag = this;  // <-- 추가 (Rigidbody2D 참조 저장)
```

##### 2-2. PhysicsWorld2D 새 메서드

```csharp
/// <summary>2D 레이캐스트 -- 가장 가까운 히트 반환.</summary>
public bool RayCast(AetherVector2 origin, AetherVector2 direction, float maxDistance,
                    out Fixture hitFixture, out AetherVector2 hitPoint,
                    out AetherVector2 hitNormal, out float fraction)

/// <summary>2D 원형 오버랩 쿼리.</summary>
public List<Fixture> OverlapCircle(AetherVector2 center, float radius)
```

**RayCast 구현 핵심**:
- `World.RayCast(callback, point1, point2)` 호출
  - `point1 = origin`
  - `point2 = origin + direction.Normalized() * maxDistance`
- callback에서 `fraction`을 반환하여 가장 가까운 히트를 추적
- callback 시그니처: `float Callback(Fixture fixture, Vector2 point, Vector2 normal, float fraction)`
  - `return fraction` -- 가장 가까운 히트로 클리핑
  - `return 1` -- RaycastAll용 (클리핑 없이 계속)

**OverlapCircle 구현**:
- `World.QueryAABB(callback, aabb)`로 원 바운딩 박스 내 후보를 검색
- callback에서 각 Fixture의 shape에 대해 점/거리 검사로 실제 원 내부인지 확인
- 또는 Aether의 `Fixture.TestPoint()`를 여러 샘플 점에서 호출하는 것보다, AABB 쿼리 후 `Body.Position`과 `center` 간 거리를 검사하는 근사 방식 사용

#### 3. PhysicsStatic.cs 스텁 교체

```csharp
public static class Physics
{
    public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
        float maxDistance = Mathf.Infinity)
    {
        hit = default;
        var mgr = IronRose.Engine.PhysicsManager.Instance;
        if (mgr == null) return false;

        var sOrigin = new SysVector3(origin.x, origin.y, origin.z);
        var sDir = new SysVector3(direction.x, direction.y, direction.z);

        if (!mgr.World3D.RayCast(sOrigin, sDir, maxDistance,
            out float t, out var normal, out var collidable))
            return false;

        // CollidableReference -> Collider/GameObject 변환
        var userData = mgr.World3D.GetUserData(collidable);
        hit = BuildRaycastHit(sOrigin, sDir, t, normal, userData);
        return true;
    }

    // ...동일 패턴으로 RaycastAll, OverlapSphere, CheckSphere
}
```

**`BuildRaycastHit` 헬퍼** -- UserData로부터 Collider/GameObject를 추출:

```csharp
private static RaycastHit BuildRaycastHit(SysVector3 origin, SysVector3 direction,
    float t, SysVector3 normal, object? userData)
{
    var hit = new RaycastHit();
    hit.distance = t;
    hit.point = ToVector3(origin + direction * t);
    hit.normal = ToVector3(normal);

    if (userData is Collider col)
    {
        hit.collider = col;
        hit.gameObject = col.gameObject;
    }
    else if (userData is Rigidbody rb)
    {
        hit.gameObject = rb.gameObject;
        hit.collider = rb.gameObject.GetComponent<Collider>();
    }

    return hit;
}
```

### 영향 범위

#### 수정이 필요한 파일

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Physics/PhysicsWorld3D.cs` | `RayCast`, `RayCastAll`, `OverlapSphere` 메서드 추가 + IRayHitHandler 구현체 2개 |
| `src/IronRose.Physics/PhysicsWorld2D.cs` | `RayCast`, `OverlapCircle` 메서드 추가 + using 추가 |
| `src/IronRose.Engine/RoseEngine/PhysicsStatic.cs` | 스텁을 실제 구현으로 교체, System.Numerics using 추가 |
| `src/IronRose.Engine/RoseEngine/BoxCollider2D.cs` | `RegisterAsStatic()`에서 `_staticBody.Tag = this` 추가 |
| `src/IronRose.Engine/RoseEngine/CircleCollider2D.cs` | `RegisterAsStatic()`에서 `_staticBody.Tag = this` 추가 |
| `src/IronRose.Engine/RoseEngine/Rigidbody2D.cs` | `RegisterWithPhysics()`에서 `aetherBody.Tag = this` 추가 |

#### 기존 기능에 미치는 영향
- **PhysicsWorld3D**: 기존 `SweepCapsule`, `OverlapCapsule`에는 영향 없음 (새 메서드 추가만)
- **PhysicsWorld2D**: 기존 메서드에는 영향 없음 (새 메서드 추가만)
- **PhysicsStatic.cs**: 기존 스텁의 시그니처를 유지하므로 호출측 코드 변경 불필요
- **2D Body.Tag 설정**: 기존에 Tag를 사용하지 않으므로 부작용 없음

## 구현 단계

- [ ] **단계 1**: PhysicsWorld3D에 IRayHitHandler 구현체 추가 (`ClosestRayHitHandler`, `AllRayHitHandler`)
- [ ] **단계 2**: PhysicsWorld3D에 `RayCast()`, `RayCastAll()`, `OverlapSphere()` 메서드 추가
- [ ] **단계 3**: PhysicsWorld2D에 `RayCast()`, `OverlapCircle()` 메서드 추가
- [ ] **단계 4**: 2D UserData 매핑 -- BoxCollider2D, CircleCollider2D, Rigidbody2D에서 `Body.Tag` 설정
- [ ] **단계 5**: PhysicsStatic.cs의 `Physics` 클래스 스텁을 실제 구현으로 교체
- [ ] **단계 6**: PhysicsStatic.cs의 `Physics2D` 클래스 스텁을 실제 구현으로 교체
- [ ] **단계 7**: `dotnet build`로 빌드 확인
- [ ] **단계 8**: (선택) 테스트 씬에서 레이캐스트 동작 검증

## 대안 검토

### 3D OverlapSphere: Sweep vs BroadPhase AABB 쿼리
- **Sweep 방식 (선택)**: 기존 `OverlapCapsule`과 동일한 패턴. Sphere shape을 짧은 거리로 sweep하여 겹침 검출. 정확하지만 약간의 오버헤드.
- **BroadPhase AABB 방식**: `Simulation.BroadPhase`의 AABB 쿼리로 후보를 빠르게 찾은 뒤, 각 shape에 대해 narrow-phase 거리 테스트. 더 정확할 수 있으나 구현이 복잡함.
- **결정**: Sweep 방식을 선택. 이미 검증된 패턴이 있고, 구현이 간결하며, 게임 로직 수준의 쿼리에 충분한 성능.

### 2D OverlapCircle: AABB 근사 vs 정확한 shape 교차
- **AABB 근사 + 거리 체크 (선택)**: `World.QueryAABB`로 원 바운딩 박스 내 후보를 찾고, 각 Body의 위치와 center 간 거리로 필터링. 간단하고 충분히 정확.
- **Fixture별 shape 교차 테스트**: 정확하지만 shape 타입마다 분기 필요. 향후 필요 시 업그레이드 가능.
- **결정**: AABB 근사 + Body 중심 거리 체크를 1차 구현으로 선택. 간결하고 대부분의 게임 시나리오에 충분.

## 미결 사항

1. **LayerMask 지원**: 현재 `Physics.Raycast`의 Unity API에는 `int layerMask` 파라미터가 존재. `GameObject.layer`가 이미 구현되어 있으므로 (기본값 0), layerMask 필터링 오버로드를 이 Phase에서 추가할지, 향후 Phase로 미룰지.
   - **제안**: 이번 Phase에서는 layerMask 파라미터 없는 기본 오버로드만 구현. layerMask 오버로드는 시그니처만 추가하되 필터링 없이 모든 레이어 통과 (향후 확장점 마련).

2. **QueryTriggerInteraction**: Unity에서 `Physics.Raycast`는 기본적으로 트리거 콜라이더를 무시할 수 있음. `Collider.isTrigger` 필드가 존재하므로 향후 필터링 가능하나, 이번 Phase에서는 트리거 구분 없이 모든 콜라이더에 대해 레이캐스트 수행.
