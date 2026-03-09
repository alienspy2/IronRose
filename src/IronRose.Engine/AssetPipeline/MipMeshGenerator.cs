using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Meshoptimizer;
using RoseEngine;
using Debug = RoseEngine.Debug;

namespace IronRose.AssetPipeline
{
    public static class MipMeshGenerator
    {
        private const int DefaultMinTriangles = 500;
        private const float DefaultTargetError = 0.02f;
        private const float DefaultReduction = 0.1f;

        /// <summary>
        /// 원본 메시로부터 MipMesh LOD 체인을 생성한다.
        /// LOD 0 = full-attribute welding 적용 (동일 정점 제거, 인덱스 버퍼 최적화).
        /// LOD 1+ = SimplifyWithAttributes로 점진적 축소.
        /// 모든 LOD는 항상 LOD 0으로부터 생성 (체이닝 금지 — 오차 누적 방지).
        /// </summary>
        public static MipMesh Generate(Mesh originalMesh, int minTriangles = DefaultMinTriangles, float targetError = DefaultTargetError, float reduction = DefaultReduction)
        {
            var sw = Stopwatch.StartNew();

            int originalTriCount = originalMesh.indices.Length / 3;
            Debug.Log($"[MipMesh] Generating LODs for '{originalMesh.name}' ({originalMesh.vertices.Length} verts, {originalTriCount} tris, minTri={minTriangles}, targetError={targetError:F4}, reduction={reduction:P0})");

            // ── Step 1: Full-attribute welding → LOD 0 ──────────────
            // 동일한 Position+Normal+UV를 가진 정점을 병합하여 중복 제거.
            // 하드 엣지/UV 심의 split vertex는 보존된다.
            var lod0 = WeldMesh(originalMesh);
            var lods = new List<Mesh> { lod0 };

            int lod0VertCount = lod0.vertices.Length;
            int lod0TriCount = lod0.indices.Length / 3;
            Debug.Log($"[MipMesh]   Full-attribute weld: {originalMesh.vertices.Length} → {lod0VertCount} unique verts, {lod0TriCount} tris");

            if (lod0TriCount <= minTriangles)
            {
                Debug.Log($"[MipMesh]   Skipped — tri count ({lod0TriCount}) <= minTriangles ({minTriangles})");
                sw.Stop();
                Debug.Log($"[MipMesh] Done: 1 LOD (welded only) in {sw.ElapsedMilliseconds}ms");
                return new MipMesh { lodMeshes = lods.ToArray() };
            }

            // ── Step 2: LOD 0에서 positions/attributes 추출 ─────────
            // Simplifier에 LOD 0 데이터를 직접 전달한다.
            // Simplifier는 position 값으로 co-located vertex를 자동 감지하므로
            // position-only welding이 불필요.
            var positions = new float[lod0VertCount * 3];
            var attributes = new float[lod0VertCount * 5];
            for (int i = 0; i < lod0VertCount; i++)
            {
                var v = lod0.vertices[i];
                positions[i * 3 + 0] = v.Position.x;
                positions[i * 3 + 1] = v.Position.y;
                positions[i * 3 + 2] = v.Position.z;

                attributes[i * 5 + 0] = v.Normal.x;
                attributes[i * 5 + 1] = v.Normal.y;
                attributes[i * 5 + 2] = v.Normal.z;
                attributes[i * 5 + 3] = v.UV.x;
                attributes[i * 5 + 4] = v.UV.y;
            }

            nuint posStride = 3 * sizeof(float); // 12 bytes
            nuint attrStride = 5 * sizeof(float); // 20 bytes
            float[] attrWeights = [1f, 1f, 1f, 1f, 1f]; // normal x3 + uv x2
            nuint attrCount = 5;

            // ── Step 3: LOD 생성 루프 ───────────────────────────────
            float reductionRatio = Math.Clamp(1f - reduction, 0.1f, 0.99f); // reduction=0.5 → 매 단계 50%로 축소
            int level = 1;
            double targetTri = lod0TriCount;
            while (true)
            {
                targetTri *= reductionRatio;
                int targetTriCount = (int)targetTri;
                if (targetTriCount < minTriangles)
                    break;

                nuint targetIndexCount = (nuint)(targetTriCount * 3);

                var lodIndices = new uint[lod0.indices.Length];
                float resultError;

                nuint actualIndexCount = MeshoptNative.SimplifyWithAttributes(
                    lodIndices,
                    lod0.indices,                    // LOD 0의 인덱스 직접 사용
                    (nuint)lod0.indices.Length,
                    positions,
                    (nuint)lod0VertCount,             // LOD 0의 vertex count
                    posStride,
                    attributes,
                    attrStride,
                    attrWeights,
                    attrCount,
                    IntPtr.Zero, // vertexLock = none
                    targetIndexCount,
                    targetError,
                    0,     // options
                    out resultError);

                int actualTriCount = (int)actualIndexCount / 3;
                int prevTriCount = lods[^1].indices.Length / 3;

                Debug.Log($"[MipMesh]   LOD{level} attempt: target={targetTriCount} tri, actual={actualTriCount} tri (indexCount={actualIndexCount}), prev={prevTriCount} tri, error={resultError:F6}");

                // 더 이상 줄어들지 않으면 중단
                if (actualTriCount >= prevTriCount || actualIndexCount == 0)
                {
                    Debug.Log($"[MipMesh]   LOD{level} stopped: {(actualIndexCount == 0 ? "simplify returned 0" : $"no reduction ({actualTriCount} >= {prevTriCount})")}");
                    break;
                }

                // 실제 사용된 인덱스만 복사
                var trimmedIndices = new uint[actualIndexCount];
                Array.Copy(lodIndices, trimmedIndices, (int)actualIndexCount);

                // Vertex cache 최적화 (인덱스 순서 재배치)
                var optimized = new uint[trimmedIndices.Length];
                Meshopt.OptimizeVertexCache(
                    ref optimized[0],
                    in trimmedIndices[0],
                    (nuint)trimmedIndices.Length,
                    (nuint)lod0VertCount);            // LOD 0의 vertex count
                trimmedIndices = optimized;

                var lodMesh = new Mesh
                {
                    name = $"{originalMesh.name}_LOD{level}",
                    vertices = lod0.vertices, // LOD 0의 welded vertex 공유
                    indices = trimmedIndices,
                };

                lods.Add(lodMesh);

                Debug.Log($"[MipMesh]   LOD{level}: {actualTriCount} tri ({(float)actualTriCount / lod0TriCount * 100:F1}%, error={resultError:F4})");

                level++;
            }

            sw.Stop();
            Debug.Log($"[MipMesh] Done: {lods.Count} LODs for '{originalMesh.name}' in {sw.ElapsedMilliseconds}ms " +
                      $"(LOD0={lod0TriCount} → LOD{lods.Count - 1}={lods[^1].indices.Length / 3} tris)");

            return new MipMesh { lodMeshes = lods.ToArray() };
        }

