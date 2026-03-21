using System.IO;
using System.Linq;
using RoseEngine;
using SharpGLTF.Schema2;
using GltfTexture = SharpGLTF.Schema2.Texture;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// SharpGLTF 기반 GLB/glTF 임포터.
    /// AssimpNet 4.1.0 번들의 libassimp.so 가 glTF2를 지원하지 않아
    /// .glb/.gltf 파일은 이 임포터를 사용한다.
    /// </summary>
    public class GltfMeshImporter
    {
        public MeshImportResult Import(string meshPath, float scale = 1.0f,
            bool generateNormals = false, bool flipUVs = true, bool triangulate = true)
        {
            if (!File.Exists(meshPath))
            {
                EditorDebug.LogError($"[GltfImporter] File not found: {meshPath}");
                return null!;
            }

            ModelRoot model;
            try
            {
                model = ModelRoot.Load(meshPath);
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[GltfImporter] Failed to load '{meshPath}': {ex.Message}");
                return null!;
            }

            if (model.LogicalMeshes.Count == 0)
            {
                EditorDebug.LogError($"[GltfImporter] No meshes found in '{meshPath}'");
                return null!;
            }

            // 메시 추출
            // glTF는 오른손 좌표계(+Z = 뷰어 방향)를 사용.
            // X축 네게이트 + 와인딩 반전으로 좌손 좌표계 보정 (기존 Assimp 파이프라인과 동일).
            var namedMeshes = new List<NamedMesh>();
            int totalVerts = 0, totalTris = 0;

            foreach (var gltfMesh in model.LogicalMeshes)
            {
                foreach (var primitive in gltfMesh.Primitives)
                {
                    var posAccessor = primitive.GetVertexAccessor("POSITION");
                    if (posAccessor == null) continue;

                    var positions = posAccessor.AsVector3Array();
                    var normalAccessor = primitive.GetVertexAccessor("NORMAL");
                    var normals = normalAccessor?.AsVector3Array();
                    var uvAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
                    var uvs = uvAccessor?.AsVector2Array();

                    bool hasNormals = normals != null && normals.Count > 0;
                    bool needsCalculateNormals = !hasNormals || generateNormals;

                    // 버텍스 빌드 (노멀 없거나 재계산 요청 시 임시 (0,0,0))
                    var vertices = new Vertex[positions.Count];
                    for (int i = 0; i < positions.Count; i++)
                    {
                        var pos = positions[i];
                        var normal = hasNormals && !generateNormals && i < normals!.Count
                            ? normals[i]
                            : new System.Numerics.Vector3(0, 0, 0);
                        var uv = uvs != null && i < uvs.Count
                            ? uvs[i]
                            : System.Numerics.Vector2.Zero;

                        // X축 네게이트로 좌손 좌표계 변환
                        vertices[i] = new Vertex
                        {
                            Position = new Vector3(-pos.X * scale, pos.Y * scale, pos.Z * scale),
                            Normal = new Vector3(-normal.X, normal.Y, normal.Z),
                            UV = flipUVs
                                ? new Vector2(uv.X, 1.0f - uv.Y)
                                : new Vector2(uv.X, uv.Y)
                        };
                    }

                    // 인덱스 빌드 — 와인딩 반전 (0,2,1)
                    var triIndices = new List<uint>();
                    foreach (var (a, b, c) in primitive.GetTriangleIndices())
                    {
                        triIndices.Add((uint)a);
                        triIndices.Add((uint)c);
                        triIndices.Add((uint)b);
                    }

                    // 노멀 계산: 에셋에 노멀이 없거나 generateNormals 옵션이 켜진 경우
                    if (needsCalculateNormals)
                    {
                        if (!hasNormals)
                            EditorDebug.Log($"[GltfImporter] No normals in primitive, calculating smooth normals");
                        else
                            EditorDebug.Log($"[GltfImporter] Recalculating normals (generate_normals=true)");
                        CalculateSmoothNormals(vertices, triIndices.ToArray());
                    }

                    string meshName = !string.IsNullOrEmpty(gltfMesh.Name)
                        ? gltfMesh.Name
                        : $"Mesh_{gltfMesh.LogicalIndex}";

                    // 프리미티브가 여러 개면 이름에 인덱스 추가
                    if (gltfMesh.Primitives.Count > 1)
                        meshName = $"{meshName}_{primitive.LogicalIndex}";

                    var mesh = new RoseEngine.Mesh
                    {
                        name = meshName,
                        vertices = vertices,
                        indices = triIndices.ToArray()
                    };

                    int matIndex = primitive.Material?.LogicalIndex ?? 0;

                    namedMeshes.Add(new NamedMesh
                    {
                        Name = meshName,
                        Mesh = mesh,
                        MaterialIndex = matIndex
                    });

                    totalVerts += vertices.Length;
                    totalTris += triIndices.Count / 3;
                }
            }

            if (namedMeshes.Count == 0)
            {
                EditorDebug.LogError($"[GltfImporter] No valid geometry in '{meshPath}'");
                return null!;
            }

            // 머티리얼 + 텍스처 추출 (텍스처 중복 제거)
            var (materials, textures) = ExtractMaterials(model);

            EditorDebug.Log($"[GltfImporter] Imported: {meshPath} ({namedMeshes.Count} meshes, {totalVerts} verts, {totalTris} tris, {materials.Length} materials, {textures.Length} textures)");

            return new MeshImportResult
            {
                Meshes = namedMeshes.ToArray(),
                Materials = materials,
                Textures = textures,
                MipMeshes = new RoseEngine.MipMesh?[namedMeshes.Count]
            };
        }

        /// <summary>
        /// 인덱스 기반 smooth normal 계산.
        /// 삼각형 면 노멀을 각 버텍스에 누적 후 정규화.
        /// </summary>
        private static void CalculateSmoothNormals(Vertex[] vertices, uint[] indices)
        {
            // 노멀 초기화
            for (int i = 0; i < vertices.Length; i++)
                vertices[i].Normal = Vector3.zero;

            // 삼각형별 면 노멀 누적
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                var i0 = indices[i];
                var i1 = indices[i + 1];
                var i2 = indices[i + 2];

                var v0 = vertices[i0].Position;
                var v1 = vertices[i1].Position;
                var v2 = vertices[i2].Position;

                var edge1 = v1 - v0;
                var edge2 = v2 - v0;
                var faceNormal = Vector3.Cross(edge1, edge2);

                vertices[i0].Normal += faceNormal;
                vertices[i1].Normal += faceNormal;
                vertices[i2].Normal += faceNormal;
            }

            // 정규화
            for (int i = 0; i < vertices.Length; i++)
            {
                var n = vertices[i].Normal;
                if (n.sqrMagnitude > 0f)
                    vertices[i].Normal = n.normalized;
                else
                    vertices[i].Normal = new Vector3(0, 1, 0);
            }
        }

        private static (RoseEngine.Material[] materials, RoseEngine.Texture2D[] textures) ExtractMaterials(ModelRoot model)
        {
            if (model.LogicalMaterials.Count == 0)
            {
                // 머티리얼이 없는 GLB는 기본 머티리얼 생성 (Assimp 동작과 동일)
                return ([new RoseEngine.Material
                {
                    name = "DefaultMaterial",
                    normalMap = Texture2D.DefaultNormal,
                    MROMap = Texture2D.DefaultMRO,
                    metallic = 0f,
                    roughness = 0.5f,
                    occlusion = 1f,
                }], []);
            }

            // 텍스처 중복 제거: GltfTexture.LogicalIndex → Texture2D
            var textureCache = new Dictionary<int, RoseEngine.Texture2D>();
            var materials = new List<RoseEngine.Material>();

            foreach (var gltfMat in model.LogicalMaterials)
            {
                var mat = new RoseEngine.Material();

                // Base Color
                var baseColorChannel = gltfMat.FindChannel("BaseColor");
                if (baseColorChannel.HasValue)
                {
                    var ch = baseColorChannel.Value;
                    mat.color = GetChannelColor(ch);

                    // Base color texture
                    if (ch.Texture != null)
                        mat.mainTexture = GetOrLoadTexture(ch.Texture, textureCache);
                }

                // Metallic / Roughness
                var mrChannel = gltfMat.FindChannel("MetallicRoughness");
                if (mrChannel.HasValue)
                {
                    var ch = mrChannel.Value;
                    var paramValues = GetChannelParams(ch);
                    mat.metallic = paramValues.X;
                    mat.roughness = paramValues.Y;
                }
                else
                {
                    mat.metallic = 0f;
                    mat.roughness = 0.5f;
                }

                // Emissive
                var emissiveChannel = gltfMat.FindChannel("Emissive");
                if (emissiveChannel.HasValue)
                {
                    var c = GetChannelColor(emissiveChannel.Value);
                    mat.emission = new Color(c.r, c.g, c.b, 1f);
                }

                // Normal map
                var normalChannel = gltfMat.FindChannel("Normal");
                if (normalChannel.HasValue && normalChannel.Value.Texture != null)
                    mat.normalMap = GetOrLoadTexture(normalChannel.Value.Texture, textureCache);

                if (mat.normalMap == null)
                    mat.normalMap = Texture2D.DefaultNormal;

                // Occlusion
                var occlusionChannel = gltfMat.FindChannel("Occlusion");
                if (occlusionChannel.HasValue)
                    mat.occlusion = GetChannelParams(occlusionChannel.Value).X;
                else
                    mat.occlusion = 1f;

                // MRO 기본값 (MRO 텍스처가 없을 경우)
                if (mat.MROMap == null)
                {
                    mat.MROMap = Texture2D.DefaultMRO;
                    if (!mrChannel.HasValue)
                    {
                        mat.metallic = 0f;
                        mat.roughness = 0.5f;
                        mat.occlusion = 1f;
                    }
                }

                mat.name = !string.IsNullOrEmpty(gltfMat.Name)
                    ? gltfMat.Name
                    : $"Material_{gltfMat.LogicalIndex}";

                materials.Add(mat);
            }

            // LogicalIndex 순으로 정렬하여 안정적인 sub-asset 인덱스 보장
            var textures = textureCache
                .OrderBy(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToArray();

            return (materials.ToArray(), textures);
        }

        /// <summary>채널 파라미터를 Vector4로 읽기 (deprecated Parameter 대신 Parameters[] 사용)</summary>
        private static System.Numerics.Vector4 GetChannelParams(MaterialChannel ch)
        {
            try
            {
                var parameters = ch.Parameters;
                if (parameters != null && parameters.Count > 0)
                {
                    var val = parameters[0].Value;
                    if (val is System.Numerics.Vector4 v4) return v4;
                    if (val is System.Numerics.Vector3 v3) return new System.Numerics.Vector4(v3, 1f);
                    if (val is float f)
                    {
                        // MetallicRoughness 등 다중 float 파라미터 채널 대응
                        float f1 = parameters.Count > 1 && parameters[1].Value is float ff1 ? ff1 : 0f;
                        float f2 = parameters.Count > 2 && parameters[2].Value is float ff2 ? ff2 : 0f;
                        float f3 = parameters.Count > 3 && parameters[3].Value is float ff3 ? ff3 : 0f;
                        return new System.Numerics.Vector4(f, f1, f2, f3);
                    }
                }
            }
            catch { /* fallback */ }

            // 폴백: deprecated Parameter 사용
#pragma warning disable CS0618
            return ch.Parameter;
#pragma warning restore CS0618
        }

        private static Color GetChannelColor(MaterialChannel ch)
        {
            var p = GetChannelParams(ch);
            return new Color(p.X, p.Y, p.Z, p.W);
        }

        /// <summary>
        /// 텍스처 캐시에서 동일 인스턴스를 반환하거나, 없으면 로드 후 캐시에 저장.
        /// Material과 Textures[] 배열이 같은 Texture2D 인스턴스를 참조하도록 보장.
        /// </summary>
        private static Texture2D? GetOrLoadTexture(GltfTexture texture, Dictionary<int, Texture2D> cache)
        {
            int key = texture.LogicalIndex;
            if (cache.TryGetValue(key, out var cached))
                return cached;

            var loaded = LoadTexture(texture);
            if (loaded != null)
            {
                var image = texture.PrimaryImage;
                loaded.name = !string.IsNullOrEmpty(image?.Name)
                    ? image!.Name
                    : $"Texture_{key}";
                cache[key] = loaded;
            }
            return loaded;
        }

        private static Texture2D? LoadTexture(GltfTexture texture)
        {
            try
            {
                var image = texture.PrimaryImage;
                if (image == null) return null;

                var content = image.Content;
                if (!content.IsValid) return null;

                var bytes = content.Content.ToArray();
                if (bytes.Length == 0) return null;

                EditorDebug.Log($"[GltfImporter] Loading texture: {image.Name ?? "unnamed"} ({bytes.Length} bytes, {content.MimeType})");
                return Texture2D.LoadFromMemory(bytes);
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[GltfImporter] Failed to load texture: {ex.Message}");
                return null;
            }
        }
    }
}
