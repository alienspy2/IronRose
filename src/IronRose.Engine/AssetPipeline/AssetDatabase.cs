using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IronRose.Engine;
using IronRose.Engine.Editor;
using RoseEngine;
using Tomlyn.Model;

namespace IronRose.AssetPipeline
{
    public class AssetDatabase : IAssetDatabase
    {
        private readonly Dictionary<string, string> _guidToPath = new();
        private readonly Dictionary<string, object> _loadedAssets = new();
        private readonly HashSet<string> _failedImports = new();
        private readonly Dictionary<Mesh, string> _meshToGuid = new();
        private readonly Dictionary<Material, string> _materialToGuid = new();
        private readonly Dictionary<Texture2D, string> _textureToGuid = new();
        private readonly Dictionary<Sprite, string> _spriteToGuid = new();
        private readonly Dictionary<AnimationClip, string> _animClipToGuid = new();
        private readonly Dictionary<RendererProfile, string> _rendererProfileToGuid = new();
        private readonly Dictionary<PostProcessProfile, string> _ppProfileToGuid = new();
        private readonly Dictionary<TextAsset, string> _textAssetToGuid = new();
        private readonly MeshImporter _meshImporter = new();
        private readonly GltfMeshImporter _gltfMeshImporter = new();
        private readonly TextureImporter _textureImporter = new();
        private readonly FontImporter _fontImporter = new();
        private readonly MaterialImporter _materialImporter = new();
        private readonly AnimationClipImporter _animClipImporter = new();
        private readonly RendererProfileImporter _rendererProfileImporter = new();
        private readonly PostProcessProfileImporter _ppProfileImporter = new();
        private readonly TextAssetImporter _textAssetImporter = new();
        private readonly RoseCache _roseCache = new(ProjectContext.CachePath);
        private PrefabImporter? _prefabImporter;

        // ─── Prefab Dependency Graph ─────────────────────────────
        // childGuid → Set<parentGuid>: child를 참조하는 부모 프리팹들
        private readonly Dictionary<string, HashSet<string>> _prefabDependents = new();
        // parentGuid → Set<childGuid>: 부모가 참조하는 자식 프리팹들 (그래프 갱신 시 역참조 정리용)
        private readonly Dictionary<string, HashSet<string>> _prefabDependencies = new();

        // ─── FileSystemWatcher ───────────────────────────────────
        private FileSystemWatcher? _watcher;
        private readonly Queue<AssetChangeEvent> _pendingChanges = new();
        private readonly object _changeLock = new();
        private readonly HashSet<string> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);
        private string? _assetsPath;

        // ─── Reentrancy guard for import ────────────────────────
        private int _importDepth;
        /// <summary>외부에서 import depth를 올려 OnRoseMetadataSaved reimport를 억제.</summary>
        public void PushImportGuard() => _importDepth++;
        public void PopImportGuard() => _importDepth--;
        // Reimport queue filled by OnSaved when async reimport is busy
        private readonly Queue<string> _pendingReimports = new();
        public bool ProjectDirty { get; set; }

        // ─── Play-mode deferred cache ops ───────────────────────
        private readonly ConcurrentQueue<(string path, Texture2D tex, RoseMetadata meta)> _pendingCacheTextures = new();
        private readonly ConcurrentQueue<(string path, MeshImportResult result, RoseMetadata meta)> _pendingCacheMeshes = new();

        /// <summary>true면 Reimport, Scan, FileWatcher 등 상세 로그를 출력한다.</summary>
        public static bool VerboseLogging { get; set; }

