# Phase B: AssetDatabase 비동기 리임포트 경로 — 상세 구현 명세서

## 목표

마스터 계획 `plans/threading-safety-fix-master.md` §Phase B에 해당. 다음 5건을 AssetDatabase 경로에서 해결한다.

- **C1**: `_reimport*` 개별 필드 (`_reimportTask`, `_reimportPath`, `_reimportOldAsset`, `_reimportMeta`, `_reimportType`, `_reimportMeshResult`, `_reimportTexResult`, `_reimportSpriteResult`, `_reimportTimer`)를 단일 불변 결과 객체 `ReimportResult`로 통합. Task 반환값으로 받고 `IsFaulted` 분기 추가.
- **C2**: `_loadedAssets`를 `Dictionary<string, object>` → `ConcurrentDictionary<string, object>`로 교체. 순회 지점은 `.ToArray()` 스냅샷.
- **H2**: FileSystemWatcher 디바운스를 **dedup 맵**(`Dictionary<string, AssetChangeEvent>`) 기반으로 재설계하여 re-enqueue race 윈도우 제거.
- **H4**: `OnRoseMetadataSaved` 핸들러가 FSW/백그라운드 스레드에서 호출되어 `ReimportAsync`를 직접 트리거하던 경로를 **`ConcurrentQueue<string> _metadataSavedQueue`** 기반 메인 큐잉으로 변경.
- **H6**: `GpuTextureCompressor.CompressBC7/CompressBC5/GenerateMipmapsGPU` 및 `Initialize`/`Dispose` 진입에 `ThreadGuard.CheckMainThread(...)` 삽입. 위반 시 빈 결과 (`Array.Empty<byte>()` / `Array.Empty<byte[]>()`) 반환하여 호출 스킵.

**주의**:
- `Bc6hEncoder.Encode/Decode`는 순수 CPU 연산(`float[]` → `byte[]` / `byte[]` → `float[]`)이라 GPU 가드 불필요. **건드리지 않는다.**
- H5(`PostProcessVolume._allVolumes` iterate)는 Phase D 범위. 여기서는 `ReplaceMeshInScene`/`ReplaceTextureInScene`/`ReplaceSpriteInScene`/`ReplaceFontInScene`와 AssetDatabase.cs 내부 `_allVolumes`/`_allRenderers` 순회에 **임시 `.ToArray()` 스냅샷**만 씌운다. (Phase D에서 정식 `Snapshot()` API로 교체.)
- `RoseMetadata.OnSaved` 자체의 event add/remove lock 래핑은 Phase D(C4) 범위. Phase B에서는 **구독자 쪽(AssetDatabase) 핸들러의 동작**만 안전화.

## 선행 조건

- **Phase A 머지됨**: `src/IronRose.Contracts/ThreadGuard.cs` 존재, `RoseEngine.ThreadGuard.CaptureMainThread()`가 `EngineCore.Initialize`에서 호출됨. `RoseEngine.ThreadGuard.CheckMainThread(string)` 사용 가능.
- `IronRose.Engine.csproj`가 `IronRose.Contracts`를 참조함 (이미 확인).
- `RoseEngine` 네임스페이스 using이 이미 `AssetDatabase.cs` (line 35), `GpuTextureCompressor.cs` (line 5)에 존재.

---

## 서브 phase 구조 및 worktree 전략

| 서브 | 주제 | 의존 | Worktree 브랜치 제안 |
|------|------|------|---------------------|
| B-1 | `_loadedAssets` → `ConcurrentDictionary` + 순회 스냅샷 | (없음) | `feat/phase-b-asset-database` (B-1~B-3 통합) |
| B-2 | `ReimportResult` 도입 + `_reimport*` 개별 필드 제거 + `IsFaulted` 분기 | B-1 | 동일 worktree |
| B-3 | Task 람다에서 `RegisterSubAssets`/`RegisterSpriteSubAssets`/`StoreCacheOrDefer` 제거 → `ProcessReimport`로 이동 | B-2 | 동일 worktree |
| B-4 | FSW 디바운스 dedup 맵 재설계 | (B-1 권장, 독립) | `feat/phase-b-fsw-debounce` (별도 worktree) |
| B-5 | GPU ThreadGuard + `OnRoseMetadataSaved` 메인 큐잉 | B-1~B-3 | `feat/phase-b-gpu-and-metadata-saved` (별도 worktree) |

**권장 머지 순서**: B-1/B-2/B-3 통합 worktree 먼저 → 리뷰 PASS 후 main 머지 → B-4 worktree(이 때 `_pendingChanges` 변경) → B-5 worktree(이 때 ReimportAsync 호출 경로가 변경된 상태에서 GPU 가드 추가).

**B-4를 분리하는 이유**: FSW 디바운스는 `_pendingChanges` 큐 자료구조 자체를 바꾸므로 B-1~B-3와 충돌 가능성이 있어 직렬 진행이 안전.

**B-5를 분리하는 이유**: `OnRoseMetadataSaved` 동작 변경은 `_pendingReimports` 기존 큐를 사용하는 경로(`ProcessPendingReimports`)와 연동되고, GPU 가드는 별도 파일 수정(`GpuTextureCompressor.cs`)이라 독립성 높음.

---

## 사전 조사 결과 요약 (aca-coder가 파일을 다시 읽지 않아도 되도록)

### AssetDatabase.cs 전체 구조 (관련 라인)

