// ------------------------------------------------------------
// @file    Collider.cs
// @brief   3D Collider 추상 기반 클래스. Rigidbody 가 없을 때 PhysicsManager 가
//          이 컴포넌트들을 순회하며 static body 로 자가등록한다.
// @deps    Component, PhysicsManager, BepuPhysics.StaticHandle, ComponentRegistry<T>
// @exports
//   abstract class Collider : Component
//     static ComponentRegistry<Collider> _allColliders          — 전역 Collider 레지스트리(스레드 안전)
//     bool isTrigger / Vector3 center                           — 공통 속성
//     bool isRegistered / StaticHandle? _staticHandle / bool _staticRegistered
//     abstract void RegisterAsStatic(PhysicsManager)            — 서브클래스가 shape 결정
//     void UnregisterStatic()                                   — static body 제거
//     static void ClearAll()                                    — 레지스트리 초기화
//     SysVector3 GetWorldPosition() / SysQuaternion GetWorldRotation()
// @note    OnComponentDestroy 순서: UnregisterStatic -> _allColliders.Unregister.
//          Register/Unregister 는 메인 스레드 한정 (ThreadGuard 검증).
//          외부(PhysicsManager)는 _allColliders.Snapshot() 을 사용해야 한다.
// ------------------------------------------------------------
using BepuPhysics;
using SysVector3 = System.Numerics.Vector3;
using SysQuaternion = System.Numerics.Quaternion;

namespace RoseEngine
{
    public abstract class Collider : Component
    {
        public bool isTrigger { get; set; }
        public Vector3 center { get; set; } = Vector3.zero;

        internal bool isRegistered = false;

        // --- Static collider 자가등록 (Unity 규칙: Rigidbody 없으면 static body) ---
        internal static readonly ComponentRegistry<Collider> _allColliders = new();
        internal StaticHandle? _staticHandle;
        internal bool _staticRegistered;

        internal override void OnAddedToGameObject()
        {
            ThreadGuard.DebugCheckMainThread("Collider.Register");
            _allColliders.Register(this);
        }

        internal override void OnComponentDestroy()
        {
            ThreadGuard.DebugCheckMainThread("Collider.Unregister");
            UnregisterStatic();
            _allColliders.Unregister(this);
        }

        /// <summary>Rigidbody가 없을 때 static body로 등록 (서브클래스가 shape 결정)</summary>
        internal abstract void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr);

        internal void UnregisterStatic()
        {
            if (!_staticRegistered || _staticHandle == null) return;
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;

            mgr.World3D.RemoveStatic(_staticHandle.Value);
            _staticHandle = null;
            _staticRegistered = false;
        }

        internal static void ClearAll() => _allColliders.Clear();

        protected SysVector3 GetWorldPosition()
        {
            // center를 lossyScale + rotation 적용하여 월드 좌표로 변환
            var worldPos = transform.TransformPoint(center);
            return new SysVector3(worldPos.x, worldPos.y, worldPos.z);
        }

        protected SysQuaternion GetWorldRotation()
        {
            var rot = transform.rotation;
            return new SysQuaternion(rot.x, rot.y, rot.z, rot.w);
        }
    }
}
