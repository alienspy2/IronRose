// ------------------------------------------------------------
// @file    BoxCollider.cs
// @brief   박스 형상의 3D 콜라이더. Rigidbody 없으면 static body로 자동 등록.
// @deps    Collider, PhysicsManager, PhysicsWorld3D, Gizmos
// @exports
//   class BoxCollider : Collider
//     size: Vector3                        — 박스 크기 (기본 Vector3.one)
//     RegisterAsStatic(PhysicsManager)     — lossyScale 적용하여 static box 등록 + UserData 설정
//     OnDrawGizmosSelected()               — 와이어프레임 박스 기즈모 렌더링
// ------------------------------------------------------------
namespace RoseEngine
{
    public class BoxCollider : Collider
    {
        public Vector3 size { get; set; } = Vector3.one;

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var s = transform.lossyScale;
            _staticHandle = mgr.World3D.AddStaticBox(
                GetWorldPosition(), GetWorldRotation(),
                size.x * Mathf.Abs(s.x), size.y * Mathf.Abs(s.y), size.z * Mathf.Abs(s.z));
            mgr.World3D.SetStaticUserData(_staticHandle.Value, this);
            _staticRegistered = true;
        }

        public override void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
            Gizmos.DrawWireCube(center, size);
        }
    }
}
