namespace RoseEngine
{
    public class BoxCollider2D : Collider2D
    {
        public Vector2 size { get; set; } = Vector2.one;

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var pos = transform.position;
            _staticBody = mgr.World2D.CreateStaticBody(pos.x + offset.x, pos.y + offset.y);
            _staticBody.Tag = this;
            mgr.World2D.AttachRectangle(_staticBody, size.x, size.y, 1f);
            _staticRegistered = true;
        }
    }
}
