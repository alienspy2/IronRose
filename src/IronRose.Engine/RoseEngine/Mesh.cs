using System.Runtime.InteropServices;
using Veldrid;

namespace RoseEngine
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;

        public Vertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            UV = uv;
        }

        public static uint SizeInBytes => (uint)Marshal.SizeOf<Vertex>();
    }

    public class Mesh
    {
        public string name { get; set; } = "";

        public Vertex[] vertices = [];
        public uint[] indices = [];

        internal DeviceBuffer? VertexBuffer;
        internal DeviceBuffer? IndexBuffer;
        internal bool isDirty = true;

        private Bounds _bounds;
        private bool _boundsDirty = true;

        public Bounds bounds
        {
            get
            {
                if (_boundsDirty)
                    RecalculateBounds();
                return _bounds;
            }
            set
            {
                _bounds = value;
                _boundsDirty = false;
            }
        }

        public void RecalculateBounds()
        {
            if (vertices.Length == 0)
            {
                _bounds = new Bounds(Vector3.zero, Vector3.zero);
                _boundsDirty = false;
                return;
            }

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < vertices.Length; i++)
            {
                var p = vertices[i].Position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            _bounds = new Bounds((min + max) / 2f, max - min);
            _boundsDirty = false;
        }

        public void UploadToGPU(GraphicsDevice device)
        {
            if (!isDirty || vertices.Length == 0 || indices.Length == 0)
                return;

            var factory = device.ResourceFactory;

            // Dispose old buffers
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();

            // Create vertex buffer
            VertexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)(vertices.Length * Marshal.SizeOf<Vertex>()),
                BufferUsage.VertexBuffer));
            device.UpdateBuffer(VertexBuffer, 0, vertices);

            // Create index buffer
            IndexBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)(indices.Length * sizeof(uint)),
                BufferUsage.IndexBuffer));
            device.UpdateBuffer(IndexBuffer, 0, indices);

            isDirty = false;
        }

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            IndexBuffer?.Dispose();
            VertexBuffer = null;
            IndexBuffer = null;
        }
    }
}