        /// <summary>
        /// Full-attribute welding: Position+Normal+UV가 완전히 동일한 정점만 병합.
        /// 하드 엣지/UV 심의 split vertex는 보존된다.
        /// Vertex cache 최적화도 적용.
        /// </summary>
        private static Mesh WeldMesh(Mesh mesh)
        {
            int vertCount = mesh.vertices.Length;

            // Position(3) + Normal(3) + UV(2) = 8 floats per vertex
            var vertexData = new float[vertCount * 8];
            for (int i = 0; i < vertCount; i++)
            {
                var v = mesh.vertices[i];
                vertexData[i * 8 + 0] = v.Position.x;
                vertexData[i * 8 + 1] = v.Position.y;
                vertexData[i * 8 + 2] = v.Position.z;
                vertexData[i * 8 + 3] = v.Normal.x;
                vertexData[i * 8 + 4] = v.Normal.y;
                vertexData[i * 8 + 5] = v.Normal.z;
                vertexData[i * 8 + 6] = v.UV.x;
                vertexData[i * 8 + 7] = v.UV.y;
            }

            var remap = new uint[vertCount];
            nuint uniqueCount;

            unsafe
            {
                fixed (float* dataPtr = vertexData)
                {
                    uniqueCount = MeshoptNative.GenerateVertexRemap(
                        remap,
                        mesh.indices,
                        (nuint)mesh.indices.Length,
                        dataPtr,
                        (nuint)vertCount,
                        8 * sizeof(float)); // full vertex stride
                }
            }

            int unique = (int)uniqueCount;

            // 인덱스 리맵
            var remappedIndices = new uint[mesh.indices.Length];
            MeshoptNative.RemapIndexBuffer(
                remappedIndices,
                mesh.indices,
                (nuint)mesh.indices.Length,
                remap);

            // Compact vertex array
            var newVertices = new Vertex[unique];
            var filled = new bool[unique];
            for (int i = 0; i < vertCount; i++)
            {
                int newIdx = (int)remap[i];
                if (filled[newIdx]) continue;
                filled[newIdx] = true;
                newVertices[newIdx] = mesh.vertices[i];
            }

            // Vertex cache 최적화
            var optimizedIndices = new uint[remappedIndices.Length];
            Meshopt.OptimizeVertexCache(
                ref optimizedIndices[0],
                in remappedIndices[0],
                (nuint)remappedIndices.Length,
                (nuint)unique);

            return new Mesh
            {
                name = mesh.name,
                vertices = newVertices,
                indices = optimizedIndices,
            };
        }

    }
}
