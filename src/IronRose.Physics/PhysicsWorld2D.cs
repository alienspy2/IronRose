using nkast.Aether.Physics2D.Collision;
using nkast.Aether.Physics2D.Dynamics;
using RoseEngine;
using AetherVector2 = nkast.Aether.Physics2D.Common.Vector2;

namespace IronRose.Physics
{
    public class PhysicsWorld2D : IDisposable
    {
        private World _world = null!;

        public void Initialize(float gravityX = 0, float gravityY = -9.81f)
        {
            _world = new World(new AetherVector2(gravityX, gravityY));
            Debug.Log("[Physics2D] Initialized");
        }

        public void Step(float deltaTime)
        {
            _world.Step(deltaTime);
        }

        // --- Body 생성 ---

        public Body CreateDynamicBody(float posX, float posY)
        {
            return _world.CreateBody(new AetherVector2(posX, posY), 0f, BodyType.Dynamic);
        }

        public Body CreateStaticBody(float posX, float posY)
        {
            return _world.CreateBody(new AetherVector2(posX, posY), 0f, BodyType.Static);
        }

        public Body CreateKinematicBody(float posX, float posY)
        {
            return _world.CreateBody(new AetherVector2(posX, posY), 0f, BodyType.Kinematic);
        }

        // --- Fixture (Shape) 추가 ---

        public void AttachRectangle(Body body, float width, float height, float density)
        {
            body.CreateRectangle(width, height, density, AetherVector2.Zero);
        }

        public void AttachCircle(Body body, float radius, float density)
        {
            body.CreateCircle(radius, density);
        }

        // --- RayCast / Overlap 쿼리 ---

        /// <summary>2D 레이캐스트 — 가장 가까운 히트 반환.</summary>
        public bool RayCast(AetherVector2 origin, AetherVector2 direction, float maxDistance,
                            out Fixture? hitFixture, out AetherVector2 hitPoint,
                            out AetherVector2 hitNormal, out float fraction)
        {
            hitFixture = null;
            hitPoint = default;
            hitNormal = default;
            fraction = float.MaxValue;

            var dirLen = direction.Length();
            if (dirLen < 1e-8f || maxDistance <= 0f) return false;

            // Aether는 endPoint를 직접 받으므로 Infinity를 실용적 상한값으로 클램핑
            var clampedDistance = float.IsInfinity(maxDistance) ? 10000f : maxDistance;
            var normalizedDir = direction / dirLen;
            var endPoint = origin + normalizedDir * clampedDistance;

            Fixture? closestFixture = null;
            AetherVector2 closestPoint = default;
            AetherVector2 closestNormal = default;
            float closestFraction = float.MaxValue;

            _world.RayCast((Fixture fixture, AetherVector2 point, AetherVector2 normal, float frac) =>
            {
                if (frac < closestFraction)
                {
                    closestFixture = fixture;
                    closestPoint = point;
                    closestNormal = normal;
                    closestFraction = frac;
                }
                return closestFraction;
            }, origin, endPoint);

            if (closestFixture == null) return false;

            hitFixture = closestFixture;
            hitPoint = closestPoint;
            hitNormal = closestNormal;
            fraction = closestFraction;
            return true;
        }

        /// <summary>2D 원형 오버랩 쿼리 — AABB 근사 + 거리 체크.</summary>
        public List<Body> OverlapCircle(AetherVector2 center, float radius)
        {
            var results = new List<Body>();
            var aabb = new AABB(
                new AetherVector2(center.X - radius, center.Y - radius),
                new AetherVector2(center.X + radius, center.Y + radius));

            float radiusSq = radius * radius;
            var visited = new HashSet<Body>();

            _world.QueryAABB((Fixture fixture) =>
            {
                var body = fixture.Body;
                if (visited.Add(body))
                {
                    float dx = body.Position.X - center.X;
                    float dy = body.Position.Y - center.Y;
                    if (dx * dx + dy * dy <= radiusSq)
                        results.Add(body);
                }
                return true;
            }, ref aabb);

            return results;
        }

        // --- Body 제거 ---

        public void RemoveBody(Body body)
        {
            _world.Remove(body);
        }

        /// <summary>모든 body 제거 (World 인스턴스 유지)</summary>
        public void Reset()
        {
            _world.Clear();
        }

        public void Dispose()
        {
            _world?.Clear();
        }
    }
}
