using System.Collections.Generic;

namespace RoseEngine
{
    public class Transform : Component
    {
        // --- Internal storage (local space) ---
        private Vector3 _localPosition = Vector3.zero;
        private Quaternion _localRotation = Quaternion.identity;
        private Vector3 _localScale = Vector3.one;

        // --- Hierarchy ---
        private Transform? _parent;
        private readonly List<Transform> _children = new();

        public Transform? parent
        {
            get => _parent;
            set => SetParent(value);
        }

        public int childCount => _children.Count;

        public Transform? root
        {
            get
            {
                var current = this;
                while (current._parent != null)
                    current = current._parent;
                return current;
            }
        }

        public void SetParent(Transform? newParent, bool worldPositionStays = true)
        {
            if (_parent == newParent) return;

            var worldPos = position;
            var worldRot = rotation;
            var worldScale = lossyScale;

            // Remove from old parent
            _parent?._children.Remove(this);

            _parent = newParent;

            // Add to new parent
            _parent?._children.Add(this);

            if (worldPositionStays)
            {
                position = worldPos;
                rotation = worldRot;
                // Preserve world scale
                if (_parent != null)
                {
                    var ps = _parent.lossyScale;
                    _localScale = new Vector3(
                        ps.x != 0 ? worldScale.x / ps.x : worldScale.x,
                        ps.y != 0 ? worldScale.y / ps.y : worldScale.y,
                        ps.z != 0 ? worldScale.z / ps.z : worldScale.z);
                }
                else
                {
                    _localScale = worldScale;
                }
            }
        }

        public Transform GetChild(int index) => _children[index];

        public Transform? Find(string name)
        {
            foreach (var child in _children)
            {
                if (child.gameObject.name == name) return child;
            }
            // Recursive search with '/' separator
            foreach (var child in _children)
            {
                if (name.Contains('/'))
                {
                    int sep = name.IndexOf('/');
                    if (child.gameObject.name == name[..sep])
                        return child.Find(name[(sep + 1)..]);
                }
            }
            return null;
        }

        public void DetachChildren()
        {
            for (int i = _children.Count - 1; i >= 0; i--)
                _children[i].SetParent(null);
        }

        public bool IsChildOf(Transform parent)
        {
            var current = _parent;
            while (current != null)
            {
                if (current == parent) return true;
                current = current._parent;
            }
            return false;
        }

        public int GetSiblingIndex()
        {
            if (_parent == null)
            {
                // 루트 오브젝트: AllGameObjects 목록에서의 루트 순서 반환
                int rootIdx = 0;
                foreach (var go in SceneManager.AllGameObjects)
                {
                    if (go.transform._parent != null) continue;
                    if (go.transform == this) return rootIdx;
                    rootIdx++;
                }
                return 0;
            }
            return _parent._children.IndexOf(this);
        }

        public void SetSiblingIndex(int index)
        {
            if (_parent == null)
            {
                SceneManager.MoveGameObjectIndex(gameObject, index);
                return;
            }
            _parent._children.Remove(this);
            _parent._children.Insert(System.Math.Min(index, _parent._children.Count), this);
        }

        // --- Local properties ---
        public Vector3 localPosition { get => _localPosition; set => _localPosition = value; }
        public Quaternion localRotation { get => _localRotation; set => _localRotation = value; }
        public Vector3 localScale { get => _localScale; set => _localScale = value; }

        public Vector3 localEulerAngles
        {
            get => _localRotation.eulerAngles;
            set => _localRotation = Quaternion.Euler(value);
        }

        // --- World properties ---
        public Vector3 position
        {
            get
            {
                if (_parent == null) return _localPosition;
                var ps = _parent.lossyScale;
                var scaled = new Vector3(_localPosition.x * ps.x, _localPosition.y * ps.y, _localPosition.z * ps.z);
                return _parent.rotation * scaled + _parent.position;
            }
            set
            {
                if (_parent == null) { _localPosition = value; return; }
                var local = Quaternion.Inverse(_parent.rotation) * (value - _parent.position);
                var ps = _parent.lossyScale;
                _localPosition = new Vector3(
                    ps.x != 0 ? local.x / ps.x : 0,
                    ps.y != 0 ? local.y / ps.y : 0,
                    ps.z != 0 ? local.z / ps.z : 0);
            }
        }

