// ------------------------------------------------------------
// @file    PhysicsManager.cs
// @brief   물리 시뮬레이션 오케스트레이션. FixedUpdate에서 Collider/Rigidbody 등록,
//          Kinematic→Physics 동기화, 시뮬레이션 스텝, Physics→Transform 동기화를 수행한다.
// @deps    IronRose.Physics.PhysicsWorld3D, IronRose.Physics.PhysicsWorld2D,
//          RoseEngine.Collider, RoseEngine.Collider2D, RoseEngine.Rigidbody, RoseEngine.Rigidbody2D,
//          RoseEngine.CharacterController, RoseEngine.Debug
// @exports
//   class PhysicsManager : IDisposable
//     Instance: PhysicsManager? (static, internal)              — 싱글턴 인스턴스
//     World3D: PhysicsWorld3D (internal)                        — 3D 물리 월드
//     World2D: PhysicsWorld2D (internal)                        — 2D 물리 월드
//     Initialize(): void                                        — 물리 월드 초기화
//     Reset(): void                                             — 씬 전환 시 리셋
//     FixedUpdate(float): void                                  — 물리 시뮬레이션 프레임
//     Dispose(): void                                           — 리소스 해제
// @note    FixedUpdate 순서: EnsureStaticColliders → EnsureRigidbodies → PushTransforms
//          (Kinematic RB + CharacterController pose 동기화) → Step → PullPhysics.
//          CharacterController + Rigidbody 조합 시 경고 로그 출력.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using IronRose.Physics;
using RoseEngine;
using SysVector3 = System.Numerics.Vector3;

namespace IronRose.Engine
{
    public class PhysicsManager : IDisposable
    {
        private PhysicsWorld3D _world3D = new();
        private PhysicsWorld2D _world2D = new();
        private int _fixedUpdateCount;

        internal PhysicsWorld3D World3D => _world3D;
        internal PhysicsWorld2D World2D => _world2D;

        internal static PhysicsManager? Instance { get; private set; }

        public void Initialize()
        {
            _world3D.Initialize();
            _world2D.Initialize();
            Instance = this;
        }

        /// <summary>씬 전환 시 물리 월드 초기화 (모든 body 제거)</summary>
        public void Reset()
        {
            _fixedUpdateCount = 0;
            _world3D.Reset();
            _world2D.Reset();
        }

        public void FixedUpdate(float fixedDeltaTime)
        {
            _fixedUpdateCount++;
            bool logThisFrame = _fixedUpdateCount <= 3;

            if (logThisFrame)
                EditorDebug.Log($"[PhysicsMgr:FixedUpdate#{_fixedUpdateCount}] colliders={Collider._allColliders.Count} rigidbodies={Rigidbody._rigidbodies.Count}");

            // 0. Rigidbody 없는 Collider를 static body로 지연 등록
            EnsureStaticColliders();

            // 0.5. 모든 Rigidbody 지연 등록 (스텝 전에 반드시 등록되어야 static collider와 충돌 가능)
            EnsureRigidbodies();

            // 1. Kinematic: Transform → Physics 동기화
            PushTransformsToPhysics();

            // 2. 물리 시뮬레이션 스텝
            _world3D.Step(fixedDeltaTime);
            _world2D.Step(fixedDeltaTime);

            // 2.5. 충돌 이벤트 콜백 디스패치 (Step에서 수집된 Enter/Stay/Exit 쌍)
            DispatchCollisionEvents3D();

            // 3. Dynamic: Physics → Transform 동기화
            PullPhysicsToTransforms();
        }

        private void EnsureRigidbodies()
        {
            var rbs = Rigidbody._rigidbodies.Snapshot();
            foreach (var rb in rbs)
            {
                if (!IsActiveBody(rb)) continue;
                rb.EnsureRegistered();
            }

            var rb2ds = Rigidbody2D._rigidbodies2D.Snapshot();
            foreach (var rb2d in rb2ds)
            {
                if (!IsActiveBody(rb2d)) continue;
                rb2d.EnsureRegistered();
            }
        }

        /// <summary>Rigidbody가 없는 Collider를 static body로 등록 (Unity 규칙)</summary>
        private void EnsureStaticColliders()
        {
            // 3D Colliders
            var colliders = Collider._allColliders.Snapshot();
            foreach (var col in colliders)
            {
                if (col._isDestroyed || col.gameObject._isEditorInternal || !col.gameObject.activeInHierarchy) continue;
                if (col._staticRegistered) continue;
                // Rigidbody가 있으면 Rigidbody가 shape을 관리 (CharacterController 제외)
                if (col.gameObject.GetComponent<Rigidbody>() != null)
                {
                    // CharacterController + Rigidbody 조합은 Unity에서도 지원하지 않음
                    if (col is CharacterController)
                        EditorDebug.LogWarning($"[PhysicsMgr] CharacterController on '{col.gameObject.name}' has a Rigidbody — this combination is not supported. Remove the Rigidbody.");
                    continue;
                }
                EditorDebug.Log($"[PhysicsMgr] Registering STATIC collider: '{col.gameObject.name}' type={col.GetType().Name} pos={col.transform.position}");
                col.RegisterAsStatic(this);
            }

            // 2D Colliders
            var colliders2D = Collider2D._allColliders2D.Snapshot();
            foreach (var col2d in colliders2D)
            {
                if (col2d._isDestroyed || col2d.gameObject._isEditorInternal || !col2d.gameObject.activeInHierarchy) continue;
                if (col2d._staticRegistered) continue;
                // Rigidbody2D가 있으면 Rigidbody2D가 shape을 관리
                if (col2d.gameObject.GetComponent<Rigidbody2D>() != null) continue;
                col2d.RegisterAsStatic(this);
            }
        }

