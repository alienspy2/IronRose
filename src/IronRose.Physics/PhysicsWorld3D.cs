// ------------------------------------------------------------
// @file    PhysicsWorld3D.cs
// @brief   BepuPhysics v2.4.0 Simulation을 래핑하여 3D 물리 월드를 관리한다.
//          Dynamic/Static/Kinematic body 추가/제거, Pose/Velocity 조회/설정,
//          Capsule Sweep/Overlap 쿼리, Body-UserData 매핑을 제공한다.
// @deps    BepuPhysics, BepuUtilities, RoseEngine.Debug
// @exports
//   struct SweepHit                                            — Sweep 쿼리 결과 (T, Normal, HitPosition, Collidable)
//   class PhysicsWorld3D
//     Initialize(Vector3?): void                                — 시뮬레이션 초기화
//     Step(float): void                                         — 시뮬레이션 스텝
//     AddDynamic*(..): BodyHandle                               — Dynamic body 추가 (Box/Sphere/Capsule/Cylinder)
//     AddStatic*(..): StaticHandle                              — Static body 추가 (Box/Sphere/Capsule/Cylinder)
//     AddKinematicBox(..): BodyHandle                           — Kinematic box body 추가
//     BodyExists(BodyHandle): bool                              — body 존재 확인
//     Get/SetBodyPose, Get/SetBodyVelocity                     — body 상태 조회/수정
//     ApplyLinearImpulse, ApplyAngularImpulse                  — 임펄스 적용
//     SetBodyUseGravity(BodyHandle, bool): void                — 중력 사용 여부 설정
//     RemoveBody(BodyHandle): void                              — dynamic body 제거 (UserData도 정리)
//     RemoveStatic(StaticHandle): void                          — static body 제거 (UserData도 정리)
//     SetStaticPose(StaticHandle, Vector3, Quaternion): void   — static body 위치/회전 설정
//     SweepCapsule(..): bool                                    — 캡슐 sweep 최초 충돌 검출
//     OverlapCapsule(..): int                                   — 캡슐 겹침 검출
//     Set/GetBodyUserData, Set/GetStaticUserData, GetUserData  — handle-object 매핑
//     Reset(): void                                             — 시뮬레이션 리셋
//     Dispose(): void                                           — 리소스 해제
// @note    Sweep은 BepuPhysics Simulation.Sweep을 사용하며, sweepDuration=1로 velocity=direction*maxDistance 방식.
//          UserData 매핑은 Dictionary<int,object>로 BodyHandle.Value/StaticHandle.Value를 키로 사용.
//          스레드 모델: Step/Reset/Dispose 및 Body/Static 수정 API 는 메인 스레드 전용.
//          Step() 진입부에서 ThreadGuard.CheckMainThread 로 위반을 감지한다 (로그 후 조기 리턴).
//          ContactEventCollector.RecordContact 만 worker thread 에서 호출되며 내부 lock 으로 보호.
// ------------------------------------------------------------
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using RoseEngine;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace IronRose.Physics
{
    // --- 충돌 이벤트 수집기 (NarrowPhaseCallbacks에서 스레드 안전하게 접촉 쌍을 기록) ---

    /// <summary>
    /// 현재 프레임에서 접촉 중인 collidable 쌍을 수집합니다.
    /// 스레드 모델:
    /// - <see cref="RecordContact"/> 는 BepuPhysics worker thread 에서 호출되며 내부 lock 으로 보호.
    /// - <see cref="Flush"/> / <see cref="Clear"/> / <see cref="GetContactingIds"/> 는 **메인 스레드 전용**.
    ///   Timestep 종료 직후에 호출되므로 worker thread 의 RecordContact 와 시간적으로 겹치지 않는다.
    /// </summary>
    internal class ContactEventCollector
    {
        private readonly object _lock = new();
        private HashSet<(int, int)> _currentContacts = new();
        private HashSet<(int, int)> _previousContacts = new();

        /// <summary>현재 프레임에서 접촉이 발생한 쌍을 기록합니다 (멀티스레드 safe).</summary>
        public void RecordContact(CollidableReference a, CollidableReference b)
        {
            int idA = GetCollidableId(a);
            int idB = GetCollidableId(b);
            // 순서를 정규화하여 (A,B)와 (B,A)를 같은 쌍으로 취급
            var pair = idA < idB ? (idA, idB) : (idB, idA);
            lock (_lock)
            {
                _currentContacts.Add(pair);
            }
        }

        /// <summary>Step 종료 후 호출. Enter/Stay/Exit 이벤트를 분류하여 반환합니다.</summary>
        public void Flush(
            out List<(int, int)> entered,
            out List<(int, int)> staying,
            out List<(int, int)> exited)
        {
            entered = new List<(int, int)>();
            staying = new List<(int, int)>();
            exited = new List<(int, int)>();

            foreach (var pair in _currentContacts)
            {
                if (_previousContacts.Contains(pair))
                    staying.Add(pair);
                else
                    entered.Add(pair);
            }

            foreach (var pair in _previousContacts)
            {
                if (!_currentContacts.Contains(pair))
                    exited.Add(pair);
            }

            // 현재를 이전으로 이동, 현재를 초기화
            (_previousContacts, _currentContacts) = (_currentContacts, _previousContacts);
            _currentContacts.Clear();
        }

        /// <summary>씬 전환 시 모든 접촉 기록을 초기화합니다.</summary>
        public void Clear()
        {
            _currentContacts.Clear();
            _previousContacts.Clear();
        }

        /// <summary>지정된 collidable ID와 접촉 중인 다른 collidable ID 목록을 반환합니다.</summary>
        public List<int> GetContactingIds(int collidableId)
        {
            var result = new List<int>();
            foreach (var (a, b) in _previousContacts)
            {
                if (a == collidableId) result.Add(b);
                else if (b == collidableId) result.Add(a);
            }
            return result;
        }

        /// <summary>CollidableReference를 정수 ID로 변환. Dynamic body와 Static body를 구별하기 위해 Static은 음수.</summary>
        private static int GetCollidableId(CollidableReference col)
        {
            if (col.Mobility == CollidableMobility.Static)
                return -(col.StaticHandle.Value + 1); // 음수로 offset (0과 구별)
            else
                return col.BodyHandle.Value;
        }
    }

    // --- Sweep 결과 구조체 ---

    /// <summary>Sweep 쿼리의 충돌 결과를 담는 구조체.</summary>
    public struct SweepHit
    {
        /// <summary>0~1 normalized hit time (0=시작, 1=maxDistance 끝).</summary>
        public float T;
        /// <summary>충돌 법선 (world space).</summary>
        public Vector3 Normal;
        /// <summary>충돌 지점 (world space).</summary>
        public Vector3 HitPosition;
        /// <summary>충돌한 오브젝트의 BepuPhysics CollidableReference.</summary>
        public CollidableReference Collidable;
    }

    /// <summary>RayCast 쿼리의 충돌 결과를 담는 구조체.</summary>
    public struct RayHit
    {
        /// <summary>origin으로부터의 히트 거리.</summary>
        public float Distance;
        /// <summary>충돌 법선 (world space).</summary>
        public Vector3 Normal;
        /// <summary>충돌 지점 (world space).</summary>
        public Vector3 Point;
        /// <summary>충돌한 오브젝트에 연결된 UserData (Collider 또는 Rigidbody).</summary>
        public object? UserData;
    }

    // --- IRayHitHandler 구현: 가장 가까운 레이 충돌만 수집 ---

    internal struct ClosestRayHitHandler : IRayHitHandler
    {
        public float ClosestT;
        public Vector3 ClosestNormal;
        public CollidableReference ClosestCollidable;
        public bool HasHit;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRayHit(in RayData ray, ref float maximumT, float t,
                             in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t < ClosestT)
            {
                ClosestT = t;
                ClosestNormal = normal;
                ClosestCollidable = collidable;
                HasHit = true;
                maximumT = t;
            }
        }
    }

    // --- IRayHitHandler 구현: 모든 레이 충돌 수집 ---

    internal struct AllRayHitHandler : IRayHitHandler
    {
        public List<(float T, Vector3 Normal, CollidableReference Collidable)> Hits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRayHit(in RayData ray, ref float maximumT, float t,
                             in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            Hits.Add((t, normal, collidable));
        }
    }

    // --- ISweepHitHandler 구현: 가장 가까운 충돌만 수집 ---

    internal struct ClosestHitHandler : ISweepHitHandler
    {
        public float ClosestT;
        public Vector3 ClosestNormal;
        public Vector3 ClosestHitPosition;
        public CollidableReference ClosestCollidable;
        public bool HasHit;
        public int ExcludeBodyHandle;
        public int[]? ExcludeStaticHandles;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Static)
            {
                if (ExcludeStaticHandles != null)
                {
                    int val = collidable.StaticHandle.Value;
                    for (int i = 0; i < ExcludeStaticHandles.Length; i++)
                        if (ExcludeStaticHandles[i] == val) return false;
                }
            }
            else
            {
                if (ExcludeBodyHandle >= 0 && collidable.BodyHandle.Value == ExcludeBodyHandle)
                    return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation,
                          in Vector3 hitNormal, CollidableReference collidable)
        {
            if (t < ClosestT)
            {
                ClosestT = t;
                ClosestNormal = hitNormal;
                ClosestHitPosition = hitLocation;
                ClosestCollidable = collidable;
                HasHit = true;
                maximumT = t;
            }
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            HasHit = true;
            ClosestT = 0;
            ClosestCollidable = collidable;
            maximumT = 0;
        }
    }

    // --- ISweepHitHandler 구현: 겹치는 collidable 수집 (OverlapCapsule용) ---

    internal struct OverlapCollectHandler : ISweepHitHandler
    {
        public CollidableReference[] Results;
        public int MaxCount;
        public int Count;
        public int ExcludeStaticHandle;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Static
                && ExcludeStaticHandle >= 0
                && collidable.StaticHandle.Value == ExcludeStaticHandle)
                return false;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation,
                          in Vector3 hitNormal, CollidableReference collidable)
        {
            if (Count < MaxCount)
                Results[Count++] = collidable;
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            if (Count < MaxCount)
                Results[Count++] = collidable;
        }
    }

    /// <summary>
    /// BepuPhysics Simulation 래퍼. 3D 물리 월드를 관리한다.
    /// 스레드 모델:
    /// - <see cref="Step"/> 및 <see cref="Flush"/> / <see cref="Reset"/> / <see cref="Dispose"/> 는 **메인 스레드 전용**.
    /// - Body/Static 추가·제거·수정 계열 API 도 메인 스레드 전용.
    /// - <see cref="ContactEventCollector.RecordContact"/> 는 BepuPhysics worker thread 에서 호출되며 내부 lock 으로 보호된다.
    /// - <see cref="ContactEventCollector.Flush"/> 는 Step 내부에서만 호출되며, 이 시점에는 worker thread 의 RecordContact 가 완료된 것이 보장된다.
    /// </summary>
    public class PhysicsWorld3D : IDisposable
    {
        private Simulation _simulation = null!;
        private BufferPool _bufferPool = null!;
        private ThreadDispatcher _threadDispatcher = null!;
        private Vector3 _gravity;
        internal readonly HashSet<int> _noGravityBodies = new();

        // --- UserData 매핑 (handle → 사용자 오브젝트) ---
        private readonly Dictionary<int, object> _bodyUserData = new();
        private readonly Dictionary<int, object> _staticUserData = new();

        // --- 충돌 이벤트 수집 ---
        private readonly ContactEventCollector _contactCollector = new();

        public void Initialize(Vector3? gravity = null)
        {
            var g = gravity ?? new Vector3(0, -9.81f, 0);
            _gravity = g;

            _bufferPool = new BufferPool();
            var threadCount = Math.Max(2, Environment.ProcessorCount - 2);
            _threadDispatcher = new ThreadDispatcher(threadCount, 16384);

            _simulation = Simulation.Create(
                _bufferPool,
                new NarrowPhaseCallbacks(_contactCollector),
                new PoseIntegratorCallbacks(g, _noGravityBodies),
                new SolveDescription(8, 1)
            );

            EditorDebug.Log($"[Physics3D] Initialized with {threadCount} threads");
        }

        private int _stepCount;

        // --- 충돌 이벤트 결과 (Step 후 FlushContactEvents로 채워짐) ---
        private List<(int, int)> _enteredPairs = new();
        private List<(int, int)> _stayingPairs = new();
        private List<(int, int)> _exitedPairs = new();

        public void Step(float deltaTime)
        {
            // 메인 전용 — 위반 시 LogError 후 안전하게 조기 리턴 (unsafe 상태에서 Timestep 호출 금지).
            if (!ThreadGuard.CheckMainThread("PhysicsWorld3D.Step")) return;

            _stepCount++;
            if (_stepCount <= 3 || _stepCount % 300 == 0)
            {
                EditorDebug.Log($"[Physics3D:Step#{_stepCount}] bodies={_simulation.Bodies.ActiveSet.Count} statics={_simulation.Statics.Count} noGravity={_noGravityBodies.Count} ({string.Join(",", _noGravityBodies)})");
            }
            _simulation.Timestep(deltaTime, _threadDispatcher);

            // Narrow phase에서 수집된 접촉 쌍을 Enter/Stay/Exit로 분류.
            // _contactCollector.Flush 자체는 lock 이 없으나, 호출 시점이 Timestep 직후이고
            // 메인 스레드이므로 worker thread 의 RecordContact 는 이미 종료되어 있다.
            _contactCollector.Flush(out _enteredPairs, out _stayingPairs, out _exitedPairs);
        }

        /// <summary>현재 스텝에서 새로 접촉이 시작된 쌍 (collidable ID 쌍).</summary>
        public IReadOnlyList<(int IdA, int IdB)> EnteredPairs => _enteredPairs;

        /// <summary>현재 스텝에서 접촉이 유지 중인 쌍.</summary>
        public IReadOnlyList<(int IdA, int IdB)> StayingPairs => _stayingPairs;

        /// <summary>현재 스텝에서 접촉이 끝난 쌍.</summary>
        public IReadOnlyList<(int IdA, int IdB)> ExitedPairs => _exitedPairs;

        // --- Dynamic Body (shape 생성 + body 등록을 한번에) ---

        public BodyHandle AddDynamicBox(Vector3 position, Quaternion rotation,
            float width, float height, float length, float mass)
        {
            var shape = new Box(width, height, length);
            var desc = BodyDescription.CreateConvexDynamic(
                new RigidPose(position, rotation), mass, _simulation.Shapes, shape);
            desc.Activity = new BodyActivityDescription(-1);
            return _simulation.Bodies.Add(desc);
        }

        public BodyHandle AddDynamicSphere(Vector3 position, Quaternion rotation,
            float radius, float mass)
        {
            var shape = new Sphere(radius);
            var desc = BodyDescription.CreateConvexDynamic(
                new RigidPose(position, rotation), mass, _simulation.Shapes, shape);
            desc.Activity = new BodyActivityDescription(-1);
            return _simulation.Bodies.Add(desc);
        }

        public BodyHandle AddDynamicCapsule(Vector3 position, Quaternion rotation,
            float radius, float length, float mass)
        {
            var shape = new Capsule(radius, length);
            var desc = BodyDescription.CreateConvexDynamic(
                new RigidPose(position, rotation), mass, _simulation.Shapes, shape);
            desc.Activity = new BodyActivityDescription(-1);
            return _simulation.Bodies.Add(desc);
        }

        public BodyHandle AddDynamicCylinder(Vector3 position, Quaternion rotation,
            float radius, float height, float mass)
        {
            var shape = new Cylinder(radius, height);
            var desc = BodyDescription.CreateConvexDynamic(
                new RigidPose(position, rotation), mass, _simulation.Shapes, shape);
            desc.Activity = new BodyActivityDescription(-1);
            return _simulation.Bodies.Add(desc);
        }

        // --- Static Body ---

        public StaticHandle AddStaticBox(Vector3 position, Quaternion rotation,
            float width, float height, float length)
        {
            var shape = new Box(width, height, length);
            var shapeIndex = _simulation.Shapes.Add(shape);
            return _simulation.Statics.Add(
                new StaticDescription(position, rotation, shapeIndex)
            );
        }

        public StaticHandle AddStaticSphere(Vector3 position, Quaternion rotation,
            float radius)
        {
            var shape = new Sphere(radius);
            var shapeIndex = _simulation.Shapes.Add(shape);
            return _simulation.Statics.Add(
                new StaticDescription(position, rotation, shapeIndex)
            );
        }

        public StaticHandle AddStaticCapsule(Vector3 position, Quaternion rotation,
            float radius, float length)
        {
            var shape = new Capsule(radius, length);
            var shapeIndex = _simulation.Shapes.Add(shape);
            return _simulation.Statics.Add(
                new StaticDescription(position, rotation, shapeIndex)
            );
        }

        public StaticHandle AddStaticCylinder(Vector3 position, Quaternion rotation,
            float radius, float height)
        {
            var shape = new Cylinder(radius, height);
            var shapeIndex = _simulation.Shapes.Add(shape);
            return _simulation.Statics.Add(
                new StaticDescription(position, rotation, shapeIndex)
            );
        }

        // --- Kinematic Body ---

        public BodyHandle AddKinematicBox(Vector3 position, Quaternion rotation,
            float width, float height, float length)
        {
            var shape = new Box(width, height, length);
            return _simulation.Bodies.Add(
                BodyDescription.CreateConvexKinematic(
                    new RigidPose(position, rotation), _simulation.Shapes, shape)
            );
        }

        // --- Body 조회/수정 ---

        /// <summary>지정된 BodyHandle이 시뮬레이션에 존재하는지 확인합니다.</summary>
        public bool BodyExists(BodyHandle handle)
        {
            return _simulation.Bodies.BodyExists(handle);
        }

        public RigidPose GetBodyPose(BodyHandle handle)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] GetBodyPose: body handle {handle.Value} does not exist");
                return default;
            }
            return _simulation.Bodies[handle].Pose;
        }

        public BodyVelocity GetBodyVelocity(BodyHandle handle)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] GetBodyVelocity: body handle {handle.Value} does not exist");
                return default;
            }
            return _simulation.Bodies[handle].Velocity;
        }

        public void SetBodyVelocity(BodyHandle handle, BodyVelocity velocity)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] SetBodyVelocity: body handle {handle.Value} does not exist");
                return;
            }
            var bodyRef = _simulation.Bodies[handle];
            if (!bodyRef.Awake)
                _simulation.Awakener.AwakenBody(handle);
            bodyRef.Velocity = velocity;
        }

        public void ApplyLinearImpulse(BodyHandle handle, Vector3 impulse)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] ApplyLinearImpulse: body handle {handle.Value} does not exist");
                return;
            }
            var bodyRef = _simulation.Bodies[handle];
            if (!bodyRef.Awake)
                _simulation.Awakener.AwakenBody(handle);
            bodyRef.ApplyLinearImpulse(impulse);
        }

        public void ApplyAngularImpulse(BodyHandle handle, Vector3 impulse)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] ApplyAngularImpulse: body handle {handle.Value} does not exist");
                return;
            }
            var bodyRef = _simulation.Bodies[handle];
            if (!bodyRef.Awake)
                _simulation.Awakener.AwakenBody(handle);
            bodyRef.ApplyAngularImpulse(impulse);
        }

        public void SetBodyPose(BodyHandle handle, RigidPose pose)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] SetBodyPose: body handle {handle.Value} does not exist");
                return;
            }
            _simulation.Bodies[handle].Pose = pose;
        }

        /// <summary>Sleep 상태의 body를 깨웁니다. 이미 깨어있으면 아무 동작도 하지 않습니다.</summary>
        public void WakeBody(BodyHandle handle)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] WakeBody: body handle {handle.Value} does not exist");
                return;
            }
            var bodyRef = _simulation.Bodies[handle];
            if (!bodyRef.Awake)
                _simulation.Awakener.AwakenBody(handle);
        }

        public void SetBodyUseGravity(BodyHandle handle, bool useGravity)
        {
            EditorDebug.Log($"[Physics3D] SetBodyUseGravity handle={handle.Value} useGravity={useGravity}");
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] SetBodyUseGravity: body handle {handle.Value} does not exist");
                return;
            }
            if (useGravity)
                _noGravityBodies.Remove(handle.Value);
            else
                _noGravityBodies.Add(handle.Value);
        }

        /// <summary>body의 sleep 허용 여부를 설정합니다. allowSleep=false이면 SleepThreshold를 음수로 설정하여 자동 sleep을 방지합니다.</summary>
        public void SetBodyAllowSleep(BodyHandle handle, bool allowSleep)
        {
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] SetBodyAllowSleep: body handle {handle.Value} does not exist");
                return;
            }
            var bodyRef = _simulation.Bodies[handle];
            if (allowSleep)
            {
                // 기본 sleep threshold 복원 (BepuPhysics 기본값: 0.01)
                if (bodyRef.Activity.SleepThreshold < 0)
                    bodyRef.Activity.SleepThreshold = 0.01f;
            }
            else
            {
                // 음수로 설정하면 자동 sleep이 불가능해짐
                bodyRef.Activity.SleepThreshold = -1f;
            }
        }

        // --- Static Pose 설정 ---

        /// <summary>Static body의 위치/회전을 직접 설정합니다.</summary>
        public void SetStaticPose(StaticHandle handle, Vector3 position, Quaternion rotation)
        {
            _simulation.Statics[handle].Pose = new RigidPose(position, rotation);
        }

        // --- Sweep / Overlap 쿼리 ---

        /// <summary>캡슐을 direction 방향으로 sweep하여 최초 충돌을 반환합니다.</summary>
        /// <param name="position">캡슐 중심 위치</param>
        /// <param name="orientation">캡슐 방향</param>
        /// <param name="radius">캡슐 반지름</param>
        /// <param name="halfLength">반구 제외 원통 부분의 절반 길이</param>
        /// <param name="direction">이동 방향 (정규화)</param>
        /// <param name="maxDistance">최대 이동 거리</param>
        /// <param name="hit">충돌 결과</param>
        /// <param name="excludeStatic">제외할 static handle (자기 자신)</param>
        /// <returns>충돌이 있으면 true</returns>
        public bool SweepCapsule(
            Vector3 position,
            Quaternion orientation,
            float radius,
            float halfLength,
            Vector3 direction,
            float maxDistance,
            out SweepHit hit,
            StaticHandle? excludeStatic = null)
        {
            return SweepCapsule(position, orientation, radius, halfLength, direction, maxDistance, out hit,
                excludeStatic.HasValue ? new[] { excludeStatic.Value } : null);
        }

        public bool SweepCapsule(
            Vector3 position,
            Quaternion orientation,
            float radius,
            float halfLength,
            Vector3 direction,
            float maxDistance,
            out SweepHit hit,
            StaticHandle[]? excludeStatics)
        {
            hit = default;
            if (maxDistance <= 0f) return false;

            var capsule = new Capsule(radius, halfLength * 2f);
            var pose = new RigidPose(position, orientation);

            const float sweepDuration = 1.0f;
            var velocity = new BodyVelocity(direction * maxDistance);

            int[]? excludeArr = null;
            if (excludeStatics != null && excludeStatics.Length > 0)
            {
                excludeArr = new int[excludeStatics.Length];
                for (int i = 0; i < excludeStatics.Length; i++)
                    excludeArr[i] = excludeStatics[i].Value;
            }

            var handler = new ClosestHitHandler
            {
                ClosestT = float.MaxValue,
                HasHit = false,
                ExcludeBodyHandle = -1,
                ExcludeStaticHandles = excludeArr,
            };

            _simulation.Sweep(capsule, pose, velocity, sweepDuration, _bufferPool, ref handler);

            if (!handler.HasHit) return false;

            hit = new SweepHit
            {
                T = handler.ClosestT,
                Normal = handler.ClosestNormal,
                HitPosition = handler.ClosestHitPosition,
                Collidable = handler.ClosestCollidable,
            };
            return true;
        }

        /// <summary>지정 위치에서 캡슐과 겹치는 collidable 수를 검출합니다 (overlap recovery용).</summary>
        /// <param name="position">캡슐 중심 위치</param>
        /// <param name="orientation">캡슐 방향</param>
        /// <param name="radius">캡슐 반지름</param>
        /// <param name="halfLength">반구 제외 원통 부분의 절반 길이</param>
        /// <param name="results">결과를 담을 Span</param>
        /// <param name="excludeStatic">제외할 static handle</param>
        /// <returns>겹친 collidable 수</returns>
        public int OverlapCapsule(
            Vector3 position,
            Quaternion orientation,
            float radius,
            float halfLength,
            Span<CollidableReference> results,
            StaticHandle? excludeStatic = null)
        {
            // 간단한 구현: 매우 짧은 거리로 sweep하여 t=0에서의 겹침을 검출
            var capsule = new Capsule(radius, halfLength * 2f);
            var pose = new RigidPose(position, orientation);

            const float tinyDistance = 0.001f;
            var velocity = new BodyVelocity(new Vector3(0, tinyDistance, 0));

            var buffer = new CollidableReference[results.Length];
            var handler = new OverlapCollectHandler
            {
                Results = buffer,
                MaxCount = results.Length,
                Count = 0,
                ExcludeStaticHandle = excludeStatic.HasValue ? excludeStatic.Value.Value : -1,
            };

            _simulation.Sweep(capsule, pose, velocity, 1.0f, _bufferPool, ref handler);

            for (int i = 0; i < handler.Count; i++)
                results[i] = buffer[i];

            return handler.Count;
        }

        // --- RayCast 쿼리 ---

        /// <summary>가장 가까운 충돌을 반환하는 3D 레이캐스트. UserData를 포함한 RayHit를 반환한다.</summary>
        public bool RayCast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
        {
            hit = default;

            var dirLen = direction.Length();
            if (dirLen < 1e-8f || maxDistance <= 0f) return false;

            var normalizedDir = direction / dirLen;

            var handler = new ClosestRayHitHandler
            {
                ClosestT = float.MaxValue,
                HasHit = false,
            };

            _simulation.RayCast(origin, normalizedDir, maxDistance, ref handler);

            if (!handler.HasHit) return false;

            hit = new RayHit
            {
                Distance = handler.ClosestT,
                Normal = handler.ClosestNormal,
                Point = origin + normalizedDir * handler.ClosestT,
                UserData = GetUserData(handler.ClosestCollidable),
            };
            return true;
        }

        /// <summary>모든 충돌을 반환하는 3D 레이캐스트. UserData를 포함한 RayHit 리스트를 반환한다.</summary>
        public List<RayHit> RayCastAll(Vector3 origin, Vector3 direction, float maxDistance)
        {
            var dirLen = direction.Length();
            if (dirLen < 1e-8f || maxDistance <= 0f)
                return new List<RayHit>();

            var normalizedDir = direction / dirLen;

            var handler = new AllRayHitHandler
            {
                Hits = new List<(float, Vector3, CollidableReference)>(),
            };

            _simulation.RayCast(origin, normalizedDir, maxDistance, ref handler);

            var results = new List<RayHit>(handler.Hits.Count);
            foreach (var (t, normal, collidable) in handler.Hits)
            {
                results.Add(new RayHit
                {
                    Distance = t,
                    Normal = normal,
                    Point = origin + normalizedDir * t,
                    UserData = GetUserData(collidable),
                });
            }
            return results;
        }

        /// <summary>구 오버랩 쿼리 — Sweep 근사 방식. UserData 배열을 반환한다.</summary>
        public List<object?> OverlapSphere(Vector3 center, float radius, int maxResults = 64)
        {
            var sphere = new Sphere(radius);
            var pose = new RigidPose(center, Quaternion.Identity);

            const float tinyDistance = 0.001f;
            var velocity = new BodyVelocity(new Vector3(0, tinyDistance, 0));

            var buffer = new CollidableReference[maxResults];
            var handler = new OverlapCollectHandler
            {
                Results = buffer,
                MaxCount = maxResults,
                Count = 0,
                ExcludeStaticHandle = -1,
            };

            _simulation.Sweep(sphere, pose, velocity, 1.0f, _bufferPool, ref handler);

            var results = new List<object?>(handler.Count);
            for (int i = 0; i < handler.Count; i++)
                results.Add(GetUserData(buffer[i]));

            return results;
        }

        // --- UserData 매핑 ---

        /// <summary>BodyHandle에 사용자 데이터를 연결합니다.</summary>
        public void SetBodyUserData(BodyHandle handle, object data) => _bodyUserData[handle.Value] = data;

        /// <summary>StaticHandle에 사용자 데이터를 연결합니다.</summary>
        public void SetStaticUserData(StaticHandle handle, object data) => _staticUserData[handle.Value] = data;

        /// <summary>CollidableReference에 연결된 사용자 데이터를 반환합니다.</summary>
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

        /// <summary>ContactEventCollector의 정수 ID에서 UserData를 조회합니다.</summary>
        public object? GetUserDataByContactId(int contactId)
        {
            if (contactId < 0)
            {
                // Static body (ID = -(staticHandle.Value + 1))
                int staticVal = -(contactId + 1);
                _staticUserData.TryGetValue(staticVal, out var data);
                return data;
            }
            else
            {
                // Dynamic/Kinematic body (ID = bodyHandle.Value)
                _bodyUserData.TryGetValue(contactId, out var data);
                return data;
            }
        }

        /// <summary>ContactEventCollector의 정수 ID에서 body의 linear velocity를 조회합니다.</summary>
        public Vector3 GetVelocityByContactId(int contactId)
        {
            if (contactId < 0)
            {
                // Static body는 속도 0
                return Vector3.Zero;
            }
            var handle = new BodyHandle(contactId);
            if (!_simulation.Bodies.BodyExists(handle))
                return Vector3.Zero;
            var vel = _simulation.Bodies[handle].Velocity.Linear;
            return vel;
        }

        // --- Body/Static 제거 ---

        public void RemoveBody(BodyHandle handle)
        {
            _noGravityBodies.Remove(handle.Value);
            _bodyUserData.Remove(handle.Value);
            if (!_simulation.Bodies.BodyExists(handle))
            {
                EditorDebug.LogWarning($"[Physics3D] RemoveBody: body handle {handle.Value} does not exist, skipping");
                return;
            }

            // body 제거 전에, 이 body와 접촉 중인 다른 dynamic body들을 깨운다.
            // sleep 상태의 body는 접촉 상대가 사라져도 자동으로 깨어나지 않으므로,
            // 지지 구조물이 파괴될 때 위의 블록들이 공중에 떠 있는 문제를 방지한다.
            WakeContactingBodies(handle.Value);

            _simulation.Bodies.Remove(handle);
        }

        /// <summary>
        /// 지정된 collidable ID와 접촉 중인 모든 dynamic body를 깨웁니다.
        /// body 제거 전에 호출하여, sleep 중인 인접 body들이 중력에 반응하도록 합니다.
        /// </summary>
        private void WakeContactingBodies(int collidableId)
        {
            var contactingIds = _contactCollector.GetContactingIds(collidableId);
            foreach (var id in contactingIds)
            {
                // 양수 ID = dynamic body handle, 음수 ID = static handle (깨울 필요 없음)
                if (id >= 0)
                {
                    var bodyHandle = new BodyHandle(id);
                    if (_simulation.Bodies.BodyExists(bodyHandle))
                    {
                        var bodyRef = _simulation.Bodies[bodyHandle];
                        if (!bodyRef.Awake)
                            _simulation.Awakener.AwakenBody(bodyHandle);
                    }
                }
            }
        }

        public void RemoveStatic(StaticHandle handle)
        {
            _staticUserData.Remove(handle.Value);
            _simulation.Statics.Remove(handle);
        }

        /// <summary>시뮬레이션 내 모든 body/static 제거 (BufferPool, ThreadDispatcher 유지)</summary>
        public void Reset()
        {
            EditorDebug.Log("[Physics3D] Reset — recreating simulation");
            _simulation?.Dispose();
            _noGravityBodies.Clear();
            _bodyUserData.Clear();
            _staticUserData.Clear();
            _contactCollector.Clear();
            _enteredPairs.Clear();
            _stayingPairs.Clear();
            _exitedPairs.Clear();
            _stepCount = 0;
            _simulation = Simulation.Create(
                _bufferPool,
                new NarrowPhaseCallbacks(_contactCollector),
                new PoseIntegratorCallbacks(_gravity, _noGravityBodies),
                new SolveDescription(8, 1)
            );
        }

        public void Dispose()
        {
            _simulation?.Dispose();
            _threadDispatcher?.Dispose();
            _bufferPool?.Clear();
        }
    }

    // --- BepuPhysics NarrowPhase 콜백 ---

    internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        private ContactEventCollector _collector;

        public NarrowPhaseCallbacks(ContactEventCollector collector)
        {
            _collector = collector;
        }

        public void Initialize(Simulation simulation) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex,
            CollidableReference a, CollidableReference b,
            ref float speculativeMargin)
            => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex,
            CollidablePair pair, int childIndexA, int childIndexB)
            => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex,
            CollidablePair pair, ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial.FrictionCoefficient = 0.5f;
            pairMaterial.MaximumRecoveryVelocity = 2f;
            pairMaterial.SpringSettings = new SpringSettings(30, 1);

            // 실제 접촉(depth > 0인 접점이 하나라도 있으면)이 있을 때만 이벤트로 기록
            if (manifold.Count > 0)
            {
                _collector.RecordContact(pair.A, pair.B);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(int workerIndex,
            CollidablePair pair, int childIndexA, int childIndexB,
            ref ConvexContactManifold manifold)
            => true;

        public void Dispose() { }
    }

    // --- BepuPhysics PoseIntegrator 콜백 (중력 적용) ---

    internal struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        private Vector3 _gravity;
        private Vector3Wide _gravityDtWide;
        private readonly HashSet<int> _noGravityBodies;
        private Simulation _simulation;

        public PoseIntegratorCallbacks(Vector3 gravity, HashSet<int> noGravityBodies)
        {
            _gravity = gravity;
            _gravityDtWide = default;
            _noGravityBodies = noGravityBodies;
            _simulation = null!;
        }

        public readonly AngularIntegrationMode AngularIntegrationMode
            => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation)
        {
            _simulation = simulation;
        }

        public void PrepareForIntegration(float dt)
        {
            _gravityDtWide = Vector3Wide.Broadcast(_gravity * dt);
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
            BodyInertiaWide localInertia, Vector<int> integrationMask,
            int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            if (_noGravityBodies.Count == 0)
            {
                velocity.Linear += _gravityDtWide;
                return;
            }

            var activeCount = _simulation.Bodies.ActiveSet.Count;
            Span<float> maskValues = stackalloc float[Vector<float>.Count];
            for (int i = 0; i < Vector<float>.Count; i++)
            {
                var idx = bodyIndices[i];
                if (integrationMask[i] == 0 || idx < 0 || idx >= activeCount)
                {
                    maskValues[i] = 1f;
                    continue;
                }
                var handle = _simulation.Bodies.ActiveSet.IndexToHandle[idx];
                maskValues[i] = _noGravityBodies.Contains(handle.Value) ? 0f : 1f;
            }
            var mask = new Vector<float>(maskValues);
            velocity.Linear.X += _gravityDtWide.X * mask;
            velocity.Linear.Y += _gravityDtWide.Y * mask;
            velocity.Linear.Z += _gravityDtWide.Z * mask;
        }
    }
}
