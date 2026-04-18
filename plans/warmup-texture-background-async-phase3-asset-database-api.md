---
name: Phase 3 — AssetDatabase Prepare/Finalize API 및 StoreCacheOrDefer 확장
type: plan
parent: plans/warmup-texture-background-async.md
scope: src/IronRose.Engine/AssetPipeline/AssetDatabase.cs (+ IAssetDatabase.cs)
depends_on: plans/warmup-texture-background-async-phase1-rosecache-split.md
status: draft
---

# Phase 3 — `AssetDatabase` 비동기 warmup API 신설

> 상위: [warmup-texture-background-async.md](warmup-texture-background-async.md)
> 선행: [Phase 1](warmup-texture-background-async-phase1-rosecache-split.md) — `RoseCache` Plan/Background/Finalize API.
> 병행: [Phase 2](warmup-texture-background-async-phase2-warmup-manager.md) — warmup manager 호출자. 동일 worktree에서 함께 구현.
> 플랫폼: **Windows / Linux 공통**. `_pendingPrecompressedTextures` 큐, `WarmupHandoff` record, ThreadGuard 삽입 모두 OS 독립.

---

## 다루는 변경

1. `AssetDatabase`에 warmup 전용 2단계 API 2개를 추가한다.
   - `PrepareTextureWarmupBackground(string path) → WarmupHandoff` — **백그라운드 안전**.
   - `FinalizeTextureWarmupOnMain(WarmupHandoff handoff)` — **메인 전용** (ThreadGuard).
2. `StoreCacheOrDefer`에 Precompressed 오버로드를 추가하여 이미 압축된 텍스처를 받아 재압축 없이 직렬화.
3. `_pendingCacheTextures` 큐 자료 타입을 Precompressed 지원 가능하도록 확장.
4. 기존 `EnsureDiskCached`는 동기 경로 호환을 위해 **변경하지 않는다** (전략 A, 상위 문서 §D3).

---

## 대상 파일 / 라인

### 주 수정: `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

| 구간 | 현재 라인 | 변경 |
|------|-----------|------|
| `_pendingCacheTextures` 필드 선언 | 검색 필요 (making_log/fix-rosecache-store-deferred-during-playmode.md 참조) | 자료 타입 확장 (옵션 C: 별도 큐 신설) |
| `StoreCacheOrDefer(string, Texture2D, RoseMetadata)` | 597-605 | 유지 (기존 StoreTexture 경로) |
| `StoreCacheOrDefer(string, Texture2D, RoseMetadata, byte[][] mipData, PixelFormat format)` (신규) | — | Precompressed 오버로드 |
| `FlushPendingCacheOps` | 618-633 | 두 큐 모두 flush. Precompressed 큐는 `StoreTexturePrecompressed` 호출. |
| `EnsureDiskCached` | 636-667 | 변경 없음. |
| `PrepareTextureWarmupBackground` (신규) | — | 파일 I/O + 디코드 + 백그라운드 압축. 예외는 handoff.Error에 저장. |
| `FinalizeTextureWarmupOnMain` (신규) | — | ThreadGuard + GPU 마무리 + StoreTexturePrecompressed + SpriteSubAsset 등록. |

### 보조 수정: `src/IronRose.Engine/AssetPipeline/IAssetDatabase.cs`

2개 신규 메서드 선언 추가. 단 내부 전용이라면 interface에는 올리지 않고 class에만 선언해도 무방.

권장: **interface에 포함시키지 않는다**. `AssetWarmupManager`는 구체 타입 `AssetDatabase`를 보유한다 (생성자 파라미터, `AssetWarmupManager.cs:35`). warmup 특화 API를 일반 인터페이스에 노출할 이유 없음.

---

## API 상세

### `PrepareTextureWarmupBackground`

