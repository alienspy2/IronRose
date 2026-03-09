# Phase 6: 물리 엔진 통합 ✅ (2026-02-14 완료)

## 목표
3D 및 2D 물리 시뮬레이션을 통합하여 Unity의 물리 기능을 재현합니다.

> **커밋**: `066922a`

---

## 아키텍처 결정

### 프로젝트 의존성 구조

현재 `IronRose.Physics`가 `IronRose.Engine`을 참조하고 있으나,
EngineCore에서 물리 업데이트를 호출해야 하므로 **의존성 방향을 역전**합니다:

```
IronRose.Physics (순수 물리 래퍼, Engine 미참조)
  ├── PhysicsWorld3D.cs  (BepuPhysics v2.4.0 래퍼, System.Numerics 타입)
  └── PhysicsWorld2D.cs  (Aether.Physics2D v2.2.0 래퍼, System.Numerics 타입)

IronRose.Engine (Physics 참조 추가)
  ├── Physics/
  │   └── PhysicsManager.cs  (PhysicsWorld3D/2D 통합 + FixedUpdate 루프)
  └── RoseEngine/
      ├── Rigidbody.cs, Rigidbody2D.cs      (Component 래퍼)
      ├── Collider.cs, BoxCollider.cs, ...   (Component 래퍼)
      ├── Collider2D.cs, BoxCollider2D.cs, ...
      ├── Physics.cs, Physics2D.cs           (static 유틸리티)
      └── ForceMode.cs, CollisionDetectionMode.cs
```

**핵심 원칙:**
- `IronRose.Physics`: BepuPhysics/Aether 순수 래퍼 (System.Numerics 타입만 사용)
- `IronRose.Engine`: Unity API 래퍼 (Component 상속) + 통합 관리자

### .csproj 변경

**IronRose.Physics.csproj** (Engine 참조 제거):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BepuPhysics" Version="2.4.0" />
    <PackageReference Include="Aether.Physics2D" Version="2.2.0" />
  </ItemGroup>
</Project>
```

**IronRose.Engine.csproj** (Physics 참조 추가):
```xml
<ItemGroup>
  <ProjectReference Include="..\IronRose.Physics\IronRose.Physics.csproj" />
  <!-- 기존 참조 유지 -->
</ItemGroup>
```

---

## 작업 항목

### 6.0 사전 작업: MonoBehaviour + EngineCore 확장

Physics 통합 전에 기존 코드에 FixedUpdate 인프라를 추가합니다.

**MonoBehaviour.cs 추가:**
```csharp
// 기존 lifecycle에 FixedUpdate, 충돌 콜백 추가
public virtual void FixedUpdate() { }

// 3D 충돌 콜백
public virtual void OnCollisionEnter(Collision collision) { }
public virtual void OnCollisionStay(Collision collision) { }
public virtual void OnCollisionExit(Collision collision) { }
public virtual void OnTriggerEnter(Collider other) { }
public virtual void OnTriggerStay(Collider other) { }
public virtual void OnTriggerExit(Collider other) { }

// 2D 충돌 콜백
public virtual void OnCollisionEnter2D(Collision2D collision) { }
public virtual void OnCollisionStay2D(Collision2D collision) { }
public virtual void OnCollisionExit2D(Collision2D collision) { }
public virtual void OnTriggerEnter2D(Collider2D other) { }
public virtual void OnTriggerStay2D(Collider2D other) { }
public virtual void OnTriggerExit2D(Collider2D other) { }
```

**EngineCore.cs — FixedUpdate 누적기 추가:**
```csharp
private const float FixedDeltaTime = 1f / 50f; // 50 Hz
private double _fixedAccumulator = 0;

