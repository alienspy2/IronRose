namespace RoseEngine
{
    /// <summary>
    /// Rigidbody / Rigidbody2D 공통 베이스 — 지연 등록 패턴 공유.
    /// </summary>
    public abstract class PhysicsComponent : Component
    {
        protected bool _registered;

        internal void EnsureRegistered()
        {
            if (_registered) return;
            RegisterWithPhysics();
            _registered = true;
        }

        protected abstract void RegisterWithPhysics();

        protected static IronRose.Engine.PhysicsManager? GetPhysicsManager()
            => IronRose.Engine.PhysicsManager.Instance;
    }
}