```csharp
/// <summary>
/// 백그라운드 스레드에서 호출 안전. 텍스처 에셋을 디코드하고 CLI/CPU 압축까지 수행한다.
/// GPU 경로는 FinalizeTextureWarmupOnMain에서 메인 스레드로 처리한다.
///
/// 이 메서드는 다음 자료구조에 절대 접근하지 않는다:
/// - _loadedAssets, _guidToPath, _materialToGuid 등 공유 dict
/// - GraphicsDevice / Veldrid
/// - _allRenderers 등 컴포넌트 _all* 리스트
///
/// 허용되는 접근:
/// - 파일 I/O (RoseMetadata.LoadOrCreate, Image.Load)
/// - RoseCache 정적 API (PlanTextureCompression, CompressTextureBackground)
/// - TextureImporter.Import (순수 CPU, 내부 상태 없음)
/// </summary>
public WarmupHandoff PrepareTextureWarmupBackground(string path)
{
    try
    {
        if (RoseConfig.DontUseCache)
            return WarmupHandoff.Skip(path, reason: "DontUseCache");

        var meta = RoseMetadata.LoadOrCreate(path);
        var importerType = GetImporterType(meta);
        if (importerType != "TextureImporter")
            return WarmupHandoff.Skip(path, reason: $"importerType={importerType}");

        var tex = _textureImporter.Import(path, meta);
        if (tex == null)
            return WarmupHandoff.Skip(path, reason: "Import returned null");

        // HDR 경로
        if (tex._hdrPixelData != null)
        {
            var hdrPlan = RoseCache.PlanHdrCompression(meta, tex);   // Phase 1 옵션
            var hdrResult = RoseCache.EncodeHdrBackground(hdrPlan, tex._hdrPixelData, tex.width, tex.height);
            return WarmupHandoff.ForHdr(path, meta, tex, hdrPlan, hdrResult);
        }

        // LDR 경로
        byte[] rgba = tex._pixelData ?? throw new InvalidOperationException($"TextureImporter returned null _pixelData for {path}");
        var plan = RoseCache.PlanTextureCompression(meta, tex.width, tex.height);
        var result = RoseCache.CompressTextureBackground(plan, rgba, tex.width, tex.height);
        return WarmupHandoff.ForLdr(path, meta, tex, rgba, plan, result);
    }
    catch (Exception ex)
    {
        return WarmupHandoff.Failed(path, ex);
    }
}
```

**핵심 불변식**:
- 모든 실패는 handoff의 Error/Skip 필드로 표현. throw 없음.
- `_textureImporter`는 stateless (단일 인스턴스를 여러 Task가 공유해도 안전: `TextureImporter.cs:10-86`은 필드 없음).
- `RoseMetadata.LoadOrCreate`는 파일 읽기만 수행 → 백그라운드 안전. (`OnSaved` 발화 여부는 aca-archi가 `RoseMetadata.cs` 재확인 후 필요 시 Phase 3 명세 본문에 반영.)

### `FinalizeTextureWarmupOnMain`

```csharp
/// <summary>
/// 메인 스레드 전용. PrepareTextureWarmupBackground가 돌려준 handoff를 받아
/// GPU 경로 마무리 + 디스크 캐시 저장 + Sprite sub-asset 등록을 수행.
/// </summary>
public void FinalizeTextureWarmupOnMain(WarmupHandoff handoff)
{
    if (!ThreadGuard.CheckMainThread("AssetDatabase.FinalizeTextureWarmupOnMain"))
        return; // 위반 — 로그 후 스킵. 해당 에셋의 디스크 캐시는 이번 세션에 만들어지지 않음.

    if (handoff.IsSkip)
    {
        EditorDebug.Log($"[AssetDatabase] Warmup skip: {handoff.AssetPath} ({handoff.SkipReason})");
        return;
    }

    if (handoff.Error != null)
    {
        EditorDebug.LogError($"[AssetDatabase] Warmup bg failure: {handoff.AssetPath} — {handoff.Error.Message}");
        return;
    }

    // HDR 경로
    if (handoff.IsHdr)
    {
        // Phase 1에서 FinalizeHdrOnMain이 있으면 그것을 호출. 없으면 inline으로 처리.
        _roseCache.StoreTextureHdrPrecompressed(
            handoff.AssetPath, handoff.Texture!, handoff.Meta,
            handoff.HdrResult!.Data, handoff.HdrResult!.FormatInt);
        // HDR도 Sprite일 수 있는지? 관례상 HDR은 Sprite가 아니므로 RegisterSpriteSubAssets 스킵.
        return;
    }

    // LDR 경로
    var (mipData, format) = RoseCache.FinalizeTextureOnMain(
        handoff.Plan, handoff.Result!, handoff.Rgba!, handoff.Texture!.width, handoff.Texture.height);

    // Precompressed 결과를 큐잉 or 즉시 저장 (플레이모드 분기).
    StoreCacheOrDefer(handoff.AssetPath, handoff.Texture, handoff.Meta, mipData, format);

    // Sprite sub-asset 등록
    if (IsSpriteTexture(handoff.Meta))
    {
        var sr = BuildSpriteImportResult(handoff.Texture, handoff.Meta);
        RegisterSpriteSubAssets(handoff.AssetPath, sr, handoff.Meta);
    }
}
```