public void Update(double deltaTime)
{
    Input.Update();
    InputSystem.Update();
    ProcessEngineKeys();

    if (Application.isPaused) return;

    // 핫 리로드 처리 (기존)
    // ...

    // Fixed timestep 물리 루프
    _fixedAccumulator += deltaTime;
    while (_fixedAccumulator >= FixedDeltaTime)
    {
        Time.fixedDeltaTime = FixedDeltaTime;
        _physicsManager?.FixedUpdate(FixedDeltaTime);
        SceneManager.FixedUpdate(FixedDeltaTime); // MonoBehaviour.FixedUpdate 호출
        _fixedAccumulator -= FixedDeltaTime;
    }

    // Variable timestep (기존)
    _scriptDomain?.Update();
    SceneManager.Update((float)deltaTime);
}
```

**SceneManager.cs — FixedUpdate 루프 추가:**
```csharp
public static void FixedUpdate(float fixedDeltaTime)
{
    for (int i = 0; i < _behaviours.Count; i++)
    {
        var b = _behaviours[i];
        if (!IsActive(b)) continue;
        try { b.FixedUpdate(); }
        catch (Exception ex)
        {
            Debug.LogError($"Exception in FixedUpdate() of {b.GetType().Name}: {ex.Message}");
        }
    }
}
```

**Time.cs 추가:**
```csharp
public static float fixedDeltaTime { get; internal set; } = 1f / 50f;
public static float fixedTime { get; internal set; }
```

---

### 6.1 3D 물리: BepuPhysics v2 통합

**PhysicsWorld3D.cs (IronRose.Physics):**

> BepuPhysics는 System.Numerics 타입만 사용합니다.
> RoseEngine 타입과의 변환은 Engine 측 래퍼에서 처리합니다.

```csharp
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace IronRose.Physics
{
    public class PhysicsWorld3D : IDisposable
    {
        private Simulation _simulation = null!;
        private BufferPool _bufferPool = null!;
        private ThreadDispatcher _threadDispatcher = null!;
        internal Simulation Simulation => _simulation;

        public void Initialize(Vector3 gravity = default)
        {
            if (gravity == default) gravity = new Vector3(0, -9.81f, 0);

            _bufferPool = new BufferPool();
            var targetThreadCount = Math.Max(2, Environment.ProcessorCount - 2);
            _threadDispatcher = new ThreadDispatcher(targetThreadCount);

            _simulation = Simulation.Create(
                _bufferPool,
                new NarrowPhaseCallbacks(),
                new PoseIntegratorCallbacks(gravity),
                new SolveDescription(8, 1)
            );

            Console.WriteLine($"[Physics3D] Initialized with {targetThreadCount} threads");
        }

        public void Step(float deltaTime)
        {
            _simulation.Timestep(deltaTime, _threadDispatcher);
        }

        public BodyHandle AddDynamicBody(Vector3 position, Quaternion rotation,
            TypedIndex shapeIndex, float mass)
        {
            var inertia = new BodyInertia();
            _simulation.Shapes.GetLocalInertia(shapeIndex, mass, out inertia);

            return _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position, rotation),
                inertia,
                shapeIndex,
                0.01f // sleepThreshold
            ));
        }

        public StaticHandle AddStaticBody(Vector3 position, Quaternion rotation,
            TypedIndex shapeIndex)
        {
            return _simulation.Statics.Add(new StaticDescription(
                new RigidPose(position, rotation),
                shapeIndex
            ));
        }

        public TypedIndex AddBoxShape(float sizeX, float sizeY, float sizeZ)
        {
            return _simulation.Shapes.Add(new Box(sizeX, sizeY, sizeZ));
        }

        public TypedIndex AddSphereShape(float radius)
        {
            return _simulation.Shapes.Add(new Sphere(radius));
        }

        public TypedIndex AddCapsuleShape(float radius, float length)
        {
            return _simulation.Shapes.Add(new Capsule(radius, length));
        }

        public RigidPose GetBodyPose(BodyHandle handle)
        {
            return _simulation.Bodies[handle].Pose;
        }

        public BodyVelocity GetBodyVelocity(BodyHandle handle)
        {
            return _simulation.Bodies[handle].Velocity;
        }

        public void SetBodyVelocity(BodyHandle handle, BodyVelocity velocity)
        {
            _simulation.Bodies[handle].Velocity = velocity;
        }

        public void ApplyImpulse(BodyHandle handle, Vector3 impulse, Vector3 offset)
        {
            _simulation.Bodies[handle].ApplyLinearImpulse(impulse);
        }

        public void Dispose()
        {
            _simulation?.Dispose();
            _threadDispatcher?.Dispose();
            _bufferPool?.Clear();
        }
    }

    // --- BepuPhysics 콜백 구현체 ---

    internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation) { }

        public bool AllowContactGeneration(int workerIndex,
            CollidableReference a, CollidableReference b,
            ref float speculativeMargin)
            => true;

        public bool AllowContactGeneration(int workerIndex,
            CollidablePair pair, int childIndexA, int childIndexB)
            => true;

        public bool ConfigureContactManifold<TManifold>(int workerIndex,
            CollidablePair pair, ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial.FrictionCoefficient = 0.5f;
            pairMaterial.MaximumRecoveryVelocity = 2f;
            pairMaterial.SpringSettings = new SpringSettings(30, 1);
            return true;
        }

        public bool ConfigureContactManifold(int workerIndex,
            CollidablePair pair, int childIndexA, int childIndexB,
            ref ConvexContactManifold manifold)
            => true;

        public void Dispose() { }
    }

    internal struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        private Vector3 _gravity;
        private Vector3Wide _gravityWide;

        public PoseIntegratorCallbacks(Vector3 gravity) { _gravity = gravity; _gravityWide = default; }

        public readonly AngularIntegrationMode AngularIntegrationMode
            => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation) { }

        public void PrepareForIntegration(float dt)
        {
            _gravityWide = Vector3Wide.Broadcast(_gravity * dt);
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
            BodyInertiaWide localInertia, Vector<int> integrationMask,
            int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            velocity.Linear += _gravityWide;
        }
    }
}
```

---

### 6.2 2D 물리: Aether.Physics2D 통합

> **주의:** NuGet 패키지는 `Aether.Physics2D v2.2.0`입니다.
> (`Box2D.NetStandard`이 아닙니다!)

**PhysicsWorld2D.cs (IronRose.Physics):**
```csharp
using tainicom.Aether.Physics2D.Dynamics;
using tainicom.Aether.Physics2D.Common;
using AetherVector2 = Microsoft.Xna.Framework.Vector2;

