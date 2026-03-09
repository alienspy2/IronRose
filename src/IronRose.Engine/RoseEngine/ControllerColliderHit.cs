// ------------------------------------------------------------
// @file    ControllerColliderHit.cs
// @brief   CharacterController가 충돌 시 OnControllerColliderHit 콜백에 전달하는 충돌 정보 클래스.
// @deps    CharacterController, Collider, GameObject, Transform, Rigidbody, Vector3
// @exports
//   class ControllerColliderHit
//     controller: CharacterController      — 충돌을 발생시킨 CharacterController
//     collider: Collider                    — 충돌한 상대 Collider
//     gameObject: GameObject                — 충돌한 상대 GameObject
//     transform: Transform                  — 충돌한 상대 Transform
//     rigidbody: Rigidbody?                 — 충돌한 상대의 Rigidbody (없으면 null)
//     point: Vector3                        — 충돌 지점 (월드 좌표)
//     normal: Vector3                       — 충돌 법선 (월드 좌표)
//     moveDirection: Vector3                — 이동 방향
//     moveLength: float                     — 이동 거리
// @note    Unity API 호환. 프로퍼티는 internal set으로 CharacterController 내부에서만 설정.
// ------------------------------------------------------------
namespace RoseEngine
{
    public class ControllerColliderHit
    {
        public CharacterController controller { get; internal set; } = null!;
        public Collider collider { get; internal set; } = null!;
        public GameObject gameObject { get; internal set; } = null!;
        public Transform transform { get; internal set; } = null!;
        public Rigidbody? rigidbody { get; internal set; }
        public Vector3 point { get; internal set; }
        public Vector3 normal { get; internal set; }
        public Vector3 moveDirection { get; internal set; }
        public float moveLength { get; internal set; }
    }
}
