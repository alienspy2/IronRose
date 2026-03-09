using System;
using System.Collections.Generic;

namespace RoseEngine
{
    public enum PrimitiveType
    {
        Cube,
        Sphere,
        Capsule,
        Cylinder,
        Plane,
        Quad,
    }

    public static class PrimitiveGenerator
    {
        public static Mesh CreateCube()
        {
            var mesh = new Mesh { name = "Cube" };

            // 24 vertices (6 faces x 4 vertices each, separate normals per face)
            mesh.vertices = new Vertex[]
            {
                // Front face (Z+)
                new(new Vector3(-0.5f, -0.5f,  0.5f), Vector3.forward, new Vector2(0, 1)),
                new(new Vector3( 0.5f, -0.5f,  0.5f), Vector3.forward, new Vector2(1, 1)),
                new(new Vector3( 0.5f,  0.5f,  0.5f), Vector3.forward, new Vector2(1, 0)),
                new(new Vector3(-0.5f,  0.5f,  0.5f), Vector3.forward, new Vector2(0, 0)),

                // Back face (Z-)
                new(new Vector3( 0.5f, -0.5f, -0.5f), Vector3.back, new Vector2(0, 1)),
                new(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.back, new Vector2(1, 1)),
                new(new Vector3(-0.5f,  0.5f, -0.5f), Vector3.back, new Vector2(1, 0)),
                new(new Vector3( 0.5f,  0.5f, -0.5f), Vector3.back, new Vector2(0, 0)),

                // Top face (Y+)
                new(new Vector3(-0.5f,  0.5f,  0.5f), Vector3.up, new Vector2(0, 1)),
                new(new Vector3( 0.5f,  0.5f,  0.5f), Vector3.up, new Vector2(1, 1)),
                new(new Vector3( 0.5f,  0.5f, -0.5f), Vector3.up, new Vector2(1, 0)),
                new(new Vector3(-0.5f,  0.5f, -0.5f), Vector3.up, new Vector2(0, 0)),

                // Bottom face (Y-)
                new(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.down, new Vector2(0, 1)),
                new(new Vector3( 0.5f, -0.5f, -0.5f), Vector3.down, new Vector2(1, 1)),
                new(new Vector3( 0.5f, -0.5f,  0.5f), Vector3.down, new Vector2(1, 0)),
                new(new Vector3(-0.5f, -0.5f,  0.5f), Vector3.down, new Vector2(0, 0)),

                // Right face (X+)
                new(new Vector3( 0.5f, -0.5f,  0.5f), Vector3.right, new Vector2(0, 1)),
                new(new Vector3( 0.5f, -0.5f, -0.5f), Vector3.right, new Vector2(1, 1)),
                new(new Vector3( 0.5f,  0.5f, -0.5f), Vector3.right, new Vector2(1, 0)),
                new(new Vector3( 0.5f,  0.5f,  0.5f), Vector3.right, new Vector2(0, 0)),

                // Left face (X-)
                new(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.left, new Vector2(0, 1)),
                new(new Vector3(-0.5f, -0.5f,  0.5f), Vector3.left, new Vector2(1, 1)),
                new(new Vector3(-0.5f,  0.5f,  0.5f), Vector3.left, new Vector2(1, 0)),
                new(new Vector3(-0.5f,  0.5f, -0.5f), Vector3.left, new Vector2(0, 0)),
            };

            // 36 indices (6 faces x 2 triangles x 3 vertices)
            mesh.indices = new uint[]
            {
                 0,  1,  2,   0,  2,  3,  // Front
                 4,  5,  6,   4,  6,  7,  // Back
                 8,  9, 10,   8, 10, 11,  // Top
                12, 13, 14,  12, 14, 15,  // Bottom
                16, 17, 18,  16, 18, 19,  // Right
                20, 21, 22,  20, 22, 23,  // Left
            };

            return mesh;
        }