namespace IronRose.Physics
{
    public class PhysicsWorld2D : IDisposable
    {
        private World _world = null!;
        internal World World => _world;

        public void Initialize(float gravityX = 0, float gravityY = -9.81f)
        {
            _world = new World(new AetherVector2(gravityX, gravityY));
            Console.WriteLine("[Physics2D] Initialized");
        }

        public void Step(float deltaTime)
        {
            _world.Step(deltaTime);
        }

        public Body CreateDynamicBody(float posX, float posY)
        {
            var body = _world.CreateBody(new AetherVector2(posX, posY), 0f, BodyType.Dynamic);
            return body;
        }

        public Body CreateStaticBody(float posX, float posY)
        {
            var body = _world.CreateBody(new AetherVector2(posX, posY), 0f, BodyType.Static);
            return body;
        }

        public Body CreateKinematicBody(float posX, float posY)
        {
            var body = _world.CreateBody(new AetherVector2(posX, posY), 0f, BodyType.Kinematic);
            return body;
        }

        // Aether API: 도형을 Body에 직접 Fixture로 추가
        public void AttachRectangle(Body body, float width, float height, float density)
        {
            body.CreateRectangle(width, height, density, AetherVector2.Zero);
        }

        public void AttachCircle(Body body, float radius, float density)
        {
            body.CreateCircle(radius, density);
        }

        public void Dispose()
        {
            _world?.Clear();
        }
    }
}
```

---

### 6.3 물리 통합 관리자

**Physics/PhysicsManager.cs (IronRose.Engine):**

> PhysicsWorld3D/2D를 통합하고, Rigidbody↔Bepu / Rigidbody2D↔Aether 동기화를 담당합니다.

```csharp
using IronRose.Physics;
using RoseEngine;

