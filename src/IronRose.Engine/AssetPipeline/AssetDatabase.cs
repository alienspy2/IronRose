// ------------------------------------------------------------
// @file    AssetDatabase.cs
// @brief   프로젝트 에셋의 GUID 매핑, 로딩, 임포트, 캐싱을 관리하는 중앙 에셋 데이터베이스.
//          ScanAssets()로 프로젝트 디렉토리를 스캔하고, Load/LoadByGuid로 에셋을 로드한다.
// @deps    IronRose.Engine/EngineCore, IronRose.Engine/ProjectContext, IronRose.Engine.Editor/EditorDebug,
//          RoseMetadata, RoseCache, SubAssetPath,
//          MeshImporter, GltfMeshImporter, TextureImporter, FontImporter, MaterialImporter
// @exports
//   class AssetDatabase : IAssetDatabase
//     ScanAssets(string): void              — 프로젝트 에셋 스캔 및 GUID 등록 (전체 DB Clear 후 재구축)
//     ScanAssetsSubtree(string): int        — 지정 서브트리만 증분 스캔 (Clear 없이 _guidToPath에 추가/갱신, touched 수 반환)
//     GetPathFromGuid(string): string?      — GUID → 파일 경로
//     GetGuidFromPath(string): string?      — 파일 경로 → GUID
//     Load<T>(string): T?                   — 경로로 에셋 로드
//     LoadByGuid<T>(string): T?             — GUID로 에셋 로드
//     Reimport(string): void                — 에셋 재임포트
//     ProcessMetadataSavedQueue(): void     — 메인에서 매 프레임 호출. FSW/백그라운드에서 enqueue된 .rose 저장 이벤트 일괄 처리
//     AssetCount: int                       — 등록된 에셋 수
//     ProjectDirty: bool                    — 프로젝트 변경 여부
// @note    ScanAssets 루프에서 100개 파일마다 EngineCore.PumpWindowEvents()를 호출하여
//          OS "응답 없음" 방지. FileSystemWatcher로 런타임 에셋 변경 감지.
//          파일 쓰기 도중 Changed 이벤트가 즉시 전달되면 PNG CRC 오류 등으로 Import가 실패할 수 있어,
//          ProcessFileChanges는 FileChangeDebounceMs(150ms) 만큼 "마지막 Changed로부터 조용한" 뒤
//          실제 Reimport를 실행한다. Reimport는 try/finally로 _importDepth를 복원하고, 실패 시
//          oldAsset을 _loadedAssets에 되돌려 씬 참조가 dangling되지 않고 후속 Reimport가 가능하게 한다.
//          sync Reimport 성공 경로와 async ProcessReimport 성공 경로는 둘 다
//          ProjectDirty = true; ReimportVersion++ 로 종료되어 Inspector preview가 최신 상태를 반영한다.
//          (Phase B-1/B-2/B-3) _loadedAssets는 ConcurrentDictionary로 전환되어 백그라운드 Read
//          경합으로부터 안전해졌고, 순회는 모두 .ToArray() 스냅샷을 사용한다. 비동기 reimport는
//          개별 _reimport* 필드 대신 단일 Task<ReimportResult>로 통합되어 Task 완료 fence 위에서만
//          관측된다. Task 람다는 _loadedAssets/_guidToPath/_spriteToGuid/GPU 리소스를 건드리지 않는
//          순수 CPU 디코드만 수행하고, RegisterSubAssets/RegisterSpriteSubAssets/StoreCacheOrDefer는
//          메인 ProcessReimport 경로에서 호출된다.
//          (Phase B-4) FSW 디바운스 큐(_pendingChanges)는 Dictionary<path, AssetChangeEvent> dedup
//          맵 기반이며, EnqueueChange가 같은 path를 덮어써 "최신 타임스탬프"만 유지한다.
//          ProcessFileChanges는 동일 lock 블록 안에서 debounce 판정과 맵 제거를 동시에 수행하여
//          re-enqueue race를 제거한다.
//          (Phase B-5 / H4) OnRoseMetadataSaved는 FSW 스레드에서도 호출되므로 Reimport를 직접
//          호출하지 않고 _metadataSavedQueue(ConcurrentQueue)에 enqueue만 수행한다.
//          실제 GUID 등록/Reimport 트리거는 메인 ProcessMetadataSavedQueue()에서 일괄 처리된다.
// ------------------------------------------------------------
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
        // 백그라운드 Task(ReimportAsync 람다)가 RegisterSubAssets/RegisterSpriteSubAssets 경로를
        // 거쳐 _loadedAssets를 쓰던 경로는 B-3에서 메인으로 이동하지만, 과도기 안전 + 미래 회귀 방지를
        // 위해 ConcurrentDictionary로 유지한다. 순회 지점은 ToArray() 스냅샷으로 보호한다.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _loadedAssets = new();
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
        // dedup 맵: path → 최신 AssetChangeEvent. FSW 콜백은 락 안에서 키를 덮어쓰므로
        // 같은 파일의 중복 이벤트가 큐에 누적되지 않고 "최신 타임스탬프"만 보관된다.
        // ProcessFileChanges는 락 안에서 debounce 통과 항목만 뽑아내고 나머지는 맵에 남겨둔다.
        private readonly Dictionary<string, AssetChangeEvent> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
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
        // (Phase B-5 / H4) FSW/백그라운드 스레드에서 RoseMetadata.OnSaved가 들어올 때 즉시
        // Reimport를 호출하지 않고 이 큐에 쌓아, 메인 ProcessMetadataSavedQueue()에서 일괄 처리한다.
        // ConcurrentQueue로 FSW 콜백 스레드에서 enqueue, 메인에서 dequeue 한다.
        private readonly ConcurrentQueue<string> _metadataSavedQueue = new();
        public bool ProjectDirty { get; set; }
        /// <summary>Reimport가 완료될 때마다 증가. UI가 프리뷰 갱신 여부를 판단하는 데 사용.</summary>
        public int ReimportVersion { get; private set; }

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
            /// <summary>이벤트 수신 시각 (UTC tick). 파일 쓰기 완료 대기용 debounce에 사용.</summary>
            public long TimestampTicks;
        }

        /// <summary>
        /// 외부 프로세스가 에셋 파일을 쓰는 중 FileSystemWatcher가 Changed 이벤트를 즉시 전달하므로,
        /// 해당 이벤트 수신 후 이 시간(ms)만큼 추가 Changed 이벤트가 없어야 실제 reimport를 실행한다.
        /// 그동안 도착하는 Changed 이벤트는 타임스탬프를 리셋하여 대기 시간을 연장.
        /// PNG CRC 오류 등 "쓰기 중간 읽기"를 방지.
        /// </summary>
        private const int FileChangeDebounceMs = 150;

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

            int fileCount = 0;
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

                    if (++fileCount % 100 == 0)
                        EngineCore.PumpWindowEvents();
                }
            }

            EditorDebug.Log($"[AssetDatabase] Scanned {_guidToPath.Count} assets in {projectPath}");

            // Subscribe to .rose save events for automatic reimport
            RoseMetadata.OnSaved -= OnRoseMetadataSaved;
            RoseMetadata.OnSaved += OnRoseMetadataSaved;

            StartWatching(projectPath);
        }

        public int ScanAssetsSubtree(string absoluteSubPath)
        {
            if (!Directory.Exists(absoluteSubPath))
            {
                EditorDebug.LogWarning($"[AssetDatabase] ScanAssetsSubtree: directory not found: {absoluteSubPath}");
                return 0;
            }

            int touched = 0;
            int fileCount = 0;
            foreach (var ext in SupportedExtensions)
            {
                var files = Directory.GetFiles(absoluteSubPath, $"*{ext}", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (Path.GetFileName(file).StartsWith('.')) continue;
                    var meta = RoseMetadata.LoadOrCreate(file);
                    if (!string.IsNullOrEmpty(meta.guid))
                    {
                        _guidToPath[meta.guid] = file;
                        touched++;

                        foreach (var sub in meta.subAssets)
                        {
                            var subPath = SubAssetPath.Build(file, sub.type, sub.index);
                            _guidToPath[sub.guid] = subPath;
                            touched++;
                        }
                    }

                    if (++fileCount % 100 == 0)
                        EngineCore.PumpWindowEvents();
                }
            }

            EditorDebug.Log($"[AssetDatabase] Subtree scan: {touched} entries touched in {absoluteSubPath}");
            return touched;
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
            // 로드된 에셋에서 역검색. ConcurrentDictionary는 순회 중 수정에 약하므로 스냅샷.
            foreach (var (path, asset) in _loadedAssets.ToArray())
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
            foreach (var (path, asset) in _loadedAssets.ToArray())
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
            foreach (var kvp in _loadedAssets.ToArray())
            {
                if (kvp.Value is not GameObject go) continue;
                if (HasScriptComponent(go))
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _loadedAssets.TryRemove(key, out _);

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

            // ─── _importDepth 누수 방지 ───
            // _importDepth++ 직후부터 finally까지 try 범위를 잡아서,
            // 선행 IO(LoadOrCreate 등) 또는 캐시 정리 단계에서 IO 예외가 발생해도
            // _importDepth--가 반드시 실행되도록 보장한다. (외부 프로세스가 .rose/.meta/
            // 에셋 파일을 쓰는 중일 때 CRC/IO Exception이 터져도 depth가 누수되지 않는다.)
            _importDepth++;
            bool reimportSucceeded = false;
            object? oldAsset = null;
            string importerType = string.Empty;
            try
            {
                _failedImports.Remove(path);
                var meta = RoseMetadata.LoadOrCreate(path);
                importerType = GetImporterType(meta);

                // 1. 기존 에셋 스냅샷 (Dispose하지 않음 — 씬 참조 교체 후 처리)
                //    try 내부 최상단에서 스냅샷을 뜨고 Remove한다. 이후 어느 단계에서
                //    예외가 나도 catch에서 oldAsset으로 복원 가능하다.
                if (_loadedAssets.TryGetValue(path, out var cur))
                    oldAsset = cur;
                _loadedAssets.TryRemove(path, out _);

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
                // 각 importer case는 성공 시 명시적으로 reimportSucceeded = true를 세팅한다.
                // (_loadedAssets.ContainsKey 같은 암묵적 판정은 TextAssetImporter처럼
                // 기존 인스턴스만 갱신하고 _loadedAssets에 다시 넣지 않는 case에서 false negative가 난다.)
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
                            reimportSucceeded = true;
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
                            reimportSucceeded = true;
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
                            reimportSucceeded = true;
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
                            reimportSucceeded = true;
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
                            reimportSucceeded = true;
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

                            // Volume의 stale profile 참조 갱신 (D-II 범위 — 임시 .ToArray())
                            foreach (var vol in PostProcessVolume._allVolumes.ToArray())
                            {
                                if (vol.profileGuid == meta.guid)
                                    vol.profile = newPp;
                            }
                            reimportSucceeded = true;
                        }
                        break;
                    }
                    case "TextAssetImporter":
                    {
                        var newTa = _textAssetImporter.Import(path, meta);
                        if (newTa != null)
                        {
                            // 기존 인스턴스가 있으면 내용만 갱신 (씬 참조 유지).
                            // 이 경로에서는 _loadedAssets에 새로 넣지 않는 대신,
                            // 앞서 Remove된 oldTa를 다시 세팅해 lookup miss를 없앤다.
                            if (oldAsset is TextAsset oldTa)
                            {
                                oldTa.text = newTa.text;
                                oldTa.bytes = newTa.bytes;
                                _loadedAssets[path] = oldTa;
                            }
                            else
                            {
                                _loadedAssets[path] = newTa;
                                if (!string.IsNullOrEmpty(meta.guid))
                                    _textAssetToGuid[newTa] = meta.guid;
                            }
                            reimportSucceeded = true;
                        }
                        break;
                    }
                    case "AnimationClipImporter":
                    {
                        // 이전 AnimationClip의 역참조맵 제거
                        if (oldAsset is AnimationClip oldClip)
                            _animClipToGuid.Remove(oldClip);

                        var newClip = _animClipImporter.Import(path, meta);
                        if (newClip != null)
                        {
                            _loadedAssets[path] = newClip;
                            if (!string.IsNullOrEmpty(meta.guid))
                                _animClipToGuid[newClip] = meta.guid;
                            reimportSucceeded = true;
                        }
                        break;
                    }
                    case "PrefabImporter":
                    {
                        // Dependency graph 기반으로 수정된 프리팹과 이를 참조하는 부모들만 캐스케이드 무효화
                        var prefabGuid = GetGuidFromPath(path);
                        if (!string.IsNullOrEmpty(prefabGuid))
                            InvalidatePrefabAndDependents(prefabGuid!);
                        // PrefabImporter는 _loadedAssets에 넣지 않는다. 무효화가 수행되면 성공.
                        reimportSucceeded = true;
                        break;
                    }
                }

                // 4. 이전 에셋 GPU 리소스 정리 (텍스처 공유로 인한 이중 Dispose 방지)
                // 실패한 경우 oldAsset을 복원해야 하므로 정리하지 않는다.
                if (reimportSucceeded)
                {
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
                }
            }
            catch (Exception ex)
            {
                // 임포트 실패: 이미지 쓰기 중간 상태를 읽어 CRC Error가 발생하는 경우,
                // LoadOrCreate에서 IO 예외가 발생하는 경우 등. oldAsset이 있다면
                // _loadedAssets에 복원하여 후속 파일 변경/Inspector Apply 경로에서
                // 정상적으로 다시 Reimport가 트리거되도록 한다.
                EditorDebug.LogError($"[AssetDatabase] Reimport failed: {path} — {ex.Message}");
            }
            finally
            {
                // 조용한 실패(importer가 예외 없이 null 반환) 또는 catch 경로에서도
                // oldAsset이 있으면 _loadedAssets를 복원. 성공 경로에는 영향이 없다.
                if (!reimportSucceeded && oldAsset != null && !_loadedAssets.ContainsKey(path))
                {
                    _loadedAssets[path] = oldAsset;
                    // sub-asset 캐시를 다시 채워서 씬의 sub-asset 참조도 일관성 유지
                    try
                    {
                        if (oldAsset is MeshImportResult oldMesh)
                        {
                            var meta2 = RoseMetadata.LoadOrCreate(path);
                            CacheSubAssets(path, oldMesh, meta2);
                        }
                        else if (oldAsset is SpriteImportResult oldSpr)
                        {
                            var meta2 = RoseMetadata.LoadOrCreate(path);
                            CacheSpriteSubAssets(path, oldSpr, meta2);
                        }
                    }
                    catch (Exception restoreEx)
                    {
                        EditorDebug.LogError($"[AssetDatabase] Reimport restore failed: {path} — {restoreEx.Message}");
                    }
                }

                ProjectDirty = true;
                if (reimportSucceeded) ReimportVersion++;
                _importDepth--;
                if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Reimport finished: {path} (success={reimportSucceeded})");
            }
        }

        // ─── Async Reimport (비동기 재임포트 + 진행 UI) ──────────────

        // 비동기 reimport가 진행 중일 때만 non-null. Task 완료 후 메인 ProcessReimport가
        // result를 꺼내 처리하고 null로 복구한다. 개별 _reimport* 필드들은 Task 완료 fence에
        // 의존하는 happens-before가 불명확하여, 단일 Task<ReimportResult>로 통합한다.
        private System.Threading.Tasks.Task<ReimportResult>? _reimportTask;

        /// <summary>리임포트 진행 중 여부 (EngineCore에서 오버레이 표시용)</summary>
        // 진행 중 상태를 Task.AsyncState(= ReimportPrototype)에서 조회.
        // _reimportTask는 Task.Factory.StartNew 호출 직후 할당하고, Task factory delegate가
        // result 객체 전체를 반환하지만, 진행 중에도 path/timer를 UI에 노출하기 위해 경량
        // prototype을 AsyncState로 전달한다.
        public bool IsReimporting => _reimportTask != null;
        public string? ReimportAssetName =>
            (_reimportTask?.AsyncState as ReimportPrototype)?.Path is string p ? Path.GetFileName(p) : null;
        public double ReimportElapsed =>
            (_reimportTask?.AsyncState as ReimportPrototype)?.Timer.Elapsed.TotalSeconds ?? 0;

        /// <summary>Task.Factory.StartNew에 AsyncState로 전달하는 진행 중 정보. Task 완료 전까지 UI에서 참조한다.</summary>
        private sealed record ReimportPrototype(string Path, System.Diagnostics.Stopwatch Timer);

        /// <summary>
        /// ReimportAsync의 백그라운드 Task가 반환하는 불변 결과 객체.
        /// Task 완료 fence를 통해 모든 필드가 메인 스레드에서 happens-before로 관측된다.
        /// 개별 _reimport* 필드를 대체한다.
        /// </summary>
        private sealed record ReimportResult(
            string Path,
            string ImporterType,
            RoseMetadata Meta,
            object? OldAsset,
            System.Diagnostics.Stopwatch Timer,
            // 백그라운드 계산 산출물 (importerType에 따라 일부만 non-null)
            MeshImportResult? Mesh,
            Texture2D? Tex,
            SpriteImportResult? Sprite,
            // 백그라운드에서 발생한 예외 (null이면 성공)
            Exception? Error
        );

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

            var meta = RoseMetadata.LoadOrCreate(path);
            var importerType = GetImporterType(meta);
            var timer = System.Diagnostics.Stopwatch.StartNew();

            // 1. 기존 에셋 보존 + 메모리 캐시에서 잠시 제거 (백그라운드 작업 중 누가 Load해도 stale 반환 방지)
            _loadedAssets.TryGetValue(path, out var oldAsset);
            _loadedAssets.TryRemove(path, out _);

            if (oldAsset is MeshImportResult oldMesh)
                RemoveSubAssetCaches(path, oldMesh);
            else if (oldAsset is SpriteImportResult oldSpr)
                RemoveSpriteSubAssetCaches(path, oldSpr);

            // 2. 디스크 캐시 무효화
            _roseCache.InvalidateCache(path);

            // 3. 백그라운드 임포트 시작. 람다는 _loadedAssets / _guidToPath / _spriteToGuid /
            // GPU 리소스를 건드리지 않는다 (순수 CPU 디코드만). 모든 공유 상태 쓰기는
            // ProcessReimport가 메인에서 수행한다.
            _importDepth++;
            var prototype = new ReimportPrototype(path, timer);
            _reimportTask = System.Threading.Tasks.Task.Factory.StartNew<ReimportResult>(
                state =>
                {
                    MeshImportResult? mesh = null;
                    Texture2D? tex = null;
                    SpriteImportResult? sprite = null;
                    Exception? error = null;
                    try
                    {
                        switch (importerType)
                        {
                            case "MeshImporter":
                                mesh = ImportMesh(path, meta);
                                break;
                            case "TextureImporter":
                                tex = _textureImporter.Import(path, meta);
                                if (tex != null && IsSpriteTexture(meta))
                                    sprite = BuildSpriteImportResult(tex, meta);
                                break;
                            case "FontImporter":
                                // 폰트는 가볍고 GPU 필요 — 메인에서 ProcessReimport가 동기 import.
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    return new ReimportResult(path, importerType, meta, oldAsset, timer, mesh, tex, sprite, error);
                },
                prototype,
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskCreationOptions.DenyChildAttach,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        /// <summary>
        /// 매 프레임 호출. 백그라운드 작업 완료 시 메인 스레드에서 씬 참조 교체 + GPU 정리.
        /// 완료 시 true 반환.
        /// </summary>
        public bool ProcessReimport()
        {
            var task = _reimportTask;
            if (task == null) return false;
            if (!task.IsCompleted) return false;

            // Task가 예외를 IsFaulted로 surface했더라도 (우리 람다는 try/catch로 포획하지만 방어),
            // GetAwaiter().GetResult()로 rethrow되지 않도록 Task.Result 접근을 안전하게 처리.
            ReimportResult result;
            if (task.IsFaulted)
            {
                // 람다 외부 예외 (ImportMesh/_textureImporter가 ThreadAbort 등): 복구 모드로 진행
                var ex = task.Exception?.InnerException ?? task.Exception;
                EditorDebug.LogError($"[AssetDatabase] Async reimport task faulted: {ex?.Message}");
                FinalizeReimportReset();
                return true;
            }
            else
            {
                try { result = task.Result; }
                catch (Exception ex)
                {
                    EditorDebug.LogError($"[AssetDatabase] Async reimport result retrieval failed: {ex.Message}");
                    FinalizeReimportReset();
                    return true;
                }
            }

            var path = result.Path;

            // 에러 처리: 람다 내부 예외는 result.Error로 전달됨
            if (result.Error != null)
            {
                EditorDebug.LogError($"[AssetDatabase] Async reimport failed: {result.Error.Message}");

                if (result.OldAsset != null)
                {
                    _loadedAssets[path] = result.OldAsset;
                    if (result.OldAsset is MeshImportResult oldMeshR)
                        CacheSubAssets(path, oldMeshR, result.Meta);
                    else if (result.OldAsset is SpriteImportResult oldSprR)
                        CacheSpriteSubAssets(path, oldSprR, result.Meta);
                }

                result.Timer.Stop();
                FinalizeReimportReset();
                return true;
            }

            // ─── 성공 경로: 메인 스레드에서 GPU 캐시/sub-asset 등록/씬 교체 ───
            switch (result.ImporterType)
            {
                case "MeshImporter":
                {
                    if (result.Mesh != null)
                    {
                        // B-3에서 Task 람다로부터 이동된 메인 전용 작업들
                        RegisterSubAssets(path, result.Mesh, result.Meta);
                        StoreCacheOrDefer(path, result.Mesh, result.Meta);

                        _loadedAssets[path] = result.Mesh;
                        CacheSubAssets(path, result.Mesh, result.Meta);
                        ReplaceMeshInScene(result.Mesh);
                    }
                    break;
                }
                case "TextureImporter":
                {
                    if (result.Tex != null)
                    {
                        // B-3에서 Task 람다로부터 이동된 메인 전용 작업
                        StoreCacheOrDefer(path, result.Tex, result.Meta);

                        var oldTexForReplace = result.OldAsset is SpriteImportResult oldSpr
                            ? oldSpr.Texture : result.OldAsset as Texture2D;
                        if (result.Sprite != null)
                        {
                            // B-3에서 Task 람다로부터 이동
                            RegisterSpriteSubAssets(path, result.Sprite, result.Meta);

                            _loadedAssets[path] = result.Sprite;
                            CacheSpriteSubAssets(path, result.Sprite, result.Meta);
                            ReplaceSpriteInScene(result.OldAsset as SpriteImportResult, result.Sprite);
                        }
                        else
                        {
                            _loadedAssets[path] = result.Tex;
                        }
                        ReplaceTextureInScene(result.Tex, oldTexForReplace);
                    }
                    break;
                }
                case "FontImporter":
                {
                    var newFont = _fontImporter.Import(path, result.Meta);
                    if (newFont != null)
                    {
                        _loadedAssets[path] = newFont;
                        ReplaceFontInScene(newFont);
                    }
                    break;
                }
            }

            // 이전 에셋 GPU 정리 (텍스처 공유로 인한 이중 Dispose 방지)
            if (result.OldAsset is MeshImportResult oldMeshResult)
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
            else if (result.OldAsset is SpriteImportResult oldSpriteResult)
            {
                oldSpriteResult.Texture?.Dispose();
            }
            else if (result.OldAsset is Texture2D oldTex)
            {
                oldTex.Dispose();
            }
            else if (result.OldAsset is Font oldFont)
            {
                oldFont.atlasTexture?.Dispose();
            }

            result.Timer.Stop();
            if (VerboseLogging) EditorDebug.Log($"[AssetDatabase] Async reimport complete: {path} ({result.Timer.Elapsed.TotalSeconds:F1}s)");

            FinalizeReimportReset();
            ReimportVersion++;
            return true;
        }

        /// <summary>ReimportAsync 완료 후 공통 상태 초기화. 성공/실패 경로 양쪽에서 호출.</summary>
        private void FinalizeReimportReset()
        {
            _reimportTask = null;
            _importDepth--;
            ProjectDirty = true;
        }

        public void Unload(string path)
        {
            if (_loadedAssets.TryGetValue(path, out var asset))
            {
                if (asset is MeshImportResult result)
                    RemoveSubAssetCaches(path, result);
                if (asset is IDisposable disposable)
                    disposable.Dispose();
                _loadedAssets.TryRemove(path, out _);
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
                if (path != null && _loadedAssets.TryRemove(path, out _) && VerboseLogging)
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

            foreach (var asset in _loadedAssets.Values.ToArray())
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
                _loadedAssets.TryRemove(SubAssetPath.Build(filePath, "Mesh", i), out _);
                _meshToGuid.Remove(result.Meshes[i].Mesh);
            }

            for (int i = 0; i < result.Materials.Length; i++)
            {
                _loadedAssets.TryRemove(SubAssetPath.Build(filePath, "Material", i), out _);
                _materialToGuid.Remove(result.Materials[i]);
            }

            for (int i = 0; i < result.Textures.Length; i++)
            {
                _loadedAssets.TryRemove(SubAssetPath.Build(filePath, "Texture2D", i), out _);
                _textureToGuid.Remove(result.Textures[i]);
            }

            for (int i = 0; i < result.MipMeshes.Length; i++)
                _loadedAssets.TryRemove(SubAssetPath.Build(filePath, "MipMesh", i), out _);
        }

        private void RemoveSpriteSubAssetCaches(string filePath, SpriteImportResult result)
        {
            for (int i = 0; i < result.Sprites.Length; i++)
            {
                _loadedAssets.TryRemove(SubAssetPath.Build(filePath, "Sprite", i), out _);
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
                    // Renamed는 즉시 처리 대상이지만 같은 path로 여러 이벤트가 연속될 수 있으므로
                    // 맵 upsert. 기존 Deleted가 남아 있으면 덮어쓰지 않는다 (삭제 우선 정책).
                    if (_pendingChanges.TryGetValue(e.FullPath, out var existing)
                        && existing.Type == AssetChangeType.Deleted)
                    {
                        return;
                    }
                    _pendingChanges[e.FullPath] = new AssetChangeEvent
                    {
                        Type = AssetChangeType.Renamed,
                        FullPath = e.FullPath,
                        OldFullPath = e.OldFullPath,
                        TimestampTicks = DateTime.UtcNow.Ticks,
                    };
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

                // dedup upsert: 기존 Deleted가 남아 있으면 덮어쓰지 않음 (삭제 우선).
                // 그 외에는 최신 이벤트로 교체하여 timestamp만 갱신 (debounce 연장).
                if (_pendingChanges.TryGetValue(fullPath, out var existing)
                    && existing.Type == AssetChangeType.Deleted)
                {
                    return;
                }
                _pendingChanges[fullPath] = new AssetChangeEvent
                {
                    Type = type,
                    FullPath = fullPath,
                    TimestampTicks = DateTime.UtcNow.Ticks,
                };
            }
        }

        /// <summary>
        /// 메인 스레드에서 매 프레임 호출. 파일 변경 이벤트를 처리한다.
        /// FileSystemWatcher가 파일 쓰기 도중에도 Changed 이벤트를 전달하므로,
        /// 이벤트 수신 후 일정 시간(FileChangeDebounceMs) 추가 Changed가 없어야 실제 임포트를 실행한다.
        /// Debounce 기간 내 이벤트는 큐에 되돌려 다음 프레임에 재평가한다.
        /// </summary>
        public void ProcessFileChanges()
        {
            // dedup 맵에서 debounce 통과한 항목만 추출하고 나머지는 맵에 남겨둔다.
            // 락 안에서 판정하므로 FSW 스레드가 새 이벤트를 넣어도 다음 프레임 판정에 반영된다.
            // (기존 구조는 "큐를 Clear → 락 밖에서 판정 → 락 안에 re-enqueue" 순서라,
            //  판정과 재투입 사이에 FSW가 새 이벤트를 넣으면 이전 이벤트가 큐에 두 번 나타나는
            //  race가 있었다 — 정적 분석 보고서 H2 참조.)
            List<AssetChangeEvent> ready;
            var nowTicks = DateTime.UtcNow.Ticks;
            var debounceTicks = TimeSpan.FromMilliseconds(FileChangeDebounceMs).Ticks;

            lock (_changeLock)
            {
                if (_pendingChanges.Count == 0) return;

                ready = new List<AssetChangeEvent>();
                var readyKeys = new List<string>();
                foreach (var kvp in _pendingChanges)
                {
                    var evt = kvp.Value;
                    // Deleted/Renamed는 debounce 없이 즉시 처리
                    if (evt.Type == AssetChangeType.Created || evt.Type == AssetChangeType.Changed)
                    {
                        if (nowTicks - evt.TimestampTicks < debounceTicks)
                            continue; // 맵에 남겨두고 다음 프레임 재평가
                    }
                    ready.Add(evt);
                    readyKeys.Add(kvp.Key);
                }

                // 처리 대상 항목만 맵에서 제거. 미경과 항목은 맵에 남겨 FSW가 갱신 가능.
                foreach (var k in readyKeys)
                    _pendingChanges.Remove(k);
            }

            if (ready.Count == 0) return;

            foreach (var evt in ready)
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
        /// FSW/백그라운드 스레드에서도 호출될 수 있으므로 여기서는 큐에 enqueue만 하고,
        /// 실제 처리는 메인 스레드의 ProcessMetadataSavedQueue()에서 수행한다. (Phase B-5 / H4)
        /// _importDepth 빠른 필터링만 이 핸들러에서 수행 (메인에서 변경되지만 int read는 atomic).
        /// </summary>
        private void OnRoseMetadataSaved(string assetPath)
        {
            // 임포트 중 RegisterSpriteSubAssets/RegisterSubAssets가 .rose를 저장할 때
            // 재귀 reimport를 방지. _importDepth는 메인에서만 ++/-- 하지만 int read는 atomic.
            // (완전히 정확하지 않더라도 메인 처리 경로에서 다시 체크되므로 안전.)
            if (_importDepth > 0) return;

            _metadataSavedQueue.Enqueue(assetPath);
        }

        /// <summary>
        /// 메인 스레드에서 매 프레임 호출. FSW/백그라운드에서 enqueue된 .rose 저장 이벤트를
        /// 일괄 처리한다. GUID 등록 갱신 + (로드된 에셋이면) Reimport 트리거를 수행하며,
        /// 비동기 reimport 진행 중이면 _pendingReimports 큐로 넘긴다. (Phase B-5)
        /// </summary>
        public void ProcessMetadataSavedQueue()
        {
            while (_metadataSavedQueue.TryDequeue(out var assetPath))
            {
                // 메인 진입 시점의 _importDepth를 다시 체크 (enqueue와 dequeue 사이에
                // 변경 가능성 있음). 재귀 구간 안에 들어온 이벤트는 버린다 (기존 로직과 동일).
                if (_importDepth > 0)
                    continue;

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
                _loadedAssets.TryRemove(fullPath, out _);
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
            foreach (var kvp in _loadedAssets.ToArray())
            {
                if (kvp.Key == oldPath)
                    assetUpdates.Add((kvp.Key, newPath, kvp.Value));
                else if (kvp.Key.StartsWith(oldPath + "#"))
                    assetUpdates.Add((kvp.Key, newPath + kvp.Key.Substring(oldPath.Length), kvp.Value));
            }
            foreach (var (ok, nk, v) in assetUpdates)
            {
                _loadedAssets.TryRemove(ok, out _);
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
            foreach (var kvp in _loadedAssets.ToArray())
            {
                if (kvp.Key.StartsWith(oldAbsFolderPath, StringComparison.Ordinal))
                    assetUpdates.Add((kvp.Key, newAbsFolderPath + kvp.Key.Substring(oldAbsFolderPath.Length), kvp.Value));
            }
            foreach (var (ok, nk, v) in assetUpdates)
            {
                _loadedAssets.TryRemove(ok, out _);
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
            var meshSnap = MeshRenderer._allRenderers.Snapshot();
            foreach (var renderer in meshSnap)
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

            var texMeshSnap = MeshRenderer._allRenderers.Snapshot();
            foreach (var renderer in texMeshSnap)
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

            var texSpriteSnap = SpriteRenderer._allSpriteRenderers.Snapshot();
            foreach (var sr in texSpriteSnap)
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
            var spriteSnap = SpriteRenderer._allSpriteRenderers.Snapshot();
            foreach (var sr in spriteSnap)
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
            var matSnap = MeshRenderer._allRenderers.Snapshot();
            foreach (var renderer in matSnap)
            {
                if (ReferenceEquals(renderer.material, oldMat))
                    renderer.material = newMat;
            }
        }

        private static void ReplaceFontInScene(Font newFont)
        {
            var fontName = newFont.name;
            var textSnap = TextRenderer._allTextRenderers.Snapshot();
            foreach (var tr in textSnap)
            {
                if (tr.font?.name == fontName)
                {
                    tr.font = newFont;
                    tr._cachedMesh = null;
                }
            }
            // UIText / UIInputField 는 Phase D-II 범위 — 임시 .ToArray() 스냅샷으로 방어.
            foreach (var ut in UIText._allUITexts.ToArray())
            {
                if (ut.font?.name == fontName)
                    ut.font = newFont;
            }
            foreach (var uif in UIInputField._allUIInputFields.ToArray())
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
