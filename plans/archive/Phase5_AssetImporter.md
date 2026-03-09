# Phase 5: Unity 에셋 임포터 ✅ (2026-02-14 완료)

> **커밋**: `d43a185` (에셋 파이프라인), `ba8c715` (SpriteRenderer + TextRenderer), `0fa6f56` (머티리얼 추출)

## 목표
Unity의 .unity (Scene), .prefab, .fbx, .glb, .png 파일을 로드할 수 있게 만듭니다.

## 기본 에셋 폴더

프로젝트 루트의 `./Assets/` 디렉토리를 기본 에셋 폴더로 사용합니다.
- `AssetDatabase.ScanAssets()`의 기본 탐색 경로: `./Assets/`
- 모든 에셋 경로는 `Assets/` 상대 경로로 참조 (예: `Assets/houseInTheForest/AntiqueBook/model.glb`)
- Unity와 동일한 컨벤션 (`Assets/` 루트 기준)

---

## 작업 항목

### 5.1 YAML 파서 통합 (IronRose.AssetPipeline)

**YamlParser.cs:**
```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.IO;

namespace IronRose.AssetPipeline
{
    public class UnityYamlParser
    {
        private readonly IDeserializer _deserializer;

        public UnityYamlParser()
        {
            _deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        public T Parse<T>(string yamlContent)
        {
            return _deserializer.Deserialize<T>(yamlContent);
        }

        public object Parse(string yamlContent)
        {
            return _deserializer.Deserialize(yamlContent);
        }
    }
}
```

**Unity Asset 구조:**
```csharp
public class UnityAsset
{
    public string guid { get; set; } = string.Empty;
    public long fileID { get; set; }
    public int type { get; set; }
}

public class UnityPrefab
{
    public Dictionary<string, object> GameObject { get; set; } = new();
    public Dictionary<string, object> Transform { get; set; } = new();
    public Dictionary<string, object>[] MonoBehaviour { get; set; } = Array.Empty<Dictionary<string, object>>();
}
```

---

### 5.2 .prefab 로더

**PrefabImporter.cs:**
```csharp
using System;
using System.IO;
using System.Collections.Generic;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    public class PrefabImporter
    {
        private readonly UnityYamlParser _yamlParser;
        private readonly AssetDatabase _assetDatabase;

        public PrefabImporter(AssetDatabase assetDatabase)
        {
            _yamlParser = new UnityYamlParser();
            _assetDatabase = assetDatabase;
        }

        public GameObject LoadPrefab(string prefabPath)
        {
            if (!File.Exists(prefabPath))
            {
                Debug.LogError($"Prefab not found: {prefabPath}");
                return null!;
            }

            string yamlContent = File.ReadAllText(prefabPath);

            // Unity YAML은 여러 문서로 구성됨 (--- 구분자)
            var documents = yamlContent.Split(new[] { "---" }, StringSplitOptions.RemoveEmptyEntries);

            GameObject rootObject = null!;
            Dictionary<long, Component> componentCache = new();

            foreach (var doc in documents)
            {
                if (doc.Contains("GameObject:"))
                {
                    rootObject = ParseGameObject(doc);
                }
                else if (doc.Contains("Transform:"))
                {
                    var transform = ParseTransform(doc);
                    if (rootObject != null && transform != null)
                    {
                        rootObject.transform.position = transform.position;
                        rootObject.transform.rotation = transform.rotation;
                        rootObject.transform.localScale = transform.localScale;
                    }
                }
                // MeshRenderer, MeshFilter 등 추가 파싱...
            }

            return rootObject;
        }

        private GameObject ParseGameObject(string yaml)
        {
            // 간단한 파싱 (실제로는 더 복잡)
            var go = new GameObject("Prefab");
            // YAML에서 이름 추출
            return go;
        }

        private TransformData? ParseTransform(string yaml)
        {
            // Transform 데이터 파싱
            return new TransformData
            {
                position = Vector3.zero,
                rotation = Quaternion.identity,
                localScale = Vector3.one
            };
        }
    }

    internal class TransformData
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localScale;
    }
}
```

---

### 5.3 .fbx 메시 로더 (AssimpNet)