namespace IronRose.Engine
{
    public class PhysicsManager
    {
        private PhysicsWorld3D _world3D = new();
        private PhysicsWorld2D _world2D = new();

        internal PhysicsWorld3D World3D => _world3D;
        internal PhysicsWorld2D World2D => _world2D;

        // 싱글턴 (EngineCore에서 설정)
        internal static PhysicsManager? Instance { get; set; }

        public void Initialize()
        {
            _world3D.Initialize();
            _world2D.Initialize();
            Instance = this;
        }

        public void FixedUpdate(float fixedDeltaTime)
        {
            // 1. Transform → Physics 동기화 (Kinematic 등)
            SyncTransformsToPhysics();

            // 2. 물리 시뮬레이션 스텝
            _world3D.Step(fixedDeltaTime);
            _world2D.Step(fixedDeltaTime);

            // 3. Physics → Transform 동기화
            SyncPhysicsToTransforms();

            // 4. 충돌 콜백 디스패치
            DispatchCollisionCallbacks();
        }

        private void SyncTransformsToPhysics()
        {
            // Kinematic Rigidbody: Transform.position → Bepu Body Pose
            foreach (var rb in Rigidbody._allRigidbodies)
            {
                if (rb._isDestroyed || !rb.gameObject.activeInHierarchy) continue;
                if (rb.isKinematic)
                    rb.SyncToPhysics();
            }

            foreach (var rb2d in Rigidbody2D._allRigidbodies2D)
            {
                if (rb2d._isDestroyed || !rb2d.gameObject.activeInHierarchy) continue;
                if (rb2d.bodyType == RigidbodyType2D.Kinematic)
                    rb2d.SyncToPhysics();
            }
        }

        private void SyncPhysicsToTransforms()
        {
            // Dynamic Rigidbody: Bepu Body Pose → Transform.position
            foreach (var rb in Rigidbody._allRigidbodies)
            {
                if (rb._isDestroyed || !rb.gameObject.activeInHierarchy) continue;
                if (!rb.isKinematic)
                    rb.SyncFromPhysics();
            }

            foreach (var rb2d in Rigidbody2D._allRigidbodies2D)
            {
                if (rb2d._isDestroyed || !rb2d.gameObject.activeInHierarchy) continue;
                if (rb2d.bodyType == RigidbodyType2D.Dynamic)
                    rb2d.SyncFromPhysics();
            }
        }

        private void DispatchCollisionCallbacks()
        {
            // TODO: BepuPhysics/Aether 충돌 이벤트 → MonoBehaviour 콜백 매핑
        }

        public void Dispose()
        {
            _world3D.Dispose();
            _world2D.Dispose();
            Instance = null;
        }
    }
}
```

---

### 6.4 Unity 3D 물리 API

**Collider.cs (RoseEngine — 기본 클래스):**
```csharp
using BepuPhysics.Collidables;

namespace RoseEngine
{
    public abstract class Collider : Component
    {
        public bool isTrigger { get; set; }
        public Vector3 center { get; set; } = Vector3.zero;

        internal TypedIndex shapeIndex;
        internal bool isRegistered;

        internal abstract TypedIndex CreateShape();
    }
}
```

**BoxCollider.cs:**
```csharp
using BepuPhysics.Collidables;

namespace RoseEngine
{
    public class BoxCollider : Collider
    {
        public Vector3 size { get; set; } = Vector3.one;

        internal override TypedIndex CreateShape()
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return default;
            return mgr.World3D.AddBoxShape(size.x, size.y, size.z);
        }
    }
}
```

**SphereCollider.cs:**
```csharp
namespace RoseEngine
{
    public class SphereCollider : Collider
    {
        public float radius { get; set; } = 0.5f;

        internal override TypedIndex CreateShape()
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return default;
            return mgr.World3D.AddSphereShape(radius);
        }
    }
}
```

**CapsuleCollider.cs:**
```csharp
namespace RoseEngine
{
    public class CapsuleCollider : Collider
    {
        public float radius { get; set; } = 0.5f;
        public float height { get; set; } = 2f;