        private void PushTransformsToPhysics()
        {
            var rbs = Rigidbody._rigidbodies.Snapshot();
            foreach (var rb in rbs)
            {
                if (!IsActiveBody(rb)) continue;
                if (rb.isKinematic)
                    rb.PushToPhysics();
            }

            var rb2ds = Rigidbody2D._rigidbodies2D.Snapshot();
            foreach (var rb2d in rb2ds)
            {
                if (!IsActiveBody(rb2d)) continue;
                if (rb2d.bodyType == RigidbodyType2D.Kinematic)
                    rb2d.PushToPhysics();
            }

            // CharacterController static body pose 동기화
            SyncCharacterControllerPoses();
        }

        /// <summary>CharacterController의 static body pose를 현재 Transform에 맞게 동기화합니다.</summary>
        private void SyncCharacterControllerPoses()
        {
            var colliders = Collider._allColliders.Snapshot();
            foreach (var col in colliders)
            {
                if (col is CharacterController cc && cc._kinematicHandle != null && cc._staticRegistered)
                {
                    cc.SyncStaticPose(this);
                }
            }
        }

        /// <summary>3D 충돌 이벤트를 MonoBehaviour 콜백으로 디스패치합니다.</summary>
        private void DispatchCollisionEvents3D()
        {
            // OnCollisionEnter
            foreach (var (idA, idB) in _world3D.EnteredPairs)
            {
                DispatchCollisionCallback3D(idA, idB, CollisionEventType.Enter);
            }

            // OnCollisionStay
            foreach (var (idA, idB) in _world3D.StayingPairs)
            {
                DispatchCollisionCallback3D(idA, idB, CollisionEventType.Stay);
            }

            // OnCollisionExit
            foreach (var (idA, idB) in _world3D.ExitedPairs)
            {
                DispatchCollisionCallback3D(idA, idB, CollisionEventType.Exit);
            }
        }

        private enum CollisionEventType { Enter, Stay, Exit }

        private void DispatchCollisionCallback3D(int idA, int idB, CollisionEventType eventType)
        {
            var userDataA = _world3D.GetUserDataByContactId(idA);
            var userDataB = _world3D.GetUserDataByContactId(idB);

            // UserData에서 GameObject를 찾음 (Rigidbody 또는 Collider)
            var goA = ResolveGameObject(userDataA);
            var goB = ResolveGameObject(userDataB);

            if (goA == null || goB == null) return;

            // 상대 속도 계산
            var velA = _world3D.GetVelocityByContactId(idA);
            var velB = _world3D.GetVelocityByContactId(idB);
            var relVel = velA - velB;
            var roseRelVel = new Vector3(relVel.X, relVel.Y, relVel.Z);

            // A → B에 대한 충돌 정보
            var collisionForA = new Collision
            {
                gameObject = goB,
                rigidbody = goB.GetComponent<Rigidbody>()!,
                collider = goB.GetComponent<Collider>()!,
                relativeVelocity = roseRelVel,
            };

            // B → A에 대한 충돌 정보
            var collisionForB = new Collision
            {
                gameObject = goA,
                rigidbody = goA.GetComponent<Rigidbody>()!,
                collider = goA.GetComponent<Collider>()!,
                relativeVelocity = roseRelVel,
            };

            FireCollisionOnGameObject(goA, collisionForA, eventType);
            FireCollisionOnGameObject(goB, collisionForB, eventType);
        }

        private static GameObject? ResolveGameObject(object? userData)
        {
            if (userData is Rigidbody rb && !rb._isDestroyed)
                return rb.gameObject;
            if (userData is Collider col && !col._isDestroyed)
                return col.gameObject;
            return null;
        }

        private static void FireCollisionOnGameObject(GameObject go, Collision collision, CollisionEventType eventType)
        {
            if (go._isDestroyed || !go.activeInHierarchy) return;

            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (!mb.enabled || !mb._hasAwoken || mb._isDestroyed) continue;
                try
                {
                    switch (eventType)
                    {
                        case CollisionEventType.Enter:
                            mb.OnCollisionEnter(collision);
                            break;
                        case CollisionEventType.Stay:
                            mb.OnCollisionStay(collision);
                            break;
                        case CollisionEventType.Exit:
                            mb.OnCollisionExit(collision);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Physics] Exception in {eventType} of {mb.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        private void PullPhysicsToTransforms()
        {
            var rbs = Rigidbody._rigidbodies.Snapshot();
            foreach (var rb in rbs)
            {
                if (!IsActiveBody(rb)) continue;
                if (!rb.isKinematic)
                    rb.PullFromPhysics();
            }

            var rb2ds = Rigidbody2D._rigidbodies2D.Snapshot();
            foreach (var rb2d in rb2ds)
            {
                if (!IsActiveBody(rb2d)) continue;
                if (rb2d.bodyType == RigidbodyType2D.Dynamic)
                    rb2d.PullFromPhysics();
            }
        }

        /// <summary>물리 시뮬레이션 대상 판별. 프리팹 템플릿(_isEditorInternal) GO는 제외.</summary>
        private static bool IsActiveBody(Component body)
            => !body._isDestroyed && !body.gameObject._isEditorInternal && body.gameObject.activeInHierarchy;

        public void Dispose()
        {
            _world3D.Dispose();
            _world2D.Dispose();
            Instance = null;
        }
    }
}
