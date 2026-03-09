namespace RoseEngine
{
    public class CircleCollider2D : Collider2D
    {
        public float radius { get; set; } = 0.5f;

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var pos = transform.position;
            _staticBody = mgr.World2D.CreateStaticBody(pos.x + offset.x, pos.y + offset.y);
            mgr.World2D.AttachCircle(_staticBody, radius, 1f);
            _staticRegistered = true;
        }
    }
}
