using Assimp;
using System.IO;
using RoseEngine;
using Scene = Assimp.Scene;

namespace IronRose.AssetPipeline
{
    public struct NamedMesh
    {
        public string Name;
        public RoseEngine.Mesh Mesh;
        public int MaterialIndex; // Materials[] 인덱스
    }

    public class MeshImportResult
    {
        public NamedMesh[] Meshes { get; set; } = [];
        public RoseEngine.Material[] Materials { get; set; } = [];
        public RoseEngine.Texture2D[] Textures { get; set; } = [];
        public RoseEngine.MipMesh?[] MipMeshes { get; set; } = [];

        // 하위 호환 접근자
        public RoseEngine.Mesh? Mesh => Meshes.Length > 0 ? Meshes[0].Mesh : null;
        public RoseEngine.MipMesh? MipMesh => MipMeshes.Length > 0 ? MipMeshes[0] : null;
    }

    public class MeshImporter
    {
        public MeshImportResult Import(string meshPath, float scale = 1.0f,
            bool generateNormals = true, bool flipUVs = true, bool triangulate = true)
        {
            if (!File.Exists(meshPath))
            {
                Debug.LogError($"Mesh file not found: {meshPath}");
                return null!;
            }

            var postProcess = PostProcessSteps.None;
            if (triangulate) postProcess |= PostProcessSteps.Triangulate;
            if (generateNormals) postProcess |= PostProcessSteps.GenerateNormals;
            if (flipUVs) postProcess |= PostProcessSteps.FlipUVs;

            var context = new AssimpContext();

            // Assimp 내부 로그 캡처
            var logStream = new LogStream((msg, userData) =>
            {
                var trimmed = msg?.TrimEnd('\n', '\r');
                if (!string.IsNullOrEmpty(trimmed))
                    Debug.Log($"[Assimp] {trimmed}");
            });
            logStream.Attach();

            // 지원 포맷 진단 (최초 1회)
            LogSupportedFormats(context);

            Scene scene;
            try
            {
                scene = context.ImportFile(meshPath, postProcess);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MeshImporter] Assimp exception for '{meshPath}': {ex.Message}");
                return null!;
            }
            finally
            {
                logStream.Detach();
            }

            if (scene == null || scene.MeshCount == 0)
            {
                Debug.LogError($"[MeshImporter] Failed to load mesh (null or 0 meshes): {meshPath}");
                return null!;
            }

            // 개별 메시 추출 (병합하지 않음)
            // glTF는 오른손 좌표계(+Z = 뷰어 방향)를 사용하지만 Assimp 변환 후
            // X축이 반전되어 좌우 미러링이 발생함. X축 네게이트 + 와인딩 반전으로 보정.
            var namedMeshes = new List<NamedMesh>();
            int totalVerts = 0, totalTris = 0;

            for (int meshIdx = 0; meshIdx < scene.MeshCount; meshIdx++)
            {
                var assimpMesh = scene.Meshes[meshIdx];
                var vertices = new List<Vertex>();
                var indices = new List<uint>();

                bool hasUVs = assimpMesh.TextureCoordinateChannelCount > 0
                    && assimpMesh.TextureCoordinateChannels[0].Count > 0;

                for (int i = 0; i < assimpMesh.VertexCount; i++)
                {
                    var pos = assimpMesh.Vertices[i];
                    var normal = assimpMesh.HasNormals
                        ? assimpMesh.Normals[i]
                        : new Vector3D(0, 1, 0);
                    var uv = hasUVs
                        ? assimpMesh.TextureCoordinateChannels[0][i]
                        : new Vector3D(0, 0, 0);

                    vertices.Add(new Vertex
                    {
                        Position = new Vector3(-pos.X * scale, pos.Y * scale, pos.Z * scale),
                        Normal = new Vector3(-normal.X, normal.Y, normal.Z),
                        UV = new Vector2(uv.X, uv.Y)
                    });
                }

                // X축 네게이트로 와인딩이 반전되므로 인덱스 순서를 뒤집어 front face 보정
                foreach (var face in assimpMesh.Faces)
                {
                    if (face.IndexCount == 3)
                    {
                        indices.Add((uint)face.Indices[0]);
                        indices.Add((uint)face.Indices[2]);
                        indices.Add((uint)face.Indices[1]);
                    }
                }

                string meshName = !string.IsNullOrEmpty(assimpMesh.Name)
                    ? assimpMesh.Name
                    : $"Mesh_{meshIdx}";

                var mesh = new RoseEngine.Mesh
                {
                    name = meshName,
                    vertices = vertices.ToArray(),
                    indices = indices.ToArray()
                };

                namedMeshes.Add(new NamedMesh
                {
                    Name = meshName,
                    Mesh = mesh,
                    MaterialIndex = assimpMesh.MaterialIndex
                });

                totalVerts += vertices.Count;
                totalTris += indices.Count / 3;
            }

