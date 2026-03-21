// ------------------------------------------------------------
// @file    PrefabUtility.cs
// @brief   프리팹 에셋 생성, 인스턴스화, Variant 생성, 관계 조회, Unpack 등 유틸리티.
//          에디터 전용 프리팹 워크플로우를 제공한다.
// @deps    RoseEngine/EditorDebug, RoseEngine/SceneSerializer, RoseEngine/PrefabInstance,
//          RoseEngine/PrefabImporter, RoseEngine/Resources, RoseEngine/Object,
//          IronRose.AssetPipeline/RoseMetadata, IronRose.Engine.Editor
// @exports
//   static class PrefabUtility
//     SaveAsPrefab(GameObject, string): string                         — GO를 .prefab로 저장, GUID 반환
//     InstantiatePrefab(string): GameObject?                           — GUID로 인스턴스화
//     InstantiatePrefab(string, Vector3, Quaternion): GameObject?      — 위치/회전 지정 인스턴스화
//     InstantiatePrefab(string, Transform): GameObject?                — 부모 지정 인스턴스화
//     InstantiatePrefabByPath(string, Vector3, Quaternion): GameObject? — 경로로 인스턴스화
//     CreateVariant(string, string): string?                           — Variant 프리팹 생성
//     IsVariant(string): bool                                          — Variant 여부 확인
//     GetBasePrefabGuid(string): string?                               — Base 프리팹 GUID 반환
//     IsPrefabInstance(GameObject): bool                                — 프리팹 인스턴스 여부
//     IsInPrefabHierarchy(GameObject): bool                             — 프리팹 계층 포함 여부
//     HasPrefabInstanceAncestor(GameObject): bool                       — 조상에 PrefabInstance 존재 여부
//     IsChildOfPrefabInstance(GameObject): bool                         — 프리팹 인스턴스 자식 여부
//     CollectHierarchy(GameObject, List<GameObject>): void              — 전체 자식 수집
//     GetPrefabGuid(GameObject): string?                                — 원본 프리팹 GUID
//     GetPrefabAssetPath(GameObject): string?                           — 원본 프리팹 경로
//     RefreshPrefabInstances(string): void                              — 씬 내 인스턴스 갱신
//     UnpackPrefabInstance(GameObject): void                            — 프리팹 연결 해제
// @note    Variant는 basePrefabGuid만 있는 빈 TOML 파일로 저장.
//          프리팹 인스턴스의 값 override는 미지원 — Variant로 대체.
// ------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using IronRose.AssetPipeline;
using IronRose.Engine.Editor;

namespace RoseEngine
{
    /// <summary>
    /// 프리팹 에셋 생성/인스턴스화/관계 조회 유틸리티.
    /// </summary>
    public static class PrefabUtility
    {
        // ── 프리팹 에셋 생성 ──

        /// <summary>씬의 GameObject 계층을 .prefab 파일로 저장 (Base 프리팹).</summary>
        public static string SaveAsPrefab(GameObject go, string path)
        {
            // 기존 PrefabInstance가 있으면 제거 (순수 데이터만 저장)
            var existing = go.GetComponent<PrefabInstance>();
            if (existing != null)
                go.RemoveComponent(existing);

            SceneSerializer.SavePrefab(go, path);

            // .rose 메타 자동 생성
            var meta = RoseMetadata.LoadOrCreate(path);
            if (!meta.importer.ContainsKey("type"))
            {
                meta.importer["type"] = "PrefabImporter";
                meta.Save(path + ".rose");
            }

            // AssetDatabase에 등록
            var db = Resources.GetAssetDatabase();
            db?.RegisterPrefabAsset(path);

            return meta.guid;
        }

        // ── 프리팹 인스턴스 생성 ──

        /// <summary>
        /// GUID로 프리팹을 씬에 인스턴스화. PrefabInstance 컴포넌트 자동 부착.
        /// </summary>
        public static GameObject? InstantiatePrefab(string prefabGuid)
        {
            return InstantiatePrefab(prefabGuid, Vector3.zero, Quaternion.identity);
        }

        /// <summary>
        /// GUID로 프리팹을 씬에 인스턴스화 (위치/회전 지정).
        /// </summary>
        public static GameObject? InstantiatePrefab(string prefabGuid, Vector3 position, Quaternion rotation)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            var template = db.LoadByGuid<GameObject>(prefabGuid);
            if (template == null)
            {
                EditorDebug.LogWarning($"[PrefabUtility] Failed to load prefab: guid={prefabGuid}");
                return null;
            }

