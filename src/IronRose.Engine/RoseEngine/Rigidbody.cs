// ------------------------------------------------------------
// @file    Rigidbody.cs
// @brief   3D 물리 시뮬레이션에 참여하는 강체 컴포넌트.
//          PhysicsComponent를 상속하며, Collider shape 기반으로 dynamic/kinematic body를 등록한다.
// @deps    PhysicsComponent, PhysicsManager, PhysicsWorld3D, BoxCollider, SphereCollider,
//          CapsuleCollider, CylinderCollider, Collider, Time
// @exports
//   class Rigidbody : PhysicsComponent
//     mass, drag, angularDrag: float       — 물리 속성
//     useGravity: bool                     — 중력 사용 여부 (런타임 변경 가능)
//     isKinematic: bool                    — 키네마틱 모드
//     allowSleep: bool                     — sleep 허용 여부 (기본 false, 런타임 변경 가능)
//     velocity, angularVelocity: Vector3   — 속도 조회/설정
//     AddForce(Vector3, ForceMode): void   — 힘/임펄스 적용
//     AddTorque(Vector3, ForceMode): void  — 토크 적용
//     PullFromPhysics(): void              — 시뮬레이션 결과를 Transform에 반영
//     PushToPhysics(): void                — Transform을 시뮬레이션에 반영
//     RemoveFromPhysics(): void            — 물리 body 제거
// @note    RegisterWithPhysics에서 Collider 타입에 따라 shape을 결정하고 UserData로 자신을 등록.
//          _rigidbodies static 리스트로 전역 관리됨.
// ------------------------------------------------------------
using System.Collections.Generic;
using BepuPhysics;
using SysVector3 = System.Numerics.Vector3;
using SysQuaternion = System.Numerics.Quaternion;

namespace RoseEngine
{
    public class Rigidbody : PhysicsComponent
    {
        internal static readonly List<Rigidbody> _rigidbodies = new();
        internal BodyHandle? bodyHandle;
        internal StaticHandle? staticHandle;

        public float mass { get; set; } = 1.0f;
        public float drag { get; set; } = 0f;
        public float angularDrag { get; set; } = 0.05f;
        private bool _useGravity = true;
        public bool useGravity
        {
            get => _useGravity;
            set
            {
                _useGravity = value;
                if (bodyHandle != null)
                    GetPhysicsManager()?.World3D.SetBodyUseGravity(bodyHandle.Value, value);
            }
        }
        public bool isKinematic { get; set; } = false;

        private bool _allowSleep = false;
        public bool allowSleep
        {
            get => _allowSleep;
            set
            {
                _allowSleep = value;
                if (bodyHandle != null)
                    GetPhysicsManager()?.World3D.SetBodyAllowSleep(bodyHandle.Value, value);
            }
        }

        public Vector3 velocity
        {
            get
            {
                if (bodyHandle == null) return Vector3.zero;
                var mgr = GetPhysicsManager();
                if (mgr == null) return Vector3.zero;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                return new Vector3(bv.Linear.X, bv.Linear.Y, bv.Linear.Z);
            }
            set
            {
                if (bodyHandle == null) return;
                var mgr = GetPhysicsManager();
                if (mgr == null) return;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                bv.Linear = new SysVector3(value.x, value.y, value.z);
                mgr.World3D.SetBodyVelocity(bodyHandle.Value, bv);
            }
        }

