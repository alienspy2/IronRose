using System;
using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Engine.Editor.SceneView
{
    /// <summary>
    /// Procedural mesh generation for Transform Gizmo handles.
    /// Each method returns a Mesh ready for GPU upload.
    /// </summary>
    internal static class GizmoMeshBuilder
    {
        private const int CylinderSegments = 12;
        private const int ConeSegments = 12;
        private const int TorusSegmentsRing = 24;
        private const int TorusSegmentsTube = 8;

        /// <summary>
        /// Translate handle: thin cylinder shaft + cone arrow tip.
        /// Points along +Y axis from origin to length.
        /// </summary>
        public static Mesh CreateArrow(float shaftLength = 0.8f, float shaftRadius = 0.02f,
            float tipLength = 0.2f, float tipRadius = 0.06f)
        {
            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // Shaft (cylinder along Y)
            AddCylinder(verts, indices, Vector3.zero, shaftLength, shaftRadius, CylinderSegments);

            // Cone tip
            uint baseIdx = (uint)verts.Count;
            AddCone(verts, indices, new Vector3(0, shaftLength, 0), tipLength, tipRadius, ConeSegments);

            return BuildMesh(verts, indices);
        }

        /// <summary>
        /// Rotate handle: torus ring in the XZ plane centered at origin.
        /// </summary>
        public static Mesh CreateRing(float majorRadius = 1f, float minorRadius = 0.02f)
        {
            var verts = new List<Vertex>();
            var indices = new List<uint>();
            AddTorus(verts, indices, majorRadius, minorRadius, TorusSegmentsRing, TorusSegmentsTube);
            return BuildMesh(verts, indices);
        }

        /// <summary>
        /// Scale handle: thin cylinder shaft + small cube at the tip.
        /// Points along +Y axis.
        /// </summary>
        public static Mesh CreateScaleHandle(float shaftLength = 0.8f, float shaftRadius = 0.02f,
            float cubeSize = 0.08f)
        {
            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // Shaft
            AddCylinder(verts, indices, Vector3.zero, shaftLength, shaftRadius, CylinderSegments);

            // Cube at tip
            AddCube(verts, indices, new Vector3(0, shaftLength + cubeSize * 0.5f, 0), cubeSize);

            return BuildMesh(verts, indices);
        }

        // ================================================================
        // Primitive generators
        // ================================================================

        private static void AddCylinder(List<Vertex> verts, List<uint> indices,
            Vector3 baseCenter, float height, float radius, int segments)
        {
            uint baseIdx = (uint)verts.Count;

            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)i / segments * MathF.PI * 2f;
                float cos = MathF.Cos(angle);
                float sin = MathF.Sin(angle);
                var normal = new Vector3(cos, 0, sin);

                verts.Add(new Vertex(
                    baseCenter + new Vector3(cos * radius, 0, sin * radius),
                    normal, new Vector2((float)i / segments, 0)));
                verts.Add(new Vertex(
                    baseCenter + new Vector3(cos * radius, height, sin * radius),
                    normal, new Vector2((float)i / segments, 1)));
            }

            for (int i = 0; i < segments; i++)
            {
                uint a = baseIdx + (uint)(i * 2);
                uint b = a + 1;
                uint c = a + 2;
                uint d = a + 3;
                indices.Add(a); indices.Add(b); indices.Add(c);
                indices.Add(c); indices.Add(b); indices.Add(d);
            }
        }

        private static void AddCone(List<Vertex> verts, List<uint> indices,
            Vector3 baseCenter, float height, float radius, int segments)
        {
            uint baseIdx = (uint)verts.Count;
            var tipPos = baseCenter + new Vector3(0, height, 0);

            // Tip vertex
            verts.Add(new Vertex(tipPos, Vector3.up, new Vector2(0.5f, 1)));
            uint tipIdx = baseIdx;

            // Base ring
            for (int i = 0; i <= segments; i++)
            {
                float angle = (float)i / segments * MathF.PI * 2f;
                float cos = MathF.Cos(angle);
                float sin = MathF.Sin(angle);
                // Cone normal: outward + slightly up
                float slopeAngle = MathF.Atan2(radius, height);
                var normal = new Vector3(cos * MathF.Cos(slopeAngle), MathF.Sin(slopeAngle), sin * MathF.Cos(slopeAngle));

                verts.Add(new Vertex(
                    baseCenter + new Vector3(cos * radius, 0, sin * radius),
                    normal, new Vector2((float)i / segments, 0)));
            }

            for (int i = 0; i < segments; i++)
            {
                uint a = baseIdx + 1 + (uint)i;
                uint b = a + 1;
                indices.Add(tipIdx); indices.Add(a); indices.Add(b);
            }
        }

        private static void AddTorus(List<Vertex> verts, List<uint> indices,
            float majorR, float minorR, int ringSegs, int tubeSegs)
        {
            uint baseIdx = (uint)verts.Count;

            for (int i = 0; i <= ringSegs; i++)
            {
                float u = (float)i / ringSegs * MathF.PI * 2f;
                float cu = MathF.Cos(u);
                float su = MathF.Sin(u);

                for (int j = 0; j <= tubeSegs; j++)
                {
                    float v = (float)j / tubeSegs * MathF.PI * 2f;
                    float cv = MathF.Cos(v);
                    float sv = MathF.Sin(v);

                    float x = (majorR + minorR * cv) * cu;
                    float y = minorR * sv;
                    float z = (majorR + minorR * cv) * su;

                    var normal = new Vector3(cv * cu, sv, cv * su);

                    verts.Add(new Vertex(
                        new Vector3(x, y, z),
                        normal,
                        new Vector2((float)i / ringSegs, (float)j / tubeSegs)));
                }
            }

            for (int i = 0; i < ringSegs; i++)
            {
                for (int j = 0; j < tubeSegs; j++)
                {
                    uint a = baseIdx + (uint)(i * (tubeSegs + 1) + j);
                    uint b = a + (uint)(tubeSegs + 1);
                    uint c = a + 1;
                    uint d = b + 1;
                    indices.Add(a); indices.Add(b); indices.Add(c);
                    indices.Add(c); indices.Add(b); indices.Add(d);
                }
            }
        }

        private static void AddCube(List<Vertex> verts, List<uint> indices,
            Vector3 center, float size)
        {
            uint baseIdx = (uint)verts.Count;
            float h = size * 0.5f;

            // 6 faces, each with 4 vertices
            // +Y face
            AddQuad(verts, indices, center + new Vector3(-h, h, -h), center + new Vector3(h, h, -h),
                center + new Vector3(h, h, h), center + new Vector3(-h, h, h), Vector3.up);
            // -Y face
            AddQuad(verts, indices, center + new Vector3(-h, -h, h), center + new Vector3(h, -h, h),
                center + new Vector3(h, -h, -h), center + new Vector3(-h, -h, -h), -Vector3.up);
            // +X face
            AddQuad(verts, indices, center + new Vector3(h, -h, -h), center + new Vector3(h, -h, h),
                center + new Vector3(h, h, h), center + new Vector3(h, h, -h), Vector3.right);
            // -X face
            AddQuad(verts, indices, center + new Vector3(-h, -h, h), center + new Vector3(-h, -h, -h),
                center + new Vector3(-h, h, -h), center + new Vector3(-h, h, h), -Vector3.right);
            // +Z face
            AddQuad(verts, indices, center + new Vector3(-h, -h, h), center + new Vector3(h, -h, h),
                center + new Vector3(h, h, h), center + new Vector3(-h, h, h), Vector3.forward);
            // -Z face
            AddQuad(verts, indices, center + new Vector3(h, -h, -h), center + new Vector3(-h, -h, -h),
                center + new Vector3(-h, h, -h), center + new Vector3(h, h, -h), -Vector3.forward);
        }

        private static void AddQuad(List<Vertex> verts, List<uint> indices,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            uint baseIdx = (uint)verts.Count;
            verts.Add(new Vertex(a, normal, new Vector2(0, 0)));
            verts.Add(new Vertex(b, normal, new Vector2(1, 0)));
            verts.Add(new Vertex(c, normal, new Vector2(1, 1)));
            verts.Add(new Vertex(d, normal, new Vector2(0, 1)));
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
        }

        /// <summary>
        /// Plane handle: wireframe rectangle (LineList) for dual-axis (plane) translation.
        /// Lies in the XY plane starting from origin. Rotate externally for XZ / YZ variants.
        /// </summary>
        public static Mesh CreatePlaneHandle(float size = 0.30f)
        {
            var verts = new List<Vertex>();
            var indices = new List<uint>();
            var n = Vector3.forward;

            // 4 corners of the rectangle in XY plane, starting from origin
            verts.Add(new Vertex(new Vector3(0, 0, 0), n, Vector2.zero));       // 0
            verts.Add(new Vertex(new Vector3(size, 0, 0), n, Vector2.zero));    // 1
            verts.Add(new Vertex(new Vector3(size, size, 0), n, Vector2.zero)); // 2
            verts.Add(new Vertex(new Vector3(0, size, 0), n, Vector2.zero));    // 3

            // 4 edges as LineList pairs
            indices.Add(0); indices.Add(1);
            indices.Add(1); indices.Add(2);
            indices.Add(2); indices.Add(3);
            indices.Add(3); indices.Add(0);

            return BuildMesh(verts, indices);
        }

        private static Mesh BuildMesh(List<Vertex> verts, List<uint> indices)
        {
            var mesh = new Mesh
            {
                vertices = verts.ToArray(),
                indices = indices.ToArray(),
            };
            return mesh;
        }
    }
}