**MeshImporter.cs:**
```csharp
using Assimp;
using System.IO;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    public class MeshImporter
    {
        public Mesh Import(string fbxPath)
        {
            if (!File.Exists(fbxPath))
            {
                Debug.LogError($"FBX file not found: {fbxPath}");
                return null!;
            }

            var context = new AssimpContext();
            var scene = context.ImportFile(fbxPath,
                PostProcessSteps.Triangulate |
                PostProcessSteps.GenerateNormals |
                PostProcessSteps.FlipUVs
            );

            if (scene == null || scene.MeshCount == 0)
            {
                Debug.LogError($"Failed to load FBX: {fbxPath}");
                return null!;
            }

            // 첫 번째 메시만 로드 (간단한 구현)
            var assimpMesh = scene.Meshes[0];
            var mesh = new Mesh();

            // Vertex 데이터 변환
            var vertices = new Vertex[assimpMesh.VertexCount];
            for (int i = 0; i < assimpMesh.VertexCount; i++)
            {
                var pos = assimpMesh.Vertices[i];
                var normal = assimpMesh.Normals[i];
                var uv = assimpMesh.TextureCoordinateChannels[0].Count > 0
                    ? assimpMesh.TextureCoordinateChannels[0][i]
                    : new Assimp.Vector3D(0, 0, 0);

                vertices[i] = new Vertex
                {
                    Position = new Vector3(pos.X, pos.Y, pos.Z),
                    Normal = new Vector3(normal.X, normal.Y, normal.Z),
                    UV = new Vector2(uv.X, uv.Y)
                };
            }

            // Index 데이터 변환
            var indices = new List<uint>();
            foreach (var face in assimpMesh.Faces)
            {
                if (face.IndexCount == 3)
                {
                    indices.Add((uint)face.Indices[0]);
                    indices.Add((uint)face.Indices[1]);
                    indices.Add((uint)face.Indices[2]);
                }
            }

            mesh.vertices = vertices;
            mesh.indices = indices.ToArray();

            Debug.Log($"Imported mesh: {assimpMesh.VertexCount} vertices, {indices.Count / 3} triangles");

            return mesh;
        }
    }
}
```

---

### 5.4 .png 텍스처 로더 (StbImageSharp)

**TextureImporter.cs:**
```csharp
using StbImageSharp;
using System.IO;
using Veldrid;

namespace IronRose.AssetPipeline
{
    public class Texture2D
    {
        public int width { get; internal set; }
        public int height { get; internal set; }
        public byte[] data { get; internal set; } = Array.Empty<byte>();

        internal Veldrid.Texture? veldridTexture { get; set; }
    }

    public class TextureImporter
    {
        public Texture2D Import(string pngPath)
        {
            if (!File.Exists(pngPath))
            {
                RoseEngine.Debug.LogError($"Texture not found: {pngPath}");
                return null!;
            }

            // StbImage로 이미지 로드
            using var stream = File.OpenRead(pngPath);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            var texture = new Texture2D
            {
                width = image.Width,
                height = image.Height,
                data = image.Data
            };

            RoseEngine.Debug.Log($"Imported texture: {image.Width}x{image.Height}");

            return texture;
        }

        public void UploadToGPU(Texture2D texture, GraphicsDevice device)
        {
            var textureDesc = TextureDescription.Texture2D(
                (uint)texture.width,
                (uint)texture.height,
                1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled
            );

            texture.veldridTexture = device.ResourceFactory.CreateTexture(textureDesc);
            device.UpdateTexture(
                texture.veldridTexture,
                texture.data,
                0, 0, 0,
                (uint)texture.width,
                (uint)texture.height,
                1,
                0, 0
            );
        }
    }
}
```

---

### 5.5 .rose 메타데이터 파일 (Unity .meta 대응)

Unity가 모든 에셋 옆에 `.meta` (YAML) 파일을 생성하듯, IronRose는 `.rose` (TOML) 파일을 사용합니다.

**규칙:**
- 모든 에셋 파일(`*.png`, `*.glb`, `*.obj`, `*.fbx` 등) 옆에 **같은 이름 + `.rose`** 확장자 파일 생성
  - 예: `model.glb` → `model.glb.rose`, `preview.png` → `preview.png.rose`
- 디렉토리에도 `.rose` 파일 생성 가능 (폴더 GUID 용도)
- `.rose` 파일이 없으면 에셋 최초 임포트 시 자동 생성
- `.rose` 파일은 **반드시 버전 관리(Git)에 포함**

**model.glb.rose 예시:**
```toml
guid = "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
version = 1

[importer]
type = "MeshImporter"
scale = 1.0
generate_normals = true
flip_uvs = true
triangulate = true

[importer.materials]
extract = true
remap = {}
```

**preview.png.rose 예시:**
```toml
guid = "f9e8d7c6-b5a4-3210-fedc-ba9876543210"
version = 1

[importer]
type = "TextureImporter"
max_size = 2048
compression = "none"
srgb = true
filter_mode = "Bilinear"
wrap_mode = "Repeat"
generate_mipmaps = true
```

**디렉토리 .rose 예시 (`houseInTheForest.rose`):**
```toml
guid = "00112233-4455-6677-8899-aabbccddeeff"
version = 1
labels = ["environment", "interior"]
```

