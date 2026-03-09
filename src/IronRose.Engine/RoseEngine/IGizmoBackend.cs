namespace RoseEngine
{
    public interface IGizmoBackend
    {
        Color color { get; set; }
        Matrix4x4 matrix { get; set; }

        void DrawLine(Vector3 from, Vector3 to);
        void DrawRay(Vector3 from, Vector3 direction);
        void DrawWireSphere(Vector3 center, float radius);
        void DrawSphere(Vector3 center, float radius);
        void DrawWireCube(Vector3 center, Vector3 size);
        void DrawCube(Vector3 center, Vector3 size);
        void DrawWireCircle(Vector3 center, Vector3 axis1, Vector3 axis2, float radius);
        void DrawWireCone(Vector3 origin, Vector3 direction, float angle, float length);
        void DrawWireCapsule(Vector3 center, float radius, float height);
        void DrawWireCylinder(Vector3 center, float radius, float height);
        void DrawIcon(Vector3 center, string name);
        void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale);
    }
}
