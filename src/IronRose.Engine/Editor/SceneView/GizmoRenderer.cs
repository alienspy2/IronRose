using System;
using System.Collections.Generic;
using System.Numerics;
using IronRose.Rendering;
using RoseEngine;
using Veldrid;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = RoseEngine.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Unity-like Gizmo drawing API for the Scene View.
    /// Collects wireframe draw commands per frame and flushes them during rendering.
    /// All shapes are rendered as LineList primitives with depth test OFF.
    /// </summary>
    public sealed class GizmoRenderer : IDisposable
    {
        // --- Drawing state (set before Draw* calls) ---
        public static Vector4 color = new(1, 1, 1, 1);
        public static Matrix4x4 matrix = Matrix4x4.Identity;

        /// <summary>
        /// Set by GizmoCallbackRunner before each component's gizmo callbacks.
        /// Used to track which GameObject owns each gizmo line for GPU picking.
        /// </summary>
        public static uint CurrentOwnerInstanceId;

        private const int CircleSegments = 32;

        // --- Per-frame batches (grouped by color) ---
        private struct GizmoBatch
        {
            public Vector4 Color;
            public List<Vertex> Vertices;
            public List<uint> Indices;
        }

        private readonly List<GizmoBatch> _batches = new();
        private GizmoBatch _currentBatch;
        private Vector4 _currentColor;

        // --- Per-owner pick batches (for GPU picking) ---
        public struct GizmoPickBatch
        {
            public uint OwnerInstanceId;
            public List<Vertex> Vertices;
            public List<uint> Indices;
        }

        private readonly Dictionary<uint, GizmoPickBatch> _pickBatchMap = new();
        private readonly List<GizmoPickBatch> _pickBatchList = new();

        // --- GPU resources ---
        private Mesh? _batchMesh;
        private Mesh? _pickBatchMesh;
        private GraphicsDevice? _device;

        public void Initialize(GraphicsDevice device)
        {
            _device = device;
            _batchMesh = new Mesh { name = "GizmoLineBatch" };
            _pickBatchMesh = new Mesh { name = "GizmoLinePickBatch" };
        }

        /// <summary>
        /// Call at the start of each frame before any Draw* calls.
        /// </summary>
        public void BeginFrame()
        {
            _batches.Clear();
            _currentBatch = default;
            _currentColor = default;
            color = new Vector4(1, 1, 1, 1);
            matrix = Matrix4x4.Identity;

            _pickBatchMap.Clear();
            _pickBatchList.Clear();
            CurrentOwnerInstanceId = 0;
        }

        // ================================================================
        // Drawing API
        // ================================================================

        public void DrawLine(Vector3 from, Vector3 to)
        {
            EnsureBatch();
            var a = TransformPoint(from);
            var b = TransformPoint(to);
            AddLineSegment(a, b);
            AddPickLineSegment(a, b);
        }

        /// <summary>
        /// Draw a wireframe sphere (3 orthogonal great circles).
        /// </summary>
        public void DrawWireSphere(Vector3 center, float radius)
        {
            EnsureBatch();
            DrawCircleInternal(center, Vector3.right, Vector3.up, radius);
            DrawCircleInternal(center, Vector3.right, Vector3.forward, radius);
            DrawCircleInternal(center, Vector3.up, Vector3.forward, radius);
        }

        /// <summary>
        /// Draw a wireframe circle in the plane defined by axis1 and axis2.
        /// </summary>
        public void DrawWireCircle(Vector3 center, Vector3 axis1, Vector3 axis2, float radius)
        {
            EnsureBatch();
            DrawCircleInternal(center, axis1, axis2, radius);
        }

        /// <summary>
        /// Draw a wireframe cone. Origin is the apex, direction points to the base.
        /// angle is the full cone angle in degrees.
        /// </summary>
        public void DrawWireCone(Vector3 origin, Vector3 direction, float angle, float length)
        {
            EnsureBatch();
            float halfAngleRad = angle * 0.5f * Mathf.Deg2Rad;
            float baseRadius = MathF.Tan(halfAngleRad) * length;
            var dir = direction.normalized;

            var perp1 = GetPerpendicular(dir);
            var perp2 = Vector3.Cross(dir, perp1).normalized;

            var baseCenter = origin + dir * length;

            // Base circle
            DrawCircleInternal(baseCenter, perp1, perp2, baseRadius);

            // 4 edge lines from apex to base circle
            for (int i = 0; i < 4; i++)
            {
                float a = i * MathF.PI * 0.5f;
                var basePoint = baseCenter + (perp1 * MathF.Cos(a) + perp2 * MathF.Sin(a)) * baseRadius;
                var pa = TransformPoint(origin);
                var pb = TransformPoint(basePoint);
                AddLineSegment(pa, pb);
                AddPickLineSegment(pa, pb);
            }
        }

        /// <summary>
        /// Draw a wireframe capsule (Y-axis aligned).
        /// Composed of 2 horizontal circles at equators, 2 vertical half-circle arcs, and 4 vertical lines.
        /// </summary>
        public void DrawWireCapsule(Vector3 center, float radius, float height)
        {
            EnsureBatch();
            float halfH = Mathf.Max(height, radius * 2f) * 0.5f;
            float bodyHalf = halfH - radius;

            var topCenter = center + new Vector3(0, bodyHalf, 0);
            var bottomCenter = center + new Vector3(0, -bodyHalf, 0);

            // Horizontal circles at the equators (where hemispheres meet the body)
            DrawCircleInternal(topCenter, Vector3.right, Vector3.forward, radius);
            DrawCircleInternal(bottomCenter, Vector3.right, Vector3.forward, radius);

            // 4 vertical lines connecting equators
            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathF.PI * 0.5f;
                var offset = new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                var a = TransformPoint(topCenter + offset);
                var b = TransformPoint(bottomCenter + offset);
                AddLineSegment(a, b);
                AddPickLineSegment(a, b);
            }

            // Top hemisphere arcs (2 orthogonal half-circles)
            DrawHalfCircleArc(topCenter, Vector3.right, Vector3.up, radius, true);
            DrawHalfCircleArc(topCenter, Vector3.forward, Vector3.up, radius, true);

            // Bottom hemisphere arcs (2 orthogonal half-circles)
            DrawHalfCircleArc(bottomCenter, Vector3.right, Vector3.up, radius, false);
            DrawHalfCircleArc(bottomCenter, Vector3.forward, Vector3.up, radius, false);
        }

        /// <summary>
        /// Draw a wireframe cylinder (Y-axis aligned).
        /// Composed of 2 horizontal circles at top/bottom and 4 vertical lines.
        /// </summary>
        public void DrawWireCylinder(Vector3 center, float radius, float height)
        {
            EnsureBatch();
            float halfH = height * 0.5f;

            var topCenter = center + new Vector3(0, halfH, 0);
            var bottomCenter = center + new Vector3(0, -halfH, 0);

            // Horizontal circles at top and bottom
            DrawCircleInternal(topCenter, Vector3.right, Vector3.forward, radius);
            DrawCircleInternal(bottomCenter, Vector3.right, Vector3.forward, radius);

            // 4 vertical lines connecting top and bottom
            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathF.PI * 0.5f;
                var offset = new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                var a = TransformPoint(topCenter + offset);
                var b = TransformPoint(bottomCenter + offset);
                AddLineSegment(a, b);
                AddPickLineSegment(a, b);
            }
        }

        /// <summary>
        /// Draw a half-circle arc. Used for capsule hemisphere visualization.
        /// </summary>
        private void DrawHalfCircleArc(Vector3 center, Vector3 horizontal, Vector3 vertical,
            float radius, bool upperHalf)
        {
            const int halfSegments = CircleSegments / 2;
            float startAngle = upperHalf ? 0f : MathF.PI;
            float endAngle = upperHalf ? MathF.PI : 2f * MathF.PI;

            Vector3? prev = null;
            for (int i = 0; i <= halfSegments; i++)
            {
                float t = (float)i / halfSegments;
                float angle = startAngle + t * (endAngle - startAngle);
                var point = center + horizontal * MathF.Cos(angle) * radius + vertical * MathF.Sin(angle) * radius;
                var worldPoint = TransformPoint(point);

                if (prev.HasValue)
                {
                    AddLineSegment(prev.Value, worldPoint);
                    AddPickLineSegment(prev.Value, worldPoint);
                }
                prev = worldPoint;
            }
        }

        /// <summary>
        /// Draw a ray (line from origin along direction).
        /// </summary>
        public void DrawRay(Vector3 origin, Vector3 direction, float length = 1f)
        {
            DrawLine(origin, origin + direction.normalized * length);
        }

        /// <summary>
        /// Draw a wireframe box.
        /// </summary>
        public void DrawWireBox(Vector3 center, Vector3 size)
        {
            EnsureBatch();
            float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
            var c = center;

            var corners = new Vector3[8];
            corners[0] = c + new Vector3(-hx, -hy, -hz);
            corners[1] = c + new Vector3(hx, -hy, -hz);
            corners[2] = c + new Vector3(hx, hy, -hz);
            corners[3] = c + new Vector3(-hx, hy, -hz);
            corners[4] = c + new Vector3(-hx, -hy, hz);
            corners[5] = c + new Vector3(hx, -hy, hz);
            corners[6] = c + new Vector3(hx, hy, hz);
            corners[7] = c + new Vector3(-hx, hy, hz);

            // 12 edges
            for (int i = 0; i < 8; i++)
                corners[i] = TransformPoint(corners[i]);

            void Edge(int i0, int i1) { AddLineSegment(corners[i0], corners[i1]); AddPickLineSegment(corners[i0], corners[i1]); }
            Edge(0, 1); Edge(1, 2); Edge(2, 3); Edge(3, 0);
            Edge(4, 5); Edge(5, 6); Edge(6, 7); Edge(7, 4);
            Edge(0, 4); Edge(1, 5); Edge(2, 6); Edge(3, 7);
        }

        // ================================================================
        // Rendering
        // ================================================================

        /// <summary>
        /// Flush all collected gizmo draw commands.
        /// Call during the render phase after scene geometry and transform gizmo.
        /// All batches are merged into a single GPU buffer upload to avoid
        /// use-after-free when Mesh.UploadToGPU disposes the previous buffer.
        /// </summary>
        public void Render(CommandList cl, EditorCamera camera, SceneViewRenderer renderer,
            float viewportWidth, float viewportHeight)
        {
            FinalizeBatch();
            if (_batches.Count == 0 || _device == null) return;

            // Merge all batches into one vertex/index buffer.
            var allVerts = new List<Vertex>();
            var allIndices = new List<uint>();
            var draws = new List<(uint indexStart, uint indexCount, Vector4 batchColor)>();

            foreach (var batch in _batches)
            {
                if (batch.Vertices.Count == 0) continue;

                uint vertexBase = (uint)allVerts.Count;
                uint indexStart = (uint)allIndices.Count;

                allVerts.AddRange(batch.Vertices);
                foreach (var idx in batch.Indices)
                    allIndices.Add(idx + vertexBase);

                draws.Add((indexStart, (uint)batch.Indices.Count, batch.Color));
            }

            if (allVerts.Count == 0) return;

            // Single upload — no buffer disposal between draw calls.
            _batchMesh!.vertices = allVerts.ToArray();
            _batchMesh.indices = allIndices.ToArray();
            _batchMesh.isDirty = true;
            _batchMesh.UploadToGPU(_device);

            float aspect = viewportWidth / viewportHeight;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();

            foreach (var (indexStart, indexCount, batchColor) in draws)
            {
                renderer.DrawGizmoLines(cl, _batchMesh,
                    Matrix4x4.Identity, viewMatrix, projMatrix, batchColor,
                    indexStart, indexCount);
            }
        }

        /// <summary>
        /// Render gizmo pick batches to the pick framebuffer.
        /// Each batch is rendered with its owner's instance ID for GPU picking.
        /// </summary>
        public void RenderPick(CommandList pickCl, Pipeline pickPipeline,
            DeviceBuffer pickIdBuffer, ResourceSet pickIdSet, Matrix4x4 viewProj)
        {
            if (_device == null) return;

            FinalizePickBatches();

            // Merge all pick batches into one buffer (same fix as Render).
            var allVerts = new List<Vertex>();
            var allIndices = new List<uint>();
            var draws = new List<(uint indexStart, uint indexCount, uint ownerId)>();

            foreach (var batch in _pickBatchList)
            {
                if (batch.Vertices == null || batch.Vertices.Count == 0) continue;

                uint vertexBase = (uint)allVerts.Count;
                uint indexStart = (uint)allIndices.Count;

                allVerts.AddRange(batch.Vertices);
                foreach (var idx in batch.Indices)
                    allIndices.Add(idx + vertexBase);

                draws.Add((indexStart, (uint)batch.Indices.Count, batch.OwnerInstanceId));
            }

            if (allVerts.Count == 0) return;

            _pickBatchMesh!.vertices = allVerts.ToArray();
            _pickBatchMesh.indices = allIndices.ToArray();
            _pickBatchMesh.isDirty = true;
            _pickBatchMesh.UploadToGPU(_device);

            foreach (var (indexStart, indexCount, ownerId) in draws)
            {
                pickCl.UpdateBuffer(pickIdBuffer, 0, new PickIdUniforms
                {
                    World = Matrix4x4.Identity,
                    ViewProjection = viewProj,
                    ObjectId = ownerId,
                });

                pickCl.SetPipeline(pickPipeline);
                pickCl.SetGraphicsResourceSet(0, pickIdSet);
                pickCl.SetVertexBuffer(0, _pickBatchMesh.VertexBuffer);
                pickCl.SetIndexBuffer(_pickBatchMesh.IndexBuffer, IndexFormat.UInt32);
                pickCl.DrawIndexed(indexCount, 1, indexStart, 0, 0);
            }
        }

        /// <summary>
        /// Get finalized pick batches (one per owner GameObject).
        /// </summary>
        public IReadOnlyList<GizmoPickBatch> GetPickBatches()
        {
            FinalizePickBatches();
            return _pickBatchList;
        }

        // ================================================================
        // Internal helpers
        // ================================================================

        private void EnsureBatch()
        {
            if (_currentBatch.Vertices == null || color != _currentColor)
            {
                FinalizeBatch();
                _currentColor = color;
                _currentBatch = new GizmoBatch
                {
                    Color = color,
                    Vertices = new List<Vertex>(128),
                    Indices = new List<uint>(256),
                };
            }
        }

        private void FinalizeBatch()
        {
            if (_currentBatch.Vertices != null && _currentBatch.Vertices.Count > 0)
            {
                _batches.Add(_currentBatch);
            }
            _currentBatch = default;
        }

        private void AddLineSegment(Vector3 a, Vector3 b)
        {
            uint baseIdx = (uint)_currentBatch.Vertices.Count;
            _currentBatch.Vertices.Add(new Vertex(a, Vector3.zero, RoseEngine.Vector2.zero));
            _currentBatch.Vertices.Add(new Vertex(b, Vector3.zero, RoseEngine.Vector2.zero));
            _currentBatch.Indices.Add(baseIdx);
            _currentBatch.Indices.Add(baseIdx + 1);
        }

        private void AddPickLineSegment(Vector3 a, Vector3 b)
        {
            if (CurrentOwnerInstanceId == 0) return;

            if (!_pickBatchMap.TryGetValue(CurrentOwnerInstanceId, out var batch))
            {
                batch = new GizmoPickBatch
                {
                    OwnerInstanceId = CurrentOwnerInstanceId,
                    Vertices = new List<Vertex>(128),
                    Indices = new List<uint>(256),
                };
                _pickBatchMap[CurrentOwnerInstanceId] = batch;
            }

            uint baseIdx = (uint)batch.Vertices.Count;
            batch.Vertices.Add(new Vertex(a, Vector3.zero, RoseEngine.Vector2.zero));
            batch.Vertices.Add(new Vertex(b, Vector3.zero, RoseEngine.Vector2.zero));
            batch.Indices.Add(baseIdx);
            batch.Indices.Add(baseIdx + 1);

            _pickBatchMap[CurrentOwnerInstanceId] = batch;
        }

        private void FinalizePickBatches()
        {
            _pickBatchList.Clear();
            foreach (var batch in _pickBatchMap.Values)
            {
                if (batch.Vertices != null && batch.Vertices.Count > 0)
                    _pickBatchList.Add(batch);
            }
        }

        private static Vector3 TransformPoint(Vector3 localPoint)
        {
            var p = new System.Numerics.Vector4(localPoint.x, localPoint.y, localPoint.z, 1f);
            var transformed = System.Numerics.Vector4.Transform(p, matrix);
            return new Vector3(transformed.X, transformed.Y, transformed.Z);
        }

        private void DrawCircleInternal(Vector3 center, Vector3 axis1, Vector3 axis2, float radius)
        {
            Vector3? prev = null;

            for (int i = 0; i <= CircleSegments; i++)
            {
                float angle = (float)i / CircleSegments * MathF.PI * 2f;
                var point = center + (axis1 * MathF.Cos(angle) + axis2 * MathF.Sin(angle)) * radius;
                var worldPoint = TransformPoint(point);

                if (prev.HasValue)
                {
                    AddLineSegment(prev.Value, worldPoint);
                    AddPickLineSegment(prev.Value, worldPoint);
                }

                prev = worldPoint;
            }
        }

        internal static Vector3 GetPerpendicular(Vector3 dir)
        {
            var absDir = new Vector3(MathF.Abs(dir.x), MathF.Abs(dir.y), MathF.Abs(dir.z));
            Vector3 helper = absDir.x < 0.9f ? Vector3.right : Vector3.up;
            return Vector3.Cross(dir, helper).normalized;
        }

        public void Dispose()
        {
            _batchMesh?.Dispose();
            _pickBatchMesh?.Dispose();
        }
    }
}