            var go = Object.Instantiate(template, position, rotation);

            // PrefabInstance 부착 (이미 있으면 건너뜀)
            var inst = go.GetComponent<PrefabInstance>();
            if (inst == null)
            {
                inst = go.AddComponent<PrefabInstance>();
                inst.prefabGuid = prefabGuid;
            }

            return go;
        }

        /// <summary>
        /// GUID로 프리팹을 씬에 인스턴스화 (부모 Transform 지정).
        /// </summary>
        public static GameObject? InstantiatePrefab(string prefabGuid, Transform parent)
        {
            var go = InstantiatePrefab(prefabGuid);
            if (go != null)
                go.transform.SetParent(parent);
            return go;
        }

        /// <summary>
        /// 에셋 경로로 프리팹을 씬에 인스턴스화.
        /// </summary>
        public static GameObject? InstantiatePrefabByPath(string prefabPath, Vector3 position, Quaternion rotation)
        {
            var db = Resources.GetAssetDatabase();
            var guid = db?.GetGuidFromPath(prefabPath);
            if (string.IsNullOrEmpty(guid))
            {
                EditorDebug.LogWarning($"[PrefabUtility] No GUID found for path: {prefabPath}");
                return null;
            }
            return InstantiatePrefab(guid!, position, rotation);
        }

        // ── Variant 생성 ──

