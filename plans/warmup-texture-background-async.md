---
name: Warmup/Reimport 텍스처 비동기 파이프라인 — UI Freeze 제거
type: plan
date: 2026-04-18
scope: IronRose.Engine (AssetWarmupManager, AssetDatabase, AssetPipeline/RoseCache, TextureImporter)
depends_on:
  - plans/threading-safety-fix-master.md
  - plans/phase-b-asset-database.md
status: draft
related_logs:
  - Logs/editor_20260418_125322.log  # 36 assets warmup 191.7s, BC7 1024x1024 2회 60초 타임아웃
---

# Warmup/Reimport 텍스처 비동기 파이프라인 — UI Freeze 제거

> 참조: [plans/threading-safety-fix-master.md](threading-safety-fix-master.md) §Phase B, §보호장치 정책
> 산출: 본 문서는 상위 설계. 각 서브 Phase 상세 명세서는 **`aca-archi`** 가 이어서 작성.
> 관련 규칙: `CLAUDE.md` §스레드 안전 규칙

---

## 배경

### 증상
에디터 기동 시 텍스처 reimport(warmup) 단계에서 UI가 장시간 freeze된다. 사용자 체감은 "에디터가 멈췄다". `Logs/editor_20260418_125322.log` 기준:

- 12:53:37 warmup 시작 → 12:56:41 warmup 완료 (**191.7초**, 36 assets).
- 이 시간 동안 ImGui가 완전 무응답 (단일 메인 스레드 블로킹).
- 최악의 케이스는 1024×1024 BC7 Quality=High 이미지 2개:
  - `car_test.png` 1024×1024 BC7 q=1.0 → CLI 타임아웃(60초) 후 GPU 폴백으로 **61057ms** 메인 블로킹 (log line 235~238).
  - `Sudoku/bg_notebook.png` 1024×1024 BC7 q=1.0 → CLI 타임아웃(60초) 후 GPU 폴백으로 **60314ms** 메인 블로킹 (log line 302~305).
  - `Sudoku/board_bg.png` 512×512 BC7 q=1.0 → CLI 38188ms 메인 블로킹 (log line 309~311).
- 그 외 작은 텍스처는 100~1000ms 범위지만 누적되면 여전히 수십 초.

### 근본 원인
`src/IronRose.Engine/AssetWarmupManager.cs:93-104` — 텍스처 에셋은 **메인 스레드 동기**로 `_assetDatabase.EnsureDiskCached(path)`를 호출한다. 주석은 *"GPU 텍스처 압축 필요"* 라고 설명하지만, 실제로는:

1. **CLI 경로**(`RoseCache.CompressWithCompressonator`): 외부 프로세스 실행 + PNG temp I/O + DDS 파싱. **순수 CPU/I-O 연산**, GPU 필요 없음.
2. **CPU 폴백**(`RoseCache.CompressWithCpuFallback`): BCnEncoder.NET. **순수 CPU 연산**.
3. **GPU 폴백**(`GpuTextureCompressor.CompressBC7/CompressBC5/GenerateMipmapsGPU`): Veldrid compute. **메인 전용** (이미 `ThreadGuard.CheckMainThread` 가드 존재, [plans/phase-b-asset-database.md](phase-b-asset-database.md) B-5 참조).

즉 현재 파이프라인은 세 경로 중 2개(CLI, CPU)가 백그라운드로 이동 가능한데도, 맨 앞의 GPU 폴백 가능성 때문에 **전체를 메인에서 돌리고 있다.** CLI가 60초 타임아웃으로 빠지는 경우에도 메인이 그대로 블록된다.

### 사용자 제약
- **품질 저하 금지**. 60초 타임아웃 단축이나 큰 이미지 CLI 스킵 같은 "단기 완화"는 사용자가 거부.
- Freeze 자체를 백그라운드 이동으로 제거해야 함. 총 처리 시간은 유지/개선.

---

## 목표

1. **Warmup 중 UI freeze 제거**. 메인 스레드는 프레임마다 수 ms 이내로 짧게만 점유.
2. **품질 유지**. CLI 타임아웃(60초) 및 BC 품질 설정 모두 변경 없음.
3. **GPU 경로 안전성**. GPU 폴백은 계속 메인 전용. 위반 시 `ThreadGuard`로 로그 + 스킵 (throw 없음).
4. **자료구조 안전성**. `Texture2D` 객체 생성/`_loadedAssets` 등록/sub-asset 등록은 메인에서만.
5. **Reimport 경로와의 일관성**. `ReimportAsync`의 기존 패턴(`Task<ReimportResult>` + `ProcessReimport`에서 메인 마무리)을 확장하여 재사용.

---

## 현재 상태 (정리)

### 주요 파일 / 라인