- `_loadedAssets` 정의: line 43 — `private readonly Dictionary<string, object> _loadedAssets = new();`
- `_loadedAssets` 모든 접근 지점: **line 260, 266, 271, 353, 438, 470, 489, 491, 515, 523, 716, 718, 735-736(주석), 746, 764, 771, 783, 798, 814, 836, 862, 866, 883, 896(주석), 942(주석), 949-952, 1022, 1024, 1098, 1134, 1149, 1156, 1168, 1231, 1237, 1260, 1378, 1383, 1507, 1593, 1613, 1623, 1636, 1646, 1652, 1658, 1663, 1670, 1821, 1841, 1882, 1910, 1952, 1958, 2016-2028, 2061-2071, 2097** (모든 접근을 ConcurrentDictionary 호환 API로 유지해야 함)
- `_reimport*` 필드 정의: line 982-992
- `IsReimporting` / `ReimportAssetName` / `ReimportElapsed`: line 995-997
- `ReimportAsync`: line 1003-1075
- `ProcessReimport`: line 1081-1227
- `_pendingChanges`, `_changeLock`: line 73-74 (`private readonly Queue<AssetChangeEvent> _pendingChanges = new();`, `private readonly object _changeLock = new();`)
- `StartWatching` (FSW 설치): line 1677-1707
- `EnqueueChange`: line 1721-1743
- `ProcessFileChanges`: line 1751-1858 (현재 "락 안에서 큐 → 로컬 복사 → 락 밖에서 디바운스 판정 → re-enqueue" 구조)
- `OnRoseMetadataSaved`: line 1865-1900
- `_pendingReimports` Queue: line 84 (메인 전용, async reimport 진행 중 OnSaved가 들어오면 enqueue)
- `ProcessPendingReimports`: line 1905~
- `RegisterSubAssets`: line 1524-1582 (meta.Save → OnSaved 트리거 가능 → `_importDepth > 0`이면 재귀 무시)
- `RegisterSpriteSubAssets`: line 1464-1500 (동일 패턴)
- `StoreCacheOrDefer`: line 573-591 — `EditorPlayMode.IsInPlaySession`이면 `ConcurrentQueue` enqueue, 아니면 `_roseCache.StoreTexture`/`StoreMesh` 즉시 호출 (→ 내부에서 `GpuTextureCompressor` 사용 가능)
- `CacheSubAssets`: line 1588-1639 (sub-asset _loadedAssets에 등록하지만 **파일 I/O 없음**, 메인에서만 안전)
- `CacheSpriteSubAssets`: line 1502-1516 (_loadedAssets에 등록, `_spriteToGuid` dict 갱신)
- `ReplaceMeshInScene`: line 2126-2149 (`MeshRenderer._allRenderers` iterate)
- `ReplaceTextureInScene`: line 2151-2195 (`MeshRenderer._allRenderers`, `SpriteRenderer._allSpriteRenderers` iterate)
- `ReplaceSpriteInScene`: line 2203-2252 (`SpriteRenderer._allSpriteRenderers`, `SceneManager.AllGameObjects` iterate)
- `ReplaceFontInScene`: line 2264-2285 (`TextRenderer._allTextRenderers`, `UIText._allUITexts`, `UIInputField._allUIInputFields` iterate)
- `PostProcessVolume._allVolumes` iterate: line 841 (sync Reimport의 PostProcessProfileImporter 케이스)
- `MeshRenderer._allRenderers` iterate: line 2128, 2155, 2257

### ReimportAsync 호출자 (반환값 사용 여부)

모두 **메인 스레드 / void 사용 (fire-and-forget)**:
1. `src/IronRose.Engine/Editor/Undo/Actions/MaterialPropertyUndoAction.cs:38` — `db?.ReimportAsync(_matPath);`
2. `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs:153` — `db?.ReimportAsync(result.AbsoluteOutputPath);`
3. `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs:849` — `db!.ReimportAsync(asset.FullPath);`
4. `AssetDatabase.OnRoseMetadataSaved` 내부 (line 1891) — `ReimportAsync(assetPath);`
5. `AssetDatabase.ProcessPendingReimports` 내부 (line 1911) — `ReimportAsync(next);`

**결론**: `ReimportAsync`의 반환 타입을 `void` 그대로 유지해도 된다 (Task 객체는 내부 필드에 보관). 외부 계약 변경 없음. 단, 내부적으로는 `Task<ReimportResult>`를 만들어 `_reimportTask` 필드에 저장한다.

### GpuTextureCompressor 공개 API

- `GpuTextureCompressor(GraphicsDevice)` ctor: GPU 접근 없음, 필드 저장만
- `Initialize(string shaderDir)`: **GPU 접근** — `factory.CreateResourceLayout`, `CreateComputePipeline`, `CreateBuffer`, `CreateCommandList`
- `CompressBC7(byte[], int, int)`: **GPU 접근**
- `CompressBC5(byte[], int, int)`: **GPU 접근**
- `GenerateMipmapsGPU(byte[], int, int)`: **GPU 접근** (`factory.CreateTexture`, `_device.UpdateTexture`, `_cl.Begin/End`, `_device.SubmitCommands`, `_device.WaitForIdle`, `_device.Map`)
- `Dispose()`: **GPU 접근** (버퍼/파이프라인 Dispose)

### Bc6hEncoder 공개 API (가드 불필요)

- `Encode(float[] hdrData, int width, int height)`: CPU만
- `Decode(byte[] bc6hData, int width, int height)`: CPU만

### EditorPlayMode.IsInPlaySession

- 위치: `src/IronRose.Engine/Editor/EditorPlayMode.cs:59`
- 정의: `public static bool IsInPlaySession => State == PlayModeState.Playing || State == PlayModeState.Paused;`
- 메인 전용 상태. 단순 bool 읽기라 race 영향 미미.

---

## B-1: `_loadedAssets` → ConcurrentDictionary

### 변경 파일

- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

### 필드 선언 변경 (line 43)

**old_string**:
```csharp
        private readonly Dictionary<string, object> _loadedAssets = new();
```

**new_string**:
```csharp
        // 백그라운드 Task(ReimportAsync 람다)가 RegisterSubAssets/RegisterSpriteSubAssets 경로를
        // 거쳐 _loadedAssets를 쓰던 경로는 B-3에서 메인으로 이동하지만, 과도기 안전 + 미래 회귀 방지를
        // 위해 ConcurrentDictionary로 유지한다. 순회 지점은 ToArray() 스냅샷으로 보호한다.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _loadedAssets = new();
```

### 순회 지점 (`foreach`) 스냅샷화

**모든 `foreach (var ... in _loadedAssets)` 또는 `foreach (var kvp in _loadedAssets)` 또는 `foreach (var asset in _loadedAssets.Values)`를 `.ToArray()` 스냅샷 위에서 순회하도록 변경한다.**