**핵심 불변식**:
- `ThreadGuard.CheckMainThread` 위반 시 바로 return. Warmup manager는 `_warmUpNext++`로 다음 에셋.
- `RegisterSpriteSubAssets`는 내부에서 `meta.Save` → `OnSaved` 트리거 가능 → FSW 큐잉 경로로 이미 안전 (Phase B-5).
- `_loadedAssets`에는 등록하지 않음. Warmup은 "디스크 캐시만" 만드는 패턴 유지 (기존 `EnsureDiskCached` 정책과 일치, `EnsureDiskCached` docstring line 635: "without keeping it in memory").

### `StoreCacheOrDefer` Precompressed 오버로드

```csharp
private void StoreCacheOrDefer(
    string path, Texture2D tex, RoseMetadata meta,
    byte[][] mipData, Veldrid.PixelFormat format)
{
    if (EditorPlayMode.IsInPlaySession)
    {
        _pendingPrecompressedTextures.Enqueue((path, tex, meta, mipData, format));
        return;
    }
    _roseCache.StoreTexturePrecompressed(path, tex, meta, mipData, format);
}
```

### `_pendingPrecompressedTextures` 큐 신설

선택지 중 **옵션 C** (별도 큐) 채택:

```csharp
private readonly System.Collections.Concurrent.ConcurrentQueue<
    (string path, Texture2D tex, RoseMetadata meta, byte[][] mipData, Veldrid.PixelFormat format)
> _pendingPrecompressedTextures = new();
```

**이유**:
- 기존 `_pendingCacheTextures` 튜플 타입과 섞으면 타입 분기 필요 → 가독성 저하.
- 서로 다른 큐여도 순서가 중요한 영역이 아님 (각 path별 단일 작업).

### `FlushPendingCacheOps` 변경

```csharp
public void FlushPendingCacheOps()
{
    int count = 0;
    while (_pendingCacheTextures.TryDequeue(out var item))
    {
        _roseCache.StoreTexture(item.path, item.tex, item.meta);
        count++;
    }
    while (_pendingPrecompressedTextures.TryDequeue(out var item))
    {
        _roseCache.StoreTexturePrecompressed(item.path, item.tex, item.meta, item.mipData, item.format);
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
```

---

## `WarmupHandoff` 설계

`src/IronRose.Engine/AssetPipeline/TextureWarmupTypes.cs` (Phase 1에서 생성된 파일)에 추가:

```csharp
public sealed class WarmupHandoff
{
    public string AssetPath { get; init; } = "";
    public RoseMetadata? Meta { get; init; }
    public Texture2D? Texture { get; init; }            // Import 결과
    public byte[]? Rgba { get; init; }                  // LDR 전용
    public TextureCompressionPlan Plan { get; init; }
    public TextureCompressionResult? Result { get; init; }
    public HdrCompressionPlan HdrPlan { get; init; }
    public HdrCompressionResult? HdrResult { get; init; }
    public bool IsHdr { get; init; }
    public bool IsSkip { get; init; }
    public string? SkipReason { get; init; }
    public Exception? Error { get; init; }

    public static WarmupHandoff Skip(string path, string reason) =>
        new() { AssetPath = path, IsSkip = true, SkipReason = reason };

    public static WarmupHandoff Failed(string path, Exception ex) =>
        new() { AssetPath = path, Error = ex };

    public static WarmupHandoff ForLdr(string path, RoseMetadata meta, Texture2D tex, byte[] rgba,
                                       TextureCompressionPlan plan, TextureCompressionResult result) =>
        new() { AssetPath = path, Meta = meta, Texture = tex, Rgba = rgba, Plan = plan, Result = result };

    public static WarmupHandoff ForHdr(string path, RoseMetadata meta, Texture2D tex,
                                       HdrCompressionPlan plan, HdrCompressionResult result) =>
        new() { AssetPath = path, Meta = meta, Texture = tex, HdrPlan = plan, HdrResult = result, IsHdr = true };
}
```

**Thread-safety**: 모든 필드가 `init;` 또는 불변. 생성 후 다른 스레드로 전달되어도 안전.

---

## ThreadGuard 삽입 위치

| 위치 | Context 문자열 |
|------|----------------|
| `FinalizeTextureWarmupOnMain` 진입부 | `"AssetDatabase.FinalizeTextureWarmupOnMain"` |
| `StoreCacheOrDefer(Precompressed 오버로드)` 내부 | (선택) `"AssetDatabase.StoreCacheOrDefer.Precompressed"` — 파일 I/O라 필수 아님. 규약 일관성을 위해 Debug 가드 권장. |
| `PrepareTextureWarmupBackground` | **삽입하지 않음**. 백그라운드 호출이 정상 경로. |

---

## 실패 시나리오 & 폴백