| 파일 | 역할 | 핵심 라인 |
|------|------|-----------|
| `src/IronRose.Engine/AssetWarmupManager.cs` | 프레임당 1개씩 warmup 진행 | 58-105 (`ProcessFrame`), 90 (mesh → Task.Run), 97 (texture → 메인 동기) |
| `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` | 에셋 임포트/캐시 진입점 | 636-667 (`EnsureDiskCached`), 597-615 (`StoreCacheOrDefer`) |
| `src/IronRose.Engine/AssetPipeline/RoseCache.cs` | 캐시 read/write, BC 압축 우선순위 | 124-157 (`StoreTexture`), 442-604 (`CompressTexture`), 606-644 (`GenerateMipChain`), 646-784 (`CompressWithCompressonator`), 793-841 (`CompressWithCpuFallback`), 845-924 (`WriteTexture`) |
| `src/IronRose.Engine/AssetPipeline/TextureImporter.cs` | 이미지 로드 (SixLabors) | 17-86 (`Import`, PNG/JPEG 디코드) — **순수 CPU** |
| `src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs` | Veldrid compute BC7/BC5 | 이미 진입부에 `ThreadGuard.CheckMainThread` 존재 |
| `src/IronRose.Engine/RoseEngine/Texture2D.cs` | GPU 텍스처 객체 | 178-186 (`CreateFromCompressed`, 메인 권장) |

### 현재 호출 체인 (메인 동기)

```
AssetWarmupManager.ProcessFrame()                    [메인]
  └─ AssetDatabase.EnsureDiskCached(path)            [메인]
       ├─ _textureImporter.Import(path, meta)        [메인 — 실제로는 CPU-only]
       └─ StoreCacheOrDefer(path, tex, meta)         [메인]
            └─ _roseCache.StoreTexture(...)          [메인]
                 └─ CompressTexture(...)             [메인 — 여기서 60초+ 블로킹]
                      ├─ CompressWithCompressonator() [CPU/I-O. 백그라운드 가능]
                      ├─ _gpuCompressor.GenerateMipmapsGPU + CompressBC7/BC5 [메인 전용]
                      └─ CompressWithCpuFallback()   [CPU. 백그라운드 가능]
```

### 호출자 감사 (`EnsureDiskCached` 및 관련 API)

`grep EnsureDiskCached` 결과:
1. `src/IronRose.Engine/AssetWarmupManager.cs:90` (mesh, Task.Run)
2. `src/IronRose.Engine/AssetWarmupManager.cs:97` (texture, 메인 동기) ← **개선 대상**
3. `src/IronRose.Engine/AssetPipeline/IAssetDatabase.cs:25` (interface declaration)

`StoreTexture` 호출자:
1. `AssetDatabase.StoreCacheOrDefer` (Texture 오버로드, line 604)
2. `AssetDatabase.FlushPendingCacheOps` (playmode 종료 후, line 623)
3. `AssetPipeline/RoseCache.cs` 내부 호출 없음 (외부 진입점)

`Reimport` / `ReimportAsync` 호출자: [plans/phase-b-asset-database.md](phase-b-asset-database.md) §사전조사 참조.

### 이미 완료된 안전장치

- `GpuTextureCompressor.Initialize/CompressBC7/CompressBC5/GenerateMipmapsGPU/Dispose`에 `ThreadGuard.CheckMainThread` 가드 존재 (Phase B-5 완료, [plans/phase-b-asset-database.md](phase-b-asset-database.md)).
- `Bc6hEncoder.Encode/Decode`는 순수 CPU (HDR 경로, WriteTexture line 855-883).
- `Texture2D.DefaultNormal/DefaultMRO`는 `Lazy<T>` with `LazyThreadSafetyMode.ExecutionAndPublication` (M2 완료).

---

## 설계

### 개요

**핵심 아이디어**: `CompressTexture`를 **2단계**로 쪼갠다.
1. **Stage 1 (백그라운드)**: CLI 시도 → 성공 시 BC 데이터 반환. 실패/타임아웃 시 CPU 폴백으로 시도 → 성공 시 BC 데이터 반환. 둘 다 실패하거나 GPU만 지원하는 포맷이면 "GPU 필요" 신호.
2. **Stage 2 (메인)**: Stage 1 결과를 받아 GPU 경로가 필요하면 GPU 압축 실행. 완료된 BC 데이터 + 포맷을 `Texture2D._mipData`/`_gpuFormat`에 기록하고 `WriteTexture`로 디스크 직렬화 + `_loadedAssets` 등록.

백그라운드로 옮기는 것은 다음 3 종류의 순수 연산:
- **텍스처 디코드** (`TextureImporter.Import` → `Image.Load<Rgba32>`): 수십~수백 ms.
- **CLI 압축** (`CompressWithCompressonator`): 60초+ 차지하는 주범.
- **CPU 폴백** (`CompressWithCpuFallback` + `GenerateMipChain`).

