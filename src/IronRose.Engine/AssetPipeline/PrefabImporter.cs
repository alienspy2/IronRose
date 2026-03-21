// ------------------------------------------------------------
// @file    PrefabImporter.cs
// @brief   .prefab 파일을 IronRose TOML 포맷으로 임포트. Base/Variant 프리팹 모두 지원하며
//          Variant는 재귀적으로 base를 로드 후 오버라이드를 적용한다.
// @deps    AssetDatabase, SceneSerializer, PrefabUtility, RoseEngine.Debug
// @exports
//   class PrefabImporter
//     LoadPrefab(string prefabPath): GameObject?                           -- .prefab 파일을 로드하여 루트 GO 반환
//     static IsVariantPrefab(string prefabPath): bool                     -- Variant 프리팹 여부 확인
//     static GetBasePrefabGuidFromFile(string prefabPath): string?        -- basePrefabGuid 반환 (Variant가 아니면 null)
// @note    MaxVariantDepth=16으로 무한 재귀 방지.
//          IsVariantPrefab/GetBasePrefabGuidFromFile은 IOException 발생 시 안전하게 false/null 반환.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using IronRose.Engine.Editor;
using RoseEngine;
using Tomlyn;
using Tomlyn.Model;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// .prefab 파일을 IronRose TOML 포맷으로 임포트.
    /// SceneSerializer의 직렬화/역직렬화 로직을 재사용.
    /// Base 프리팹 및 Variant 프리팹 모두 지원.
    /// </summary>
    public class PrefabImporter
    {
        private readonly AssetDatabase _assetDatabase;
        private const int MaxVariantDepth = 16; // 무한 재귀 방지

        public PrefabImporter(AssetDatabase assetDatabase)
        {
            _assetDatabase = assetDatabase;
        }

        /// <summary>
        /// .prefab 파일을 로드하여 루트 GameObject를 반환.
        /// Variant인 경우 재귀적으로 base를 로드 후 오버라이드를 적용.
        /// 반환된 GO는 _isEditorInternal = true (프리팹 템플릿).
        /// </summary>
        public GameObject? LoadPrefab(string prefabPath)
        {
            return LoadPrefabInternal(prefabPath, 0);
        }

        private GameObject? LoadPrefabInternal(string prefabPath, int depth)
        {
            if (depth > MaxVariantDepth)
            {
                EditorDebug.LogError($"[PrefabImporter] Variant depth exceeded max ({MaxVariantDepth}): {prefabPath}");
                return null;
            }

            if (!File.Exists(prefabPath))
            {
                EditorDebug.LogError($"[PrefabImporter] File not found: {prefabPath}");
                return null;
            }

            var tomlStr = File.ReadAllText(prefabPath);
            TomlTable root;
            try { root = Toml.ToModel(tomlStr); }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[PrefabImporter] TOML parse error in {prefabPath}: {ex.Message}");
                return null;
            }

            // Variant 감지: basePrefabGuid가 있으면 Variant
            string? basePrefabGuid = null;
            if (root.TryGetValue("prefab", out var pVal) && pVal is TomlTable prefabTable)
            {
                if (prefabTable.TryGetValue("basePrefabGuid", out var bgVal) && bgVal is string bg && !string.IsNullOrEmpty(bg))
                    basePrefabGuid = bg;
            }

            if (basePrefabGuid != null)
                return LoadVariant(root, basePrefabGuid, prefabPath, depth);

            return LoadBase(root, prefabPath);
        }

        private GameObject? LoadBase(TomlTable root, string prefabPath)
        {
            var gameObjects = SceneSerializer.LoadPrefabGameObjectsFromString(Toml.FromModel(root));
            if (gameObjects.Count == 0)
            {
                EditorDebug.LogWarning($"[PrefabImporter] No GameObjects in prefab: {prefabPath}");
                return null;
            }

            var rootGo = gameObjects[0];
            EditorDebug.Log($"[PrefabImporter] Loaded prefab: {prefabPath} → '{rootGo.name}' ({gameObjects.Count} GOs)");
            return rootGo;
        }

        private GameObject? LoadVariant(TomlTable root, string basePrefabGuid, string variantPath, int depth)
        {
            // 1. Base 프리팹 재귀 로드
            var basePath = _assetDatabase.GetPathFromGuid(basePrefabGuid);
            if (string.IsNullOrEmpty(basePath))
            {
                EditorDebug.LogWarning($"[PrefabImporter] Base prefab not found for guid: {basePrefabGuid}");
                // 폴백: gameObjects가 있으면 직접 로드
                return LoadBase(root, variantPath);
            }

            var baseRoot = LoadPrefabInternal(basePath!, depth + 1);
            if (baseRoot == null)
            {
                EditorDebug.LogWarning($"[PrefabImporter] Failed to load base prefab: {basePath}");
                return LoadBase(root, variantPath);
            }

            // 2. Base의 전체 GO 목록 수집
            var allGOs = new List<GameObject>();
            CollectHierarchy(baseRoot, allGOs);

            // 3. 오버라이드 적용
            if (root.TryGetValue("overrides", out var ovVal) && ovVal is TomlTableArray overrides)
            {
                SceneSerializer.ApplyOverrides(allGOs, overrides);
            }

            // 4. Variant 이름 적용
            if (root.TryGetValue("prefab", out var pVal) && pVal is TomlTable prefabTable)
            {
                if (prefabTable.TryGetValue("rootName", out var rnVal) && rnVal is string rn)
                    baseRoot.name = rn;
            }

            EditorDebug.Log($"[PrefabImporter] Loaded variant: {variantPath} (base: {basePath})");
            return baseRoot;
        }

        private static void CollectHierarchy(GameObject root, List<GameObject> result)
            => PrefabUtility.CollectHierarchy(root, result);

        /// <summary>
        /// .prefab 파일이 Variant인지 확인 (basePrefabGuid 존재 여부).
        /// </summary>
        public static bool IsVariantPrefab(string prefabPath)
        {
            try
            {
                if (!File.Exists(prefabPath)) return false;
                var tomlStr = File.ReadAllText(prefabPath);
                return SceneSerializer.GetBasePrefabGuid(tomlStr) != null;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// .prefab 파일에서 basePrefabGuid를 읽어 반환. Variant가 아니면 null.
        /// </summary>
        public static string? GetBasePrefabGuidFromFile(string prefabPath)
        {
            try
            {
                if (!File.Exists(prefabPath)) return null;
                var tomlStr = File.ReadAllText(prefabPath);
                return SceneSerializer.GetBasePrefabGuid(tomlStr);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
