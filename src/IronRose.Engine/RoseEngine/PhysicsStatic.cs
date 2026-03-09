using System;

namespace RoseEngine
{
    public struct RaycastHit
    {
        public GameObject gameObject;
        public Collider collider;
        public float distance;
        public Vector3 point;
        public Vector3 normal;
    }

    public static class Physics
    {
        public static Vector3 gravity { get; set; } = new Vector3(0, -9.81f, 0);

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
            float maxDistance = Mathf.Infinity)
        {
            hit = default;
            return false;
        }

        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction,
            float maxDistance = Mathf.Infinity)
        {
            return Array.Empty<RaycastHit>();
        }

        public static Collider[] OverlapSphere(Vector3 position, float radius)
        {
            return Array.Empty<Collider>();
        }

        public static bool CheckSphere(Vector3 position, float radius)
        {
            return false;
        }
    }

    public struct RaycastHit2D
    {
        public GameObject gameObject;
        public Collider2D collider;
        public float distance;
        public Vector2 point;
        public Vector2 normal;
        public float fraction;
    }

    public static class Physics2D
    {
        public static Vector2 gravity { get; set; } = new Vector2(0, -9.81f);

        public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction,
            float distance = Mathf.Infinity)
        {
            return default;
        }

        public static Collider2D[] OverlapCircle(Vector2 point, float radius)
        {
            return Array.Empty<Collider2D>();
        }
    }
}