메인에 남겨야 하는 것:
- `GpuTextureCompressor.*` 호출 (Veldrid compute).
- `Texture2D` 생성자 / `_pixelData` / `_mipData` / `_gpuFormat` 최종 대입.
- `RoseCache.StoreTexture` 내부의 파일 쓰기는 실제로는 I/O 뿐이지만, 관행상 기존 `Inspector Reimport`와 동일하게 **메인에서 수행**하여 `_loadedAssets` / `_roseCache` 내부 상태 접근과 직렬화를 단순화.

### Phase 개요 (큰 구조)

```
              Phase 1 (RoseCache 리팩토링)
         CompressTexture → Plan + Finalize 2단계 분리
                     │
                     ▼
              Phase 2 (AssetWarmupManager)
        텍스처 엔트리도 Task.Run 기반 파이프라인으로 변경
        (BackgroundWork → 다음 프레임 메인 FinalizeOnMain)
                     │
                     ▼
              Phase 3 (AssetDatabase 통합 API)
          EnsureDiskCachedAsync() 추가 (백그라운드 분할)
        StoreCacheOrDefer 분기 추가 (사전 압축된 텍스처 지원)
                     │
                     ▼
              Phase 4 (검증 + 회귀 방지)
       로그 패턴 확인 / ThreadGuard 위반 확인 / warmup 시간 실측
```

- **Phase 1~3는 같은 worktree**로 묶어 진행 (서로 API 계약이 연동됨).
- **Phase 4는 동일 worktree 내의 검증 단계** (코드 변경 최소, 주로 스모크 테스트).

### 상위 데이터 흐름 (새 구조)

```
AssetWarmupManager.ProcessFrame()                            [메인]
  ├─ (텍스처 에셋인 경우)
  │     _backgroundTextureTask = Task.Run(() => {            [백그라운드]
  │         // 1. 이미지 디코드
  │         rgba = TextureImporter.ImportRaw(path, meta)
  │         // 2. 플랜 생성 (포맷, 경로)
  │         plan = RoseCache.PlanTextureCompression(...)
  │         // 3. CLI 또는 CPU로 BC 데이터 생성 (가능한 경우)
  │         bc = RoseCache.CompressTextureBackground(plan, rgba)
  │         // bc.Stage: Completed | NeedsGpu
  │         return new TextureWarmupHandoff(path, plan, rgba, bc)
  │     })
  │
  ├─ (다음 프레임) _backgroundTextureTask.IsCompleted == true
  │     handoff = _backgroundTextureTask.Result
  │     if (handoff.Error != null) log + skip, 다음 에셋
  │     else: RoseCache.FinalizeTextureOnMain(handoff)        [메인]
  │             ├─ GPU 필요 시 GpuTextureCompressor.* 호출
  │             ├─ Texture2D 객체 조립 (_pixelData/_mipData/_gpuFormat)
  │             ├─ WriteTexture로 디스크 직렬화 (.rosecache)
  │             └─ return Texture2D
  │     _assetDatabase.FinalizeTextureWarmupOnMain(path, tex, meta, handoff)
  │             ├─ StoreCacheOrDefer 경로 (디스크 쓰기는 이미 수행됨 → 필요 시 스킵)
  │             └─ IsSpriteTexture(meta) ? RegisterSpriteSubAssets(...) : (nothing)
  │     _warmUpNext++
  │
  └─ (메시 에셋인 경우 — 기존 동작 그대로)
        Task.Run(() => _assetDatabase.EnsureDiskCached(path))
```

### 상세 설계

#### D1. `RoseCache`의 2단계 분리

**현재**: `CompressTexture(rgbaData, width, height, textureType, quality, isSrgb, generateMipmaps)` — CLI/GPU/CPU를 한 메서드에서 순차 시도.

**변경**:
- `TextureCompressionPlan`: 입력 메타 + Resolver 결과를 보관하는 불변 구조체.
  - 필드: `TextureType`, `Quality`, `IsSrgb`, `GenerateMipmaps`, `CompressonatorFormat` (CLI 포맷명, null=NoCompression), `CompressonatorQuality`, `VeldridFormat`, `IsUncompressed`, `GpuSupported` (BC7/BC5만 true).