        internal override TypedIndex CreateShape()
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return default;
            return mgr.World3D.AddCapsuleShape(radius, height - 2f * radius);
        }
    }
}
```

**ForceMode.cs:**
```csharp
namespace RoseEngine
{
    public enum ForceMode
    {
        Force,           // 연속 힘 (mass 적용)
        Acceleration,    // 연속 가속 (mass 무시)
        Impulse,         // 순간 충격 (mass 적용)
        VelocityChange   // 순간 속도 변경 (mass 무시)
    }
}
```

**Rigidbody.cs:**
```csharp
using BepuPhysics;

namespace RoseEngine
{
    public class Rigidbody : Component
    {
        internal static readonly List<Rigidbody> _allRigidbodies = new();
        internal BodyHandle? bodyHandle;

        public float mass { get; set; } = 1.0f;
        public float drag { get; set; } = 0f;
        public float angularDrag { get; set; } = 0.05f;
        public bool useGravity { get; set; } = true;
        public bool isKinematic { get; set; } = false;

        public Vector3 velocity
        {
            get
            {
                if (bodyHandle == null) return Vector3.zero;
                var mgr = IronRose.Engine.PhysicsManager.Instance;
                if (mgr == null) return Vector3.zero;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                return new Vector3(bv.Linear.X, bv.Linear.Y, bv.Linear.Z);
            }
            set
            {
                if (bodyHandle == null) return;
                var mgr = IronRose.Engine.PhysicsManager.Instance;
                if (mgr == null) return;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                bv.Linear = new System.Numerics.Vector3(value.x, value.y, value.z);
                mgr.World3D.SetBodyVelocity(bodyHandle.Value, bv);
            }
        }

        public Vector3 angularVelocity
        {
            get
            {
                if (bodyHandle == null) return Vector3.zero;
                var mgr = IronRose.Engine.PhysicsManager.Instance;
                if (mgr == null) return Vector3.zero;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                return new Vector3(bv.Angular.X, bv.Angular.Y, bv.Angular.Z);
            }
            set
            {
                if (bodyHandle == null) return;
                var mgr = IronRose.Engine.PhysicsManager.Instance;
                if (mgr == null) return;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                bv.Angular = new System.Numerics.Vector3(value.x, value.y, value.z);
                mgr.World3D.SetBodyVelocity(bodyHandle.Value, bv);
            }
        }

        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            if (bodyHandle == null) return;
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;
            var f = new System.Numerics.Vector3(force.x, force.y, force.z);
            mgr.World3D.ApplyImpulse(bodyHandle.Value, f, System.Numerics.Vector3.Zero);
        }

        internal void SyncFromPhysics()
        {
            if (bodyHandle == null) return;
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;
            var pose = mgr.World3D.GetBodyPose(bodyHandle.Value);
            transform.position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            transform.rotation = new Quaternion(pose.Orientation.X, pose.Orientation.Y,
                                                 pose.Orientation.Z, pose.Orientation.W);
        }

        internal void SyncToPhysics()
        {
            // Kinematic: Transform → Bepu (향후 구현)
        }

        internal override void OnAddedToGameObject()
        {
            _allRigidbodies.Add(this);
            RegisterWithPhysics();
        }

        private void RegisterWithPhysics()
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;

            // Collider에서 shape 가져오기
            var collider = gameObject.GetComponent<Collider>();
            if (collider == null) return;

            if (!collider.isRegistered)
            {
                collider.shapeIndex = collider.CreateShape();
                collider.isRegistered = true;
            }

            var pos = transform.position;
            var rot = transform.rotation;
            bodyHandle = mgr.World3D.AddDynamicBody(
                new System.Numerics.Vector3(pos.x, pos.y, pos.z),
                new System.Numerics.Quaternion(rot.x, rot.y, rot.z, rot.w),
                collider.shapeIndex,
                mass
            );
        }

        internal static void ClearAll() => _allRigidbodies.Clear();
    }
}
```

---

### 6.5 Unity 2D 물리 API

**Collider2D.cs (기본 클래스):**
```csharp
namespace RoseEngine
{
    public abstract class Collider2D : Component
    {
        public bool isTrigger { get; set; }
        public Vector2 offset { get; set; } = Vector2.zero;
    }