대상 라인:
- **line 438** (`FindGuidForPrefab`): `foreach (var (path, asset) in _loadedAssets)` → `foreach (var (path, asset) in _loadedAssets.ToArray())`
- **line 470** (`FindPathForMesh`): 동일 패턴 `.ToArray()` 추가
- **line 515** (`InvalidateScriptPrefabCache`): `foreach (var kvp in _loadedAssets)` → `foreach (var kvp in _loadedAssets.ToArray())`
- **line 1378** (`UnloadAll`): `foreach (var asset in _loadedAssets.Values)` → `foreach (var asset in _loadedAssets.Values.ToArray())`
- **line 2018** (`RenameAsset`): `foreach (var kvp in _loadedAssets)` → `foreach (var kvp in _loadedAssets.ToArray())`
- **line 2063** (`RenameFolderPaths`): 동일 `.ToArray()` 추가

**구체 변경 예** (line 438):

**old_string**:
```csharp
            // 로드된 에셋에서 역검색
            foreach (var (path, asset) in _loadedAssets)
            {
                if (ReferenceEquals(asset, prefab) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    return GetGuidFromPath(path);
            }
```

**new_string**:
```csharp
            // 로드된 에셋에서 역검색. ConcurrentDictionary는 순회 중 수정에 약하므로 스냅샷.
            foreach (var (path, asset) in _loadedAssets.ToArray())
            {
                if (ReferenceEquals(asset, prefab) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    return GetGuidFromPath(path);
            }
```

**line 470**:

**old_string**:
```csharp
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
```

**new_string**:
```csharp
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
```

**line 515** (`InvalidateScriptPrefabCache`):

**old_string**:
```csharp
            var toRemove = new List<string>();
            foreach (var kvp in _loadedAssets)
            {
                if (kvp.Value is not GameObject go) continue;
                if (HasScriptComponent(go))
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _loadedAssets.Remove(key);
```

**new_string**:
```csharp
            var toRemove = new List<string>();
            foreach (var kvp in _loadedAssets.ToArray())
            {
                if (kvp.Value is not GameObject go) continue;
                if (HasScriptComponent(go))
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _loadedAssets.TryRemove(key, out _);
```

**line 1378** (`UnloadAll`):

**old_string**:
```csharp
            foreach (var asset in _loadedAssets.Values)
            {
                if (asset is IDisposable disposable)
                    disposable.Dispose();
            }
            _loadedAssets.Clear();
```

**new_string**:
```csharp
            foreach (var asset in _loadedAssets.Values.ToArray())
            {
                if (asset is IDisposable disposable)
                    disposable.Dispose();
            }
            _loadedAssets.Clear();
```

**line 2016-2028** (`RenameAsset`):

**old_string**:
```csharp
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
```

**new_string**:
```csharp
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
```

**line 2061-2071** (`RenameFolderPaths`): 같은 패턴

**old_string**:
```csharp
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
```

**new_string**:
```csharp
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
```

### `Remove`/indexer 호환성 확인

`ConcurrentDictionary`는 `Dictionary`와 호환되는 다음 API를 모두 제공한다 (API 시그니처 동일):
- `TryGetValue(key, out value)` ✓
- `ContainsKey(key)` ✓
- `[key] = value` (AddOrUpdate 의미) ✓
- `[key]` getter (KeyNotFoundException 던짐) ✓
- `Values` (IEnumerable<TValue>) ✓
- `Clear()` ✓
- `Count` ✓ (단, ConcurrentDictionary는 `Count` 접근이 모든 버킷을 순회하여 비용 큼 — 기존 코드에서 자주 쓰지 않으므로 문제없음)

**비호환 API** (치환 필요):
- `Remove(key)` → `TryRemove(key, out _)` — `bool` 반환은 동일. 대상 라인: **523, 716, 718, 1024, 1237, 1260, 1646, 1652, 1658, 1663, 1670, 1958, 2027, 2070** (14개)

**모든 `.Remove(key)` 호출은 `.TryRemove(key, out _)`로 치환**.

**구체 변경 예** (line 716-718):

**old_string**:
```csharp
                if (_loadedAssets.TryGetValue(path, out var cur))
                    disposedSet.Add(cur);
                _loadedAssets.Remove(path);
```

**new_string**:
```csharp
                if (_loadedAssets.TryGetValue(path, out var cur))
                    disposedSet.Add(cur);
                _loadedAssets.TryRemove(path, out _);
```

나머지 `.Remove(key)` 호출도 동일 패턴으로 치환 (line 523의 `_loadedAssets.Remove(key)` 포함). 치환할 라인 번호 전체: `523, 716(아래 line은 718), 1024, 1237, 1260, 1646, 1652, 1658, 1663, 1670, 1958, 2027, 2070`.

`_loadedAssets.Remove(path) && VerboseLogging` 같은 조합 사용처 (line 1260):

**old_string**:
```csharp
                if (path != null && _loadedAssets.Remove(path) && VerboseLogging)
```

**new_string**:
```csharp
                if (path != null && _loadedAssets.TryRemove(path, out _) && VerboseLogging)
```

### 검증 스텝 (B-1 끝)

- [ ] `dotnet build` 성공
- [ ] `_loadedAssets.Remove(` 검색 시 0건 (모두 `TryRemove`로 치환됐는지 확인)
- [ ] `foreach (var ... in _loadedAssets)` 검색 시 0건 (모두 `.ToArray()` 스냅샷 위에서 순회)
- [ ] 에디터 시작 → 큰 텍스처 5회 Reimport → 프로세스 hang/crash 없음
- [ ] `grep -n '_loadedAssets' src/IronRose.Engine/AssetPipeline/AssetDatabase.cs | wc -l` 치환 전후 라인 수 차이가 치환 대상 14건과 일치

---

## B-2: `ReimportResult` 도입 + `_reimport*` 개별 필드 제거

### 변경 파일

- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

### 신규 타입 정의

AssetDatabase 클래스 내부 (또는 같은 파일 끝, 동일 네임스페이스)에 아래 record를 추가한다. **권장 위치**: AssetDatabase 클래스 내부 private nested record (`class AssetDatabase { ... }` 안쪽 맨 아래, 기존 `private class SpriteImportResult` (line 1394) 근처).