- `CompressTextureStage`: enum { `Completed`, `NeedsGpu`, `Uncompressed`, `Failed` }.
- `TextureCompressionResult`: `Stage`, `MipData` (byte[][]? — Completed/Uncompressed일 때 완전, NeedsGpu일 때는 null), `ActualCompressonatorFormat` (BC1→BC3 런타임 폴백 반영), `ActualVeldridFormat`, `SourceTag` (로그용: "CLI"/"CPU"/"UncompressedLDR"), `DurationMs`.
- `RoseCache.PlanTextureCompression(RoseMetadata, int w, int h) → TextureCompressionPlan`: 순수 함수.
- `RoseCache.CompressTextureBackground(TextureCompressionPlan, byte[] rgba, int w, int h) → TextureCompressionResult`: **백그라운드 호출 가능**. 내부 로직:
  1. `Plan.IsUncompressed` 또는 `RoseConfig.DontUseCompressTexture` → `Uncompressed`, mip0 = rgba 그대로, Veldrid=R8G8B8A8_UNorm.
  2. 아니면 `CompressWithCompressonator` 시도 → 성공 시 mipmap 체인 구성(CLI로 각 mip 개별 압축, 중간 실패 시 CPU fallback full regen 현 로직 유지). 결과 `Completed` + `SourceTag="CLI"`.
  3. CLI 실패 + `Plan.GpuSupported == true` → `NeedsGpu` 반환 (MipData는 null). 이 때 메인이 GPU 경로를 돌린다.
  4. CLI 실패 + `Plan.GpuSupported == false` → `CompressWithCpuFallback` 실행. `Completed` + `SourceTag="CPU"`. BC1→BC3 런타임 폴백 반영.
  5. 전체 실패(예외) → `Failed` (또는 R8G8B8A8 uncompressed로 `Completed` 폴백 — 기존 line 599-603 유지).
- `RoseCache.FinalizeTextureOnMain(TextureCompressionPlan, TextureCompressionResult result, byte[] rgba, int w, int h) → (byte[][] mipData, Veldrid.PixelFormat format)`:
  - `ThreadGuard.CheckMainThread("RoseCache.FinalizeTextureOnMain")` 가드 삽입 (위반 시 result의 기존 데이터로 최선 폴백 + LogError).
  - `result.Stage == Completed` → 그대로 반환.
  - `result.Stage == NeedsGpu` → GPU 경로 실행:
    - `Plan.GenerateMipmaps` 이면 `_gpuCompressor.GenerateMipmapsGPU(rgba, w, h)` 후 각 mip에 `CompressBC7/CompressBC5`.
    - 아니면 mip0만 `CompressBC7/CompressBC5`.
    - 실패 시 (ThreadGuard 위반 → 빈 배열) `CompressWithCpuFallback` 재시도 후 반환.
  - `result.Stage == Uncompressed` → `(new[] { rgba }, R8G8B8A8_UNorm)`.
  - `result.Stage == Failed` → `(new[] { rgba }, R8G8B8A8_UNorm)` 폴백.

**WriteTexture 경로**: 현재 `WriteTexture`가 내부에서 `CompressTexture`를 호출한다 (line 896). 이 호출을 **제거**하고, `StoreTexture`는 호출자로부터 "이미 계산된 mipData + format"을 받는 오버로드를 추가:
- `StoreTexture(string assetPath, Texture2D texture, RoseMetadata meta)` — 기존 시그니처 (동기 경로 호환). 내부에서 `PlanTextureCompression` + `CompressTextureBackground` + `FinalizeTextureOnMain`을 순차 호출.
- `StoreTexturePrecompressed(string assetPath, Texture2D texture, RoseMetadata meta, byte[][] mipData, Veldrid.PixelFormat format)` — 새 오버로드. 이미 압축된 결과를 받아 직렬화만 수행. `texture._mipData`/`_gpuFormat`에 반영.

**HDR (BC6H) 경로**: `_hdrPixelData != null` 경로(line 855-883)는 `Bc6hEncoder.Encode` (CPU) 호출이므로 백그라운드 이동 가능. 같은 구조로 `PlanHdrCompression` / `EncodeHdrBackground` / `FinalizeHdrOnMain` 추가 (Phase 1에 포함).

#### D2. `AssetWarmupManager` 리팩토링

**현재**: 85-104 — 메시는 `Task.Run`, 텍스처는 메인 동기.

**변경**: 상태 머신을 확장한다.

필드 추가:
```csharp
// 현재 warmup 항목의 백그라운드 디코드/압축 작업.
private Task<WarmupHandoff>? _textureBackgroundTask;

// 타입 정의 (AssetWarmupManager 또는 AssetPipeline 하위):
internal record WarmupHandoff(
    string AssetPath,
    RoseMetadata Meta,
    Texture2D? Texture,          // HDR float / LDR rgba 보유 (아직 _mipData 미설정)
    byte[] Rgba,                 // LDR만
    int Width, int Height,
    TextureCompressionPlan Plan,
    TextureCompressionResult Result,
    Exception? Error
);
```