        public Quaternion rotation
        {
            get => _parent != null ? _parent.rotation * _localRotation : _localRotation;
            set => _localRotation = _parent != null ? Quaternion.Inverse(_parent.rotation) * value : value;
        }

        public Vector3 eulerAngles
        {
            get => rotation.eulerAngles;
            set => rotation = Quaternion.Euler(value);
        }

        public Vector3 lossyScale
        {
            get
            {
                if (_parent == null) return _localScale;
                var ps = _parent.lossyScale;
                return new Vector3(_localScale.x * ps.x, _localScale.y * ps.y, _localScale.z * ps.z);
            }
        }

        // --- Direction vectors (world space) ---
        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 right => rotation * Vector3.right;
        public Vector3 up => rotation * Vector3.up;

        // --- Transform methods ---
        public void Translate(Vector3 translation)
        {
            position += translation;
        }

        public void Translate(float x, float y, float z)
        {
            position += new Vector3(x, y, z);
        }

        public void Translate(Vector3 translation, Space relativeTo)
        {
            if (relativeTo == Space.Self)
                position += rotation * translation;
            else
                position += translation;
        }

        public void Rotate(Vector3 eulers)
        {
            rotation = rotation * Quaternion.Euler(eulers);
        }

        public void Rotate(float xAngle, float yAngle, float zAngle)
        {
            rotation = rotation * Quaternion.Euler(xAngle, yAngle, zAngle);
        }

        public void Rotate(Vector3 axis, float angle)
        {
            rotation = rotation * Quaternion.AngleAxis(angle, axis);
        }

        public void Rotate(Vector3 eulers, Space relativeTo)
        {
            if (relativeTo == Space.World)
                rotation = Quaternion.Euler(eulers) * rotation;
            else
                rotation = rotation * Quaternion.Euler(eulers);
        }

        public void RotateAround(Vector3 point, Vector3 axis, float angle)
        {
            var q = Quaternion.AngleAxis(angle, axis);
            position = point + q * (position - point);
            rotation = q * rotation;
        }

        public void LookAt(Transform target)
        {
            LookAt(target.position, Vector3.up);
        }

        public void LookAt(Vector3 worldPosition, Vector3 worldUp = default)
        {
            if (worldUp == Vector3.zero) worldUp = Vector3.up;
            var direction = worldPosition - position;
            if (direction.sqrMagnitude > 1e-10f)
                rotation = Quaternion.LookRotation(direction, worldUp);
        }

        // --- Space transformation ---
        public Vector3 TransformPoint(Vector3 localPoint)
        {
            var s = lossyScale;
            return rotation * new Vector3(localPoint.x * s.x, localPoint.y * s.y, localPoint.z * s.z) + position;
        }

        public Vector3 InverseTransformPoint(Vector3 worldPoint)
        {
            var unrotated = Quaternion.Inverse(rotation) * (worldPoint - position);
            var s = lossyScale;
            return new Vector3(
                s.x != 0 ? unrotated.x / s.x : 0,
                s.y != 0 ? unrotated.y / s.y : 0,
                s.z != 0 ? unrotated.z / s.z : 0);
        }

        public Vector3 TransformDirection(Vector3 localDirection)
        {
            return rotation * localDirection;
        }

        public Vector3 InverseTransformDirection(Vector3 worldDirection)
        {
            return Quaternion.Inverse(rotation) * worldDirection;
        }

        public Vector3 TransformVector(Vector3 localVector)
        {
            var s = lossyScale;
            return rotation * new Vector3(localVector.x * s.x, localVector.y * s.y, localVector.z * s.z);
        }

        public Vector3 InverseTransformVector(Vector3 worldVector)
        {
            var unrotated = Quaternion.Inverse(rotation) * worldVector;
            var s = lossyScale;
            return new Vector3(
                s.x != 0 ? unrotated.x / s.x : 0,
                s.y != 0 ? unrotated.y / s.y : 0,
                s.z != 0 ? unrotated.z / s.z : 0);
        }
    }

    public enum Space
    {
        World,
        Self
    }
}
