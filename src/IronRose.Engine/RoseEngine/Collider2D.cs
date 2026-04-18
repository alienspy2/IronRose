// ------------------------------------------------------------
// @file    Collider2D.cs
// @brief   2D Collider 추상 기반 클래스. Rigidbody2D 가 없을 때 PhysicsManager 가
//          이 컴포넌트들을 순회하며 static body 로 자가등록한다.
// @deps    Component, PhysicsManager, nkast.Aether.Physics2D.Dynamics.Body,
//          ComponentRegistry<T>
// @exports
//   abstract class Collider2D : Component
//     static ComponentRegistry<Collider2D> _allColliders2D      — 전역 Collider2D 레지스트리(스레드 안전)
//     bool isTrigger / Vector2 offset                           — 공통 속성
//     Body? _staticBody / bool _staticRegistered                — static body 상태
//     abstract void RegisterAsStatic(PhysicsManager)            — 서브클래스가 shape 결정
//     void UnregisterStatic()                                   — static body 제거
//     static void ClearAll()                                    — 레지스트리 초기화
//   enum RigidbodyType2D { Dynamic, Kinematic, Static }
// @note    OnComponentDestroy 순서: UnregisterStatic -> _allColliders2D.Unregister.
//          Register/Unregister 는 메인 스레드 한정 (ThreadGuard 검증).
//          외부(PhysicsManager)는 _allColliders2D.Snapshot() 을 사용해야 한다.
// ------------------------------------------------------------
using nkast.Aether.Physics2D.Dynamics;

namespace RoseEngine
{
    public abstract class Collider2D : Component
    {
        public bool isTrigger { get; set; }
        public Vector2 offset { get; set; } = Vector2.zero;

        // --- Static collider 자가등록 (Unity 규칙: Rigidbody2D 없으면 static body) ---
        internal static readonly ComponentRegistry<Collider2D> _allColliders2D = new();
        internal Body? _staticBody;
        internal bool _staticRegistered;

        internal override void OnAddedToGameObject()
        {
            ThreadGuard.DebugCheckMainThread("Collider2D.Register");
            _allColliders2D.Register(this);
        }

        internal override void OnComponentDestroy()
        {
            ThreadGuard.DebugCheckMainThread("Collider2D.Unregister");
            UnregisterStatic();
            _allColliders2D.Unregister(this);
        }

        /// <summary>Rigidbody2D가 없을 때 static body로 등록 (서브클래스가 shape 결정)</summary>
        internal abstract void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr);

        internal void UnregisterStatic()
        {
            if (!_staticRegistered || _staticBody == null) return;
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;

            mgr.World2D.RemoveBody(_staticBody);
            _staticBody = null;
            _staticRegistered = false;
        }

        internal static void ClearAll() => _allColliders2D.Clear();
    }

    public enum RigidbodyType2D
    {
        Dynamic,
        Kinematic,
        Static
    }
}
