using System.Collections.Generic;
using System.IO;
using IronRose.AssetPipeline;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 프로젝트 내 모든 .prefab 파일의 Variant 관계 맵.
    /// AssetDatabase 초기화 시 1회 빌드 + .prefab 파일 변경 시 갱신.
    /// </summary>
    public class PrefabVariantTree
    {
        public class TreeNode
        {
            public string Guid;
            public string? Path;
            public string DisplayName;
            public bool IsVariant;
            public List<TreeNode> Children = new();

            public TreeNode(string guid, string? path, string displayName, bool isVariant)
            {
                Guid = guid;
                Path = path;
                DisplayName = displayName;
                IsVariant = isVariant;
            }
        }

        // basePrefabGuid → List<variantGuid>
        private readonly Dictionary<string, List<string>> _childVariants = new();

        // prefabGuid → basePrefabGuid
        private readonly Dictionary<string, string> _parentMap = new();

        // prefabGuid → path
        private readonly Dictionary<string, string> _guidToPath = new();

        private static PrefabVariantTree? _instance;
        public static PrefabVariantTree Instance => _instance ??= new PrefabVariantTree();

        /// <summary>모든 .prefab 파일을 스캔하여 Variant 관계 맵 구축.</summary>
        public void Rebuild()
        {
            _childVariants.Clear();
            _parentMap.Clear();
            _guidToPath.Clear();

            var db = Resources.GetAssetDatabase();
            if (db == null) return;

            // Assets 디렉토리에서 모든 .prefab 파일 찾기
            var assetsDir = Path.Combine(Directory.GetCurrentDirectory(), "Assets");
            if (!Directory.Exists(assetsDir)) return;

            var prefabFiles = Directory.GetFiles(assetsDir, "*.prefab", SearchOption.AllDirectories);

            foreach (var prefabPath in prefabFiles)
            {
                var metaPath = prefabPath + ".rose";
                if (!File.Exists(metaPath)) continue;

                var meta = RoseMetadata.LoadOrCreate(prefabPath);
                if (string.IsNullOrEmpty(meta.guid)) continue;

                var guid = meta.guid;
                _guidToPath[guid] = prefabPath;

                var baseGuid = PrefabImporter.GetBasePrefabGuidFromFile(prefabPath);
                if (baseGuid != null)
                {
                    _parentMap[guid] = baseGuid;

                    if (!_childVariants.ContainsKey(baseGuid))
                        _childVariants[baseGuid] = new List<string>();
                    _childVariants[baseGuid].Add(guid);
                }
            }
        }

        /// <summary>주어진 프리팹 GUID의 루트(Base) GUID를 찾아 반환.</summary>
        public string FindRootBase(string prefabGuid)
        {
            var current = prefabGuid;
            int depth = 0;
            while (_parentMap.TryGetValue(current, out var parent) && depth < 32)
            {
                current = parent;
                depth++;
            }
            return current;
        }

        /// <summary>주어진 프리팹 기준 전체 트리 (루트부터) 반환.</summary>
        public TreeNode? BuildTree(string prefabGuid)
        {
            var rootGuid = FindRootBase(prefabGuid);
            return BuildNodeRecursive(rootGuid);
        }

        private TreeNode? BuildNodeRecursive(string guid)
        {
            _guidToPath.TryGetValue(guid, out var path);
            var displayName = path != null ? Path.GetFileNameWithoutExtension(path) : guid;
            bool isVariant = _parentMap.ContainsKey(guid);

            var node = new TreeNode(guid, path, displayName, isVariant);

            if (_childVariants.TryGetValue(guid, out var children))
            {
                foreach (var childGuid in children)
                {
                    var childNode = BuildNodeRecursive(childGuid);
                    if (childNode != null)
                        node.Children.Add(childNode);
                }
            }

            return node;
        }

        /// <summary>주어진 프리팹이 Variant인지 확인.</summary>
        public bool IsVariant(string prefabGuid)
        {
            return _parentMap.ContainsKey(prefabGuid);
        }

        /// <summary>주어진 프리팹의 부모(Base) GUID 반환.</summary>
        public string? GetParentGuid(string prefabGuid)
        {
            return _parentMap.TryGetValue(prefabGuid, out var parent) ? parent : null;
        }

        /// <summary>주어진 프리팹의 Variant 자식 GUID 목록 반환.</summary>
        public IReadOnlyList<string>? GetChildVariants(string prefabGuid)
        {
            return _childVariants.TryGetValue(prefabGuid, out var children) ? children : null;
        }
    }
}
