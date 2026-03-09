using RoseEngine;
using SysVector4 = System.Numerics.Vector4;
using SysMatrix = System.Numerics.Matrix4x4;

namespace IronRose.Engine.Editor.SceneView
{
    public sealed class GizmoRendererBackend : IGizmoBackend
    {
        private readonly GizmoRenderer _renderer;

        public GizmoRendererBackend(GizmoRenderer renderer)
        {
            _renderer = renderer;
        }

        public Color color
        {
            get
            {
                var c = GizmoRenderer.color;
                return new Color(c.X, c.Y, c.Z, c.W);
            }
            set => GizmoRenderer.color = new SysVector4(value.r, value.g, value.b, value.a);
        }

        public Matrix4x4 matrix
        {
            get => new Matrix4x4 { inner = GizmoRenderer.matrix };
            set => GizmoRenderer.matrix = value.ToNumerics();
        }

        public void DrawLine(Vector3 from, Vector3 to)
            => _renderer.DrawLine(from, to);

        public void DrawRay(Vector3 from, Vector3 direction)
            => _renderer.DrawLine(from, from + direction);

        public void DrawWireSphere(Vector3 center, float radius)
            => _renderer.DrawWireSphere(center, radius);

        public void DrawSphere(Vector3 center, float radius)
            => _renderer.DrawWireSphere(center, radius); // fallback to wireframe

        public void DrawWireCube(Vector3 center, Vector3 size)
            => _renderer.DrawWireBox(center, size);

        public void DrawCube(Vector3 center, Vector3 size)
            => _renderer.DrawWireBox(center, size); // fallback to wireframe

        public void DrawWireCircle(Vector3 center, Vector3 axis1, Vector3 axis2, float radius)
            => _renderer.DrawWireCircle(center, axis1, axis2, radius);

        public void DrawWireCone(Vector3 origin, Vector3 direction, float angle, float length)
            => _renderer.DrawWireCone(origin, direction, angle, length);

        public void DrawWireCapsule(Vector3 center, float radius, float height)
            => _renderer.DrawWireCapsule(center, radius, height);

        public void DrawWireCylinder(Vector3 center, float radius, float height)
            => _renderer.DrawWireCylinder(center, radius, height);

        public void DrawIcon(Vector3 center, string name)
        {
            // TODO: icon rendering
        }

        public void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            // TODO: mesh gizmo rendering
        }
    }
}