            // Material 추출
            var materials = ExtractMaterials(scene, meshPath);

            Debug.Log($"Imported mesh: {meshPath} ({namedMeshes.Count} meshes, {totalVerts} vertices, {totalTris} triangles, {materials.Length} materials)");

            return new MeshImportResult
            {
                Meshes = namedMeshes.ToArray(),
                Materials = materials,
                MipMeshes = new RoseEngine.MipMesh?[namedMeshes.Count]
            };
        }

        private RoseEngine.Material[] ExtractMaterials(Scene scene, string meshPath)
        {
            if (!scene.HasMaterials)
                return [];

            var meshDir = Path.GetDirectoryName(meshPath) ?? "";
            var materials = new List<RoseEngine.Material>();

            // Assimp이 GLB 임베디드 텍스처를 추출하지 못하는 경우를 위한 폴백
            GlbTextureExtractor.GlbTextures? glbTextures = null;
            if (!scene.HasTextures)
                glbTextures = GlbTextureExtractor.Extract(meshPath);

            int matIndex = 0;
            foreach (var assimpMat in scene.Materials)
            {
                var mat = new RoseEngine.Material();

                // Diffuse color
                if (assimpMat.HasColorDiffuse)
                {
                    var c = assimpMat.ColorDiffuse;
                    mat.color = new Color(c.R, c.G, c.B, c.A);
                }

                // Emissive color
                if (assimpMat.HasColorEmissive)
                {
                    var c = assimpMat.ColorEmissive;
                    mat.emission = new Color(c.R, c.G, c.B, c.A);
                }

                // PBR: metallic & roughness
                if (assimpMat.HasReflectivity)
                    mat.metallic = assimpMat.Reflectivity;
                if (assimpMat.HasShininess)
                    mat.roughness = 1.0f - MathF.Min(assimpMat.Shininess / 1000f, 1.0f);

                // Diffuse texture → mainTexture
                if (assimpMat.HasTextureDiffuse)
                    mat.mainTexture = LoadTexture(scene, assimpMat.TextureDiffuse, meshDir);

                // Assimp 실패 시 GLB 직접 파싱 폴백
                if (mat.mainTexture == null && glbTextures != null)
                    mat.mainTexture = LoadGlbBaseColorTexture(glbTextures, matIndex);

                // Normal map
                if (assimpMat.HasTextureNormal)
                    mat.normalMap = LoadTexture(scene, assimpMat.TextureNormal, meshDir);

                // Assign shared default textures when maps are missing
                if (mat.normalMap == null)
                    mat.normalMap = Texture2D.DefaultNormal;

                if (mat.MROMap == null)
                {
                    mat.MROMap = Texture2D.DefaultMRO;
                    // Assimp's Reflectivity/Shininess don't map to PBR metallic/roughness;
                    // use known-good defaults when no MRO texture is provided
                    mat.metallic = 0f;
                    mat.roughness = 0.5f;
                    mat.occlusion = 1f;
                }

                mat.name = !string.IsNullOrEmpty(assimpMat.Name)
                    ? assimpMat.Name
                    : $"Material_{matIndex}";
                materials.Add(mat);
                matIndex++;
            }

            return materials.ToArray();
        }