`ProcessFrame` 흐름:
1. `_backgroundTask`(기존 메시용) 또는 `_textureBackgroundTask` 중 하나라도 진행 중이면 `IsCompleted` 확인. 미완이면 return.
2. 완료된 경우:
   - 메시: 기존 로직 유지 (`_backgroundTask.Exception` 로깅 + `_warmUpNext++`).
   - 텍스처: `_textureBackgroundTask.Result`에서 handoff 획득 → `_assetDatabase.FinalizeTextureWarmupOnMain(handoff)` 호출 (신규 API) → `_warmUpNext++`.
3. 다음 에셋 선택:
   - 메시 → `_backgroundTask = Task.Run(() => _assetDatabase.EnsureDiskCached(path))` (기존과 동일).
   - 텍스처 → `_textureBackgroundTask = Task.Run(() => _assetDatabase.PrepareTextureWarmupBackground(path))` (신규 API, 백그라운드 전용).

**장점**: 한 프레임에 한 에셋씩 진행하는 기존 UX를 유지. 프로그레스 바(CurrentIndex/CurrentAssetName/ElapsedSeconds)도 그대로 동작.

#### D3. `AssetDatabase` 신규 API 2개

```csharp
// 1) 백그라운드 전용 — 파일 로드 + CLI/CPU 압축까지.
public WarmupHandoff PrepareTextureWarmupBackground(string path);

// 2) 메인 전용 — GPU 마무리 + Texture2D 등록 + 디스크 캐시 저장 + sub-asset 등록.
public void FinalizeTextureWarmupOnMain(WarmupHandoff handoff);
```

**`PrepareTextureWarmupBackground`**:
- `RoseConfig.DontUseCache` → 빈 handoff (skip 플래그) 반환. `ProcessFrame`은 그냥 `_warmUpNext++`로 넘어간다.
- `meta = RoseMetadata.LoadOrCreate(path)` — 파일 I/O만, 메인 자료구조 접근 없음. 단 `RoseMetadata.LoadOrCreate`가 `OnSaved`를 트리거하지 않는지 확인 필요. 트리거한다면 이는 FSW 큐잉 경로로 이미 dedup되므로 안전. ([plans/phase-b-asset-database.md](phase-b-asset-database.md) B-5 경로에서 보장됨.)
- `importerType = GetImporterType(meta)` → `"TextureImporter"` 가 아니면 빈 handoff.
- `tex = _textureImporter.Import(path, meta)` — 순수 CPU (SixLabors). `_pixelData` 또는 `_hdrPixelData` 보유한 Texture2D 객체 반환. **주의**: Texture2D 객체 자체를 백그라운드에서 생성하는 것은 `VeldridTexture`/`TextureView` 필드가 null인 상태라 안전 (Veldrid 리소스 생성은 `UploadToGPU`에서만 발생).
- `plan = RoseCache.PlanTextureCompression(meta, tex.width, tex.height)`.
- `result = RoseCache.CompressTextureBackground(plan, tex._pixelData ?? tex._hdrPixelData-bytes, w, h)`.
- handoff 반환. Exception 발생 시 handoff.Error에 저장하고 반환 (throw하지 않음).

**`FinalizeTextureWarmupOnMain`**:
- `ThreadGuard.CheckMainThread("AssetDatabase.FinalizeTextureWarmupOnMain")` 가드 삽입. 위반 시 로그 + 바로 return (`_warmUpNext++`는 호출자가 이미 책임).
- `handoff.Error != null` → `EditorDebug.LogError` + return.
- `RoseCache.FinalizeTextureOnMain(handoff)` 호출 → `(mipData, veldridFormat)` 획득.
- `Texture2D` 객체 완성: `handoff.Texture._mipData = mipData; handoff.Texture._gpuFormat = veldridFormat;` (기존 `StoreTexture` line 900-907 동작과 동일).
- `RoseCache.StoreTexturePrecompressed(path, tex, meta, mipData, veldridFormat)` — 디스크 직렬화. 이 경로는 **메인 전용**으로 간주 (파일 I/O지만 `StoreCacheOrDefer`와 동일 원칙).
- `StoreCacheOrDefer` 분기는 유지. 플레이모드 여부 판정 + `_pendingCacheTextures` enqueue 또는 즉시 `StoreTexturePrecompressed` 호출. 즉 `StoreCacheOrDefer`에 **Precompressed 오버로드**를 추가.
- `IsSpriteTexture(meta)` → `BuildSpriteImportResult` + `RegisterSpriteSubAssets`.
- Warmup 경로에서는 `_loadedAssets`에 등록하지 않는다 (기존 `EnsureDiskCached`가 "without keeping it in memory" 주석대로 등록 안 함, line 635).

