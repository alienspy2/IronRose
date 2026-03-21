using System.Collections.Generic;
using nkast.Aether.Physics2D.Dynamics;
using AetherVector2 = nkast.Aether.Physics2D.Common.Vector2;

namespace RoseEngine
{
    public class Rigidbody2D : PhysicsComponent
    {
        internal static readonly List<Rigidbody2D> _rigidbodies2D = new();
        internal Body? aetherBody;

        public RigidbodyType2D bodyType { get; set; } = RigidbodyType2D.Dynamic;
        public float mass { get; set; } = 1.0f;
        public float gravityScale { get; set; } = 1.0f;
        public float drag { get; set; } = 0f;
        public float angularDrag { get; set; } = 0.05f;

        public Vector2 velocity
        {
            get
            {
                if (aetherBody == null) return Vector2.zero;
                var v = aetherBody.LinearVelocity;
                return new Vector2(v.X, v.Y);
            }
            set
            {
                if (aetherBody == null) return;
                aetherBody.LinearVelocity = new AetherVector2(value.x, value.y);
            }
        }

        public float angularVelocity
        {
            get => aetherBody?.AngularVelocity ?? 0f;
            set { if (aetherBody != null) aetherBody.AngularVelocity = value; }
        }

        public void AddForce(Vector2 force)
        {
            aetherBody?.ApplyForce(new AetherVector2(force.x, force.y));
        }

        public void AddForce(Vector2 force, ForceMode2D mode)
        {
            if (aetherBody == null) return;
            if (mode == ForceMode2D.Impulse)
                aetherBody.ApplyLinearImpulse(new AetherVector2(force.x, force.y));
            else
                aetherBody.ApplyForce(new AetherVector2(force.x, force.y));
        }

        public void AddTorque(float torque)
        {
            aetherBody?.ApplyTorque(torque);
        }

        internal void PullFromPhysics()
        {
            EnsureRegistered();
            if (aetherBody == null) return;
            transform.position = new Vector3(
                aetherBody.Position.X, aetherBody.Position.Y,
                transform.position.z);
            transform.rotation = Quaternion.Euler(0, 0, aetherBody.Rotation * Mathf.Rad2Deg);
        }

        internal void PushToPhysics()
        {
            EnsureRegistered();
            if (aetherBody == null) return;
            aetherBody.Position = new AetherVector2(transform.position.x, transform.position.y);
            aetherBody.Rotation = transform.rotation.eulerAngles.z * Mathf.Deg2Rad;
        }

        internal override void OnAddedToGameObject()
        {
            _rigidbodies2D.Add(this);
            // Rigidbody2D가 추가되면 같은 GO의 static collider2D를 해제
            UnregisterSiblingStaticColliders();
            // Deferred: 첫 FixedUpdate 시점에 등록 (bodyType 등 프로퍼티 설정 이후)
        }

        internal override void OnComponentDestroy()
        {
            RemoveFromPhysics();
            _rigidbodies2D.Remove(this);
            // Rigidbody2D 제거 시 남은 Collider2D를 다시 static으로 등록되게 표시
            MarkSiblingCollidersForStaticReregistration();
        }

        protected override void RegisterWithPhysics()
        {
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            // Rigidbody2D 등록 전, 같은 GO의 static collider2D 해제
            UnregisterSiblingStaticColliders();

            var pos = transform.position;

            switch (bodyType)
            {
                case RigidbodyType2D.Dynamic:
                    aetherBody = mgr.World2D.CreateDynamicBody(pos.x, pos.y);
                    break;
                case RigidbodyType2D.Kinematic:
                    aetherBody = mgr.World2D.CreateKinematicBody(pos.x, pos.y);
                    break;
                case RigidbodyType2D.Static:
                    aetherBody = mgr.World2D.CreateStaticBody(pos.x, pos.y);
                    break;
            }

            if (aetherBody == null) return;

            aetherBody.Tag = this;
            aetherBody.IgnoreGravity = gravityScale == 0f;
            aetherBody.LinearDamping = drag;
            aetherBody.AngularDamping = angularDrag;

            // Collider2D에서 Fixture 생성
            var boxCol = gameObject.GetComponent<BoxCollider2D>();
            if (boxCol != null)
            {
                mgr.World2D.AttachRectangle(aetherBody, boxCol.size.x, boxCol.size.y, 1f);
                return;
            }

            var circleCol = gameObject.GetComponent<CircleCollider2D>();
            if (circleCol != null)
            {
                mgr.World2D.AttachCircle(aetherBody, circleCol.radius, 1f);
                return;
            }

            // Collider 없으면 기본 unit box
            mgr.World2D.AttachRectangle(aetherBody, 1f, 1f, 1f);
        }

        internal void RemoveFromPhysics()
        {
            if (aetherBody == null) return;
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            mgr.World2D.RemoveBody(aetherBody);
            aetherBody = null;
            _registered = false;
        }

        private void UnregisterSiblingStaticColliders()
        {
            foreach (var col in gameObject.GetComponents<Collider2D>())
                col.UnregisterStatic();
        }

        private void MarkSiblingCollidersForStaticReregistration()
        {
            foreach (var col in gameObject.GetComponents<Collider2D>())
            {
                col._staticRegistered = false;
                col._staticBody = null;
            }
        }

        internal static void ClearAll() => _rigidbodies2D.Clear();
    }
}