        public Vector3 angularVelocity
        {
            get
            {
                if (bodyHandle == null) return Vector3.zero;
                var mgr = GetPhysicsManager();
                if (mgr == null) return Vector3.zero;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                return new Vector3(bv.Angular.X, bv.Angular.Y, bv.Angular.Z);
            }
            set
            {
                if (bodyHandle == null) return;
                var mgr = GetPhysicsManager();
                if (mgr == null) return;
                var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                bv.Angular = new SysVector3(value.x, value.y, value.z);
                mgr.World3D.SetBodyVelocity(bodyHandle.Value, bv);
            }
        }

        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            if (bodyHandle == null) return;
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            var f = new SysVector3(force.x, force.y, force.z);
            switch (mode)
            {
                case ForceMode.Force:
                    // 연속 힘: F * dt로 변환 (FixedUpdate 주기에 맞춰 누적)
                    mgr.World3D.ApplyLinearImpulse(bodyHandle.Value, f * Time.fixedDeltaTime);
                    break;
                case ForceMode.Impulse:
                    mgr.World3D.ApplyLinearImpulse(bodyHandle.Value, f);
                    break;
                case ForceMode.VelocityChange:
                    var bv = mgr.World3D.GetBodyVelocity(bodyHandle.Value);
                    bv.Linear += f;
                    mgr.World3D.SetBodyVelocity(bodyHandle.Value, bv);
                    break;
                case ForceMode.Acceleration:
                    mgr.World3D.ApplyLinearImpulse(bodyHandle.Value, f * mass * Time.fixedDeltaTime);
                    break;
            }
        }

        public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
        {
            if (bodyHandle == null) return;
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            var t = new SysVector3(torque.x, torque.y, torque.z);
            if (mode == ForceMode.Impulse)
                mgr.World3D.ApplyAngularImpulse(bodyHandle.Value, t);
            else
                mgr.World3D.ApplyAngularImpulse(bodyHandle.Value, t * Time.fixedDeltaTime);
        }

        internal void PullFromPhysics()
        {
            EnsureRegistered();
            if (bodyHandle == null) return;
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            var pose = mgr.World3D.GetBodyPose(bodyHandle.Value);
            transform.position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            transform.rotation = new Quaternion(
                pose.Orientation.X, pose.Orientation.Y,
                pose.Orientation.Z, pose.Orientation.W);
        }

        internal void PushToPhysics()
        {
            EnsureRegistered();
            if (bodyHandle == null) return;
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            var pos = transform.position;
            var rot = transform.rotation;
            mgr.World3D.SetBodyPose(bodyHandle.Value,
                new BepuPhysics.RigidPose(
                    new SysVector3(pos.x, pos.y, pos.z),
                    new SysQuaternion(rot.x, rot.y, rot.z, rot.w)));
        }

        internal override void OnAddedToGameObject()
        {
            _rigidbodies.Add(this);
            // Rigidbody가 추가되면 같은 GO의 static collider를 해제 (Rigidbody가 shape을 관리)
            UnregisterSiblingStaticColliders();
            // Deferred: 첫 FixedUpdate 시점에 등록 (isKinematic 등 프로퍼티 설정 이후)
        }

        internal override void OnComponentDestroy()
        {
            RemoveFromPhysics();
            _rigidbodies.Remove(this);
            // Rigidbody 제거 시 남은 Collider를 다시 static으로 등록되게 표시
            MarkSiblingCollidersForStaticReregistration();
        }

