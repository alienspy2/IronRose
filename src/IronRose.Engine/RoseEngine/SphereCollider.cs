// ------------------------------------------------------------
// @file    SphereCollider.cs
// @brief   구 형상의 3D 콜라이더. Rigidbody 없으면 static body로 자동 등록.
// @deps    Collider, PhysicsManager, PhysicsWorld3D, Gizmos
// @exports
//   class SphereCollider : Collider
//     radius: float                        — 구 반지름 (기본 0.5)
//     RegisterAsStatic(PhysicsManager)     — lossyScale 최대값 적용하여 static sphere 등록 + UserData 설정
//     OnDrawGizmosSelected()               — 와이어프레임 구 기즈모 렌더링
// ------------------------------------------------------------
namespace RoseEngine
{
    public class SphereCollider : Collider
    {
        public float radius { get; set; } = 0.5f;

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var s = transform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            _staticHandle = mgr.World3D.AddStaticSphere(
                GetWorldPosition(), GetWorldRotation(),
                radius * maxScale);
            mgr.World3D.SetStaticUserData(_staticHandle.Value, this);
            _staticRegistered = true;
        }

        public override void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 1f);
            var scale = transform.lossyScale;
            float maxScale = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            Gizmos.DrawWireSphere(transform.position + center, radius * maxScale);
        }
    }
}