        /// <summary>UV sphere, radius=0.5, 24 longitude x 16 latitude (Unity-compatible).</summary>
        public static Mesh CreateSphere(int lonSegments = 24, int latSegments = 16)
        {
            var mesh = new Mesh { name = "Sphere" };
            float radius = 0.5f;

            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // Generate vertices ring by ring from top pole to bottom pole
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float theta = MathF.PI * lat / latSegments;       // 0 (top) → π (bottom)
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = 2f * MathF.PI * lon / lonSegments; // 0 → 2π
                    float sinPhi = MathF.Sin(phi);
                    float cosPhi = MathF.Cos(phi);

                    var normal = new Vector3(sinTheta * cosPhi, cosTheta, sinTheta * sinPhi);
                    var pos = normal * radius;
                    var uv = new Vector2((float)lon / lonSegments, (float)lat / latSegments);

                    verts.Add(new Vertex(pos, normal, uv));
                }
            }

            // Generate indices (CW winding)
            int ringSize = lonSegments + 1;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    uint current = (uint)(lat * ringSize + lon);
                    uint next = current + (uint)ringSize;

                    indices.Add(current);
                    indices.Add(current + 1);
                    indices.Add(next);

                    indices.Add(current + 1);
                    indices.Add(next + 1);
                    indices.Add(next);
                }
            }

            mesh.vertices = verts.ToArray();
            mesh.indices = indices.ToArray();
            return mesh;
        }

        /// <summary>Capsule: height=2, radius=0.5, Y-axis aligned (Unity-compatible).</summary>
        public static Mesh CreateCapsule(int lonSegments = 24, int capRings = 8, int bodyRings = 1)
        {
            var mesh = new Mesh { name = "Capsule" };
            float radius = 0.5f;
            float halfHeight = 0.5f; // half of cylinder body height (total height = 2 = body 1 + caps 1)

            var verts = new List<Vertex>();
            var indices = new List<uint>();
            int ringSize = lonSegments + 1;

            // --- Top hemisphere (Y = halfHeight .. halfHeight+radius) ---
            for (int lat = 0; lat <= capRings; lat++)
            {
                float theta = MathF.PI * 0.5f * lat / capRings; // 0 (pole) → π/2 (equator)
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = 2f * MathF.PI * lon / lonSegments;
                    var normal = new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
                    var pos = new Vector3(normal.x * radius, halfHeight + normal.y * radius, normal.z * radius);
                    float v = 0.5f * (1f - (float)lat / capRings) * 0.5f; // 0 → 0.25
                    verts.Add(new Vertex(pos, normal, new Vector2((float)lon / lonSegments, v)));
                }
            }

            // --- Cylinder body ---
            for (int ring = 0; ring <= bodyRings; ring++)
            {
                float t = (float)ring / bodyRings;
                float y = halfHeight - t * (2f * halfHeight); // halfHeight → -halfHeight

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = 2f * MathF.PI * lon / lonSegments;
                    var normal = new Vector3(MathF.Cos(phi), 0f, MathF.Sin(phi));
                    var pos = new Vector3(normal.x * radius, y, normal.z * radius);
                    float v = 0.25f + t * 0.5f; // 0.25 → 0.75
                    verts.Add(new Vertex(pos, normal, new Vector2((float)lon / lonSegments, v)));
                }
            }

            // --- Bottom hemisphere (Y = -halfHeight .. -(halfHeight+radius)) ---
            for (int lat = 0; lat <= capRings; lat++)
            {
                float theta = MathF.PI * 0.5f + MathF.PI * 0.5f * lat / capRings; // π/2 → π
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float phi = 2f * MathF.PI * lon / lonSegments;
                    var normal = new Vector3(sinTheta * MathF.Cos(phi), cosTheta, sinTheta * MathF.Sin(phi));
                    var pos = new Vector3(normal.x * radius, -halfHeight + normal.y * radius, normal.z * radius);
                    float v = 0.75f + 0.25f * (float)lat / capRings; // 0.75 → 1.0
                    verts.Add(new Vertex(pos, normal, new Vector2((float)lon / lonSegments, v)));
                }
            }

            // Generate indices for all rings (CW winding)
            int totalRings = capRings + bodyRings + capRings + 1; // +1 for connecting ring
            int totalVertexRows = capRings + 1 + bodyRings + 1 + capRings + 1;
            for (int row = 0; row < totalVertexRows - 1; row++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    uint current = (uint)(row * ringSize + lon);
                    uint next = current + (uint)ringSize;

                    indices.Add(current);
                    indices.Add(current + 1);
                    indices.Add(next);

                    indices.Add(current + 1);
                    indices.Add(next + 1);
                    indices.Add(next);
                }
            }

            mesh.vertices = verts.ToArray();
            mesh.indices = indices.ToArray();
            return mesh;
        }

        /// <summary>Plane: 10x10 units, 10x10 subdivisions, Y-up (Unity-compatible).</summary>
        public static Mesh CreatePlane(int resolution = 10)
        {
            var mesh = new Mesh { name = "Plane" };
            float size = 10f;
            float half = size * 0.5f;

            var verts = new Vertex[(resolution + 1) * (resolution + 1)];
            var indices = new List<uint>();

            for (int z = 0; z <= resolution; z++)
            {
                for (int x = 0; x <= resolution; x++)
                {
                    float px = -half + size * x / resolution;
                    float pz = -half + size * z / resolution;
                    float u = (float)x / resolution;
                    float v = (float)z / resolution;

                    verts[z * (resolution + 1) + x] = new Vertex(
                        new Vector3(px, 0f, pz),
                        Vector3.up,
                        new Vector2(u, v));
                }
            }

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    uint i = (uint)(z * (resolution + 1) + x);
                    uint row = (uint)(resolution + 1);

                    indices.Add(i);
                    indices.Add(i + row);
                    indices.Add(i + 1);

                    indices.Add(i + 1);
                    indices.Add(i + row);
                    indices.Add(i + row + 1);
                }
            }

            mesh.vertices = verts;
            mesh.indices = indices.ToArray();
            return mesh;
        }

        /// <summary>Cylinder: height=2, radius=0.5, Y-axis aligned (Unity-compatible).</summary>
        public static Mesh CreateCylinder(int segments = 24)
        {
            var mesh = new Mesh { name = "Cylinder" };
            float radius = 0.5f;
            float halfHeight = 1f; // total height = 2

            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // --- Top cap center ---
            uint topCenterIdx = (uint)verts.Count;
            verts.Add(new Vertex(new Vector3(0, halfHeight, 0), Vector3.up, new Vector2(0.5f, 0.5f)));

            // Top cap ring
            uint topRingStart = (uint)verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float x = MathF.Cos(angle);
                float z = MathF.Sin(angle);
                var pos = new Vector3(x * radius, halfHeight, z * radius);
                var uv = new Vector2(x * 0.5f + 0.5f, z * 0.5f + 0.5f);
                verts.Add(new Vertex(pos, Vector3.up, uv));
            }

            // Top cap triangles (CW winding)
            for (int i = 0; i < segments; i++)
            {
                indices.Add(topCenterIdx);
                indices.Add(topRingStart + (uint)i + 1);
                indices.Add(topRingStart + (uint)i);
            }

            // --- Bottom cap center ---
            uint bottomCenterIdx = (uint)verts.Count;
            verts.Add(new Vertex(new Vector3(0, -halfHeight, 0), Vector3.down, new Vector2(0.5f, 0.5f)));

            // Bottom cap ring
            uint bottomRingStart = (uint)verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float x = MathF.Cos(angle);
                float z = MathF.Sin(angle);
                var pos = new Vector3(x * radius, -halfHeight, z * radius);
                var uv = new Vector2(x * 0.5f + 0.5f, z * 0.5f + 0.5f);
                verts.Add(new Vertex(pos, Vector3.down, uv));
            }

            // Bottom cap triangles (CW winding, reversed for downward normal)
            for (int i = 0; i < segments; i++)
            {
                indices.Add(bottomCenterIdx);
                indices.Add(bottomRingStart + (uint)i);
                indices.Add(bottomRingStart + (uint)i + 1);
            }

            // --- Side wall ---
            // Top ring (with outward normals)
            uint sideTopStart = (uint)verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float x = MathF.Cos(angle);
                float z = MathF.Sin(angle);
                var normal = new Vector3(x, 0, z);
                var pos = new Vector3(x * radius, halfHeight, z * radius);
                verts.Add(new Vertex(pos, normal, new Vector2((float)i / segments, 0)));
            }

            // Bottom ring (with outward normals)
            uint sideBottomStart = (uint)verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float x = MathF.Cos(angle);
                float z = MathF.Sin(angle);
                var normal = new Vector3(x, 0, z);
                var pos = new Vector3(x * radius, -halfHeight, z * radius);
                verts.Add(new Vertex(pos, normal, new Vector2((float)i / segments, 1)));
            }

            // Side wall triangles (CW winding)
            for (int i = 0; i < segments; i++)
            {
                uint t0 = sideTopStart + (uint)i;
                uint t1 = sideTopStart + (uint)i + 1;
                uint b0 = sideBottomStart + (uint)i;
                uint b1 = sideBottomStart + (uint)i + 1;

                indices.Add(t0);
                indices.Add(t1);
                indices.Add(b0);

                indices.Add(t1);
                indices.Add(b1);
                indices.Add(b0);
            }

            mesh.vertices = verts.ToArray();
            mesh.indices = indices.ToArray();
            return mesh;
        }

        /// <summary>Cone: apex at origin, base at Z=1, radius=1. For spot light volumes.</summary>
        public static Mesh CreateCone(int segments = 16)
        {
            var mesh = new Mesh { name = "Cone" };
            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // Apex vertex
            verts.Add(new Vertex(Vector3.zero, new Vector3(0, 0, -1), new Vector2(0.5f, 0)));

            // Base circle vertices
            for (int i = 0; i <= segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float x = MathF.Cos(angle);
                float y = MathF.Sin(angle);
                var pos = new Vector3(x, y, 1f);
                // Side normal: perpendicular to cone surface, pointing outward
                var sideNormal = new Vector3(x, y, 1f);
                sideNormal = sideNormal.normalized;
                verts.Add(new Vertex(pos, sideNormal, new Vector2((float)i / segments, 1)));
            }

            // Side triangles: apex → base[i+1] → base[i] (CW winding)
            for (int i = 0; i < segments; i++)
            {
                indices.Add(0);               // apex
                indices.Add((uint)(i + 2));   // base[i+1]
                indices.Add((uint)(i + 1));   // base[i]
            }

            // Base cap center vertex
            uint baseCenterIdx = (uint)verts.Count;
            verts.Add(new Vertex(new Vector3(0, 0, 1), Vector3.forward, new Vector2(0.5f, 0.5f)));

            // Base cap triangles: center → base[i] → base[i+1] (CW winding, outward normal = +Z)
            for (int i = 0; i < segments; i++)
            {
                indices.Add(baseCenterIdx);
                indices.Add((uint)(i + 1));   // base[i]
                indices.Add((uint)(i + 2));   // base[i+1]
            }

            mesh.vertices = verts.ToArray();
            mesh.indices = indices.ToArray();
            return mesh;
        }

        /// <summary>Quad: 1x1, centered, facing Z+ (Unity-compatible).</summary>
        public static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "Quad" };

            mesh.vertices = new Vertex[]
            {
                new(new Vector3(-0.5f, -0.5f, 0f), Vector3.forward, new Vector2(0, 1)),
                new(new Vector3( 0.5f, -0.5f, 0f), Vector3.forward, new Vector2(1, 1)),
                new(new Vector3( 0.5f,  0.5f, 0f), Vector3.forward, new Vector2(1, 0)),
                new(new Vector3(-0.5f,  0.5f, 0f), Vector3.forward, new Vector2(0, 0)),
            };

            mesh.indices = new uint[]
            {
                0, 1, 2,  0, 2, 3,
            };

            return mesh;
        }
    }
}