**기존 `EnsureDiskCached`는 유지**: 외부 인터페이스(`IAssetDatabase.EnsureDiskCached`, CLI 경로 등)가 사용하므로 삭제하지 않는다. 내부 구현을 "Prepare + Finalize"로 합성해서 재구성하거나, 단순 호환 쉼으로 남겨둔다. 두 전략 중:
- **전략 A (권장)**: `EnsureDiskCached`는 기존 동기 구현 유지 (메인에서 동기 호출됨). Warmup에서만 새 비동기 API 사용.
- **전략 B**: `EnsureDiskCached` 내부를 `PrepareTextureWarmupBackground` + `FinalizeTextureWarmupOnMain` 동기 합성으로 리팩토링. → 동작 일관성은 좋지만 변경 범위가 크고 비동기 경로 오류 발생 시 원인 추적이 어려움.

**결정**: 전략 A로 진행. 서브 Phase 2에서 확정.

#### D4. 에러/폴백 시나리오 매트릭스

| 경로 | Stage 1 (백그라운드) | Stage 2 (메인) | 결과 | 품질 영향 |
|------|----------------------|----------------|------|-----------|
| CLI 성공 (BC7/BC1/BC3/BC5) | `Completed` + mipData | passthrough | 정상 | 없음 |
| CLI 타임아웃 + BC7/BC5 | `NeedsGpu` | GPU compute | 정상 (GPU 품질) | 기존과 동일 |
| CLI 타임아웃 + BC1/BC3 | `CompressWithCpuFallback` 즉시 수행 → `Completed` | passthrough | 정상 (CPU BCnEncoder) | 기존과 동일 |
| CLI 성공 + 중간 mip 실패 + BC1→BC3 런타임 폴백 | `Completed` (전체 체인 CPU 재생성) | passthrough | 정상 | 기존과 동일 |
| GPU 미지원 포맷 + CPU 폴백 중 예외 | `Failed` | R8G8B8A8 uncompressed | 품질 저하 + 경고 로그 | 기존과 동일 (line 599-603 폴백) |
| ThreadGuard 위반 (GPU 단계가 메인 아님) | `NeedsGpu` 상태로 도착 | `CheckMainThread == false` → CPU 폴백 재시도 | 품질 저하 최소화 (CPU 폴백) | Warmup은 메인 실행이라 발생 불가. 방어선만 존재. |
| `RoseConfig.DontUseCache == true` | skip 플래그 | skip | warmup 자체가 수행되지 않음 (`GetUncachedAssetPaths`가 빈 배열 반환, line 574) | 없음 |
| 플레이모드 중 warmup (이론상 불가) | 정상 진행 | `StoreCacheOrDefer`가 `_pendingCacheTextures`에 enqueue | 플레이 종료 후 flush | 없음 |

---

## 영향 범위

### 수정 파일
- `src/IronRose.Engine/AssetPipeline/RoseCache.cs` — Phase 1 핵심 (2단계 API, `StoreTexturePrecompressed`, HDR 분기).
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` — Phase 3 (`PrepareTextureWarmupBackground`, `FinalizeTextureWarmupOnMain`, `StoreCacheOrDefer` Precompressed 오버로드).
- `src/IronRose.Engine/AssetWarmupManager.cs` — Phase 2 (텍스처 Task 분기).
- `src/IronRose.Engine/AssetPipeline/IAssetDatabase.cs` — Phase 3 신규 메서드 2개 추가 (선택, 내부 전용이면 인터페이스 없이도 가능).

### 신규 파일 (선택)
- `src/IronRose.Engine/AssetPipeline/TextureWarmupTypes.cs` — `TextureCompressionPlan`, `TextureCompressionResult`, `WarmupHandoff` record 정의. RoseCache.cs와 AssetWarmupManager.cs가 공통 참조.

### 변경 없음 (영향 확인 필요)
- `src/IronRose.Engine/AssetPipeline/TextureImporter.cs` — 내부 변경 없이 그대로 백그라운드에서 호출 가능 (SixLabors ImageSharp는 thread-safe per instance). 단 `EditorDebug.Log` 호출이 백그라운드에서 발생 — CLAUDE.md `## 스레드 안전 규칙`에 의하면 `EditorDebug`는 로그 큐 기반(이미 thread-safe). 확인 필요.
- `src/IronRose.Engine/AssetPipeline/GpuTextureCompressor.cs` — 이미 ThreadGuard 적용됨. 변경 없음.
- `src/IronRose.Engine/RoseEngine/Texture2D.cs` — 생성자 자체는 Veldrid 리소스를 만들지 않아 백그라운드 생성 가능. 변경 없음.
- `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` — `LoadOrCreate`가 파일 I/O만 수행하고 공유 상태 수정 없음인지 확인 (Phase B-5에서 `OnSaved` 큐잉 확인됨).

