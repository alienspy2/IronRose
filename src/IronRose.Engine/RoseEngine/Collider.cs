using System.Collections.Generic;
using BepuPhysics;
using SysVector3 = System.Numerics.Vector3;
using SysQuaternion = System.Numerics.Quaternion;

namespace RoseEngine
{
    public abstract class Collider : Component
    {
        public bool isTrigger { get; set; }
        public Vector3 center { get; set; } = Vector3.zero;

        internal bool isRegistered = false;

        // --- Static collider 자가등록 (Unity 규칙: Rigidbody 없으면 static body) ---
        internal static readonly List<Collider> _allColliders = new();
        internal StaticHandle? _staticHandle;
        internal bool _staticRegistered;

        internal override void OnAddedToGameObject()
        {
            _allColliders.Add(this);
        }

        internal override void OnComponentDestroy()
        {
            UnregisterStatic();
            _allColliders.Remove(this);
        }

        /// <summary>Rigidbody가 없을 때 static body로 등록 (서브클래스가 shape 결정)</summary>
        internal abstract void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr);

        internal void UnregisterStatic()
        {
            if (!_staticRegistered || _staticHandle == null) return;
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return;

            mgr.World3D.RemoveStatic(_staticHandle.Value);
            _staticHandle = null;
            _staticRegistered = false;
        }

        internal static void ClearAll() => _allColliders.Clear();

        protected SysVector3 GetWorldPosition()
        {
            // center를 lossyScale + rotation 적용하여 월드 좌표로 변환
            var worldPos = transform.TransformPoint(center);
            return new SysVector3(worldPos.x, worldPos.y, worldPos.z);
        }

        protected SysQuaternion GetWorldRotation()
        {
            var rot = transform.rotation;
            return new SysQuaternion(rot.x, rot.y, rot.z, rot.w);
        }
    }
}
