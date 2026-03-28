using System;
using System.IO;
using IronRose.AssetPipeline;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// Project 패널에서 Scene View로 에셋을 드래그-앤-드롭할 때 GameObject를 생성하는 유틸리티.
    /// 멀티 메시 GLB는 parent+children 계층 구조로 생성.
    /// Undo/Redo에서도 재사용.
    /// </summary>
    internal static class AssetSpawner
    {
        private static readonly string[] MeshExtensions = { ".glb", ".gltf", ".fbx", ".obj" };

        /// <summary>
        /// 에셋 경로로부터 적절한 GameObject를 생성하고 지정된 위치에 배치합니다.
        /// 지원하지 않는 에셋 타입이면 null을 반환합니다.
        /// </summary>
        public static GameObject? SpawnFromAsset(string assetPath, Vector3 position)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            var assetName = Path.GetFileNameWithoutExtension(assetPath);

            if (Array.IndexOf(MeshExtensions, ext) >= 0)
                return SpawnMesh(db, assetPath, assetName, position);

            if (ext == ".prefab")
                return SpawnPrefab(db, assetPath, assetName, position);

            return null;
        }

        /// <summary>지정된 에셋 확장자가 Scene View에 드롭 가능한 타입인지 확인합니다.</summary>
        public static bool IsSpawnableAsset(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return Array.IndexOf(MeshExtensions, ext) >= 0 || ext == ".prefab";
        }

        private static GameObject? SpawnMesh(AssetDatabase db, string path,
            string name, Vector3 position)
        {
            var result = db.GetMeshImportResult(path);
            if (result == null || result.Meshes.Length == 0)
            {
                EditorDebug.LogWarning($"[AssetSpawner] Failed to load mesh: {path}");
                return null;
            }

            // 단일 메시: 단순 GO 생성
            if (result.Meshes.Length == 1)
            {
                return CreateMeshGO(name, position, result.Meshes[0], result, 0);
            }

            // 멀티 메시: parent + children 계층 구조
            var parent = new GameObject(name);
            parent.transform.position = position;

            for (int i = 0; i < result.Meshes.Length; i++)
            {
                var child = CreateMeshGO(result.Meshes[i].Name, Vector3.zero, result.Meshes[i], result, i);
                child.transform.SetParent(parent.transform, false);
            }

            return parent;
        }

        private static GameObject CreateMeshGO(string name, Vector3 position,
            NamedMesh namedMesh, MeshImportResult result, int meshIndex)
        {
            var go = new GameObject(name);
            go.transform.position = position;

            var filter = go.AddComponent<MeshFilter>();
            filter.mesh = namedMesh.Mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            var matIdx = namedMesh.MaterialIndex;
            renderer.material = (matIdx >= 0 && matIdx < result.Materials.Length)
                ? result.Materials[matIdx]
                : new Material();

            if (meshIndex < result.MipMeshes.Length && result.MipMeshes[meshIndex] != null)
            {
                var mipFilter = go.AddComponent<MipMeshFilter>();
                mipFilter.mipMesh = result.MipMeshes[meshIndex];
            }

            return go;
        }

        private static GameObject? SpawnPrefab(AssetDatabase db, string path,
            string name, Vector3 position)
        {
            var go = PrefabUtility.InstantiatePrefabByPath(path, position, Quaternion.identity);
            if (go != null)
                go.name = name;
            return go;
        }
    }
}