### 기존 기능 영향
- **동기 경로 (`EnsureDiskCached`, `Reimport`, CLI `asset.reimport`)**: 전략 A로 유지 — 동작 변화 없음.
- **Warmup UX**: 프로그레스 바 동작 유지. 완료 시점만 빠르게 온다.
- **플레이모드 중 에셋 변경**: `StoreCacheOrDefer`의 `IsInPlaySession` 분기 그대로 → `_pendingCacheTextures`가 Precompressed 튜플도 받을 수 있도록 enqueue 자료 타입 확장 필요.

---

## 완료 기준 (Definition of Done)

1. **UI freeze 제거**: Warmup 중 매 프레임 메인 스레드가 점유하는 시간 ≤ 16ms (통상 ≤ 5ms). 1024×1024 BC7 이미지 처리 중에도 ImGui 반응.
   - 측정: warmup 중 `Ctrl+Shift+P` 등 ImGui 입력이 지연 없이 반응.
2. **총 warmup 시간**: 기존 191.7s 대비 동등 또는 감소 (디코드/CLI가 메인 블로킹 해제로 병렬화되므로 체감상 동일 또는 개선).
   - 목표: 동일 프로젝트 warmup이 **≤ 191s** (CPU 사용 효율은 메인 freeze 제거가 주된 이득).
3. **품질 동등성**: 생성된 `.rosecache` 바이너리가 기존과 바이트 단위로 동일 (CLI/GPU/CPU 경로 선택이 기존 우선순위 그대로).
   - 검증: warmup 전 캐시 폴더 백업 → 재 warmup → diff 결과 없음.
4. **ThreadGuard 위반 로그 0건**: 에디터 기동 ~ warmup 완료 ~ 씬 로드 사이 `[ThreadGuard]` 에러 로그가 한 번도 찍히지 않아야 함.
5. **에러 경로 graceful**: 존재하지 않는 파일 / 손상된 PNG / GPU 드라이버 비활성 상태에서 warmup이 멈추지 않고 해당 에셋만 스킵하며 진행.
6. **스모크 테스트 통과**: 아래 `## 스모크 테스트 시나리오` 6가지 모두 통과.

---

## 스모크 테스트 시나리오

1. **기본 warmup**: 빈 캐시 폴더에서 에디터 기동 → warmup 진행 중 ImGui 메뉴 여는 것이 즉시 반응.
2. **대용량 BC7**: 2048×2048 PNG 여러 개 포함된 프로젝트 warmup → UI freeze 없음.
3. **CLI 타임아웃 유발**: 특정 이미지가 CLI 60초 타임아웃 → GPU 폴백으로 진행되며 UI는 계속 반응.
4. **CLI 없는 환경**: `externalTools/compressonatorcli`를 임시 제거 → CPU 폴백 경로로 warmup 완료.
5. **Reimport All**: 메뉴에서 `Reimport All` → 캐시 클리어 + warmup 재실행. UI freeze 없이 완료.
6. **플레이모드 진입 직전/직후**: warmup 완료 후 바로 플레이 → 크래시/race 없음.

**측정 도구**:
- 로그 검색: `grep "Warm-up complete" Logs/editor_*.log` → 총 시간 비교.
- 로그 검색: `grep "BC compress done" Logs/editor_*.log | awk '{print $NF}'` → 에셋별 소요 확인.
- 로그 검색: `grep "\[ThreadGuard\]" Logs/editor_*.log` → 위반 0건 확인.

---

## 서브 Phase 분할

상세 명세서는 `aca-archi`가 이어서 작성한다. 각 서브 Phase는 별도 `.md` 문서로 분리:

| 서브 | 파일 | 내용 |
|------|------|------|
| Phase 1 | [warmup-texture-background-async-phase1-rosecache-split.md](warmup-texture-background-async-phase1-rosecache-split.md) | `RoseCache.CompressTexture` → `Plan` + `CompressTextureBackground` + `FinalizeTextureOnMain` 분리. `StoreTexture` vs `StoreTexturePrecompressed` 분화. HDR/BC6H 경로 동일 분리. |
| Phase 2 | [warmup-texture-background-async-phase2-warmup-manager.md](warmup-texture-background-async-phase2-warmup-manager.md) | `AssetWarmupManager.ProcessFrame` 상태머신 확장 (텍스처 Task.Run 분기). `WarmupHandoff` record 정의. |
| Phase 3 | [warmup-texture-background-async-phase3-asset-database-api.md](warmup-texture-background-async-phase3-asset-database-api.md) | `AssetDatabase.PrepareTextureWarmupBackground` / `FinalizeTextureWarmupOnMain` 신설. `StoreCacheOrDefer` Precompressed 오버로드. `_pendingCacheTextures` 큐 타입 확장. |
| Phase 4 | [warmup-texture-background-async-phase4-verification.md](warmup-texture-background-async-phase4-verification.md) | 스모크 테스트 실행, 로그 분석, 기준 충족 확인. 필요 시 버그 픽스. |