    public enum RigidbodyType2D { Dynamic, Kinematic, Static }
}
```

**BoxCollider2D.cs:**
```csharp
namespace RoseEngine
{
    public class BoxCollider2D : Collider2D
    {
        public Vector2 size { get; set; } = Vector2.one;
    }
}
```

**CircleCollider2D.cs:**
```csharp
namespace RoseEngine
{
    public class CircleCollider2D : Collider2D
    {
        public float radius { get; set; } = 0.5f;
    }
}
```

**Rigidbody2D.cs:**
```csharp
using tainicom.Aether.Physics2D.Dynamics;
using AetherVector2 = Microsoft.Xna.Framework.Vector2;

namespace RoseEngine
{
    public class Rigidbody2D : Component
    {
        internal static readonly List<Rigidbody2D> _allRigidbodies2D = new();
        internal Body? aetherBody;

        public RigidbodyType2D bodyType { get; set; } = RigidbodyType2D.Dynamic;
        public float mass { get; set; } = 1.0f;
        public float gravityScale { get; set; } = 1.0f;
        public float drag { get; set; } = 0f;
        public float angularDrag { get; set; } = 0.05f;

        public Vector2 velocity
        {
            get
            {
                if (aetherBody == null) return Vector2.zero;
                var v = aetherBody.LinearVelocity;
                return new Vector2(v.X, v.Y);
            }
            set
            {
                aetherBody?.SetLinearVelocity(new AetherVector2(value.x, value.y));
            }
        }

        public float angularVelocity
        {
            get => aetherBody?.AngularVelocity ?? 0f;
            set { if (aetherBody != null) aetherBody.AngularVelocity = value; }
        }

        public void AddForce(Vector2 force)
        {
            aetherBody?.ApplyForce(new AetherVector2(force.x, force.y));
        }

        public void AddForce(Vector2 force, ForceMode2D mode)
        {
            if (aetherBody == null) return;
            if (mode == ForceMode2D.Impulse)
                aetherBody.ApplyLinearImpulse(new AetherVector2(force.x, force.y));
            else
                aetherBody.ApplyForce(new AetherVector2(force.x, force.y));
        }

        public void AddTorque(float torque)
        {
            aetherBody?.ApplyTorque(torque);
        }

        internal void SyncFromPhysics()
        {
            if (aetherBody == null) return;
            transform.position = new Vector3(aetherBody.Position.X, aetherBody.Position.Y,
                                              transform.position.z);
            transform.rotation = Quaternion.Euler(0, 0, aetherBody.Rotation * Mathf.Rad2Deg);
        }

        internal void SyncToPhysics()
        {
            if (aetherBody == null) return;
            aetherBody.Position = new AetherVector2(transform.position.x, transform.position.y);
            aetherBody.Rotation = transform.rotation.eulerAngles.z * Mathf.Deg2Rad;
        }

        internal override void OnAddedToGameObject()
        {
            _allRigidbodies2D.Add(this);
            RegisterWithPhysics();
        }

        private void RegisterWithPhysics()
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;

            var pos = transform.position;
            aetherBody = mgr.World2D.CreateDynamicBody(pos.x, pos.y);
            aetherBody.GravityScale = gravityScale;

            // Collider2D에서 Fixture 생성
            var boxCol = gameObject.GetComponent<BoxCollider2D>();
            if (boxCol != null)
            {
                mgr.World2D.AttachRectangle(aetherBody, boxCol.size.x, boxCol.size.y, 1f);
                return;
            }
            var circleCol = gameObject.GetComponent<CircleCollider2D>();
            if (circleCol != null)
            {
                mgr.World2D.AttachCircle(aetherBody, circleCol.radius, 1f);
            }
        }

