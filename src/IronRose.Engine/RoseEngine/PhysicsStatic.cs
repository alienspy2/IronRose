using System;
using System.Collections.Generic;
using IronRose.Physics;
using SysVector3 = System.Numerics.Vector3;

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
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return false;

            var sOrigin = new SysVector3(origin.x, origin.y, origin.z);
            var sDir = new SysVector3(direction.x, direction.y, direction.z);

            if (!mgr.World3D.RayCast(sOrigin, sDir, maxDistance, out RayHit rayHit))
                return false;

            hit = BuildRaycastHit(rayHit);
            return true;
        }

        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction,
            float maxDistance = Mathf.Infinity)
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return Array.Empty<RaycastHit>();

            var sOrigin = new SysVector3(origin.x, origin.y, origin.z);
            var sDir = new SysVector3(direction.x, direction.y, direction.z);

            var hits = mgr.World3D.RayCastAll(sOrigin, sDir, maxDistance);
            if (hits.Count == 0) return Array.Empty<RaycastHit>();

            var result = new RaycastHit[hits.Count];
            for (int i = 0; i < hits.Count; i++)
                result[i] = BuildRaycastHit(hits[i]);
            return result;
        }

        public static Collider[] OverlapSphere(Vector3 position, float radius)
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return Array.Empty<Collider>();

            var sCenter = new SysVector3(position.x, position.y, position.z);
            var userDataList = mgr.World3D.OverlapSphere(sCenter, radius);

            var colliders = new List<Collider>();
            foreach (var userData in userDataList)
            {
                if (userData is Collider col)
                    colliders.Add(col);
                else if (userData is Rigidbody rb)
                {
                    var c = rb.gameObject.GetComponent<Collider>();
                    if (c != null) colliders.Add(c);
                }
            }
            return colliders.ToArray();
        }

        public static bool CheckSphere(Vector3 position, float radius)
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return false;

            var sCenter = new SysVector3(position.x, position.y, position.z);
            var userDataList = mgr.World3D.OverlapSphere(sCenter, radius, 1);
            return userDataList.Count > 0;
        }

        private static RaycastHit BuildRaycastHit(RayHit rayHit)
        {
            var hit = new RaycastHit
            {
                distance = rayHit.Distance,
                point = new Vector3(rayHit.Point.X, rayHit.Point.Y, rayHit.Point.Z),
                normal = new Vector3(rayHit.Normal.X, rayHit.Normal.Y, rayHit.Normal.Z),
            };

            if (rayHit.UserData is Collider col)
            {
                hit.collider = col;
                hit.gameObject = col.gameObject;
            }
            else if (rayHit.UserData is Rigidbody rb)
            {
                hit.gameObject = rb.gameObject;
                hit.collider = rb.gameObject.GetComponent<Collider>()!;
            }

            return hit;
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

        /// <summary>히트가 있으면 true (Unity 호환: implicit bool 변환).</summary>
        public static implicit operator bool(RaycastHit2D hit) => hit.collider != null;
    }

    public static class Physics2D
    {
        public static Vector2 gravity { get; set; } = new Vector2(0, -9.81f);

        public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction,
            float distance = Mathf.Infinity)
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return default;

            var aOrigin = new nkast.Aether.Physics2D.Common.Vector2(origin.x, origin.y);
            var aDir = new nkast.Aether.Physics2D.Common.Vector2(direction.x, direction.y);

            if (!mgr.World2D.RayCast(aOrigin, aDir, distance,
                out var hitFixture, out var hitPoint, out var hitNormal, out float fraction))
                return default;

            var hit = new RaycastHit2D
            {
                point = new Vector2(hitPoint.X, hitPoint.Y),
                normal = new Vector2(hitNormal.X, hitNormal.Y),
                fraction = fraction,
                distance = fraction * (float.IsInfinity(distance) ? 10000f : distance),
            };

            if (hitFixture != null)
            {
                var tag = hitFixture.Body.Tag;
                if (tag is Collider2D col2d)
                {
                    hit.collider = col2d;
                    hit.gameObject = col2d.gameObject;
                }
                else if (tag is Rigidbody2D rb2d)
                {
                    hit.gameObject = rb2d.gameObject;
                    hit.collider = rb2d.gameObject.GetComponent<Collider2D>()!;
                }
            }

            return hit;
        }

        public static Collider2D[] OverlapCircle(Vector2 point, float radius)
        {
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return Array.Empty<Collider2D>();

            var aCenter = new nkast.Aether.Physics2D.Common.Vector2(point.x, point.y);
            var bodies = mgr.World2D.OverlapCircle(aCenter, radius);
            if (bodies.Count == 0) return Array.Empty<Collider2D>();

            var colliders = new List<Collider2D>();
            foreach (var body in bodies)
            {
                if (body.Tag is Collider2D col2d)
                    colliders.Add(col2d);
                else if (body.Tag is Rigidbody2D rb2d)
                {
                    var c = rb2d.gameObject.GetComponent<Collider2D>();
                    if (c != null) colliders.Add(c);
                }
            }
            return colliders.ToArray();
        }
    }
}