```csharp
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
```

### 필드 선언 변경 (line 982-992)

**old_string**:
```csharp
        private System.Threading.Tasks.Task? _reimportTask;
        private string? _reimportPath;
        private object? _reimportOldAsset;
        private System.Diagnostics.Stopwatch? _reimportTimer;

        // Async reimport 결과 필드 (Task 완료 후 메인에서 읽음)
        private MeshImportResult? _reimportMeshResult;
        private RoseMetadata? _reimportMeta;
        private string? _reimportType;
        private Texture2D? _reimportTexResult;
        private SpriteImportResult? _reimportSpriteResult;
```

**new_string**:
```csharp
        // 비동기 reimport가 진행 중일 때만 non-null. Task 완료 후 메인 ProcessReimport가
        // result를 꺼내 처리하고 null로 복구한다. 개별 _reimport* 필드들은 Task 완료 fence에
        // 의존하는 happens-before가 불명확하여, 단일 Task<ReimportResult>로 통합한다.
        private System.Threading.Tasks.Task<ReimportResult>? _reimportTask;
```

### `IsReimporting` / `ReimportAssetName` / `ReimportElapsed` 프로퍼티 수정 (line 995-997)

**old_string**:
```csharp
        public bool IsReimporting => _reimportTask != null;
        public string? ReimportAssetName => _reimportPath != null ? Path.GetFileName(_reimportPath) : null;
        public double ReimportElapsed => _reimportTimer?.Elapsed.TotalSeconds ?? 0;
```

**new_string**:
```csharp
        // 진행 중 상태를 Task.AsyncState(= ReimportResult initial prototype)에서 조회.
        // _reimportTask는 Task.Run 호출 직후 할당하고, Task factory delegate가 result 객체 전체를
        // 반환하지만, 진행 중에도 path/timer를 UI에 노출하기 위해 경량 prototype을 AsyncState로 전달한다.
        public bool IsReimporting => _reimportTask != null;
        public string? ReimportAssetName =>
            (_reimportTask?.AsyncState as ReimportPrototype)?.Path is string p ? Path.GetFileName(p) : null;
        public double ReimportElapsed =>
            (_reimportTask?.AsyncState as ReimportPrototype)?.Timer.Elapsed.TotalSeconds ?? 0;

        /// <summary>Task.Run에 AsyncState로 전달하는 진행 중 정보. Task 완료 전까지 UI에서 참조한다.</summary>
        private sealed record ReimportPrototype(string Path, System.Diagnostics.Stopwatch Timer);
```

### `ReimportAsync` 전면 재작성 (line 1003-1075)

**old_string** (전체 `ReimportAsync` 메서드):
```csharp
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
```

**new_string** (전체 교체. B-3 개정까지 포함 — Task 람다는 공유 상태 쓰기 제거):
```csharp
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
```

**설계 포인트**:
- `Task.Factory.StartNew`로 `AsyncState`를 `prototype`으로 주입 → `IsReimporting`/`ReimportAssetName`/`ReimportElapsed`가 Task 진행 중에도 경량 데이터에 접근.
- 람다 안에서 `try/catch`로 모든 예외 포획. Task의 `IsFaulted`를 유발하지 않고 `ReimportResult.Error`로 전달 (Exception propagation을 명시적으로 제어).
- 람다 밖에서 이미 `oldAsset`, `meta`, `importerType`, `path`, `timer`를 캡처 — 모두 메인에서 준비된 값.
- 람다 안에서 `_reimport*` 필드 쓰기 제거, `RegisterSubAssets`/`RegisterSpriteSubAssets`/`StoreCacheOrDefer` 호출 제거 (B-3의 핵심 변경).

### `ProcessReimport` 전면 재작성 (line 1081-1227)

**old_string** (전체 `ProcessReimport`):
```csharp
        public bool ProcessReimport()
        {
            if (_reimportTask == null) return false;
            if (!_reimportTask.IsCompleted) return false;

            var path = _reimportPath!;
            bool faulted = _reimportTask.IsFaulted;

            // 에러 처리: 실패 시 oldAsset을 _loadedAssets에 복원하여 씬 참조가 dangling되지 않도록
            // 보장하고, 이후 파일 변경/Inspector Apply 경로에서 다시 Reimport가 트리거되도록 한다.
            if (faulted)
            {
                var ex = _reimportTask.Exception?.InnerException;
                EditorDebug.LogError($"[AssetDatabase] Async reimport failed: {ex?.Message}");

                if (_reimportOldAsset != null)
                {
                    _loadedAssets[path] = _reimportOldAsset;
                    if (_reimportOldAsset is MeshImportResult oldMeshR)
                    {
                        var meta2 = _reimportMeta ?? RoseMetadata.LoadOrCreate(path);
                        CacheSubAssets(path, oldMeshR, meta2);
                    }
                    else if (_reimportOldAsset is SpriteImportResult oldSprR)
                    {
                        var meta2 = _reimportMeta ?? RoseMetadata.LoadOrCreate(path);
                        CacheSpriteSubAssets(path, oldSprR, meta2);
                    }
                }

                // GPU 정리 스킵하고 바로 상태 초기화로.
                _reimportTimer?.Stop();
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
            // sync Reimport의 finally 경로(ProjectDirty = true; ReimportVersion++)와 대칭.
            // Inspector preview 등 ReimportVersion을 구독하는 UI가 async 경로에서도 갱신되도록 보장.
            ReimportVersion++;

            return true;
        }
```

**new_string** (전체 교체):
```csharp
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
```

**설계 포인트**:
- 모든 `_reimport*` 접근이 `result.*`로 교체됨 → 단일 Task 완료 fence 위에서만 관측. C1 해소.
- `result.Error != null`과 `task.IsFaulted` 두 경로 모두 방어. 람다 내부에서 잡힌 예외는 `result.Error`로, 잡히지 않은 예외는 `IsFaulted`로.
- **B-3 통합**: 람다 밖에서 `RegisterSubAssets`/`RegisterSpriteSubAssets`/`StoreCacheOrDefer`를 메인에서 호출. 이 호출들은 `_guidToPath` 수정, `meta.Save` (OnSaved 트리거), `_roseCache.StoreTexture`/`StoreMesh` (GPU 호출 포함)를 메인에서 수행하므로 안전.