        internal static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".glb", ".gltf", ".fbx", ".obj",
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".exr",
            ".prefab", ".ttf", ".otf", ".scene", ".mat",
            ".anim",
            ".renderer",
            ".ppprofile",
            ".txt", ".json", ".xml", ".csv",
            ".html", ".css"
        };

        private enum AssetChangeType { Created, Changed, Deleted, Renamed }
        private struct AssetChangeEvent
        {
            public AssetChangeType Type;
            public string FullPath;
            public string? OldFullPath;
        }

        public int AssetCount => _guidToPath.Count;

        public void ScanAssets(string projectPath)
        {
            if (RoseConfig.ForceClearCache)
            {
                EditorDebug.Log("[AssetDatabase] ForceClearCache enabled — clearing cache");
                _roseCache.ClearAll();
            }

            _guidToPath.Clear();
            _meshToGuid.Clear();
            _materialToGuid.Clear();
            _textureToGuid.Clear();
            _spriteToGuid.Clear();

            if (!Directory.Exists(projectPath))
            {
                EditorDebug.LogWarning($"Asset directory not found: {projectPath}");
                return;
            }

            foreach (var ext in SupportedExtensions)
            {
                var files = Directory.GetFiles(projectPath, $"*{ext}", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (Path.GetFileName(file).StartsWith('.')) continue;
                    var meta = RoseMetadata.LoadOrCreate(file);
                    if (!string.IsNullOrEmpty(meta.guid))
                    {
                        _guidToPath[meta.guid] = file;

                        // Sub-asset GUID 등록
                        foreach (var sub in meta.subAssets)
                        {
                            var subPath = SubAssetPath.Build(file, sub.type, sub.index);
                            _guidToPath[sub.guid] = subPath;
                        }
                    }
                }
            }

            EditorDebug.Log($"[AssetDatabase] Scanned {_guidToPath.Count} assets in {projectPath}");

            // Subscribe to .rose save events for automatic reimport
            RoseMetadata.OnSaved -= OnRoseMetadataSaved;
            RoseMetadata.OnSaved += OnRoseMetadataSaved;

            StartWatching(projectPath);
        }

        public string? GetPathFromGuid(string guid)
        {
            return _guidToPath.TryGetValue(guid, out var path) ? path : null;
        }

        public string? GetGuidFromPath(string path)
        {
            foreach (var kvp in _guidToPath)
            {
                if (kvp.Value == path)
                    return kvp.Key;
            }
            return null;
        }

        // ─── GUID 기반 로드 (1순위) ─────────────────────────────

        public T? LoadByGuid<T>(string guid) where T : class
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (!_guidToPath.TryGetValue(guid, out var path))
            {
                EditorDebug.LogWarning($"[AssetDatabase] LoadByGuid<{typeof(T).Name}>: GUID '{guid}' not found in _guidToPath ({_guidToPath.Count} entries)");
                return null;
            }
            var result = Load<T>(path);
            if (result == null)
                EditorDebug.LogWarning($"[AssetDatabase] LoadByGuid<{typeof(T).Name}>: Load('{path}') returned null for GUID '{guid}'");
            return result;
        }

        // ─── 경로 기반 로드 (내부/폴백) ─────────────────────────

        public T? Load<T>(string path) where T : class
        {
            // Sub-asset 경로 처리 (예: "Assets/Model.glb#Mesh:0")
            if (SubAssetPath.TryParse(path, out var filePath, out var subType, out var subIndex))
            {
                // 메모리 캐시 확인
                if (_loadedAssets.TryGetValue(path, out var cachedSub))
                    return cachedSub as T;

                // 부모 파일 로드 (sub-asset 캐시 생성 트리거)
                EnsureParentLoaded(filePath);

                return _loadedAssets.TryGetValue(path, out cachedSub) ? cachedSub as T : null;
            }

            // 일반 파일 경로 로드
            // 1. 메모리 캐시 확인
            if (_loadedAssets.TryGetValue(path, out var cached))
            {
                return ExtractFromResult<T>(cached);
            }

            // 1-1. 임포트 실패 캐시 확인 (매 프레임 재시도 방지)
            if (_failedImports.Contains(path))
            {
                return null;
            }

            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = GetImporterType(meta);

            object? asset = null;
            bool loadedFromCache = false;

            // 2. 디스크 캐시 → 임포터 호출
            bool useCache = !RoseConfig.DontUseCache;
            switch (importerType)
            {
                case "MeshImporter":
                    if (useCache)
                        asset = _roseCache.TryLoadMesh(path, meta);
                    if (asset != null)
                        loadedFromCache = true;
                    else
                        asset = ImportMesh(path, meta);
                    break;

                case "TextureImporter":
                {
                    Texture2D? tex = null;
                    if (useCache)
                        tex = _roseCache.TryLoadTexture(path, meta);
                    if (tex != null)
                        loadedFromCache = true;
                    else
                        tex = _textureImporter.Import(path, meta);
                    if (tex != null && IsSpriteTexture(meta))
                        asset = BuildSpriteImportResult(tex, meta);
                    else
                        asset = tex;
                    break;
                }

                case "PrefabImporter":
                    asset = ImportPrefab(path);
                    EditorDebug.Log($"[AssetDatabase] ImportPrefab result: {(asset != null ? ((RoseEngine.GameObject)asset).name : "NULL")} for path={path}");
                    break;

                case "FontImporter":
                    asset = _fontImporter.Import(path, meta);
                    break;

                case "MaterialImporter":
                    asset = _materialImporter.Import(path, meta, this);
                    break;

                case "AnimationClipImporter":
                    asset = _animClipImporter.Import(path, meta);
                    break;

                case "RendererProfileImporter":
                    asset = _rendererProfileImporter.Import(path, meta);
                    break;

                case "PostProcessProfileImporter":
                    asset = _ppProfileImporter.Import(path, meta);
                    break;

                case "TextAssetImporter":
                    asset = _textAssetImporter.Import(path, meta);
                    break;
            }

            // 3. 메모리 캐시에 저장 + sub-asset 등록
            // 캐시에서 로드한 경우 RegisterSubAssets를 건너뜀:
            // - 캐시에 Textures가 포함되지 않아 sub-asset diff가 발생하여 .rose 타임스탬프를 변경시킴
            // - ScanAssets에서 이미 등록된 GUID/경로가 있으므로 재등록 불필요
            if (asset != null)
            {
                _loadedAssets[path] = asset;

                if (asset is MeshImportResult meshResult)
                {
                    if (!loadedFromCache)
                        RegisterSubAssets(path, meshResult, meta);
                    CacheSubAssets(path, meshResult, meta);
                }
                else if (asset is SpriteImportResult spriteResult)
                {
                    if (!loadedFromCache)
                        RegisterSpriteSubAssets(path, spriteResult, meta);
                    CacheSpriteSubAssets(path, spriteResult, meta);
                }
                else if (asset is Material mat && !string.IsNullOrEmpty(meta.guid))
                {
                    _materialToGuid[mat] = meta.guid;
                }
                else if (asset is AnimationClip animClip && !string.IsNullOrEmpty(meta.guid))
                {
                    _animClipToGuid[animClip] = meta.guid;
                }
                else if (asset is RendererProfile rp && !string.IsNullOrEmpty(meta.guid))
                {
                    _rendererProfileToGuid[rp] = meta.guid;
                }
                else if (asset is PostProcessProfile pp && !string.IsNullOrEmpty(meta.guid))
                {
                    _ppProfileToGuid[pp] = meta.guid;
                }
                else if (asset is TextAsset textAsset && !string.IsNullOrEmpty(meta.guid))
                {
                    _textAssetToGuid[textAsset] = meta.guid;
                }
            }
            else
            {
                _failedImports.Add(path);
                EditorDebug.LogWarning($"[AssetDatabase] Import failed, marking as failed (will not retry): {path}");
            }

            return ExtractFromResult<T>(asset);
        }

        // ─── GUID 역검색 ────────────────────────────────────────

        public string? FindGuidForMesh(Mesh mesh)
        {
            if (mesh == null) return null;
            return _meshToGuid.TryGetValue(mesh, out var guid) ? guid : null;
        }

        public string? FindGuidForMaterial(Material material)
        {
            if (material == null) return null;
            return _materialToGuid.TryGetValue(material, out var guid) ? guid : null;
        }

        public string? FindGuidForTexture(Texture2D texture)
        {
            if (texture == null) return null;
            return _textureToGuid.TryGetValue(texture, out var guid) ? guid : null;
        }

        public string? FindGuidForSprite(Sprite sprite)
        {
            if (sprite == null) return null;
            if (_spriteToGuid.TryGetValue(sprite, out var guid)) return guid;
            return !string.IsNullOrEmpty(sprite.guid) ? sprite.guid : null;
        }

        public string? FindGuidForAnimationClip(AnimationClip clip)
        {
            if (clip == null) return null;
            return _animClipToGuid.TryGetValue(clip, out var guid) ? guid : null;
        }

        public string? FindGuidForPrefab(GameObject prefab)
        {
            if (prefab == null) return null;
            // PrefabInstance 컴포넌트가 있으면 prefabGuid 사용
            var inst = prefab.GetComponent<PrefabInstance>();
            if (inst != null && !string.IsNullOrEmpty(inst.prefabGuid))
                return inst.prefabGuid;
            // 로드된 에셋에서 역검색
            foreach (var (path, asset) in _loadedAssets)
            {
                if (ReferenceEquals(asset, prefab) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    return GetGuidFromPath(path);
            }
            return null;
        }

        public string? FindGuidForRendererProfile(RendererProfile profile)
        {
            if (profile == null) return null;
            return _rendererProfileToGuid.TryGetValue(profile, out var guid) ? guid : null;
        }

        public string? FindGuidForPostProcessProfile(PostProcessProfile profile)
        {
            if (profile == null) return null;
            return _ppProfileToGuid.TryGetValue(profile, out var guid) ? guid : null;
        }

        public string? FindGuidForTextAsset(TextAsset textAsset)
        {
            if (textAsset == null) return null;
            return _textAssetToGuid.TryGetValue(textAsset, out var guid) ? guid : null;
        }

        /// <summary>
        /// 로드된 Mesh로부터 원본 에셋 경로를 역검색합니다.
        /// </summary>
        public string? FindPathForMesh(Mesh mesh)
        {
            if (mesh == null) return null;
            foreach (var (path, asset) in _loadedAssets)
            {
                if (asset is MeshImportResult result)
                {
                    for (int i = 0; i < result.Meshes.Length; i++)
                    {
                        if (result.Meshes[i].Mesh == mesh)
                            return path;
                    }
                }
            }
            return null;
        }

        // ─── Sub-asset / Import result 조회 ─────────────────────

        /// <summary>MeshImportResult를 가져온다. 미로드 시 임포트 트리거.</summary>
        internal MeshImportResult? GetMeshImportResult(string filePath)
        {
            if (!_loadedAssets.ContainsKey(filePath))
                Load<Mesh>(filePath);
            return _loadedAssets.TryGetValue(filePath, out var cached) ? cached as MeshImportResult : null;
        }

        public IReadOnlyList<SubAssetEntry> GetSubAssets(string filePath)
        {
            var meta = RoseMetadata.LoadOrCreate(filePath);
            return meta.subAssets;
        }

        public IReadOnlyCollection<string> GetAllAssetPaths()
            => _guidToPath.Values.Where(p => !SubAssetPath.IsSubAssetPath(p)).ToList();

        // ─── 캐시 관리 ──────────────────────────────────────────

        public void ClearCache() => _roseCache.ClearAll();

        /// <summary>
        /// 스크립트 핫 리로드 후, 스크립트 타입 컴포넌트를 포함하는 프리팹 캐시를 무효화한다.
        /// 캐시된 프리팹 템플릿의 컴포넌트가 이전 ALC의 타입을 참조하고 있으므로,
        /// 해당 엔트리를 제거하여 다음 접근 시 새 ALC 타입으로 재역직렬화되도록 한다.
        /// </summary>
        public void InvalidateScriptPrefabCache()
        {
            var toRemove = new List<string>();
            foreach (var kvp in _loadedAssets)
            {
                if (kvp.Value is not GameObject go) continue;
                if (HasScriptComponent(go))
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _loadedAssets.Remove(key);

            if (toRemove.Count > 0)
                EditorDebug.Log($"[AssetDatabase] InvalidateScriptPrefabCache: evicted {toRemove.Count} prefab(s) with script components", force: true);
        }

        /// <summary>GO 계층 내에 Scripts 어셈블리의 컴포넌트가 있는지 재귀 검사.</summary>
        private static bool HasScriptComponent(GameObject go)
        {
            foreach (var comp in go.InternalComponents)
            {
                if (comp is Transform) continue;
                var asmName = comp.GetType().Assembly.GetName().Name;
                if (asmName == "Scripts")
                    return true;
            }
            for (int i = 0; i < go.transform.childCount; i++)
            {
                if (HasScriptComponent(go.transform.GetChild(i).gameObject))
                    return true;
            }
            return false;
        }

        /// <summary>Returns paths of cacheable assets (mesh/texture) that don't have a valid disk cache.</summary>
        public string[] GetUncachedAssetPaths()
        {
            if (RoseConfig.DontUseCache)
                return [];

            var uncached = new List<string>();
            foreach (var path in _guidToPath.Values)
            {
                // Sub-asset 경로는 건너뛰기 (부모 파일만 캐싱)
                if (SubAssetPath.IsSubAssetPath(path)) continue;

                var meta = RoseMetadata.LoadOrCreate(path);
                var importerType = GetImporterType(meta);

                if (importerType is "MeshImporter" or "TextureImporter")
                {
                    if (!_roseCache.HasValidCache(path))
                        uncached.Add(path);
                }
            }
            return uncached.ToArray();
        }

        // ─── Play-mode deferred cache helpers ───────────────────

        private void StoreCacheOrDefer(string path, Texture2D tex, RoseMetadata meta)
        {
            if (EditorPlayMode.IsInPlaySession)
            {
                _pendingCacheTextures.Enqueue((path, tex, meta));
                return;
            }
            _roseCache.StoreTexture(path, tex, meta);
        }

        private void StoreCacheOrDefer(string path, MeshImportResult result, RoseMetadata meta)
        {
            if (EditorPlayMode.IsInPlaySession)
            {
                _pendingCacheMeshes.Enqueue((path, result, meta));
                return;
            }
            _roseCache.StoreMesh(path, result, meta);
        }

        /// <summary>플레이모드 종료 후, 보류 중인 캐시 저장을 일괄 수행한다.</summary>
        public void FlushPendingCacheOps()
        {
            int count = 0;
            while (_pendingCacheTextures.TryDequeue(out var item))
            {
                _roseCache.StoreTexture(item.path, item.tex, item.meta);
                count++;
            }
            while (_pendingCacheMeshes.TryDequeue(out var item))
            {
                _roseCache.StoreMesh(item.path, item.result, item.meta);
                count++;
            }
            if (count > 0)
                EditorDebug.Log($"[AssetDatabase] Flushed {count} deferred cache operations after Play stop");
        }

        /// <summary>Import and write disk cache for a single asset without keeping it in memory.</summary>
        public void EnsureDiskCached(string path)
        {
            if (RoseConfig.DontUseCache) return;

            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = GetImporterType(meta);

            switch (importerType)
            {
                case "MeshImporter":
                    var meshResult = ImportMesh(path, meta);
                    if (meshResult != null)
                    {
                        // .rose 저장 후 캐시 저장 (타임스탬프 일관성 보장)
                        RegisterSubAssets(path, meshResult, meta);
                        StoreCacheOrDefer(path, meshResult, meta);
                    }
                    break;
                case "TextureImporter":
                    var tex = _textureImporter.Import(path, meta);
                    if (tex != null)
                    {
                        StoreCacheOrDefer(path, tex, meta);
                        if (IsSpriteTexture(meta))
                        {
                            var sr = BuildSpriteImportResult(tex, meta);
                            RegisterSpriteSubAssets(path, sr, meta);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Assimp/이미지 임포트만 수행 (메인 스레드 전용).
        /// 결과와 메타데이터를 반환하여 StoreDiskCache에서 백그라운드 저장 가능.
        /// </summary>
        internal (object? asset, RoseMetadata meta, string importerType) ImportForCache(string path)
        {
            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = GetImporterType(meta);

            object? asset = null;
            switch (importerType)
            {
                case "MeshImporter":
                    asset = ImportMesh(path, meta);
                    break;
                case "TextureImporter":
                    asset = _textureImporter.Import(path, meta);
                    break;
            }
            return (asset, meta, importerType);
        }

        /// <summary>
        /// 임포트 결과를 디스크 캐시에 저장 (Task.Run에서 호출 가능 — 네이티브 호출 없음).
        /// </summary>
        internal void StoreDiskCache(string path, object asset, RoseMetadata meta, string importerType)
        {
            try
            {
                switch (importerType)
                {
                    case "MeshImporter":
                        if (asset is MeshImportResult meshResult)
                            StoreCacheOrDefer(path, meshResult, meta);
                        break;
                    case "TextureImporter":
                        if (asset is Texture2D tex)
                            StoreCacheOrDefer(path, tex, meta);
                        break;
                }
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[AssetDatabase] StoreDiskCache failed: {path} — {ex.Message}");
            }
        }

        // ─── Reimport ───────────────────────────────────────────

        public void Reimport(string path)
        {
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Reimport starting: {path}");
            _importDepth++;
            _failedImports.Remove(path);
            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = GetImporterType(meta);

            // 1. 기존 에셋 보존 (Dispose하지 않음 — 씬 참조 교체 후 처리)
            _loadedAssets.TryGetValue(path, out var oldAsset);
            _loadedAssets.Remove(path);

            // 기존 sub-asset 캐시/역참조 맵 정리
            if (oldAsset is MeshImportResult oldResult)
            {
                RemoveSubAssetCaches(path, oldResult);
            }
            else if (oldAsset is SpriteImportResult oldSprResult)
            {
                RemoveSpriteSubAssetCaches(path, oldSprResult);
            }

            // 2. 디스크 캐시 무효화
            _roseCache.InvalidateCache(path);

            // 3. 재임포트 + 캐시 저장 + 메모리 로드 + 씬 참조 교체
            switch (importerType)
            {
                case "MeshImporter":
                {
                    var newResult = ImportMesh(path, meta);
                    if (newResult != null)
                    {
                        RegisterSubAssets(path, newResult, meta);
                        StoreCacheOrDefer(path, newResult, meta);
                        _loadedAssets[path] = newResult;
                        CacheSubAssets(path, newResult, meta);
                        ReplaceMeshInScene(newResult);

                    }
                    break;
                }
                case "TextureImporter":
                {
                    var newTex = _textureImporter.Import(path, meta);
                    if (newTex != null)
                    {
                        StoreCacheOrDefer(path, newTex, meta);
                        var oldTex = oldAsset is SpriteImportResult oldSpr ? oldSpr.Texture : oldAsset as Texture2D;
                        if (IsSpriteTexture(meta))
                        {
                            var sr = BuildSpriteImportResult(newTex, meta);
                            RegisterSpriteSubAssets(path, sr, meta);
                            _loadedAssets[path] = sr;
                            CacheSpriteSubAssets(path, sr, meta);
                            var oldSprResult = oldAsset as SpriteImportResult;
                            ReplaceSpriteInScene(oldSprResult, sr);
                        }
                        else
                        {
                            _loadedAssets[path] = newTex;
                        }
                        ReplaceTextureInScene(newTex, oldTex);
                    }
                    break;
                }
                case "FontImporter":
                {
                    var newFont = _fontImporter.Import(path, meta);
                    if (newFont != null)
                    {
                        _loadedAssets[path] = newFont;
                        ReplaceFontInScene(newFont);
                    }
                    break;
                }
                case "MaterialImporter":
                {
                    // 이전 Material 역참조맵 제거
                    if (oldAsset is Material oldMat)
                        _materialToGuid.Remove(oldMat);

                    var newMat = _materialImporter.Import(path, meta, this);
                    if (newMat != null)
                    {
                        _loadedAssets[path] = newMat;
                        if (!string.IsNullOrEmpty(meta.guid))
                            _materialToGuid[newMat] = meta.guid;
                        ReplaceMaterialInScene(newMat, oldAsset as Material);
                    }
                    break;
                }
                case "RendererProfileImporter":
                {
                    if (oldAsset is RendererProfile oldRp)
                        _rendererProfileToGuid.Remove(oldRp);

                    var newRp = _rendererProfileImporter.Import(path, meta);
                    if (newRp != null)
                    {
                        _loadedAssets[path] = newRp;
                        if (!string.IsNullOrEmpty(meta.guid))
                            _rendererProfileToGuid[newRp] = meta.guid;

                        // 활성 프로파일이면 참조 교체
                        if (RenderSettings.activeRendererProfile == oldAsset)
                        {
                            RenderSettings.activeRendererProfile = newRp;
                            newRp.ApplyToRenderSettings();
                        }
                    }
                    break;
                }
                case "PostProcessProfileImporter":
                {
                    if (oldAsset is PostProcessProfile oldPp)
                        _ppProfileToGuid.Remove(oldPp);

                    var newPp = _ppProfileImporter.Import(path, meta);
                    if (newPp != null)
                    {
                        _loadedAssets[path] = newPp;
                        if (!string.IsNullOrEmpty(meta.guid))
                            _ppProfileToGuid[newPp] = meta.guid;

                        // Volume의 stale profile 참조 갱신
                        foreach (var vol in PostProcessVolume._allVolumes)
                        {
                            if (vol.profileGuid == meta.guid)
                                vol.profile = newPp;
                        }
                    }
                    break;
                }
                case "TextAssetImporter":
                {
                    var newTa = _textAssetImporter.Import(path, meta);
                    if (newTa != null)
                    {
                        // 기존 인스턴스가 있으면 내용만 갱신 (씬 참조 유지)
                        if (oldAsset is TextAsset oldTa)
                        {
                            oldTa.text = newTa.text;
                            oldTa.bytes = newTa.bytes;
                        }
                        else
                        {
                            _loadedAssets[path] = newTa;
                            if (!string.IsNullOrEmpty(meta.guid))
                                _textAssetToGuid[newTa] = meta.guid;
                        }
                    }
                    break;
                }
                case "PrefabImporter":
                {
                    // Dependency graph 기반으로 수정된 프리팹과 이를 참조하는 부모들만 캐스케이드 무효화
                    var prefabGuid = GetGuidFromPath(path);
                    if (!string.IsNullOrEmpty(prefabGuid))
                        InvalidatePrefabAndDependents(prefabGuid!);
                    break;
                }
            }

            // 4. 이전 에셋 GPU 리소스 정리 (텍스처 공유로 인한 이중 Dispose 방지)
            if (oldAsset is MeshImportResult oldMeshResult)
            {
                var disposed = new HashSet<Texture2D>();
                foreach (var tex in oldMeshResult.Textures)
                {
                    if (tex != null && disposed.Add(tex))
                        DisposeIfNotDefault(tex);
                }
                foreach (var mat in oldMeshResult.Materials)
                {
                    if (mat.mainTexture != null && disposed.Add(mat.mainTexture))
                        DisposeIfNotDefault(mat.mainTexture);
                    if (mat.normalMap != null && disposed.Add(mat.normalMap))
                        DisposeIfNotDefault(mat.normalMap);
                    if (mat.MROMap != null && disposed.Add(mat.MROMap))
                        DisposeIfNotDefault(mat.MROMap);
                }
            }
            else if (oldAsset is SpriteImportResult oldSpriteResult)
            {
                oldSpriteResult.Texture?.Dispose();
            }
            else if (oldAsset is Texture2D oldTex)
            {
                oldTex.Dispose();
            }
            else if (oldAsset is Font oldFont)
            {
                oldFont.atlasTexture?.Dispose();
            }

            ProjectDirty = true;
            _importDepth--;
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Reimported: {path}");
        }

        // ─── Async Reimport (비동기 재임포트 + 진행 UI) ──────────────

        private System.Threading.Tasks.Task? _reimportTask;
        private string? _reimportPath;
        private object? _reimportOldAsset;
        private System.Diagnostics.Stopwatch? _reimportTimer;

        // 백그라운드 작업 결과 (FinalizeReimport에서 메인 스레드로 처리)
        private MeshImportResult? _reimportMeshResult;
        private RoseMetadata? _reimportMeta;
        private string? _reimportType;
        private Texture2D? _reimportTexResult;
        private SpriteImportResult? _reimportSpriteResult;

        /// <summary>리임포트 진행 중 여부 (EngineCore에서 오버레이 표시용)</summary>
        public bool IsReimporting => _reimportTask != null;
        public string? ReimportAssetName => _reimportPath != null ? Path.GetFileName(_reimportPath) : null;
        public double ReimportElapsed => _reimportTimer?.Elapsed.TotalSeconds ?? 0;

        /// <summary>
        /// 비동기 재임포트 시작. 무거운 임포트 + 캐시 저장을 백그라운드에서 수행하고
        /// EngineCore가 매 프레임 ProcessReimport()로 완료를 확인한 뒤 메인 스레드에서 마무리.
        /// </summary>
        public void ReimportAsync(string path)
        {
            if (_reimportTask != null)
            {
                EditorDebug.LogWarning("[AssetDatabase] Reimport already in progress, ignoring");
                return;
            }

            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Async reimport starting: {path}");
            _failedImports.Remove(path);
            _reimportPath = path;
            _reimportTimer = System.Diagnostics.Stopwatch.StartNew();

            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = GetImporterType(meta);
            _reimportMeta = meta;
            _reimportType = importerType;

            // 1. 기존 에셋 보존
            _loadedAssets.TryGetValue(path, out var oldAsset);
            _reimportOldAsset = oldAsset;
            _loadedAssets.Remove(path);

            if (oldAsset is MeshImportResult oldResult)
                RemoveSubAssetCaches(path, oldResult);
            else if (oldAsset is SpriteImportResult oldSprResult)
                RemoveSpriteSubAssetCaches(path, oldSprResult);

            // 2. 디스크 캐시 무효화
            _roseCache.InvalidateCache(path);

            // 3. 백그라운드 임포트 시작
            _importDepth++;
            _reimportTask = System.Threading.Tasks.Task.Run(() =>
            {
                switch (importerType)
                {
                    case "MeshImporter":
                    {
                        var result = ImportMesh(path, meta);
                        if (result != null)
                        {
                            RegisterSubAssets(path, result, meta);
                            StoreCacheOrDefer(path, result, meta);
                        }
                        _reimportMeshResult = result;
                        break;
                    }
                    case "TextureImporter":
                    {
                        var tex = _textureImporter.Import(path, meta);
                        if (tex != null)
                            StoreCacheOrDefer(path, tex, meta);
                        // Sprite 서브 에셋 등록 (IO만 — bg 스레드 안전)
                        if (tex != null && IsSpriteTexture(meta))
                        {
                            var sr = BuildSpriteImportResult(tex, meta);
                            RegisterSpriteSubAssets(path, sr, meta);
                            _reimportSpriteResult = sr;
                        }
                        else
                            _reimportSpriteResult = null;
                        _reimportTexResult = tex;
                        break;
                    }
                    case "FontImporter":
                    {
                        // 폰트는 가볍고 GPU 불필요 — 동기 처리
                        break;
                    }
                }
            });
        }

        /// <summary>
        /// 매 프레임 호출. 백그라운드 작업 완료 시 메인 스레드에서 씬 참조 교체 + GPU 정리.
        /// 완료 시 true 반환.
        /// </summary>
        public bool ProcessReimport()
        {
            if (_reimportTask == null) return false;
            if (!_reimportTask.IsCompleted) return false;

            // 에러 처리
            if (_reimportTask.IsFaulted)
            {
                var ex = _reimportTask.Exception?.InnerException;
                EditorDebug.LogError($"[AssetDatabase] Async reimport failed: {ex?.Message}");
            }

            var path = _reimportPath!;

            // 메인 스레드 마무리
            switch (_reimportType)
            {
                case "MeshImporter":
                {
                    if (_reimportMeshResult != null)
                    {
                        _loadedAssets[path] = _reimportMeshResult;
                        CacheSubAssets(path, _reimportMeshResult, _reimportMeta!);
                        ReplaceMeshInScene(_reimportMeshResult);

                    }
                    break;
                }
                case "TextureImporter":
                {
                    if (_reimportTexResult != null)
                    {
                        var oldTex = _reimportOldAsset is SpriteImportResult oldSpr
                            ? oldSpr.Texture : _reimportOldAsset as Texture2D;
                        if (_reimportSpriteResult != null)
                        {
                            _loadedAssets[path] = _reimportSpriteResult;
                            CacheSpriteSubAssets(path, _reimportSpriteResult, _reimportMeta!);
                            var oldSprResult = _reimportOldAsset as SpriteImportResult;
                            ReplaceSpriteInScene(oldSprResult, _reimportSpriteResult);
                        }
                        else
                        {
                            _loadedAssets[path] = _reimportTexResult;
                        }
                        ReplaceTextureInScene(_reimportTexResult, oldTex);
                    }
                    break;
                }
                case "FontImporter":
                {
                    var meta = _reimportMeta ?? RoseMetadata.LoadOrCreate(path);
                    var newFont = _fontImporter.Import(path, meta);
                    if (newFont != null)
                    {
                        _loadedAssets[path] = newFont;
                        ReplaceFontInScene(newFont);
                    }
                    break;
                }
            }

            // 이전 에셋 GPU 정리 (텍스처 공유로 인한 이중 Dispose 방지)
            if (_reimportOldAsset is MeshImportResult oldMeshResult)
            {
                var disposed = new HashSet<Texture2D>();
                foreach (var tex in oldMeshResult.Textures)
                {
                    if (tex != null && disposed.Add(tex))
                        DisposeIfNotDefault(tex);
                }
                foreach (var mat in oldMeshResult.Materials)
                {
                    if (mat.mainTexture != null && disposed.Add(mat.mainTexture))
                        DisposeIfNotDefault(mat.mainTexture);
                    if (mat.normalMap != null && disposed.Add(mat.normalMap))
                        DisposeIfNotDefault(mat.normalMap);
                    if (mat.MROMap != null && disposed.Add(mat.MROMap))
                        DisposeIfNotDefault(mat.MROMap);
                }
            }
            else if (_reimportOldAsset is SpriteImportResult oldSpriteResult)
            {
                oldSpriteResult.Texture?.Dispose();
            }
            else if (_reimportOldAsset is Texture2D oldTex)
            {
                oldTex.Dispose();
            }
            else if (_reimportOldAsset is Font oldFont)
            {
                oldFont.atlasTexture?.Dispose();
            }

            _reimportTimer?.Stop();
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Async reimport complete: {path} ({_reimportTimer?.Elapsed.TotalSeconds:F1}s)");

            // 상태 초기화
            _reimportTask = null;
            _reimportPath = null;
            _reimportOldAsset = null;
            _reimportTimer = null;
            _reimportMeshResult = null;
            _reimportTexResult = null;
            _reimportSpriteResult = null;
            _reimportMeta = null;
            _reimportType = null;
            _importDepth--;
            ProjectDirty = true;

            return true;
        }

        public void Unload(string path)
        {
            if (_loadedAssets.TryGetValue(path, out var asset))
            {
                if (asset is MeshImportResult result)
                    RemoveSubAssetCaches(path, result);
                if (asset is IDisposable disposable)
                    disposable.Dispose();
                _loadedAssets.Remove(path);
            }
        }

        /// <summary>
        /// 지정된 프리팹과, dependency graph에서 이를 참조하는 모든 부모 프리팹의 캐시를 재귀적으로 무효화.
        /// BFS로 탐색하여 순환 참조에 안전함.
        /// </summary>
        public void InvalidatePrefabAndDependents(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(guid);
            visited.Add(guid);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                // 캐시에서 제거
                var path = GetPathFromGuid(current);
                if (path != null && _loadedAssets.Remove(path) && VerboseLogging)
                    EditorDebug.Log($"[AssetDatabase] Invalidated prefab cache: {Path.GetFileName(path)} (guid={current})");

                // 이 프리팹을 참조하는 부모들도 무효화
                if (_prefabDependents.TryGetValue(current, out var parents))
                {
                    foreach (var parentGuid in parents)
                    {
                        if (visited.Add(parentGuid))
                            queue.Enqueue(parentGuid);
                    }
                }
            }
        }

        /// <summary>
        /// 프리팹 파일(TOML)을 파싱하여 nested prefab 참조(prefabInstance.prefabGuid, basePrefabGuid)를 추출하고
        /// dependency graph를 갱신.
        /// </summary>
        private void UpdatePrefabDependencies(string parentGuid, string prefabPath)
        {
            // 기존 의존성 정리
            if (_prefabDependencies.TryGetValue(parentGuid, out var oldDeps))
            {
                foreach (var oldChild in oldDeps)
                {
                    if (_prefabDependents.TryGetValue(oldChild, out var oldParents))
                    {
                        oldParents.Remove(parentGuid);
                        if (oldParents.Count == 0)
                            _prefabDependents.Remove(oldChild);
                    }
                }
                oldDeps.Clear();
            }
            else
            {
                oldDeps = new HashSet<string>();
                _prefabDependencies[parentGuid] = oldDeps;
            }

            // TOML 파싱하여 새 의존성 수집
            if (!File.Exists(prefabPath)) return;

            try
            {
                var config = TomlConfig.LoadFile(prefabPath, "[AssetDatabase]");
                if (config == null) return;

                // 1. Variant의 basePrefabGuid
                var prefabSection = config.GetSection("prefab");
                if (prefabSection != null)
                {
                    var bg = prefabSection.GetString("basePrefabGuid", "");
                    if (!string.IsNullOrEmpty(bg))
                        oldDeps.Add(bg);
                }

                // 2. Nested prefab의 prefabGuid (gameObjects 배열)
                var goArray = config.GetArray("gameObjects");
                if (goArray != null)
                {
                    foreach (var goConfig in goArray)
                    {
                        var piSection = goConfig.GetSection("prefabInstance");
                        if (piSection != null)
                        {
                            var pg = piSection.GetString("prefabGuid", "");
                            if (!string.IsNullOrEmpty(pg))
                                oldDeps.Add(pg);
                        }
                    }
                }

                // 역참조 맵 갱신
                foreach (var childGuid in oldDeps)
                {
                    if (!_prefabDependents.TryGetValue(childGuid, out var parents))
                    {
                        parents = new HashSet<string>();
                        _prefabDependents[childGuid] = parents;
                    }
                    parents.Add(parentGuid);
                }

                if (oldDeps.Count > 0 && VerboseLogging)
                    EditorDebug.Log($"[AssetDatabase] Prefab dependencies updated: {Path.GetFileName(prefabPath)} depends on [{string.Join(", ", oldDeps)}]");
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[AssetDatabase] Failed to parse prefab dependencies: {prefabPath} — {ex.Message}");
            }
        }

        /// <summary>
        /// 프리팹이 언로드될 때 dependency graph에서 해당 프리팹의 의존성 정보를 제거.
        /// </summary>
        private void RemovePrefabDependencies(string guid)
        {
            if (_prefabDependencies.TryGetValue(guid, out var deps))
            {
                foreach (var childGuid in deps)
                {
                    if (_prefabDependents.TryGetValue(childGuid, out var parents))
                    {
                        parents.Remove(guid);
                        if (parents.Count == 0)
                            _prefabDependents.Remove(childGuid);
                    }
                }
                _prefabDependencies.Remove(guid);
            }
        }

        public void UnloadAll()
        {
            RoseMetadata.OnSaved -= OnRoseMetadataSaved;

            foreach (var asset in _loadedAssets.Values)
            {
                if (asset is IDisposable disposable)
                    disposable.Dispose();
            }
            _loadedAssets.Clear();
            _meshToGuid.Clear();
            _materialToGuid.Clear();
            _textureToGuid.Clear();
            _spriteToGuid.Clear();
            _prefabDependents.Clear();
            _prefabDependencies.Clear();
        }

        // ─── Sprite Sub-asset ─────────────────────────────────────

        internal class SpriteImportResult
        {
            public Texture2D Texture = null!;
            public Sprite[] Sprites = [];
        }

        private static bool IsSpriteTexture(RoseMetadata meta)
        {
            return meta.importer.TryGetValue("texture_type", out var tt)
                   && tt?.ToString() == "Sprite";
        }

        private static SpriteImportResult BuildSpriteImportResult(Texture2D tex, RoseMetadata meta)
        {
            var result = new SpriteImportResult { Texture = tex };

            float ppu = 100f;
            if (meta.importer.TryGetValue("pixels_per_unit", out var ppuVal))
                ppu = ppuVal is double d ? (float)d : (ppuVal is long l ? l : 100f);

            var spriteMode = meta.importer.TryGetValue("sprite_mode", out var sm)
                ? sm?.ToString() : "Single";

            if (spriteMode == "Multiple" && meta.subAssets.Any(s => s.type == "Sprite"))
            {
                // Multiple 모드: metadata 슬라이스 기반
                var sprites = new List<Sprite>();
                foreach (var sub in meta.subAssets.Where(s => s.type == "Sprite").OrderBy(s => s.index))
                {
                    var rect = new Rect(0, 0, tex.width, tex.height);
                    var pivot = new Vector2(0.5f, 0.5f);
                    var border = new Vector4(0, 0, 0, 0);

                    var sliceKey = $"sprite_{sub.name}";
                    if (meta.importer.TryGetValue(sliceKey, out var sliceVal) && sliceVal is Tomlyn.Model.TomlTable st)
                    {
                        if (st.TryGetValue("rect", out var rv) && rv is Tomlyn.Model.TomlArray ra && ra.Count >= 4)
                            rect = new Rect(Convert.ToSingle(ra[0]), Convert.ToSingle(ra[1]),
                                            Convert.ToSingle(ra[2]), Convert.ToSingle(ra[3]));
                        if (st.TryGetValue("pivot", out var pvs) && pvs is Tomlyn.Model.TomlArray pas && pas.Count >= 2)
                            pivot = new Vector2(Convert.ToSingle(pas[0]), Convert.ToSingle(pas[1]));
                        if (st.TryGetValue("border", out var bvs) && bvs is Tomlyn.Model.TomlArray bas && bas.Count >= 4)
                            border = new Vector4(Convert.ToSingle(bas[0]), Convert.ToSingle(bas[1]),
                                                 Convert.ToSingle(bas[2]), Convert.ToSingle(bas[3]));
                    }

                    var sprite = Sprite.Create(tex, rect, pivot, ppu, border);
                    sprite.spriteName = sub.name;
                    sprite.guid = sub.guid;
                    sprites.Add(sprite);
                }
                result.Sprites = sprites.ToArray();
            }
            else
            {
                // Single 모드: 전체 텍스처 + pivot/border from metadata
                var pivot = new Vector2(0.5f, 0.5f);
                var border = new Vector4(0, 0, 0, 0);
                if (meta.importer.TryGetValue("pivot", out var pv) && pv is Tomlyn.Model.TomlArray pa && pa.Count >= 2)
                    pivot = new Vector2(Convert.ToSingle(pa[0]), Convert.ToSingle(pa[1]));
                if (meta.importer.TryGetValue("border", out var bv) && bv is Tomlyn.Model.TomlArray ba && ba.Count >= 4)
                    border = new Vector4(Convert.ToSingle(ba[0]), Convert.ToSingle(ba[1]), Convert.ToSingle(ba[2]), Convert.ToSingle(ba[3]));
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, ppu, border);
                sprite.spriteName = tex.name;
                result.Sprites = new[] { sprite };
            }

            return result;
        }

        private void RegisterSpriteSubAssets(string filePath, SpriteImportResult result, RoseMetadata meta)
        {
            var beforeSnapshot = meta.subAssets
                .Select(s => (s.name, s.type, s.index, s.guid)).ToList();

            var activeKeys = new HashSet<string>();

            for (int i = 0; i < result.Sprites.Length; i++)
            {
                var spriteName = result.Sprites[i].spriteName ?? $"Sprite_{i}";
                var entry = meta.GetOrCreateSubAsset(spriteName, "Sprite", i);
                _guidToPath[entry.guid] = SubAssetPath.Build(filePath, "Sprite", i);
                activeKeys.Add($"Sprite:{spriteName}");
                result.Sprites[i].guid = entry.guid;
            }

            meta.PruneSubAssets(activeKeys);

            bool changed = meta.subAssets.Count != beforeSnapshot.Count;
            if (!changed)
            {
                for (int i = 0; i < meta.subAssets.Count; i++)
                {
                    var s = meta.subAssets[i];
                    if (i >= beforeSnapshot.Count ||
                        s.name != beforeSnapshot[i].name || s.type != beforeSnapshot[i].type ||
                        s.index != beforeSnapshot[i].index || s.guid != beforeSnapshot[i].guid)
                    { changed = true; break; }
                }
            }

            if (changed)
            {
                meta.Save(filePath + ".rose");
                ProjectDirty = true;
            }
        }

        private void CacheSpriteSubAssets(string filePath, SpriteImportResult result, RoseMetadata meta)
        {
            for (int i = 0; i < result.Sprites.Length; i++)
            {
                var subPath = SubAssetPath.Build(filePath, "Sprite", i);
                _loadedAssets[subPath] = result.Sprites[i];

                var subEntry = meta.subAssets.FirstOrDefault(s => s.type == "Sprite" && s.index == i);
                if (subEntry != null)
                {
                    _spriteToGuid[result.Sprites[i]] = subEntry.guid;
                    result.Sprites[i].guid = subEntry.guid;
                }
            }
        }

        // ─── Sub-asset 등록/캐싱 ────────────────────────────────

        /// <summary>
        /// 임포트 결과의 mesh/material/texture를 .rose 파일에 sub-asset으로 등록하고
        /// _guidToPath에 GUID를 등록한다.
        /// </summary>
        private void RegisterSubAssets(string filePath, MeshImportResult result, RoseMetadata meta)
        {
            // 변경 전 스냅샷 (이름/타입/인덱스/GUID + 개수)
            var beforeCount = meta.subAssets.Count;
            var beforeSnapshot = meta.subAssets
                .Select(s => (s.name, s.type, s.index, s.guid))
                .ToList();

            var activeKeys = new HashSet<string>();

            for (int i = 0; i < result.Meshes.Length; i++)
            {
                var meshName = result.Meshes[i].Name ?? $"Mesh_{i}";
                var entry = meta.GetOrCreateSubAsset(meshName, "Mesh", i);
                _guidToPath[entry.guid] = SubAssetPath.Build(filePath, "Mesh", i);
                activeKeys.Add($"Mesh:{meshName}");
            }

            for (int i = 0; i < result.Materials.Length; i++)
            {
                var matName = result.Materials[i].name ?? $"Material_{i}";
                var entry = meta.GetOrCreateSubAsset(matName, "Material", i);
                _guidToPath[entry.guid] = SubAssetPath.Build(filePath, "Material", i);
                activeKeys.Add($"Material:{matName}");
            }

            for (int i = 0; i < result.Textures.Length; i++)
            {
                var texName = result.Textures[i].name ?? $"Texture_{i}";
                var entry = meta.GetOrCreateSubAsset(texName, "Texture2D", i);
                _guidToPath[entry.guid] = SubAssetPath.Build(filePath, "Texture2D", i);
                activeKeys.Add($"Texture2D:{texName}");
            }

            meta.PruneSubAssets(activeKeys);

            // sub_assets가 실제로 변경된 경우에만 .rose 저장 (타임스탬프 변경 방지)
            bool changed = meta.subAssets.Count != beforeCount;
            if (!changed)
            {
                for (int i = 0; i < meta.subAssets.Count; i++)
                {
                    var s = meta.subAssets[i];
                    var b = beforeSnapshot[i];
                    if (s.name != b.name || s.type != b.type || s.index != b.index || s.guid != b.guid)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Sub-assets changed, saving .rose: {filePath}");
                meta.Save(filePath + ".rose");
                ProjectDirty = true;
            }
        }

        /// <summary>
        /// 임포트 결과의 개별 에셋을 sub-asset 경로로 메모리 캐시에 등록하고
        /// 역참조 맵(_meshToGuid, _materialToGuid)을 갱신한다.
        /// </summary>
        private void CacheSubAssets(string filePath, MeshImportResult result, RoseMetadata meta)
        {
            for (int i = 0; i < result.Meshes.Length; i++)
            {
                var subPath = SubAssetPath.Build(filePath, "Mesh", i);
                _loadedAssets[subPath] = result.Meshes[i].Mesh;

                var subEntry = meta.subAssets.FirstOrDefault(s => s.type == "Mesh" && s.index == i);
                if (subEntry != null)
                {
                    _meshToGuid[result.Meshes[i].Mesh] = subEntry.guid;

                    // MipMesh LOD 메시들도 같은 GUID로 등록 (MipMeshFilter가 LOD 메시로 교체 시 직렬화 가능)
                    var mip = i < result.MipMeshes.Length ? result.MipMeshes[i] : null;
                    if (mip != null)
                    {
                        for (int lod = 1; lod < mip.LodCount; lod++)
                            _meshToGuid[mip.lodMeshes[lod]] = subEntry.guid;
                    }
                }
            }

            for (int i = 0; i < result.Materials.Length; i++)
            {
                var subPath = SubAssetPath.Build(filePath, "Material", i);
                _loadedAssets[subPath] = result.Materials[i];

                var subEntry = meta.subAssets.FirstOrDefault(s => s.type == "Material" && s.index == i);
                if (subEntry != null)
                    _materialToGuid[result.Materials[i]] = subEntry.guid;
            }

            for (int i = 0; i < result.Textures.Length; i++)
            {
                var subPath = SubAssetPath.Build(filePath, "Texture2D", i);
                _loadedAssets[subPath] = result.Textures[i];

                var subEntry = meta.subAssets.FirstOrDefault(s => s.type == "Texture2D" && s.index == i);
                if (subEntry != null)
                    _textureToGuid[result.Textures[i]] = subEntry.guid;
            }

            // MipMesh (GUID 없이 캐시만 — Mesh와 동일 인덱스)
            for (int i = 0; i < result.MipMeshes.Length; i++)
            {
                if (result.MipMeshes[i] != null)
                {
                    var subPath = SubAssetPath.Build(filePath, "MipMesh", i);
                    _loadedAssets[subPath] = result.MipMeshes[i]!;
                }
            }
        }

        /// <summary>sub-asset 캐시와 역참조 맵에서 기존 항목을 제거한다.</summary>
        private void RemoveSubAssetCaches(string filePath, MeshImportResult result)
        {
            for (int i = 0; i < result.Meshes.Length; i++)
            {
                _loadedAssets.Remove(SubAssetPath.Build(filePath, "Mesh", i));
                _meshToGuid.Remove(result.Meshes[i].Mesh);
            }

            for (int i = 0; i < result.Materials.Length; i++)
            {
                _loadedAssets.Remove(SubAssetPath.Build(filePath, "Material", i));
                _materialToGuid.Remove(result.Materials[i]);
            }

            for (int i = 0; i < result.Textures.Length; i++)
            {
                _loadedAssets.Remove(SubAssetPath.Build(filePath, "Texture2D", i));
                _textureToGuid.Remove(result.Textures[i]);
            }

            for (int i = 0; i < result.MipMeshes.Length; i++)
                _loadedAssets.Remove(SubAssetPath.Build(filePath, "MipMesh", i));
        }

        private void RemoveSpriteSubAssetCaches(string filePath, SpriteImportResult result)
        {
            for (int i = 0; i < result.Sprites.Length; i++)
            {
                _loadedAssets.Remove(SubAssetPath.Build(filePath, "Sprite", i));
                _spriteToGuid.Remove(result.Sprites[i]);
            }
        }

        // ─── FileSystemWatcher ───────────────────────────────────

        private void StartWatching(string projectPath)
        {
            _watcher?.Dispose();
            _assetsPath = projectPath;

            _watcher = new FileSystemWatcher(projectPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };

            _watcher.Created += (_, e) => EnqueueChange(AssetChangeType.Created, e.FullPath);
            _watcher.Changed += (_, e) => EnqueueChange(AssetChangeType.Changed, e.FullPath);
            _watcher.Deleted += (_, e) => EnqueueChange(AssetChangeType.Deleted, e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                lock (_changeLock)
                {
                    _pendingChanges.Enqueue(new AssetChangeEvent
                    {
                        Type = AssetChangeType.Renamed,
                        FullPath = e.FullPath,
                        OldFullPath = e.OldFullPath,
                    });
                }
            };

            EditorDebug.Log($"[AssetDatabase] FileSystemWatcher active on {projectPath}");
        }

        /// <summary>
        /// 지정 경로의 다음 FileSystemWatcher Changed 이벤트를 무시한다.
        /// 에디터 자체가 파일을 쓴 직후 불필요한 reimport를 방지하기 위해 사용.
        /// </summary>
        public void SuppressNextChange(string absolutePath)
        {
            lock (_changeLock)
            {
                _suppressedPaths.Add(absolutePath);
            }
        }

        private void EnqueueChange(AssetChangeType type, string fullPath)
        {
            var ext = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(ext)) return;
            // Ignore .rose sidecar files, thumbnails, and unsupported extensions
            if (ext.Equals(".rose", StringComparison.OrdinalIgnoreCase)) return;
            if (Path.GetFileName(fullPath).StartsWith('.')) return;
            if (!SupportedExtensions.Contains(ext)) return;

            lock (_changeLock)
            {
                // 에디터 자체 쓰기로 인한 변경은 무시
                if (_suppressedPaths.Remove(fullPath))
                    return;

                _pendingChanges.Enqueue(new AssetChangeEvent
                {
                    Type = type,
                    FullPath = fullPath,
                });
            }
        }

        /// <summary>
        /// 메인 스레드에서 매 프레임 호출. 파일 변경 이벤트를 처리한다.
        /// </summary>
        public void ProcessFileChanges()
        {
            List<AssetChangeEvent> events;
            lock (_changeLock)
            {
                if (_pendingChanges.Count == 0) return;
                events = new List<AssetChangeEvent>(_pendingChanges);
                _pendingChanges.Clear();
            }

            // Deduplicate: keep only the last event per file path
            var deduped = new Dictionary<string, AssetChangeEvent>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in events)
                deduped[evt.FullPath] = evt;

            foreach (var evt in deduped.Values)
            {
                try
                {
                    switch (evt.Type)
                    {
                        case AssetChangeType.Created:
                            RegisterNewAsset(evt.FullPath);
                            break;
                        case AssetChangeType.Changed:
                            if (GetGuidFromPath(evt.FullPath) != null)
                            {
                                // Already known asset → reimport if loaded
                                if (_loadedAssets.ContainsKey(evt.FullPath))
                                    Reimport(evt.FullPath);
                                else
                                    _roseCache.InvalidateCache(evt.FullPath);
                            }
                            else
                            {
                                // New file written in two steps (Created not caught)
                                RegisterNewAsset(evt.FullPath);
                            }
                            break;
                        case AssetChangeType.Deleted:
                            UnregisterAsset(evt.FullPath);
                            break;
                        case AssetChangeType.Renamed:
                            if (evt.OldFullPath != null && evt.OldFullPath != evt.FullPath)
                                UnregisterAsset(evt.OldFullPath);
                            // 원자적 쓰기(rename)로 같은 경로가 오면 Changed와 동일하게 처리
                            if (GetGuidFromPath(evt.FullPath) != null)
                            {
                                if (_loadedAssets.ContainsKey(evt.FullPath))
                                    Reimport(evt.FullPath);
                                else
                                    _roseCache.InvalidateCache(evt.FullPath);
                            }
                            else
                            {
                                RegisterNewAsset(evt.FullPath);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    EditorDebug.LogError($"[AssetDatabase] ProcessFileChanges error: {evt.FullPath} ({evt.Type}) — {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// RoseMetadata.OnSaved 이벤트 핸들러.
        /// .rose 파일이 저장되면 해당 에셋을 자동으로 reimport 한다.
        /// _importDepth가 0보다 크면 import 중 발생한 재귀 호출이므로 무시.
        /// </summary>
        private void OnRoseMetadataSaved(string assetPath)
        {
            // 임포트 중 RegisterSpriteSubAssets/RegisterSubAssets가 .rose를 저장할 때
            // 재귀 reimport를 방지
            if (_importDepth > 0) return;

            // GUID 등록 갱신 (새 sub-asset이 추가됐을 수 있음)
            if (File.Exists(assetPath))
            {
                var meta = RoseMetadata.LoadOrCreate(assetPath);
                if (!string.IsNullOrEmpty(meta.guid))
                    _guidToPath[meta.guid] = assetPath;
                foreach (var sub in meta.subAssets)
                    _guidToPath[sub.guid] = SubAssetPath.Build(assetPath, sub.type, sub.index);
            }

            // 이미 로드된 에셋이면 reimport
            if (_loadedAssets.ContainsKey(assetPath))
            {
                if (_reimportTask != null)
                {
                    // 비동기 reimport 진행 중이면 큐에 추가
                    _pendingReimports.Enqueue(assetPath);
                }
                else
                {
                    ReimportAsync(assetPath);
                }
            }
            else
            {
                // 로드되지 않은 에셋은 캐시만 무효화하고 Project 패널 갱신
                _roseCache.InvalidateCache(assetPath);
                ProjectDirty = true;
            }
        }

        /// <summary>
        /// 큐에 쌓인 pending reimport를 하나씩 처리. ProcessReimport 완료 후 호출.
        /// </summary>
        public void ProcessPendingReimports()
        {
            if (_reimportTask != null) return;
            if (_pendingReimports.Count == 0) return;
            var next = _pendingReimports.Dequeue();
            if (_loadedAssets.ContainsKey(next))
                ReimportAsync(next);
            else
            {
                _roseCache.InvalidateCache(next);
                ProjectDirty = true;
            }
        }

        private void RegisterNewAsset(string fullPath)
        {
            if (!File.Exists(fullPath)) return;
            var ext = Path.GetExtension(fullPath);
            if (!SupportedExtensions.Contains(ext)) return;

            // Already registered?
            if (GetGuidFromPath(fullPath) != null) return;

            var meta = RoseMetadata.LoadOrCreate(fullPath);
            if (!string.IsNullOrEmpty(meta.guid))
            {
                _guidToPath[meta.guid] = fullPath;
                foreach (var sub in meta.subAssets)
                {
                    var subPath = SubAssetPath.Build(fullPath, sub.type, sub.index);
                    _guidToPath[sub.guid] = subPath;
                }
                ProjectDirty = true;
                if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] New asset detected: {fullPath}");
            }
        }

        private void UnregisterAsset(string fullPath)
        {
            var guid = GetGuidFromPath(fullPath);
            if (guid == null) return;

            // Prefab dependency graph 정리
            if (fullPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                RemovePrefabDependencies(guid);

            // Unload from memory + sub-assets
            if (_loadedAssets.TryGetValue(fullPath, out var asset))
            {
                if (asset is MeshImportResult result)
                    RemoveSubAssetCaches(fullPath, result);
                if (asset is IDisposable disposable)
                    disposable.Dispose();
                _loadedAssets.Remove(fullPath);
            }

            // Remove sub-asset GUIDs from map (.rose가 있을 때만 로드, 없으면 생성하지 않음)
            var rosePath = fullPath + ".rose";
            if (File.Exists(rosePath))
            {
                var meta = RoseMetadata.LoadOrCreate(fullPath);
                foreach (var sub in meta.subAssets)
                    _guidToPath.Remove(sub.guid);

                // 에셋 파일이 이미 없으면 고아 .rose 삭제
                if (!File.Exists(fullPath))
                    File.Delete(rosePath);
            }

            // Remove main GUID
            _guidToPath.Remove(guid);

            _roseCache.InvalidateCache(fullPath);

            ProjectDirty = true;
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Asset removed: {fullPath}");
        }

        // ─── Rename ─────────────────────────────────────────────

        /// <summary>
        /// 에셋 파일과 .rose 사이드카 파일을 함께 이름 변경한다.
        /// GUID는 그대로 보존되며, 내부 캐시 경로만 갱신된다.
        /// </summary>
        public void RenameAsset(string oldPath, string newPath)
        {
            if (!File.Exists(oldPath)) return;
            if (oldPath == newPath) return;
            if (File.Exists(newPath)) return;

            // 1. 물리 파일 이름 변경
            File.Move(oldPath, newPath);

            // 2. .rose 사이드카 이름 변경
            var oldRose = oldPath + ".rose";
            var newRose = newPath + ".rose";
            if (File.Exists(oldRose))
                File.Move(oldRose, newRose);

            // 3. _guidToPath 갱신 (메인 에셋 + 서브에셋)
            var guidUpdates = new List<(string guid, string path)>();
            foreach (var kvp in _guidToPath)
            {
                if (kvp.Value == oldPath)
                    guidUpdates.Add((kvp.Key, newPath));
                else if (kvp.Value.StartsWith(oldPath + "#"))
                    guidUpdates.Add((kvp.Key, newPath + kvp.Value.Substring(oldPath.Length)));
            }
            foreach (var (g, p) in guidUpdates)
                _guidToPath[g] = p;

            // 4. _loadedAssets 캐시 키 갱신 (메인 + 서브에셋 + MipMesh)
            var assetUpdates = new List<(string oldKey, string newKey, object val)>();
            foreach (var kvp in _loadedAssets)
            {
                if (kvp.Key == oldPath)
                    assetUpdates.Add((kvp.Key, newPath, kvp.Value));
                else if (kvp.Key.StartsWith(oldPath + "#"))
                    assetUpdates.Add((kvp.Key, newPath + kvp.Key.Substring(oldPath.Length), kvp.Value));
            }
            foreach (var (ok, nk, v) in assetUpdates)
            {
                _loadedAssets.Remove(ok);
                _loadedAssets[nk] = v;
            }

            // 5. 디스크 캐시 무효화
            _roseCache.InvalidateCache(oldPath);
            _failedImports.Remove(oldPath);

            ProjectDirty = true;
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Asset renamed: {oldPath} → {newPath}");
        }

        /// <summary>
        /// 폴더 이름 변경 후 내부 경로 매핑을 일괄 갱신한다.
        /// 물리 파일 이동(Directory.Move)은 호출자가 이미 수행한 상태여야 한다.
        /// </summary>
        public void RenameFolderPaths(string oldAbsFolderPath, string newAbsFolderPath)
        {
            // Ensure trailing separator for prefix matching
            if (!oldAbsFolderPath.EndsWith('/') && !oldAbsFolderPath.EndsWith('\\'))
                oldAbsFolderPath += Path.DirectorySeparatorChar;
            if (!newAbsFolderPath.EndsWith('/') && !newAbsFolderPath.EndsWith('\\'))
                newAbsFolderPath += Path.DirectorySeparatorChar;

            // 1. _guidToPath 갱신 (메인 에셋 + 서브에셋)
            var guidUpdates = new List<(string guid, string newPath)>();
            foreach (var kvp in _guidToPath)
            {
                if (kvp.Value.StartsWith(oldAbsFolderPath, StringComparison.Ordinal))
                    guidUpdates.Add((kvp.Key, newAbsFolderPath + kvp.Value.Substring(oldAbsFolderPath.Length)));
            }
            foreach (var (g, p) in guidUpdates)
                _guidToPath[g] = p;

            // 2. _loadedAssets 캐시 키 갱신
            var assetUpdates = new List<(string oldKey, string newKey, object val)>();
            foreach (var kvp in _loadedAssets)
            {
                if (kvp.Key.StartsWith(oldAbsFolderPath, StringComparison.Ordinal))
                    assetUpdates.Add((kvp.Key, newAbsFolderPath + kvp.Key.Substring(oldAbsFolderPath.Length), kvp.Value));
            }
            foreach (var (ok, nk, v) in assetUpdates)
            {
                _loadedAssets.Remove(ok);
                _loadedAssets[nk] = v;
            }

            // 3. _failedImports 갱신
            var failedUpdates = _failedImports.Where(p => p.StartsWith(oldAbsFolderPath, StringComparison.Ordinal)).ToList();
            foreach (var old in failedUpdates)
            {
                _failedImports.Remove(old);
                _failedImports.Add(newAbsFolderPath + old.Substring(oldAbsFolderPath.Length));
            }

            ProjectDirty = true;
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Folder paths renamed: {oldAbsFolderPath} → {newAbsFolderPath} ({guidUpdates.Count} entries updated)");
        }

        public void StopWatching()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        // ─── Internal helpers ───────────────────────────────────

        /// <summary>부모 파일이 아직 로드되지 않았으면 로드 트리거.</summary>
        private void EnsureParentLoaded(string filePath)
        {
            if (_loadedAssets.ContainsKey(filePath)) return;
            // Load<object>로 호출하면 전체 import + RegisterSubAssets + CacheSubAssets 실행
            Load<object>(filePath);
        }

        private static T? ExtractFromResult<T>(object? asset) where T : class
        {
            if (asset is MeshImportResult result)
            {
                if (typeof(T) == typeof(Mesh)) return result.Mesh as T;
                if (typeof(T) == typeof(Material)) return result.Materials.FirstOrDefault() as T;
                if (typeof(T) == typeof(MipMesh)) return result.MipMesh as T;
            }
            if (asset is SpriteImportResult spriteResult)
            {
                if (typeof(T) == typeof(Sprite))
                    return spriteResult.Sprites.Length > 0 ? spriteResult.Sprites[0] as T : null;
                if (typeof(T) == typeof(Texture2D))
                    return spriteResult.Texture as T;
            }
            return asset as T;
        }

        private static string GetImporterType(RoseMetadata meta)
        {
            return meta.importer.TryGetValue("type", out var typeVal)
                ? typeVal?.ToString() ?? "" : "";
        }

        private static void ReplaceMeshInScene(MeshImportResult newResult)
        {
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                var filter = renderer.gameObject.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;

                for (int i = 0; i < newResult.Meshes.Length; i++)
                {
                    if (filter.mesh.name == newResult.Meshes[i].Name)
                    {
                        filter.mesh = newResult.Meshes[i].Mesh;
                        var matIdx = newResult.Meshes[i].MaterialIndex;
                        if (matIdx >= 0 && matIdx < newResult.Materials.Length)
                            renderer.material = newResult.Materials[matIdx];

                        var mipFilter = renderer.gameObject.GetComponent<MipMeshFilter>();
                        if (mipFilter != null && i < newResult.MipMeshes.Length)
                            mipFilter.mipMesh = newResult.MipMeshes[i];
                        break;
                    }
                }
            }
        }

        private static void ReplaceTextureInScene(Texture2D newTex, Texture2D? oldTex)
        {
            var texName = newTex.name;

            foreach (var renderer in MeshRenderer._allRenderers)
            {
                var mat = renderer.material;
                if (mat == null) continue;

                if (oldTex != null)
                {
                    if (ReferenceEquals(mat.mainTexture, oldTex))
                        mat.mainTexture = newTex;
                    if (ReferenceEquals(mat.normalMap, oldTex))
                        mat.normalMap = newTex;
                    if (ReferenceEquals(mat.MROMap, oldTex))
                        mat.MROMap = newTex;
                }
                else
                {
                    if (mat.mainTexture != null && mat.mainTexture.name == texName)
                        mat.mainTexture = newTex;
                    if (mat.normalMap != null && mat.normalMap.name == texName)
                        mat.normalMap = newTex;
                    if (mat.MROMap != null && mat.MROMap.name == texName)
                        mat.MROMap = newTex;
                }
            }

            foreach (var sr in SpriteRenderer._allSpriteRenderers)
            {
                if (sr.sprite == null) continue;
                var spriteTex = sr.sprite.texture;

                bool match = oldTex != null
                    ? ReferenceEquals(spriteTex, oldTex)
                    : spriteTex.name == texName;

                if (match)
                {
                    sr.sprite.ReplaceTexture(newTex);
                    sr._cachedMesh = null;
                }
            }
        }

        /// <summary>
        /// Reimport로 새 SpriteImportResult가 생성되었을 때,
        /// 씬의 모든 컴포넌트가 참조하는 이전 Sprite를 새 Sprite로 교체한다.
        /// guid 기반 매칭으로 정확한 Sprite 인스턴스를 찾아 교체하며,
        /// PPU, border, rect, UV 등 모든 속성이 새 값으로 반영된다.
        /// </summary>
        private static void ReplaceSpriteInScene(SpriteImportResult? oldResult, SpriteImportResult newResult)
        {
            if (oldResult == null || oldResult.Sprites.Length == 0) return;

            // Build old guid → new sprite mapping
            var guidToNewSprite = new Dictionary<string, Sprite>();
            foreach (var newSprite in newResult.Sprites)
            {
                if (!string.IsNullOrEmpty(newSprite.guid))
                    guidToNewSprite[newSprite.guid] = newSprite;
            }

            // Also build old instance → new sprite mapping for direct reference matching
            var oldToNew = new Dictionary<Sprite, Sprite>();
            foreach (var oldSprite in oldResult.Sprites)
            {
                if (!string.IsNullOrEmpty(oldSprite.guid) && guidToNewSprite.TryGetValue(oldSprite.guid, out var mapped))
                    oldToNew[oldSprite] = mapped;
            }

            if (oldToNew.Count == 0) return;

            // Replace in SpriteRenderers
            foreach (var sr in SpriteRenderer._allSpriteRenderers)
            {
                if (sr.sprite != null && oldToNew.TryGetValue(sr.sprite, out var newSprite))
                {
                    sr.sprite = newSprite;
                    sr._cachedMesh = null;
                }
            }

            // Replace in UIImage / UIPanel via scene traversal
            foreach (var go in SceneManager.AllGameObjects)
            {
                foreach (var comp in go.InternalComponents)
                {
                    if (comp is UIImage uiImage && uiImage.sprite != null
                        && oldToNew.TryGetValue(uiImage.sprite, out var newImgSprite))
                    {
                        uiImage.sprite = newImgSprite;
                    }
                    else if (comp is UIPanel uiPanel && uiPanel.sprite != null
                        && oldToNew.TryGetValue(uiPanel.sprite, out var newPanelSprite))
                    {
                        uiPanel.sprite = newPanelSprite;
                    }
                }
            }
        }

        private static void ReplaceMaterialInScene(Material newMat, Material? oldMat)
        {
            if (oldMat == null) return;
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (ReferenceEquals(renderer.material, oldMat))
                    renderer.material = newMat;
            }
        }

        private static void ReplaceFontInScene(Font newFont)
        {
            var fontName = newFont.name;
            foreach (var tr in TextRenderer._allTextRenderers)
            {
                if (tr.font?.name == fontName)
                {
                    tr.font = newFont;
                    tr._cachedMesh = null;
                }
            }
            foreach (var ut in UIText._allUITexts)
            {
                if (ut.font?.name == fontName)
                    ut.font = newFont;
            }
            foreach (var uif in UIInputField._allUIInputFields)
            {
                if (uif.font?.name == fontName)
                    uif.font = newFont;
            }
        }

        private static void DisposeIfNotDefault(Texture2D? tex)
        {
            if (tex != null && tex != Texture2D.DefaultNormal && tex != Texture2D.DefaultMRO)
                tex.Dispose();
        }

        private MeshImportResult? ImportMesh(string path, RoseMetadata meta)
        {
            float scale = 1.0f;
            bool generateNormals = true;
            bool flipUVs = true;
            bool triangulate = true;

            if (meta.importer.TryGetValue("scale", out var scaleVal))
                scale = Convert.ToSingle(scaleVal);
            if (meta.importer.TryGetValue("generate_normals", out var gnVal) && gnVal is bool gn)
                generateNormals = gn;
            if (meta.importer.TryGetValue("flip_uvs", out var fuVal) && fuVal is bool fu)
                flipUVs = fu;
            if (meta.importer.TryGetValue("triangulate", out var triVal) && triVal is bool tri)
                triangulate = tri;

            // .glb/.gltf → SharpGLTF (AssimpNet 4.1.0 번들이 glTF2 미지원)
            // glTF 2.0은 UV 원점이 좌상단(top-left)으로 Vulkan/Veldrid와 동일 → flip 불필요.
            // OBJ/FBX는 좌하단(bottom-left) 원점이므로 Assimp FlipUVs가 필요.
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var result = (ext is ".glb" or ".gltf")
                ? _gltfMeshImporter.Import(path, scale, generateNormals, false, triangulate)
                : _meshImporter.Import(path, scale, generateNormals, flipUVs, triangulate);

            // MipMesh LOD 생성 (메시별)
            if (result?.Meshes.Length > 0)
            {
                bool generateMipMesh = false;
                int minTriangles = 500;
                float targetError = 0.02f;
                float reduction = 0.1f;

                bool hasKey = meta.importer.TryGetValue("generate_mipmesh", out var mmVal);
                if (VerboseLogging) EditorDebug.Log($"[MipMesh] '{path}' — generate_mipmesh key found={hasKey}, rawValue={mmVal} (type={mmVal?.GetType().Name ?? "null"})");
                if (hasKey && mmVal is bool mm)
                    generateMipMesh = mm;
                else if (hasKey)
                    EditorDebug.LogWarning($"[MipMesh] generate_mipmesh value is not bool — attempting Convert.ToBoolean");
                if (hasKey && !generateMipMesh && mmVal != null)
                {
                    try { generateMipMesh = Convert.ToBoolean(mmVal); }
                    catch { /* ignore */ }
                }

                if (meta.importer.TryGetValue("mipmesh_min_triangles", out var mtVal))
                    minTriangles = Convert.ToInt32(mtVal);
                if (meta.importer.TryGetValue("mipmesh_target_error", out var teVal))
                    targetError = Convert.ToSingle(teVal);
                if (meta.importer.TryGetValue("mipmesh_reduction", out var redVal))
                    reduction = Convert.ToSingle(redVal);

                if (VerboseLogging) EditorDebug.Log($"[MipMesh] '{path}' — generateMipMesh={generateMipMesh}, meshCount={result.Meshes.Length}, minTri={minTriangles}, targetError={targetError}, reduction={reduction}");

                if (generateMipMesh)
                {
                    for (int i = 0; i < result.Meshes.Length; i++)
                    {
                        var mesh = result.Meshes[i].Mesh;
                        int triCount = mesh.indices.Length / 3;
                        if (VerboseLogging) EditorDebug.Log($"[MipMesh] Generating LOD for mesh[{i}] '{mesh.name}' ({mesh.vertices.Length} verts, {triCount} tris)");
                        var mip = MipMeshGenerator.Generate(mesh, minTriangles, targetError, reduction);
                        result.Meshes[i].Mesh = mip.lodMeshes[0];
                        result.MipMeshes[i] = mip;
                        if (VerboseLogging) EditorDebug.Log($"[MipMesh] mesh[{i}] '{mesh.name}' → {mip.LodCount} LODs generated");
                    }
                    if (VerboseLogging) EditorDebug.Log($"[MipMesh] Generated LODs for {result.Meshes.Length} meshes in {path}");
                }
                else
                {
                    EditorDebug.LogWarning($"[MipMesh] LOD generation SKIPPED for '{path}' — generate_mipmesh={generateMipMesh}");
                }
            }
            else
            {
                EditorDebug.LogWarning($"[MipMesh] No meshes found in import result for '{path}' — skipping LOD generation");
            }

            return result;
        }

        private GameObject? ImportPrefab(string path)
        {
            _prefabImporter ??= new PrefabImporter(this);
            var result = _prefabImporter.LoadPrefab(path);

            // Dependency graph 업데이트
            var parentGuid = GetGuidFromPath(path);
            if (!string.IsNullOrEmpty(parentGuid))
                UpdatePrefabDependencies(parentGuid!, path);

            return result;
        }

        /// <summary>새로 생성된 .prefab 파일을 GUID 맵에 등록.</summary>
        public void RegisterPrefabAsset(string fullPath)
        {
            if (GetGuidFromPath(fullPath) != null) return; // 이미 등록됨

            var meta = RoseMetadata.LoadOrCreate(fullPath);
            if (!string.IsNullOrEmpty(meta.guid))
            {
                _guidToPath[meta.guid] = fullPath;
                ProjectDirty = true;
                if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Registered prefab: {fullPath} (guid={meta.guid})");
            }
        }
    }
}
