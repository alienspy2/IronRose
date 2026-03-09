using System;

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