### `OnRoseMetadataSaved` 내부 `_reimportTask != null` 체크 유지 (line 1884)

라인 1884의 `if (_reimportTask != null)` 체크는 그대로 유지 (field 타입이 `Task` → `Task<ReimportResult>`로 바뀌었지만 null 비교는 동일).

### `ProcessPendingReimports` 내부 `_reimportTask != null` 체크 유지 (line 1907)

동일하게 유지.

### 검증 스텝 (B-2 끝)

- [ ] `dotnet build` 성공
- [ ] `_reimportPath`, `_reimportOldAsset`, `_reimportTimer`, `_reimportMeshResult`, `_reimportMeta`, `_reimportType`, `_reimportTexResult`, `_reimportSpriteResult` 심볼이 파일 전체에서 0건
- [ ] `_reimportTask` 심볼만 남아있음
- [ ] `grep -n "ReimportResult\|ReimportPrototype" src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` 에 record 정의 + 사용처 확인
- [ ] 에디터 시작 → 메시/텍스처 Reimport → 성공/실패 모두 로그 정상
- [ ] Reimport 진행 중 IsReimporting/ReimportAssetName/ReimportElapsed를 Inspector UI에서 올바르게 표시

---

## B-3: Task 람다에서 공유 상태 쓰기 제거

**B-2 변경에 이미 포함되었다.** Task.Factory.StartNew 람다 안에서 다음 호출이 제거되었고 `ProcessReimport`의 성공 경로로 이동됨:

1. `RegisterSubAssets(path, result, meta)` → `ProcessReimport` MeshImporter case로 이동
2. `StoreCacheOrDefer(path, result, meta)` (Mesh) → 동일 위치
3. `StoreCacheOrDefer(path, tex, meta)` (Texture) → `ProcessReimport` TextureImporter case로 이동
4. `RegisterSpriteSubAssets(path, sr, meta)` → 동일 위치

**주의**:
- `StoreCacheOrDefer`는 Play 세션 중이면 `ConcurrentQueue` enqueue로 defer되고, 그렇지 않으면 `_roseCache.Store*`가 즉시 실행되는데 그 안에서 `GpuTextureCompressor`를 호출할 수 있다. 메인에서 호출하는 것으로 옮겨졌으므로 안전해진다.
- `RegisterSubAssets` 내부의 `meta.Save(filePath + ".rose")`가 `RoseMetadata.OnSaved` 이벤트를 발생시키며, 이것이 `OnRoseMetadataSaved` 핸들러로 들어간다. 핸들러는 `_importDepth > 0` 체크로 재귀 reimport를 차단하는데, 이미 메인 스레드에서 `_importDepth > 0` 상태이므로 기존 동작 유지됨.
- `CacheSubAssets`, `CacheSpriteSubAssets`는 이미 기존에도 메인의 `ProcessReimport`에서 호출되므로 변경 없음.

### 추가 검증 (B-3 끝)

- [ ] `grep -n "RegisterSubAssets\|RegisterSpriteSubAssets\|StoreCacheOrDefer" src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` 결과에서 `ReimportAsync` 메서드 범위(line ~1003-1075 부근) 안쪽에 해당 호출이 **없어야** 함
- [ ] `grep -n "RegisterSubAssets\|RegisterSpriteSubAssets\|StoreCacheOrDefer" src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` 결과에서 `ProcessReimport` 메서드 범위 안쪽에 모두 호출됨이 확인됨
- [ ] 에디터 시작 → 큰 텍스처(1024x1024 이상) Reimport → GPU 관련 예외 로그 없음
- [ ] 에디터 시작 → 큰 메시(Triangle > 10k) Reimport → 캐시 저장 정상 (`.rose_cache` 디렉토리 생성 확인)
- [ ] Play 세션 중 텍스처 Reimport → 즉시 반영되고, Play stop 후 디스크 캐시 생성 확인

---

## B-4: FileSystemWatcher 디바운스 dedup 맵 재설계

### 변경 파일

- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

### 필드 선언 변경 (line 73)

**old_string**:
```csharp
        // ─── FileSystemWatcher ───────────────────────────────────
        private FileSystemWatcher? _watcher;
        private readonly Queue<AssetChangeEvent> _pendingChanges = new();
        private readonly object _changeLock = new();
```

**new_string**:
```csharp
        // ─── FileSystemWatcher ───────────────────────────────────
        private FileSystemWatcher? _watcher;
        // dedup 맵: path → 최신 AssetChangeEvent. FSW 콜백은 락 안에서 키를 덮어쓰므로
        // 같은 파일의 중복 이벤트가 큐에 누적되지 않고 "최신 타임스탬프"만 보관된다.
        // ProcessFileChanges는 락 안에서 debounce 통과 항목만 뽑아내고 나머지는 맵에 남겨둔다.
        private readonly Dictionary<string, AssetChangeEvent> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _changeLock = new();
```

### `StartWatching`의 Renamed 콜백 수정 (line 1692-1704)

**old_string**:
```csharp
            _watcher.Renamed += (_, e) =>
            {
                lock (_changeLock)
                {
                    _pendingChanges.Enqueue(new AssetChangeEvent
                    {
                        Type = AssetChangeType.Renamed,
                        FullPath = e.FullPath,
                        OldFullPath = e.OldFullPath,
                        TimestampTicks = DateTime.UtcNow.Ticks,
                    });
                }
            };
```

**new_string**:
```csharp
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
```

### `EnqueueChange` 수정 (line 1721-1743)

**old_string**:
```csharp
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
                    TimestampTicks = DateTime.UtcNow.Ticks,
                });
            }
        }
```

**new_string**:
```csharp
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
```

### `ProcessFileChanges` 전면 재작성 (line 1751-1858)

