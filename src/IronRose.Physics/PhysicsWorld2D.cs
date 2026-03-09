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