        protected override void RegisterWithPhysics()
        {
            var mgr = GetPhysicsManager();
            if (mgr == null)
            {
                EditorDebug.LogWarning($"[Rigidbody] RegisterWithPhysics: PhysicsManager is null for '{gameObject.name}'");
                return;
            }

            // Rigidbody 등록 전, 같은 GO의 static collider 해제
            UnregisterSiblingStaticColliders();

            var pos = transform.position;
            var rot = transform.rotation;
            var sPos = new SysVector3(pos.x, pos.y, pos.z);
            var sRot = new SysQuaternion(rot.x, rot.y, rot.z, rot.w);

            // Collider 타입에 따라 shape 결정 (lossyScale 적용)
            var boxCol = gameObject.GetComponent<BoxCollider>();
            var sphereCol = gameObject.GetComponent<SphereCollider>();
            var capsuleCol = gameObject.GetComponent<CapsuleCollider>();
            var cylinderCol = gameObject.GetComponent<CylinderCollider>();
            var scale = transform.lossyScale;

            string shapeType;
            if (isKinematic)
            {
                if (boxCol != null)
                    bodyHandle = mgr.World3D.AddKinematicBox(sPos, sRot,
                        boxCol.size.x * Mathf.Abs(scale.x), boxCol.size.y * Mathf.Abs(scale.y), boxCol.size.z * Mathf.Abs(scale.z));
                else if (sphereCol != null)
                    bodyHandle = mgr.World3D.AddKinematicBox(sPos, sRot, 1, 1, 1); // fallback
                EditorDebug.Log($"[Rigidbody] Registered KINEMATIC '{gameObject.name}' handle={bodyHandle?.Value} pos={pos} scale={scale}");
                if (bodyHandle != null)
                    mgr.World3D.SetBodyUserData(bodyHandle.Value, this);
                return;
            }

            // Dynamic body
            if (boxCol != null)
            {
                float bw = boxCol.size.x * Mathf.Abs(scale.x);
                float bh = boxCol.size.y * Mathf.Abs(scale.y);
                float bl = boxCol.size.z * Mathf.Abs(scale.z);
                shapeType = $"Box({bw:F2},{bh:F2},{bl:F2})";
                bodyHandle = mgr.World3D.AddDynamicBox(sPos, sRot, bw, bh, bl, mass);
            }
            else if (sphereCol != null)
            {
                float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                float sr = sphereCol.radius * maxScale;
                shapeType = $"Sphere({sr:F2})";
                bodyHandle = mgr.World3D.AddDynamicSphere(sPos, sRot, sr, mass);
            }
            else if (capsuleCol != null)
            {
                float radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                float scaledRadius = capsuleCol.radius * radiusScale;
                float scaledHeight = capsuleCol.height * Mathf.Abs(scale.y);
                float capsuleLength = Mathf.Max(0.01f, scaledHeight - 2f * scaledRadius);
                shapeType = $"Capsule({scaledRadius:F2},{capsuleLength:F2})";
                bodyHandle = mgr.World3D.AddDynamicCapsule(sPos, sRot, scaledRadius, capsuleLength, mass);
            }
            else if (cylinderCol != null)
            {
                float radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                float cr = cylinderCol.radius * radiusScale;
                float ch = cylinderCol.height * Mathf.Abs(scale.y);
                shapeType = $"Cylinder({cr:F2},{ch:F2})";
                bodyHandle = mgr.World3D.AddDynamicCylinder(sPos, sRot, cr, ch, mass);
            }
            else
            {
                // Collider 없으면 기본 unit box (스케일 적용)
                float bw = Mathf.Abs(scale.x);
                float bh = Mathf.Abs(scale.y);
                float bl = Mathf.Abs(scale.z);
                shapeType = $"Box({bw:F2},{bh:F2},{bl:F2}) default";
                bodyHandle = mgr.World3D.AddDynamicBox(sPos, sRot, bw, bh, bl, mass);
            }

            EditorDebug.Log($"[Rigidbody] Registered DYNAMIC '{gameObject.name}' handle={bodyHandle?.Value} shape={shapeType} pos={pos} mass={mass} useGravity={_useGravity}");

            if (bodyHandle != null)
                mgr.World3D.SetBodyUserData(bodyHandle.Value, this);

            if (!_useGravity && bodyHandle != null)
                mgr.World3D.SetBodyUseGravity(bodyHandle.Value, false);

            if (!_allowSleep && bodyHandle != null)
                mgr.World3D.SetBodyAllowSleep(bodyHandle.Value, false);
        }

        internal void RemoveFromPhysics()
        {
            var mgr = GetPhysicsManager();
            if (mgr == null) return;

            if (bodyHandle != null)
            {
                mgr.World3D.RemoveBody(bodyHandle.Value);
                bodyHandle = null;
            }
            if (staticHandle != null)
            {
                mgr.World3D.RemoveStatic(staticHandle.Value);
                staticHandle = null;
            }
            _registered = false;
        }

        private void UnregisterSiblingStaticColliders()
        {
            foreach (var col in gameObject.GetComponents<Collider>())
                col.UnregisterStatic();
        }

        private void MarkSiblingCollidersForStaticReregistration()
        {
            foreach (var col in gameObject.GetComponents<Collider>())
            {
                col._staticRegistered = false;
                col._staticHandle = null;
            }
        }

        internal static void ClearAll() => _rigidbodies.Clear();
    }
}