**old_string** (전체 메서드):
```csharp
        public void ProcessFileChanges()
        {
            List<AssetChangeEvent> events;
            lock (_changeLock)
            {
                if (_pendingChanges.Count == 0) return;
                events = new List<AssetChangeEvent>(_pendingChanges);
                _pendingChanges.Clear();
            }

            // Deduplicate: keep only the last (most recent) event per file path.
            // Created/Deleted/Renamed는 type을 그대로 유지하되 timestamp는 최신으로 갱신한다.
            var deduped = new Dictionary<string, AssetChangeEvent>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in events)
            {
                if (deduped.TryGetValue(evt.FullPath, out var existing))
                {
                    // Deleted가 들어오면 이후 이벤트로 덮어쓰지 않고 Deleted 유지.
                    // 그 외에는 최신 이벤트로 교체하면서 타임스탬프 갱신 → debounce 연장.
                    if (existing.Type == AssetChangeType.Deleted) continue;
                }
                deduped[evt.FullPath] = evt;
            }

            // Debounce: 아직 안정화 대기 시간이 지나지 않은 이벤트는 큐에 되돌린다.
            // 쓰기 중에는 Changed가 연속으로 도착하여 타임스탬프가 계속 갱신되므로 처리가 지연된다.
            var nowTicks = DateTime.UtcNow.Ticks;
            var debounceTicks = TimeSpan.FromMilliseconds(FileChangeDebounceMs).Ticks;
            var toRequeue = new List<AssetChangeEvent>();
            var ready = new List<AssetChangeEvent>();
            foreach (var evt in deduped.Values)
            {
                // Deleted/Renamed는 debounce하지 않음 (원자적 이벤트로 간주)
                if (evt.Type == AssetChangeType.Created || evt.Type == AssetChangeType.Changed)
                {
                    if (nowTicks - evt.TimestampTicks < debounceTicks)
                    {
                        toRequeue.Add(evt);
                        continue;
                    }
                }
                ready.Add(evt);
            }

            if (toRequeue.Count > 0)
            {
                lock (_changeLock)
                {
                    foreach (var evt in toRequeue)
                        _pendingChanges.Enqueue(evt);
                }
            }

            if (ready.Count == 0) return;

            // ready 리스트는 이미 path-unique한 deduped.Values에서 debounce 통과한 것만 추린 것이라
            // FullPath가 유일하다. 별도 Dictionary 재집계(deduped_ready)는 no-op이므로 제거.
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
```

**new_string** (전체 교체):
```csharp
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
```

**설계 포인트**:
- `_pendingChanges`가 `Queue → Dictionary`로 바뀌었으므로 `Enqueue`는 indexer `[key] = value`로, `Clear`는 그대로 사용 가능하지만 이 구현에서는 "처리된 키만 제거" 방식.
- 판정과 제거가 **같은 락 블록** 안에서 일어나므로, FSW 스레드가 락을 잡고 새 이벤트를 넣으면 그 이벤트의 timestamp는 현재 판정 루프가 놓치고 다음 프레임에서 다시 판정됨 — 일관된 동작.
- 락 밖에서는 실제 `Reimport`/`UnregisterAsset` 등 비용 큰 작업만 수행 (락 hold 시간 최소화).

### `Queue.Enqueue` 호환성 주의

기존 `_pendingChanges`를 다른 곳에서 참조하는지 확인 — grep 결과 `_pendingChanges`는 `StartWatching`(Renamed 콜백), `EnqueueChange`, `ProcessFileChanges`, 파일 상단 declare만 사용. 모두 위 변경에 포함됨.

### 검증 스텝 (B-4 끝)

- [ ] `dotnet build` 성공
- [ ] `_pendingChanges.Enqueue` 검색 0건, `_pendingChanges.Clear` 검색 0건 (indexer/Remove로 치환됨)
- [ ] 외부 에디터로 `.png` 파일을 빠르게 연속 덮어쓰기 (예: `touch` loop 5회) → 디바운스가 정상 동작, 무한 reimport 루프 없음
- [ ] `.png` 파일 수정 중 에디터가 Running → 에디터 UI freeze 없음
- [ ] 파일 Rename → 정상적으로 한 번만 처리됨

---

## B-5: GPU 가드 + `OnRoseMetadataSaved` 메인 큐잉

### 변경 파일

