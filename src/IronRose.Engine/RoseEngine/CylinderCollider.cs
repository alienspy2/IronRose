// ------------------------------------------------------------
// @file    CylinderCollider.cs
// @brief   실린더 형상의 3D 콜라이더. Rigidbody 없으면 static body로 자동 등록.
// @deps    Collider, PhysicsManager, PhysicsWorld3D, Gizmos
// @exports
//   class CylinderCollider : Collider
//     radius: float                        — 실린더 반지름 (기본 0.5)
//     height: float                        — 실린더 높이 (기본 2.0)
//     RegisterAsStatic(PhysicsManager)     — lossyScale 적용하여 static cylinder 등록 + UserData 설정
//     OnDrawGizmosSelected()               — 와이어프레임 실린더 기즈모 렌더링
// ------------------------------------------------------------
namespace RoseEngine
{
    public class CylinderCollider : Collider
    {
        public float radius { get; set; } = 0.5f;
        public float height { get; set; } = 2f;

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var s = transform.lossyScale;
            float radiusScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            _staticHandle = mgr.World3D.AddStaticCylinder(
                GetWorldPosition(), GetWorldRotation(),
                radius * radiusScale, height * Mathf.Abs(s.y));
            mgr.World3D.SetStaticUserData(_staticHandle.Value, this);
            _staticRegistered = true;
        }

        public override void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.DrawWireCylinder(center, radius, height);
        }
    }
}
