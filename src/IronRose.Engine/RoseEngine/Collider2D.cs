using System.Collections.Generic;
using nkast.Aether.Physics2D.Dynamics;

namespace RoseEngine
{
    public abstract class Collider2D : Component
    {
        public bool isTrigger { get; set; }
        public Vector2 offset { get; set; } = Vector2.zero;

        // --- Static collider 자가등록 (Unity 규칙: Rigidbody2D 없으면 static body) ---
        internal static readonly List<Collider2D> _allColliders2D = new();
        internal Body? _staticBody;
        internal bool _staticRegistered;

        internal override void OnAddedToGameObject()
        {
            _allColliders2D.Add(this);
        }

        internal override void OnComponentDestroy()
        {
            UnregisterStatic();
            _allColliders2D.Remove(this);
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