| 시나리오 | 기대 동작 |
|----------|-----------|
| `RoseMetadata.LoadOrCreate`가 파일 I/O 예외 | `WarmupHandoff.Failed(path, ex)` → Finalize에서 LogError + return. |
| `_textureImporter.Import`가 null 반환 (지원 안 되는 포맷) | `WarmupHandoff.Skip` 반환 → Finalize가 Log + return. |
| `CompressTextureBackground`가 `Failed` stage로 반환 | `FinalizeTextureOnMain`이 uncompressed로 폴백. 이후 `StoreTexturePrecompressed`가 R8G8B8A8로 저장. |
| 플레이모드 중 warmup 완료 (이론상 불가 — `EngineCore`가 막음) | `StoreCacheOrDefer(Precompressed)`가 `_pendingPrecompressedTextures`에 enqueue. 플레이 종료 시 flush. |
| `FinalizeTextureWarmupOnMain`이 백그라운드에서 호출됨 (버그) | ThreadGuard 위반 로그 + return. Warmup manager는 `_warmUpNext++`로 진행. |

---

## 검증 방법

1. **단위 점검**:
   - Warmup 완료 후 모든 uncached였던 에셋이 `.rosecache` 파일을 가지고 있는지 (`HasValidCache` 루프).
   - Sprite 텍스처의 sub-asset GUID가 정상 등록되는지 (`_spriteToGuid` 크기 기록).
2. **플레이모드 진입/종료 사이클**:
   - 플레이 중 에셋 Reimport (다른 경로로 발생 가능) → `_pendingPrecompressedTextures`에 쌓이는지 확인.
   - 플레이 종료 후 `FlushPendingCacheOps`가 전체 drain 하는지.
3. **ThreadGuard 로그**:
   - `grep "\[ThreadGuard\] AssetDatabase\." Logs/editor_*.log` → 0건.

---

## 리뷰 체크리스트

- [ ] `PrepareTextureWarmupBackground`가 `_loadedAssets` / `_guidToPath` / Veldrid에 접근하지 않는가?
- [ ] `PrepareTextureWarmupBackground`의 모든 예외 경로가 `WarmupHandoff.Failed`로 잡히는가?
- [ ] `FinalizeTextureWarmupOnMain` 진입부에 ThreadGuard가 있는가?
- [ ] `StoreCacheOrDefer` Precompressed 오버로드가 플레이모드 분기를 올바르게 처리하는가?
- [ ] `_pendingPrecompressedTextures` 큐가 ConcurrentQueue로 thread-safe한가?
- [ ] `FlushPendingCacheOps`가 두 큐를 모두 drain하는가?
- [ ] `RegisterSpriteSubAssets`가 warmup 경로에서 올바르게 호출되는가 (Sprite 텍스처만)?
- [ ] `EnsureDiskCached` 기존 동기 경로가 깨지지 않는가?
- [ ] `_loadedAssets`에 warmup 결과가 등록되지 **않는가** (기존 정책 유지)?
- [ ] `IAssetDatabase` 인터페이스가 과도하게 확장되지 않는가? (warmup API는 class 전용 권장)

---

## 미결 사항 (Phase 3 한정)

1. **`RoseMetadata.LoadOrCreate`의 스레드 안전성**: 백그라운드 호출 시 `OnSaved` 발화 가능성 재확인 필요. 발화한다면 Phase B-5의 `_metadataSavedQueue`가 이미 처리하므로 안전하지만, `LoadOrCreate` 내부가 별도 path를 가지는지 aca-archi가 `RoseMetadata.cs` 열람 후 확정.
2. **`_pendingCacheTextures` vs `_pendingPrecompressedTextures` 통합 여부**: 별도 큐 유지가 심플하지만, 향후 Reimport도 비동기화하면 세 번째 큐가 추가될 수 있음. 장기적으로 polymorphic `IDeferredStoreOp` 같은 추상화가 적합. → Phase 3 범위 밖.
3. **warmup 경로에서 `_loadedAssets` 등록 여부**: 현재 설계는 "등록 안 함" (기존 `EnsureDiskCached` 정책). 단 씬 로드 단계에서 즉시 `GetOrLoad` → `_roseCache.TryLoadTexture`로 이어지므로 성능 문제 없음. 그러나 Warmup 중에 다른 코드가 `_loadedAssets[path]`를 조회하려 하면 miss. 이는 기존 동작과 동일하므로 변경 불필요. → 확정.

---

## 다음 단계

Phase 2와 Phase 3를 **같은 worktree에서 함께 구현** → 빌드 + 스모크 → Phase 4 검증.