**RoseMetadata.cs:**
```csharp
using Tomlyn;
using Tomlyn.Model;
using System;
using System.IO;

namespace IronRose.AssetPipeline
{
    public class RoseMetadata
    {
        public string guid { get; set; } = Guid.NewGuid().ToString();
        public int version { get; set; } = 1;
        public TomlTable importer { get; set; } = new();

        /// <summary>에셋 경로에서 .rose 메타데이터를 로드. 없으면 자동 생성.</summary>
        public static RoseMetadata LoadOrCreate(string assetPath)
        {
            var rosePath = assetPath + ".rose";

            if (File.Exists(rosePath))
            {
                var toml = Toml.ToModel(File.ReadAllText(rosePath));
                return FromToml(toml);
            }

            // 자동 생성
            var meta = new RoseMetadata();
            meta.importer = InferImporter(assetPath);
            meta.Save(rosePath);
            return meta;
        }

        public void Save(string rosePath)
        {
            var toml = ToToml();
            File.WriteAllText(rosePath, Toml.FromModel(toml));
        }

        private static TomlTable InferImporter(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return ext switch
            {
                ".glb" or ".fbx" or ".obj" => new TomlTable
                {
                    ["type"] = "MeshImporter",
                    ["scale"] = 1.0,
                    ["generate_normals"] = true,
                    ["flip_uvs"] = true,
                    ["triangulate"] = true,
                },
                ".png" or ".jpg" or ".tga" => new TomlTable
                {
                    ["type"] = "TextureImporter",
                    ["max_size"] = 2048,
                    ["compression"] = "none",
                    ["srgb"] = true,
                    ["filter_mode"] = "Bilinear",
                    ["wrap_mode"] = "Repeat",
                    ["generate_mipmaps"] = true,
                },
                _ => new TomlTable { ["type"] = "DefaultImporter" },
            };
        }

        private TomlTable ToToml() { /* serialize */ return new(); }
        private static RoseMetadata FromToml(TomlTable table) { /* deserialize */ return new(); }
    }
}
```

---

### 5.6 AssetDatabase (GUID 매핑)

**AssetDatabase.cs:**
```csharp
using System.Collections.Generic;
using System.IO;
using RoseEngine;

namespace IronRose.AssetPipeline
{
    public class AssetDatabase
    {
        private Dictionary<string, string> _guidToPath = new();
        private Dictionary<string, object> _loadedAssets = new();

        /// <summary>프로젝트 내 모든 .rose 파일을 스캔하여 GUID→경로 매핑 구축</summary>
        public void ScanAssets(string projectPath)
        {
            var roseFiles = Directory.GetFiles(projectPath, "*.rose", SearchOption.AllDirectories);

            foreach (var roseFile in roseFiles)
            {
                var meta = RoseMetadata.LoadOrCreate(roseFile.Replace(".rose", ""));
                var assetPath = roseFile.Replace(".rose", "");

                if (!string.IsNullOrEmpty(meta.guid))
                {
                    _guidToPath[meta.guid] = assetPath;
                }
            }

            Debug.Log($"Scanned {_guidToPath.Count} assets");
        }

        public string GetPathFromGuid(string guid)
        {
            return _guidToPath.TryGetValue(guid, out var path) ? path : string.Empty;
        }

        public T Load<T>(string path) where T : class
        {
            if (_loadedAssets.TryGetValue(path, out var cached))
            {
                return (T)cached;
            }

            // .rose 메타데이터에서 임포터 타입 결정
            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = meta.importer["type"]?.ToString() ?? "";

            object asset = importerType switch
            {
                "MeshImporter" => new MeshImporter().Import(path),
                "TextureImporter" => new TextureImporter().Import(path),
                _ => null!,
            };

            if (asset != null)
            {
                _loadedAssets[path] = asset;
            }

            return (T)asset;
        }
    }
}
```

---

## 검증 기준

✅ GLB/FBX 모델 로드 + 텍스처 적용 가능
✅ `.rose` 파일 없는 에셋 최초 임포트 시 자동 생성
✅ `.rose` 파일에서 GUID 추출 및 AssetDatabase 매핑 정상 작동
✅ `.rose` 임포터 옵션 변경 시 재임포트 반영
✅ Unity .prefab 파일 로드하여 렌더링

---

## 테스트 시나리오

1. `Assets/houseInTheForest/AntiqueBook/model.glb` 로드
2. `.rose` 파일 자동 생성 확인 (`model.glb.rose`)
3. `AssetDatabase.Load<Mesh>("Assets/houseInTheForest/AntiqueBook/model.glb")` 호출
4. 로드된 Mesh가 화면에 렌더링됨
5. `.rose` 파일의 `scale` 값 변경 후 재임포트 → 크기 반영 확인

---

## 예상 소요 시간
**4-5일**

---

## 다음 단계
→ [Phase 6: 물리 엔진 통합](Phase6_PhysicsEngine.md)