        internal static void ClearAll() => _allRigidbodies2D.Clear();
    }

    public enum ForceMode2D { Force, Impulse }
}
```

---

### 6.6 Unity 물리 유틸리티 API

**Collision.cs / Collision2D.cs (충돌 데이터):**
```csharp
namespace RoseEngine
{
    public class Collision
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Rigidbody rigidbody { get; internal set; } = null!;
        public Collider collider { get; internal set; } = null!;
        public Vector3 relativeVelocity { get; internal set; }
        public ContactPoint[] contacts { get; internal set; } = Array.Empty<ContactPoint>();
    }

    public struct ContactPoint
    {
        public Vector3 point;
        public Vector3 normal;
        public float separation;
    }

    public class Collision2D
    {
        public GameObject gameObject { get; internal set; } = null!;
        public Rigidbody2D rigidbody { get; internal set; } = null!;
        public Collider2D collider { get; internal set; } = null!;
        public Vector2 relativeVelocity { get; internal set; }
        public ContactPoint2D[] contacts { get; internal set; } = Array.Empty<ContactPoint2D>();
    }

    public struct ContactPoint2D
    {
        public Vector2 point;
        public Vector2 normal;
        public float separation;
    }
}
```

**Physics.cs (3D Raycast 등):**
```csharp
namespace RoseEngine
{
    public struct RaycastHit
    {
        public GameObject gameObject;
        public Collider collider;
        public float distance;
        public Vector3 point;
        public Vector3 normal;
    }

    public static class Physics
    {
        public static Vector3 gravity
        {
            get => new Vector3(0, -9.81f, 0);
            // set → PhysicsWorld3D gravity 변경
        }

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
            float maxDistance = Mathf.Infinity)
        {
            // TODO: BepuPhysics Simulation.RayCast 호출
            hit = default;
            return false;
        }

        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction,
            float maxDistance = Mathf.Infinity)
        {
            return Array.Empty<RaycastHit>();
        }

        public static Collider[] OverlapSphere(Vector3 position, float radius)
        {
            return Array.Empty<Collider>();
        }

        public static bool CheckSphere(Vector3 position, float radius)
        {
            return false;
        }
    }
}
```

**Physics2D.cs:**
```csharp
namespace RoseEngine
{
    public struct RaycastHit2D
    {
        public GameObject gameObject;
        public Collider2D collider;
        public float distance;
        public Vector2 point;
        public Vector2 normal;
        public float fraction;
    }

    public static class Physics2D
    {
        public static Vector2 gravity
        {
            get => new Vector2(0, -9.81f);
        }

        public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction,
            float distance = Mathf.Infinity)
        {
            // TODO: Aether World.RayCast 호출
            return default;
        }

        public static Collider2D[] OverlapCircle(Vector2 point, float radius)
        {
            return Array.Empty<Collider2D>();
        }
    }
}
```

---

### 6.7 SceneManager 통합

**SceneManager.cs ExecuteDestroy() 추가 항목:**
```csharp
// 기존 MeshRenderer, SpriteRenderer, Light 정리 패턴과 동일하게:
if (comp is Rigidbody rb)
    Rigidbody._allRigidbodies.Remove(rb);

if (comp is Rigidbody2D rb2d)
    Rigidbody2D._allRigidbodies2D.Remove(rb2d);
```

**SceneManager.Clear() 추가:**
```csharp
Rigidbody.ClearAll();
Rigidbody2D.ClearAll();
```

---

## 파일 구성 요약

```
src/IronRose.Physics/                    (NuGet 래퍼, Engine 미참조)
├── PhysicsWorld3D.cs                    (~130줄) BepuPhysics v2.4.0
├── PhysicsWorld2D.cs                    (~60줄) Aether.Physics2D v2.2.0
└── IronRose.Physics.csproj