**Phase 의존성**: 1 → 2 → 3은 순차, Phase 2와 3은 상호 API 계약을 공유하므로 **같은 worktree에서 묶어 구현**하고 한 번에 리뷰받는 것을 권장. Phase 4는 최종 검증.

**Worktree 제안**: `feat/warmup-texture-background-async` 단일 worktree에서 Phase 1~3 진행 + Phase 4 검증.

---

## 대안 검토

### 대안 1: "큰 이미지 CLI 스킵" 또는 "CLI 타임아웃 단축"
- 장점: 코드 변경 최소.
- 단점: 품질 저하. 사용자가 거부한 방안.
- **기각**.

### 대안 2: `Task.Run` 안에서 GPU도 호출하도록 Veldrid 락 확장
- 장점: 모든 경로 백그라운드화.
- 단점: Veldrid는 단일 스레드 드라이브 가정. CLAUDE.md `## 스레드 안전 규칙` 정면 위반. `GpuTextureCompressor`에 이미 ThreadGuard 있음.
- **기각**.

### 대안 3: 전체 warmup을 단일 큰 백그라운드 Task로 수행 (현재 프레임 분할 제거)
- 장점: 구현 단순, 전체를 한 번에 끝냄.
- 단점: 프로그레스 바 UX 퇴화. GPU 폴백 필요 시 메인 동기화 포인트가 복잡해짐. CLI 실패 → GPU 전환 시 Task가 await 하나로 메인 대기해야 함.
- **기각** (복잡도 증가 대비 이득 없음).

### 대안 4: `CompressTexture`를 `async Task<...>`로 직접 변환
- 장점: C# native 패턴.
- 단점: 내부에 GPU 동기 호출(Veldrid)이 있어 깔끔한 await 경계가 없음. 복수 진입점(Plan/Background/Finalize) 분리가 명시적이지 않아 호출자(Warmup/Reimport)가 경계를 다루기 애매.
- **기각**. 명시적 2단계 분리가 더 명확.

---

## 미결 사항

1. **`RoseMetadata.LoadOrCreate`의 백그라운드 호출 안전성**: Phase B-5 이후 `OnSaved` 큐잉으로 안전하다고 판단했지만, `LoadOrCreate` 자체가 `OnSaved`를 발화하지 않는지 재확인 필요. → Phase 3 상세 명세 작성 시 aca-archi가 `RoseMetadata.cs` 소스를 다시 확인.
2. **`EditorDebug.Log`의 백그라운드 호출 안전성**: 이미 Phase 3(스레드 안전)에서 락 기반 큐잉 구현되어 있다고 가정. `src/IronRose.Contracts/EditorDebug.cs` (또는 현재 위치)에서 확인 필요.
3. **Warmup 중 Reimport 요청이 들어올 때의 순서**: 현재 `ReimportAsync`와 `AssetWarmupManager.ProcessFrame`이 동시에 돌 수 있음 (메인 틱 내 둘 다 호출). 새 경로에서도 충돌하지 않는지 확인. `_reimportTask`와 `_textureBackgroundTask`가 서로 다른 필드이고, Warmup은 `_loadedAssets` 비터치이므로 충돌 없음으로 보임.
4. **`StoreCacheOrDefer` Precompressed 오버로드의 `_pendingCacheTextures` 자료 타입**:
   - 현재: `Queue<(string path, Texture2D tex, RoseMetadata meta)>`.
   - 변경 후 옵션 A: 기존 큐 그대로 두고 Flush 시 `StoreTexture` (느림, warmup 경로는 이미 압축됨).
   - 옵션 B: 큐에 `IStoreOp` 같은 인터페이스로 polymorphic 저장.
   - 옵션 C: 별도 큐 `_pendingPrecompressedTextures` 신설.
   - → Phase 3 상세 명세 작성 시 결정.
5. **HDR (BC6H) 경로의 실제 warmup 빈도**: 현재 로그에 HDR 에셋은 warmup에 등장하지 않음. 우선순위는 낮지만 Phase 1에서 같이 분리해 두면 이후 Reimport 비동기화에도 재사용 가능.

---

## 다음 단계

**→ `aca-archi`로 Phase 1 상세 명세서 작성**.

Phase 1이 가장 API 계약을 많이 정의하므로 먼저 명세화하여 Phase 2, 3의 입력이 된다.

각 Phase `aca-archi` 호출 시 다음을 참조로 포함:
- 본 문서: `plans/warmup-texture-background-async.md`
- 스레드 안전 마스터: `plans/threading-safety-fix-master.md`
- Phase B 상세: `plans/phase-b-asset-database.md`
- 관련 소스: `RoseCache.cs`, `AssetWarmupManager.cs`, `AssetDatabase.cs`, `TextureImporter.cs`, `GpuTextureCompressor.cs`, `Texture2D.cs`
