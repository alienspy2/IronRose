namespace RoseEngine
{
    public static class Gizmos
    {
        internal static IGizmoBackend? Backend;
        internal static bool IsDrawing;

        public static Color color
        {
            get => Backend?.color ?? Color.white;
            set { if (Backend != null && IsDrawing) Backend.color = value; }
        }

        public static Matrix4x4 matrix
        {
            get => Backend?.matrix ?? Matrix4x4.identity;
            set { if (Backend != null && IsDrawing) Backend.matrix = value; }
        }

        public static void DrawLine(Vector3 from, Vector3 to)
        {
            if (Backend != null && IsDrawing) Backend.DrawLine(from, to);
        }

        public static void DrawRay(Vector3 from, Vector3 direction)
        {
            if (Backend != null && IsDrawing) Backend.DrawRay(from, direction);
        }

        public static void DrawWireSphere(Vector3 center, float radius)
        {
            if (Backend != null && IsDrawing) Backend.DrawWireSphere(center, radius);
        }

        public static void DrawSphere(Vector3 center, float radius)
        {
            if (Backend != null && IsDrawing) Backend.DrawSphere(center, radius);
        }

        public static void DrawWireCube(Vector3 center, Vector3 size)
        {
            if (Backend != null && IsDrawing) Backend.DrawWireCube(center, size);
        }

        public static void DrawCube(Vector3 center, Vector3 size)
        {
            if (Backend != null && IsDrawing) Backend.DrawCube(center, size);
        }

        public static void DrawWireCircle(Vector3 center, Vector3 axis1, Vector3 axis2, float radius)
        {
            if (Backend != null && IsDrawing) Backend.DrawWireCircle(center, axis1, axis2, radius);
        }

        public static void DrawWireCone(Vector3 origin, Vector3 direction, float angle, float length)
        {
            if (Backend != null && IsDrawing) Backend.DrawWireCone(origin, direction, angle, length);
        }

        public static void DrawWireCapsule(Vector3 center, float radius, float height)
        {
            if (Backend != null && IsDrawing) Backend.DrawWireCapsule(center, radius, height);
        }

        public static void DrawWireCylinder(Vector3 center, float radius, float height)
        {
            if (Backend != null && IsDrawing) Backend.DrawWireCylinder(center, radius, height);
        }

        public static void DrawIcon(Vector3 center, string name)
        {
            if (Backend != null && IsDrawing) Backend.DrawIcon(center, name);
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (Backend != null && IsDrawing) Backend.DrawMesh(mesh, position, rotation, scale);
        }
    }
}