        /// <summary>
        /// 기존 프리팹(Base)을 기반으로 빈 Variant 프리팹 파일을 생성.
        /// basePrefabGuid만 있는 빈 Variant .prefab 파일을 생성하고 등록.
        /// </summary>
        public static string? CreateVariant(string basePrefabGuid, string variantPath)
        {
            var db = Resources.GetAssetDatabase();
            if (db == null) return null;

            // Base 프리팹 경로 확인
            var basePath = db.GetPathFromGuid(basePrefabGuid);
            if (string.IsNullOrEmpty(basePath))
            {
                EditorDebug.LogWarning($"[PrefabUtility] Base prefab not found: guid={basePrefabGuid}");
                return null;
            }

            // base를 로드하여 루트 이름 가져오기
            var baseTemplate = db.LoadByGuid<GameObject>(basePrefabGuid);
            var rootName = baseTemplate != null
                ? Path.GetFileNameWithoutExtension(variantPath)
                : "Variant";

            // 빈 Variant TOML 생성 (overrides 없음)
            var toml = new Tomlyn.Model.TomlTable();
            toml["prefab"] = new Tomlyn.Model.TomlTable
            {
                ["version"] = (long)1,
                ["rootName"] = rootName,
                ["basePrefabGuid"] = basePrefabGuid,
            };

            var dir = Path.GetDirectoryName(variantPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(variantPath, Tomlyn.Toml.FromModel(toml));

            // .rose 메타 생성
            var meta = RoseMetadata.LoadOrCreate(variantPath);
            if (!meta.importer.ContainsKey("type"))
            {
                meta.importer["type"] = "PrefabImporter";
                meta.Save(variantPath + ".rose");
            }

            // AssetDatabase에 등록
            db.RegisterPrefabAsset(variantPath);

            EditorDebug.Log($"[PrefabUtility] Created variant: {variantPath} (base: {basePath})");
            return meta.guid;
        }

        /// <summary>해당 프리팹이 Variant인지 확인.</summary>
        public static bool IsVariant(string prefabGuid)
        {
            var db = Resources.GetAssetDatabase();
            var path = db?.GetPathFromGuid(prefabGuid);
            if (string.IsNullOrEmpty(path)) return false;
            return PrefabImporter.IsVariantPrefab(path!);
        }

        /// <summary>Variant의 Base 프리팹 GUID 반환. Variant가 아니면 null.</summary>
        public static string? GetBasePrefabGuid(string prefabGuid)
        {
            var db = Resources.GetAssetDatabase();
            var path = db?.GetPathFromGuid(prefabGuid);
            if (string.IsNullOrEmpty(path)) return null;
            return PrefabImporter.GetBasePrefabGuidFromFile(path!);
        }

        // ── 프리팹 관계 조회 ──

        /// <summary>GameObject가 프리팹 인스턴스인지 확인.</summary>
        public static bool IsPrefabInstance(GameObject go)
        {
            return go.GetComponent<PrefabInstance>() != null;
        }

        // ── 프리팹 계층 검사 ──

        /// <summary>
        /// GO 자체에 PrefabInstance가 있거나, 조상 중 하나가 PrefabInstance를 보유하면 true.
        /// 에디터 상태(IsEditingPrefab 등)와 무관한 순수 계층 검사.
        /// </summary>
        public static bool IsInPrefabHierarchy(GameObject go)
        {
            if (go.GetComponent<PrefabInstance>() != null)
                return true;
            var p = go.transform.parent;
            while (p != null)
            {
                if (p.gameObject.GetComponent<PrefabInstance>() != null)
                    return true;
                p = p.parent;
            }
            return false;
        }

        /// <summary>
        /// 조상 중 하나가 PrefabInstance를 보유하면 true (자기 자신은 검사하지 않음).
        /// 프리팹 루트 아래의 모든 GO를 잠글 때 사용.
        /// </summary>
        public static bool HasPrefabInstanceAncestor(GameObject go)
        {
            var p = go.transform.parent;
            while (p != null)
            {
                if (p.gameObject.GetComponent<PrefabInstance>() != null)
                    return true;
                p = p.parent;
            }
            return false;
        }

        /// <summary>
        /// GO가 PrefabInstance를 직접 보유하지 않으면서,
        /// 조상 중 하나가 PrefabInstance를 보유하면 true.
        /// 즉, 프리팹 인스턴스의 '자식' 노드인 경우.
        /// </summary>
        public static bool IsChildOfPrefabInstance(GameObject go)
        {
            if (go.GetComponent<PrefabInstance>() != null)
                return false;
            var p = go.transform.parent;
            while (p != null)
            {
                if (p.gameObject.GetComponent<PrefabInstance>() != null)
                    return true;
                p = p.parent;
            }
            return false;
        }

        /// <summary>
        /// root부터 시작하여 전체 자식 계층을 깊이 우선으로 수집.
        /// 파괴된(_isDestroyed) 오브젝트는 건너뛴다.
        /// </summary>
        public static void CollectHierarchy(GameObject root, List<GameObject> result)
        {
            if (root._isDestroyed) return;
            result.Add(root);
            for (int i = 0; i < root.transform.childCount; i++)
                CollectHierarchy(root.transform.GetChild(i).gameObject, result);
        }

        /// <summary>프리팹 인스턴스의 원본 프리팹 GUID 반환.</summary>
        public static string? GetPrefabGuid(GameObject instanceRoot)
        {
            var inst = instanceRoot.GetComponent<PrefabInstance>();
            return inst?.prefabGuid;
        }

        /// <summary>프리팹 인스턴스의 원본 프리팹 에셋 경로 반환.</summary>
        public static string? GetPrefabAssetPath(GameObject instanceRoot)
        {
            var guid = GetPrefabGuid(instanceRoot);
            if (string.IsNullOrEmpty(guid)) return null;
            var db = Resources.GetAssetDatabase();
            return db?.GetPathFromGuid(guid!);
        }

        // ── 프리팹 에셋 갱신 ──

        /// <summary>프리팹 에셋 변경 후 씬 내 모든 인스턴스를 갱신.</summary>
        public static void RefreshPrefabInstances(string prefabGuid)
        {
            var allGOs = SceneManager.AllGameObjects;
            var toRefresh = new List<(GameObject go, Vector3 pos, Quaternion rot, Vector3 scale, Transform? parent)>();

            for (int i = allGOs.Count - 1; i >= 0; i--)
            {
                var go = allGOs[i];
                var inst = go.GetComponent<PrefabInstance>();
                if (inst == null || inst.prefabGuid != prefabGuid) continue;

                toRefresh.Add((go, go.transform.localPosition, go.transform.localRotation,
                    go.transform.localScale, go.transform.parent));
            }

            foreach (var (go, pos, rot, scale, parent) in toRefresh)
            {
                Object.DestroyImmediate(go);

                var newGo = InstantiatePrefab(prefabGuid, pos, rot);
                if (newGo != null)
                {
                    newGo.transform.localScale = scale;
                    if (parent != null)
                        newGo.transform.SetParent(parent, false);
                }
            }

            if (toRefresh.Count > 0)
                EditorDebug.Log($"[PrefabUtility] Refreshed {toRefresh.Count} instances of prefab {prefabGuid}");
        }

        // ── Unpack ──

        /// <summary>프리팹 연결 해제 (일반 GameObject로 변환).</summary>
        public static void UnpackPrefabInstance(GameObject instanceRoot)
        {
            var inst = instanceRoot.GetComponent<PrefabInstance>();
            if (inst == null) return;

            instanceRoot.RemoveComponent(inst);
            EditorDebug.Log($"[PrefabUtility] Unpacked prefab instance: {instanceRoot.name}");
        }
    }
}