1. `src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs`
2. `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

### 5.1 `GpuTextureCompressor.cs` — ThreadGuard 삽입

#### `Initialize` 메서드 시작부 가드 (line 56-57)

**old_string**:
```csharp
        public void Initialize(string shaderDir)
        {
            if (_initialized) return;
            var factory = _device.ResourceFactory;
```

**new_string**:
```csharp
        public void Initialize(string shaderDir)
        {
            if (_initialized) return;
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.Initialize"))
                return; // 메인이 아니면 초기화를 건너뛴다 (드라이버 충돌 회피)
            var factory = _device.ResourceFactory;
```

#### `CompressBC7` (line 105-108)

**old_string**:
```csharp
        public byte[] CompressBC7(byte[] rgbaData, int width, int height)
        {
            return CompressInternal(rgbaData, width, height, _bc7Pipeline!);
        }
```

**new_string**:
```csharp
        public byte[] CompressBC7(byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.CompressBC7"))
                return Array.Empty<byte>();
            return CompressInternal(rgbaData, width, height, _bc7Pipeline!);
        }
```

#### `CompressBC5` (line 110-113)

**old_string**:
```csharp
        public byte[] CompressBC5(byte[] rgbaData, int width, int height)
        {
            return CompressInternal(rgbaData, width, height, _bc5Pipeline!);
        }
```

**new_string**:
```csharp
        public byte[] CompressBC5(byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.CompressBC5"))
                return Array.Empty<byte>();
            return CompressInternal(rgbaData, width, height, _bc5Pipeline!);
        }
```

#### `GenerateMipmapsGPU` (line 208-211)

**old_string**:
```csharp
        public byte[][] GenerateMipmapsGPU(byte[] rgbaData, int width, int height)
        {
            lock (_lock)
            {
                var factory = _device.ResourceFactory;
```

**new_string**:
```csharp
        public byte[][] GenerateMipmapsGPU(byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.GenerateMipmapsGPU"))
                return Array.Empty<byte[]>();
            lock (_lock)
            {
                var factory = _device.ResourceFactory;
```

#### `Dispose` (line 290-293)

**old_string**:
```csharp
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
```

**new_string**:
```csharp
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.Dispose"))
                return; // 메인이 아니면 GPU 리소스 해제를 스킵 (드라이버 충돌 회피. 프로세스 종료 시 자동 정리됨)
```

**설계 포인트**:
- 위반 시 `Array.Empty<byte>()`/`Array.Empty<byte[]>()` 반환 → `RoseCache.StoreTexture` 내부가 "빈 데이터"를 감지하고 CPU 폴백 또는 저장 스킵으로 분기되어야 한다. **aca-coder가 `RoseCache.cs`의 GPU 경로를 읽어 빈 배열 처리가 이미 존재하는지 확인 필요** (미결 사항).
- `_lock` 바깥에 가드를 두는 이유: 비메인 스레드가 락을 못 잡은 채 대기하는 걸 방지 (H6 보고서의 "드라이버 동시성 위반" 회피).
- `Dispose`는 `_disposed = true` 먼저 설정하여 멱등성 유지.

### 5.2 `AssetDatabase.cs` — `OnRoseMetadataSaved` 메인 큐잉

#### 신규 큐 필드 추가 (line 84 근처, `_pendingReimports` 바로 아래)

**old_string** (line 83-85):
```csharp
        // Reimport queue filled by OnSaved when async reimport is busy
        private readonly Queue<string> _pendingReimports = new();
        public bool ProjectDirty { get; set; }
```

**new_string**:
```csharp
        // Reimport queue filled by OnSaved when async reimport is busy
        private readonly Queue<string> _pendingReimports = new();
        // FSW/백그라운드 스레드에서 RoseMetadata.OnSaved가 들어올 때 즉시 Reimport 호출하지 않고
        // 이 큐에 쌓아 메인 ProcessMetadataSavedQueue()에서 일괄 처리한다. (H4)
        // ConcurrentQueue로 FSW 콜백 스레드에서 enqueue, 메인에서 dequeue.
        private readonly ConcurrentQueue<string> _metadataSavedQueue = new();
        public bool ProjectDirty { get; set; }
```

**주의**: `using System.Collections.Concurrent;`는 파일 상단 line 29에 이미 있음 (확인 완료).

#### `OnRoseMetadataSaved` 핸들러 수정 (line 1865-1900)

**old_string**:
```csharp
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
```

**new_string**:
```csharp
        /// <summary>
        /// RoseMetadata.OnSaved 이벤트 핸들러. FSW/백그라운드 스레드에서도 호출될 수 있으므로
        /// 여기서는 큐에 enqueue만 하고 실제 처리는 메인 ProcessMetadataSavedQueue에서 수행한다.
        /// _importDepth 빠른 필터링만 이 핸들러에서 수행 (메인에서 변경되지만 read는 atomic).
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
        /// 메인 스레드에서 매 프레임 호출. FSW/백그라운드에서 enqueue된
        /// .rose 저장 이벤트를 일괄 처리한다.
        /// </summary>
        public void ProcessMetadataSavedQueue()
        {
            while (_metadataSavedQueue.TryDequeue(out var assetPath))
            {
                // 메인 진입 시점의 _importDepth를 다시 체크 (enqueue와 dequeue 사이에
                // 변경 가능성 있음).
                if (_importDepth > 0)
                {
                    // 재귀 구간 안에 들어온 이벤트는 버린다 (기존 로직과 동일).
                    continue;
                }

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
```

#### `EngineCore`에서 `ProcessMetadataSavedQueue` 호출 추가

`ProcessMetadataSavedQueue`가 매 프레임 호출되어야 한다. 기존 `ProcessReimport` / `ProcessFileChanges` / `ProcessPendingReimports` 호출 위치를 찾아 같은 블록에 추가해야 함.

**aca-coder 탐색 힌트**: `grep -n "ProcessReimport\|ProcessFileChanges\|ProcessPendingReimports" src/IronRose.Engine/EngineCore.cs` 로 호출 위치 확인 → 그 근처에 `assetDb.ProcessMetadataSavedQueue();` 한 줄 추가. 없으면 `EditorCore.Update` 같은 에디터 메인 루프에서 호출.

**임시 가이드** (구체 위치를 aca-coder가 확정):
```csharp
// 기존 패턴 근처에 추가
assetDb.ProcessFileChanges();
assetDb.ProcessMetadataSavedQueue(); // NEW (B-5)
assetDb.ProcessReimport();
assetDb.ProcessPendingReimports();
```

### 검증 스텝 (B-5 끝)

- [ ] `dotnet build` 성공
- [ ] `grep -n "ThreadGuard.CheckMainThread" src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs | wc -l` = 5 (Initialize + CompressBC7 + CompressBC5 + GenerateMipmapsGPU + Dispose)
- [ ] `_metadataSavedQueue` 심볼이 `OnRoseMetadataSaved`, `ProcessMetadataSavedQueue`에서 사용됨
- [ ] `ProcessMetadataSavedQueue`가 메인 루프에서 호출됨 (`grep -n ProcessMetadataSavedQueue src/`)
- [ ] 에디터 시작 → 외부 편집으로 `.png` 수정 후 저장 → 정상 reimport (ThreadGuard 에러 로그 없음)
- [ ] CLI로 .mat 편집 시뮬레이션 (Inspector Apply 대신 직접 파일 수정 + 메타 저장) → 메인에서 처리됨 확인
- [ ] `[ThreadGuard] GpuTextureCompressor.*` 로그가 에디터 시작 후 5분 런타임 동안 찍히지 않음

---

## 전체 Phase B 통합 검증 체크리스트

**리뷰 체크리스트** (마스터 문서 §Phase B 기준):

- [ ] `_reimport*` 개별 필드 전부 제거되었는가? (`_reimportTask`만 남음)
- [ ] `Task<ReimportResult>` 반환이고 `IsFaulted` 및 `result.Error` 처리가 되는가?
- [ ] `_loadedAssets`가 `ConcurrentDictionary`이고 모든 `.Remove(k)`가 `.TryRemove`로, 모든 `foreach`가 `.ToArray()` 스냅샷인가?
- [ ] `ReimportAsync` 람다가 GPU API나 `_all*` 리스트, `_loadedAssets`, `_guidToPath`를 쓰지 않는가?
- [ ] `RegisterSubAssets`/`RegisterSpriteSubAssets`/`StoreCacheOrDefer`가 람다 밖(메인 `ProcessReimport`)에서만 호출되는가?
- [ ] FSW 디바운스가 dedup 맵(`Dictionary<string, AssetChangeEvent>`) 기반이고 판정과 제거가 같은 락 블록에 있는가?
- [ ] `OnRoseMetadataSaved`가 `_metadataSavedQueue.Enqueue`만 하고 `ReimportAsync`를 직접 호출하지 않는가?
- [ ] `ProcessMetadataSavedQueue`가 메인 루프에서 매 프레임 호출되는가?
- [ ] `GpuTextureCompressor`의 GPU 진입 지점 5곳에 `ThreadGuard.CheckMainThread(...)`가 있고, 위반 시 `Array.Empty`/`return`으로 스킵하는가?
- [ ] 위반 시 throw 없음 (`if (!ThreadGuard.CheckMainThread(...)) return/return Array.Empty...`)

### 통합 스모크 테스트

**시나리오 1**: 큰 mesh/texture 에셋 반복 Reimport (10회)
- 기대: `[ThreadGuard]` 로그 0건, reimport 완료 후 `_loadedAssets` 카운트 안정 (누수 없음)

**시나리오 2**: 외부 에디터로 .png 덮어쓰기 + 메타 Save 동시 발생
- 기대: 디바운스가 정상 동작하여 한 번의 reimport로 합쳐짐. `[AssetDatabase] ProcessFileChanges error` 없음.

**시나리오 3**: 플레이모드 중 에셋 변경 → 플레이 Stop → 리임포트 완료 확인
- 기대: Play 중 `_pendingCacheTextures`/`_pendingCacheMeshes`에 defer되고, Play Stop 후 `FlushPendingCacheOps`에서 정상 저장.

**시나리오 4**: 메시 + 텍스처 Reimport가 연속으로 발생 (.glb 저장 → 그 안의 .png도 저장)
- 기대: `_pendingReimports` 큐가 올바르게 동작하여 두 번째 reimport가 첫 번째 완료 후 시작.

**시나리오 5**: Debug 빌드에서 비정상 경로 강제 유발 (`Task.Run(() => assetDb.Load<Texture2D>("test.png"))`를 임시 테스트 코드로 삽입 — 머지 전 제거)
- 기대: `_loadedAssets` ConcurrentDictionary 덕분에 crash 없음. GPU 경로 진입 시 `[ThreadGuard]` 로그 출력.

---

## 미결 사항 (aca-coder가 구현 중 확정)

1. **`ProcessMetadataSavedQueue` 호출 위치**: `EngineCore.cs` / `EditorCore.cs` / 다른 메인 루프 중 어디에 삽입하는 것이 정확한지 grep으로 기존 `ProcessReimport` 호출 지점을 찾아 같은 블록에 삽입. 만약 기존 `ProcessReimport`가 여러 곳에서 호출된다면 모두에 추가 (또는 같은 곳에 묶어 재구성).

2. **`RoseCache.cs`의 빈 배열 처리**: `GpuTextureCompressor.CompressBC7/BC5`가 `Array.Empty<byte>()` 를 반환했을 때 호출자(`RoseCache.cs:547, 557` 등)가 크래시 없이 처리하는지 확인. 만약 빈 배열을 그대로 저장하면 나중에 로드 시 문제가 될 수 있으므로, **빈 배열 반환 시 즉시 CPU 폴백으로 fallback하는 분기**를 `RoseCache.cs`에 추가하는 것이 안전. (이것이 필요하면 별도 mini-patch로 진행.)
   - **aca-coder에게 지시**: `RoseCache.cs` line 540-560 구간을 읽어, `mipData[i] = _gpuCompressor.Compress*` 결과가 빈 배열인 경우 CPU 폴백으로 분기하도록 추가. 이미 그런 로직이 있으면 생략.

3. **`_metadataSavedQueue`의 `_importDepth` 경합**: 현재 `int _importDepth` 는 메인에서만 ++/-- 하지만 FSW 핸들러가 read한다. int read는 atomic이지만 visibility가 불확실. 실제로는 메인 처리 시점에 재검사하므로 기능적 문제 없음. `Interlocked` 변환은 Phase D 범위 (이 명세서 범위 밖).

4. **`ReplaceMeshInScene`/`ReplaceTextureInScene`/`ReplaceSpriteInScene`/`ReplaceFontInScene` 및 line 841의 `_allVolumes` 순회**: Phase D에서 `Snapshot()` API로 교체되지만 Phase B 완료 시점에는 기존 그대로 둔다 (임시 `.ToArray()` 미적용). 이유: 메인에서만 호출되므로 현재도 안전하고, Phase D의 정식 교체와 충돌 방지.

5. **RoseCache 내부 `_gpuCompressor` 호출이 기본적으로 메인 스레드인가?**: 명세서는 ReimportAsync의 백그라운드 람다에서 `StoreCacheOrDefer` → `_gpuCompressor.*` 경로를 제거(B-3)했으므로, 이후 모든 GPU 압축 호출은 메인. 혹시 `AssetWarmupManager`나 다른 경로에서 백그라운드로 들어올 수 있다면 ThreadGuard가 잡아서 로그를 찍을 것.

---

## 참고

- 마스터 계획: `plans/threading-safety-fix-master.md` §Phase B (라인 195-262)
- 정적 분석 원문: `plans/static-analysis-threading-race-deadlock.md` §C1, §C2, §H2, §H4, §H6
- Phase A 명세서 (머지됨): `plans/phase-a-thread-guard.md`
- `ConcurrentDictionary` 주의: `Count` 접근은 모든 버킷 순회 비용, 자주 쓰지 말 것. `TryGetValue`와 `TryRemove`가 lock-free fast path 사용.
- `Task.Factory.StartNew(..., DenyChildAttach, TaskScheduler.Default)`: ThreadPool 고정 + child task 부착 금지 — 결정성 확보.