        private static Texture2D? LoadGlbBaseColorTexture(GlbTextureExtractor.GlbTextures glbTextures, int materialIndex)
        {
            if (materialIndex >= glbTextures.MaterialBaseColorImageIndex.Count)
                return null;

            int imageIndex = glbTextures.MaterialBaseColorImageIndex[materialIndex];
            if (imageIndex < 0 || imageIndex >= glbTextures.Images.Count)
                return null;

            var imageData = glbTextures.Images[imageIndex];
            if (imageData.Length == 0)
                return null;

            Debug.Log($"[MeshImporter] Loading GLB embedded texture (image[{imageIndex}], {imageData.Length} bytes) for material[{materialIndex}]");
            return Texture2D.LoadFromMemory(imageData);
        }

        private Texture2D? LoadTexture(Scene scene, TextureSlot slot, string meshDir)
        {
            var filePath = slot.FilePath;
            if (string.IsNullOrEmpty(filePath))
                return null;

            try
            {
                // 임베디드 텍스처: "*0", "*1" 형태의 참조
                if (filePath.StartsWith("*") && int.TryParse(filePath.AsSpan(1), out int texIndex))
                {
                    if (scene.HasTextures && texIndex < scene.TextureCount)
                        return LoadEmbeddedTexture(scene.Textures[texIndex]);
                }

                // 외부 파일 텍스처: 메시 파일 기준 상대 경로
                var fullPath = Path.GetFullPath(Path.Combine(meshDir, filePath));
                if (File.Exists(fullPath))
                    return Texture2D.LoadFromFile(fullPath);

                // GLB 등 임베디드 텍스처: filePath가 파일명이지만 실제 파일이 없는 경우
                // 경로에서 숫자 인덱스를 추출하거나 순차 탐색으로 임베디드 텍스처 로드
                if (scene.HasTextures)
                {
                    var result = TryLoadEmbeddedByIndex(scene, filePath);
                    if (result != null)
                        return result;
                }

                Debug.LogWarning($"Texture not found: {fullPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to load texture '{filePath}': {ex.Message}");
            }

            return null;
        }

        private Texture2D? TryLoadEmbeddedByIndex(Scene scene, string filePath)
        {
            // filePath에서 숫자 추출 시도 (예: "texture_0.png" → 0, "2" → 2)
            int index = -1;
            var nameOnly = Path.GetFileNameWithoutExtension(filePath);

            // 끝에서부터 연속 숫자 추출
            int numStart = nameOnly.Length;
            while (numStart > 0 && char.IsDigit(nameOnly[numStart - 1]))
                numStart--;

            if (numStart < nameOnly.Length
                && int.TryParse(nameOnly.AsSpan(numStart), out int parsed)
                && parsed < scene.TextureCount)
            {
                index = parsed;
            }

            if (index >= 0)
            {
                Debug.Log($"Loading embedded texture [{index}] for '{filePath}'");
                return LoadEmbeddedTexture(scene.Textures[index]);
            }

            // 인덱스 추출 실패 시: 첫 번째 임베디드 텍스처를 폴백으로 사용
            if (scene.TextureCount > 0)
            {
                Debug.LogWarning($"Cannot resolve embedded index for '{filePath}', falling back to texture[0]");
                return LoadEmbeddedTexture(scene.Textures[0]);
            }

            return null;
        }

        private static Texture2D LoadEmbeddedTexture(EmbeddedTexture embedded)
        {
            if (embedded.IsCompressed)
                return Texture2D.LoadFromMemory(embedded.CompressedData);

            int w = embedded.Width;
            int h = embedded.Height;
            var data = new byte[w * h * 4];
            var texels = embedded.NonCompressedData;
            for (int i = 0; i < texels.Length; i++)
            {
                data[i * 4 + 0] = texels[i].R;
                data[i * 4 + 1] = texels[i].G;
                data[i * 4 + 2] = texels[i].B;
                data[i * 4 + 3] = texels[i].A;
            }
            return new Texture2D(w, h) { _pixelData = data };
        }

        private static bool _formatsLogged;
        private static void LogSupportedFormats(AssimpContext context)
        {
            if (_formatsLogged) return;
            _formatsLogged = true;
            try
            {
                var formats = context.GetSupportedImportFormats();
                Debug.Log($"[Assimp] Supported import formats ({formats.Length}): {string.Join(", ", formats)}");
            }
            catch { /* ignore */ }
        }
    }
}
