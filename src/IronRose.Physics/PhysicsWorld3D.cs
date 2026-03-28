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

        public void Initialize(Vector3? gravity = null)
        {
            var g = gravity ?? new Vector3(0, -9.81f, 0);
            _gravity = g;

            _bufferPool = new BufferPool();
            var threadCount = Math.Max(2, Environment.ProcessorCount - 2);
            _threadDispatcher = new ThreadDispatcher(threadCount, 16384);

            _simulation = Simulation.Create(
                _bufferPool,
                new NarrowPhaseCallbacks(),
                new PoseIntegratorCallbacks(g, _noGravityBodies),
                new SolveDescription(8, 1)
            );

            EditorDebug.Log($"[Physics3D] Initialized with {threadCount} threads");
        }

        private int _stepCount;

        public void Step(float deltaTime)
        {
            _stepCount++;
            if (_stepCount <= 3 || _stepCount % 300 == 0)
            {
                EditorDebug.Log($"[Physics3D:Step#{_stepCount}] bodies={_simulation.Bodies.ActiveSet.Count} statics={_simulation.Statics.Count} noGravity={_noGravityBodies.Count} ({string.Join(",", _noGravityBodies)})");
            }
            _simulation.Timestep(deltaTime, _threadDispatcher);
        }

        // --- Dynamic Body (shape 생성 + body 등록을 한번에) ---

        public BodyHandle AddDynamicBox(Vector3 position, Quaternion rotation,
            float width, float height, float length, float mass)
        {
            var shape = new Box(width, height, length);
            return _simulation.Bodies.Add(
                BodyDescription.CreateConvexDynamic(
                    new RigidPose(position, rotation), mass, _simulation.Shapes, shape)
            );
        }

        public BodyHandle AddDynamicSphere(Vector3 position, Quaternion rotation,
            float radius, float mass)
        {
            var shape = new Sphere(radius);
            return _simulation.Bodies.Add(
                BodyDescription.CreateConvexDynamic(
                    new RigidPose(position, rotation), mass, _simulation.Shapes, shape)
            );
        }

        public BodyHandle AddDynamicCapsule(Vector3 position, Quaternion rotation,
            float radius, float length, float mass)
        {
            var shape = new Capsule(radius, length);
            return _simulation.Bodies.Add(
                BodyDescription.CreateConvexDynamic(
                    new RigidPose(position, rotation), mass, _simulation.Shapes, shape)
            );
        }

        public BodyHandle AddDynamicCylinder(Vector3 position, Quaternion rotation,
            float radius, float height, float mass)
        {
            var shape = new Cylinder(radius, height);
            return _simulation.Bodies.Add(
                BodyDescription.CreateConvexDynamic(
                    new RigidPose(position, rotation), mass, _simulation.Shapes, shape)
            );
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
            _simulation.Bodies.Remove(handle);
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
            _stepCount = 0;
            _simulation = Simulation.Create(
                _bufferPool,
                new NarrowPhaseCallbacks(),
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