src/IronRose.Engine/
├── Physics/
│   └── PhysicsManager.cs               (~100줄) 통합 관리자
└── RoseEngine/
    ├── Collider.cs                      (~20줄) 3D 콜라이더 기본 클래스
    ├── BoxCollider.cs                   (~15줄)
    ├── SphereCollider.cs                (~15줄)
    ├── CapsuleCollider.cs               (~15줄)
    ├── Rigidbody.cs                     (~120줄) BepuPhysics ↔ Transform 동기화
    ├── Collider2D.cs                    (~15줄) 2D 콜라이더 기본 클래스
    ├── BoxCollider2D.cs                 (~10줄)
    ├── CircleCollider2D.cs              (~10줄)
    ├── Rigidbody2D.cs                   (~100줄) Aether ↔ Transform 동기화
    ├── Collision.cs                     (~30줄) 충돌 데이터 구조체
    ├── Physics.cs                       (~40줄) Raycast, OverlapSphere 등
    ├── Physics2D.cs                     (~30줄) 2D Raycast, OverlapCircle 등
    └── ForceMode.cs                     (~10줄) ForceMode, ForceMode2D enum
```

**신규 파일: ~16개**
**수정 파일: ~5개** (MonoBehaviour, EngineCore, SceneManager, Time, IronRose.Physics.csproj, IronRose.Engine.csproj)

---

## 구현 순서

1. **6.0 사전 작업** — MonoBehaviour.FixedUpdate + EngineCore 누적기 + Time.fixedDeltaTime
2. **6.1 PhysicsWorld3D** — BepuPhysics 래퍼 (IronRose.Physics)
3. **6.2 PhysicsWorld2D** — Aether 래퍼 (IronRose.Physics)
4. **6.3 PhysicsManager** — 통합 관리자 (IronRose.Engine)
5. **6.4 3D 컴포넌트** — Collider, BoxCollider, SphereCollider, CapsuleCollider, Rigidbody
6. **6.5 2D 컴포넌트** — Collider2D, BoxCollider2D, CircleCollider2D, Rigidbody2D
7. **6.6 유틸리티** — Physics, Physics2D, Collision, ForceMode
8. **6.7 통합** — SceneManager 정리, EngineCore 연결

---

## 검증 기준

**LiveCode 테스트 스크립트:**
```csharp
public class FallingCube : MonoBehaviour
{
    public override void Start()
    {
        // 바닥
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.transform.position = new Vector3(0, -2, 5);
        floor.transform.localScale = new Vector3(10, 0.2f, 10);
        floor.AddComponent<BoxCollider>();
        // Static → Rigidbody 없음 (또는 isKinematic)

        // 떨어지는 큐브
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.position = new Vector3(0, 5, 5);
        var col = cube.AddComponent<BoxCollider>();
        var rb = cube.AddComponent<Rigidbody>();
        rb.mass = 1.0f;
        rb.useGravity = true;
    }

    public override void FixedUpdate()
    {
        // FixedUpdate에서 물리 관련 로직
    }

    public override void Update()
    {
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
            Debug.Log($"Velocity: {rb.velocity}");
    }
}
```

- [x] 큐브가 바닥으로 떨어지는 중력 시뮬레이션 ✅
- [x] 바닥에서 정지 (충돌 + 안정화) ✅
- [x] MonoBehaviour.FixedUpdate() 50Hz 호출 확인 ✅
- [ ] Raycast로 마우스 클릭 오브젝트 감지 (향후)
- [ ] 두 오브젝트 충돌 시 OnCollisionEnter 콜백 호출 (향후)

---

## 참고 자료
- [BepuPhysics v2 GitHub](https://github.com/bepu/bepuphysics2)
- [BepuPhysics v2 Demos](https://github.com/bepu/bepuphysics2/tree/master/Demos)
- [Aether.Physics2D GitHub](https://github.com/tainicom/Aether.Physics2D)
- [Aether.Physics2D Samples](https://github.com/tainicom/Aether.Physics2D/tree/master/Samples)

---

## 예상 소요 시간
**3-5일** (선택사항이므로 필요시에만)

---

## 다음 단계
→ [Phase 7: Deferred Rendering & PBR](Phase7_DeferredPBR.md)
