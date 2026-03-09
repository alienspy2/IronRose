// ------------------------------------------------------------
// @file    CapsuleCollider.cs
// @brief   캡슐 형상의 3D 콜라이더. Rigidbody 없으면 static body로 자동 등록.
// @deps    Collider, PhysicsManager, PhysicsWorld3D, Gizmos
// @exports
//   class CapsuleCollider : Collider
//     radius: float                        — 캡슐 반지름 (기본 0.5)
//     height: float                        — 캡슐 전체 높이 (기본 2.0, 반구 포함)
//     RegisterAsStatic(PhysicsManager)     — lossyScale 적용, height에서 반구 길이를 뺀 원통 길이로 static capsule 등록 + UserData 설정
//     OnDrawGizmosSelected()               — 와이어프레임 캡슐 기즈모 렌더링
// @note    BepuPhysics Capsule의 length 파라미터는 반구 제외 원통 길이 = max(0.01, scaledHeight - 2*scaledRadius)
// ------------------------------------------------------------
namespace RoseEngine
{
    public class CapsuleCollider : Collider
    {
        public float radius { get; set; } = 0.5f;
        public float height { get; set; } = 2f;

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var s = transform.lossyScale;
            float radiusScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            float scaledRadius = radius * radiusScale;
            float scaledHeight = height * Mathf.Abs(s.y);
            float capsuleLength = Mathf.Max(0.01f, scaledHeight - 2f * scaledRadius);
            _staticHandle = mgr.World3D.AddStaticCapsule(
                GetWorldPosition(), GetWorldRotation(),
                scaledRadius, capsuleLength);
            mgr.World3D.SetStaticUserData(_staticHandle.Value, this);
            _staticRegistered = true;
        }

        public override void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.DrawWireCapsule(center, radius, height);
        }
    }
}
