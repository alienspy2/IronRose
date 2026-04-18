---
name: Phase 1 — RoseCache 2단계 분리 (Plan / Background / FinalizeOnMain)
type: plan
parent: plans/warmup-texture-background-async.md
scope: src/IronRose.Engine/AssetPipeline/RoseCache.cs (+ 선택 신규 TextureWarmupTypes.cs)
status: draft
---

# Phase 1 — RoseCache `CompressTexture` 2단계 분리

> 상위: [warmup-texture-background-async.md](warmup-texture-background-async.md)
> 목표: `CompressTexture`를 백그라운드-가능 부분과 메인 전용 GPU 마무리 부분으로 분리하여 Phase 2/3에서 호출 가능한 API로 재설계.

---

## 다루는 변경

1. `RoseCache` 공개/내부 API에 3개 함수 + 2개 값 타입을 추가한다.
   - `public static TextureCompressionPlan PlanTextureCompression(RoseMetadata, int, int)`
   - `public static TextureCompressionResult CompressTextureBackground(TextureCompressionPlan, byte[] rgba, int w, int h)`
   - `public static (byte[][] mipData, Veldrid.PixelFormat format) FinalizeTextureOnMain(TextureCompressionPlan, TextureCompressionResult, byte[] rgba, int w, int h)`
2. 기존 `private static CompressTexture(...)`는 위 3개 호출을 순차 합성한 **내부 동기 합성 래퍼**로 유지 (기존 동기 경로 호환).
3. `StoreTexture`에 **Precompressed 오버로드**를 추가하여 이미 압축된 mipData/format을 받아 디스크 직렬화만 수행.
4. HDR/BC6H 경로도 동일한 분리를 추가 (선택이지만 Phase 1에 포함 권장):
   - `public static HdrCompressionPlan PlanHdrCompression(RoseMetadata, Texture2D)`
   - `public static HdrCompressionResult EncodeHdrBackground(HdrCompressionPlan, float[] hdrPixelData, int w, int h)`
   - `public static (byte[] data, int formatInt) FinalizeHdrOnMain(HdrCompressionPlan, HdrCompressionResult)` (실질적으로 passthrough — BC6H는 CPU)

---

## 대상 파일 / 라인

### 주 수정: `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

| 구간 | 현재 라인 | 변경 |
|------|-----------|------|
| `CompressTexture` | 442-604 | 내부 로직을 Plan/Background/Finalize 세 함수로 분해. 기존 시그니처는 합성 래퍼로 유지. |
| `CompressWithCompressonator` | 646-784 | 변경 없음 (이미 백그라운드 안전). |
| `CompressWithCpuFallback` | 793-841 | 변경 없음 (이미 백그라운드 안전). |
| `GenerateMipChain` | 606-644 | 변경 없음 (순수 CPU). |
| `StoreTexture(string, Texture2D, RoseMetadata)` | 124-157 | 내부에서 Plan → Background → Finalize → WriteTexture 순으로 합성. 외부 계약 동일. |
| `StoreTexturePrecompressed(...)` (신규) | — | 이미 계산된 mipData/format을 받아 `Texture2D._mipData`/`_gpuFormat` 대입 후 WriteTexture. |
| `WriteTexture` | 845-924 | 내부 `CompressTexture` 호출(line 896)을 제거하고, 호출자가 이미 `tex._mipData`/`tex._gpuFormat`을 채워둔 상태로 진입하도록 불변식 변경. Precompressed 경로에서 이 전제를 만족시킨다. |
| HDR 블록 | 855-883 | 현 분기 구조 유지. Plan/Background/Finalize 분해는 옵션. |

### 신규 파일 (선택): `src/IronRose.Engine/AssetPipeline/TextureWarmupTypes.cs`

공통 값 타입 정의. `RoseCache.cs`와 `AssetWarmupManager.cs`, `AssetDatabase.cs`가 공유.

```csharp
namespace IronRose.AssetPipeline
{
    public enum TextureCompressionStage
    {
        Completed,      // mipData가 이미 준비됨 (CLI 또는 CPU fallback 성공)
        NeedsGpu,       // GPU 경로에서 마무리 필요 (BC7/BC5만 해당)
        Uncompressed,   // NoCompression 또는 RoseConfig.DontUseCompressTexture
        Failed          // 예외 — 호출자가 uncompressed fallback 결정
    }

    public readonly record struct TextureCompressionPlan(
        string TextureType,
        string Quality,
        bool IsSrgb,
        bool GenerateMipmaps,
        string? CompressonatorFormat,   // null = Uncompressed
        double CompressonatorQuality,
        Veldrid.PixelFormat InitialVeldridFormat,
        bool IsUncompressed,
        bool GpuSupported               // BC7 || BC5
    );

    public sealed class TextureCompressionResult
    {
        public TextureCompressionStage Stage { get; init; }
        public byte[][]? MipData { get; init; }
        public string ActualCompressonatorFormat { get; init; } = "";
        public Veldrid.PixelFormat ActualVeldridFormat { get; init; }
        public string SourceTag { get; init; } = "";   // "CLI" / "CPU" / "UncompressedLDR"
        public long DurationMs { get; init; }
        public bool BC1FallbackApplied { get; init; }
        public Exception? Error { get; init; }
    }
}
```

**HDR용**:
```csharp
public readonly record struct HdrCompressionPlan(bool UseBc6h, int Width, int Height);

public sealed class HdrCompressionResult
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public int FormatInt { get; init; }             // FormatBC6H_UFloat 또는 PixelFormat.R16_G16_B16_A16_Float (int cast)
    public long DurationMs { get; init; }
}
```

---

## API 시그니처 상세

### `PlanTextureCompression`

```csharp
public static TextureCompressionPlan PlanTextureCompression(RoseMetadata meta, int width, int height)
{
    var textureType = GetMetaString(meta, "texture_type", "Color");
    var quality = GetMetaString(meta, "quality", "High");
    var isSrgb = GetMetaBool(meta, "srgb", false);
    var genMips = GetMetaBool(meta, "generate_mipmaps", false);
    var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);

    string? cliFormat = resolution.IsUncompressed ? null : resolution.CompressonatorFormat;
    double cliQuality = cliFormat != null
        ? TextureCompressionFormatResolver.GetCompressonatorQuality(cliFormat, quality)
        : 0.0;
    bool gpuSupported = cliFormat == "BC7" || cliFormat == "BC5";

    return new TextureCompressionPlan(
        textureType, quality, isSrgb, genMips,
        cliFormat, cliQuality,
        resolution.VeldridFormat,
        resolution.IsUncompressed,
        gpuSupported);
}
```

**순수 함수**. 백그라운드 호출 안전.

### `CompressTextureBackground`

```csharp
public static TextureCompressionResult CompressTextureBackground(
    TextureCompressionPlan plan, byte[] rgba, int width, int height)
```

**백그라운드 호출 가능**. 내부에서 `_gpuCompressor`에 절대 접근하지 않는다.

로직:
1. `plan.IsUncompressed || RoseConfig.DontUseCompressTexture`:
   - Uncompressed 경로. Veldrid 포맷을 `RoseConfig.DontUseCompressTexture`면 `R8_G8_B8_A8_UNorm`, 아니면 `plan.InitialVeldridFormat`.
   - `Stage = Uncompressed`, `MipData = new[] { rgba }`, `SourceTag = "UncompressedLDR"`, return.
2. `CompressWithCompressonator(rgba, width, height, plan.CompressonatorFormat!, plan.CompressonatorQuality)` 시도:
   - 성공 (`cliResult != null`):
     - `GenerateMipmaps`이면 각 mip별 CLI 시도. 중간 실패 시 CPU fallback full regen (현 로직 line 512-526 유지). BC1 런타임 폴백 반영.
     - `Stage = Completed`, `SourceTag = "CLI"` (또는 `"CLI+CPU"` if fallback 발생), return.
   - 실패 (null):
     - `plan.GpuSupported == true`: `Stage = NeedsGpu`, `MipData = null`, return. (메인에서 GPU 처리.)
     - 아니면: `CompressWithCpuFallback(rgba, width, height, plan.CompressonatorFormat!, plan.IsSrgb, out var actualFormat)`로 전체 CPU 처리.
       - `GenerateMipmaps`면 `GenerateMipChain` + 각 레벨 `CompressWithCpuFallback`.
       - `Stage = Completed`, `SourceTag = "CPU"`, `ActualCompressonatorFormat = actualFormat`, `BC1FallbackApplied = (actualFormat != plan.CompressonatorFormat)`, return.
3. 예외 catch: `Stage = Failed`, `Error = ex`, `MipData = new[] { rgba }` (uncompressed fallback 데이터 제공), return. 현재 line 599-603 동작 유지.

**로그**: 기존 `CompressTexture`의 로그 메시지(`"BC compress {type} ... → ..."`, `"BC compress done via ... ({fmt}, q=...)"`, `"Fallback path: CLI=..."`)는 이 함수에서 출력. 다만 "BC compress done" 로그는 `Stage == Completed`일 때만 찍고, `NeedsGpu`일 때는 `"BC compress background → needs GPU"` 같은 분리된 로그를 찍어 추적성 확보.

### `FinalizeTextureOnMain`

```csharp
public static (byte[][] mipData, Veldrid.PixelFormat format) FinalizeTextureOnMain(
    TextureCompressionPlan plan, TextureCompressionResult result, byte[] rgba, int width, int height)
```

**메인 전용**. 진입부에 `ThreadGuard.CheckMainThread("RoseCache.FinalizeTextureOnMain")` 삽입.

로직:
1. `ThreadGuard.CheckMainThread("RoseCache.FinalizeTextureOnMain") == false`: 로그 후 `(new[] { rgba }, PixelFormat.R8_G8_B8_A8_UNorm)` 반환. 호출자 스킵 유도.
2. `result.Stage == Completed`: `(result.MipData!, result.ActualVeldridFormat != 0 ? result.ActualVeldridFormat : plan.InitialVeldridFormat)` 반환. (Background가 BC1→BC3 폴백했다면 `ActualVeldridFormat` 세팅되어 있음.)
3. `result.Stage == Uncompressed`: `(result.MipData!, RoseConfig.DontUseCompressTexture ? R8_G8_B8_A8_UNorm : plan.InitialVeldridFormat)` 반환.
4. `result.Stage == NeedsGpu`:
   - `_gpuCompressor == null`일 경우 → CPU 폴백 수행 (`CompressWithCpuFallback`로 `plan.CompressonatorFormat!` 인코딩 + 필요 시 mip 체인). 반환.
   - 아니면 GPU 경로 실행:
     ```csharp
     byte[][] mipData;
     if (plan.GenerateMipmaps)
     {
         var mipChain = _gpuCompressor.GenerateMipmapsGPU(rgba, width, height);
         // GenerateMipmapsGPU이 ThreadGuard 위반으로 빈 배열 반환 시 CPU 폴백.
         if (mipChain.Length == 0) { ... CPU 폴백 ...; return; }
         mipData = new byte[mipChain.Length][];
         int mw = width, mh = height;
         for (int i = 0; i < mipChain.Length; i++)
         {
             mipData[i] = plan.CompressonatorFormat == "BC5"
                 ? _gpuCompressor.CompressBC5(mipChain[i], mw, mh)
                 : _gpuCompressor.CompressBC7(mipChain[i], mw, mh);
             if (mipData[i].Length == 0) { ... CPU 폴백 ...; return; }
             mw = Math.Max(1, mw / 2);
             mh = Math.Max(1, mh / 2);
         }
     }
     else
     {
         mipData = new byte[1][];
         mipData[0] = plan.CompressonatorFormat == "BC5"
             ? _gpuCompressor.CompressBC5(rgba, width, height)
             : _gpuCompressor.CompressBC7(rgba, width, height);
         if (mipData[0].Length == 0) { ... CPU 폴백 ...; return; }
     }
     return (mipData, plan.InitialVeldridFormat);
     ```
5. `result.Stage == Failed`: `(new[] { rgba }, PixelFormat.R8_G8_B8_A8_UNorm)` 반환 + 경고 로그.

**로그**: GPU 경로 실행 후 `"BC compress done via GPU ({fmt}, q=...): {mips} mips, {ms}ms"` (현 line 594와 동일 형식), `"Fallback path: CLI=..., GPU=true, CPU=false, BC1→BC3=false"`. CPU 폴백 발생 시 해당 태그로 기록.

### `StoreTexturePrecompressed`

```csharp
public void StoreTexturePrecompressed(
    string assetPath, Texture2D texture, RoseMetadata meta,
    byte[][] mipData, Veldrid.PixelFormat format)
```

메인 전용 (진입부 `ThreadGuard.CheckMainThread("RoseCache.StoreTexturePrecompressed")` 삽입).

로직:
1. `texture._mipData = mipData; texture._gpuFormat = format;` (Precompressed 경로에서만 필요. 아직 `WriteTexture`가 `CompressTexture`를 호출하지 않도록 변경됐기 때문에 필수.)
2. 기존 `StoreTexture` (line 124-157)의 파일 쓰기 블록(`using var fs = File.Create(...)`, `WriteValidationHeader`, `writer.Write((byte)2)`, `WriteTexture(...)`, `File.Move(...)`)을 그대로 수행.
3. `WriteTexture`는 `texture._mipData != null`이면 기존 로직(현 line 888-893)에 따라 compressed 경로로 진행되므로, 내부 `CompressTexture` 호출이 제거돼도 안전.

### `StoreTexture` (기존 시그니처 유지)

합성 경로로 리팩토링:
1. `plan = PlanTextureCompression(meta, texture.width, texture.height)`.
2. HDR 경로(`texture._hdrPixelData != null`)이면 기존 블록(line 855-883) 유지 또는 `PlanHdrCompression` + `EncodeHdrBackground` + `FinalizeHdrOnMain` 합성으로 변경 (선택).
3. LDR:
   - `result = CompressTextureBackground(plan, texture._pixelData!, texture.width, texture.height)`.
   - `(mipData, format) = FinalizeTextureOnMain(plan, result, texture._pixelData!, texture.width, texture.height)`.
   - `StoreTexturePrecompressed(assetPath, texture, meta, mipData, format)` 위임.

외부 계약은 동일. 기존 동기 호출자(예: `Reimport`, `EnsureDiskCached`)는 영향 없음.

### `WriteTexture` 불변식 변경

**기존** (line 885-924 요약):
```csharp
if (tex._mipData != null) {
    mipData = tex._mipData; format = tex._gpuFormat;
} else if (tex._pixelData != null) {
    (mipData, format) = CompressTexture(tex._pixelData, ...);   // ← 제거
    if (!IsUncompressed && !DontUseCompressTexture) {
        tex._mipData = mipData; tex._gpuFormat = format;
    }
} else {
    writer.Write(0); return;
}
```

**변경 후**:
```csharp
if (tex._mipData != null) {
    mipData = tex._mipData; format = tex._gpuFormat;
} else if (tex._pixelData != null) {
    // Precompressed가 아닌 경로로 진입했다는 뜻 → 호출자가 먼저 압축했어야 함.
    // 호환을 위해 LDR raw로 저장.
    mipData = new[] { tex._pixelData };
    format = Veldrid.PixelFormat.R8_G8_B8_A8_UNorm;
    tex._gpuFormat = format;
    // 경고 로그: WriteTexture entered without precompression — writing as uncompressed.
} else {
    writer.Write(0); return;
}
```

이 변경으로 `WriteTexture`는 **순수 직렬화 함수**가 된다. 모든 압축은 상위 `StoreTexture` 또는 `StoreTexturePrecompressed`에서 완료된 상태로 도착.

---

## ThreadGuard 삽입 위치

| 위치 | Context 문자열 | 비고 |
|------|----------------|------|
| `FinalizeTextureOnMain` 진입부 | `"RoseCache.FinalizeTextureOnMain"` | 위반 시 uncompressed fallback |
| `StoreTexturePrecompressed` 진입부 | `"RoseCache.StoreTexturePrecompressed"` | 위반 시 경고 + 수행 (파일 I/O는 기술적으로는 안전하지만 규약상 메인) |
| `FinalizeHdrOnMain` 진입부 (HDR 경로 분리 시) | `"RoseCache.FinalizeHdrOnMain"` | 동일 패턴 |

**기존 가드 유지**: `GpuTextureCompressor.*`는 이미 ThreadGuard 가드 보유 (Phase B-5 완료). 변경 없음.

---

## 영향 범위

### 수정 파일
- `src/IronRose.Engine/AssetPipeline/RoseCache.cs` (주)

### 신규 파일
- `src/IronRose.Engine/AssetPipeline/TextureWarmupTypes.cs` (record/enum 모음)

### 호환성
- `RoseCache.StoreTexture(string, Texture2D, RoseMetadata)` 외부 시그니처 **동일**.
- `RoseCache.CompressTexture(...)` private static이므로 외부 영향 없음. 호환을 위해 동일 private 합성 래퍼로 유지하거나 삭제 후 모든 내부 호출을 신규 API로 교체.
- `StoreCacheOrDefer` 호출 지점 (`AssetDatabase.cs` line 604, 623) 동작 변화 없음.

---

## 실패 시나리오 & 폴백

| 시나리오 | 기대 동작 |
|----------|-----------|
| `CompressWithCompressonator` 반환 null + GPU 없음 + CPU 성공 | `Completed` (SourceTag="CPU") |
| `CompressWithCompressonator` null + GPU compressor null + CPU 실패 | `Failed` → `FinalizeTextureOnMain`이 R8G8B8A8 uncompressed로 반환 |
| CLI 성공 + BC1 중간 mip 실패 → CPU BC1 미지원 → BC3 재생성 | `Completed` (ActualCompressonatorFormat="BC3", BC1FallbackApplied=true, ActualVeldridFormat=BC3_UNorm) |
| `plan.GpuSupported == true`, 하지만 `FinalizeTextureOnMain`이 백그라운드에서 호출됨 | ThreadGuard 위반 로그 + CPU 폴백으로 마무리 |
| `GpuTextureCompressor.CompressBC7`가 ThreadGuard 위반으로 `Array.Empty<byte>()` 반환 | `FinalizeTextureOnMain`이 빈 배열 감지 → CPU 폴백 재시도 |

---

## 검증 방법

1. **단위 수준**:
   - 기존 warmup 완료 후 `.rosecache` 파일 SHA-256 기록.
   - Phase 1 적용 후 다시 warmup → 동일 해시.
2. **로그 비교**:
   - `grep "BC compress done via" Logs/editor_*.log` 로그 형식이 유지되고, 각 에셋에 대해 `CLI`/`GPU`/`CPU` 분포가 리팩토링 전후 동일.
   - `grep "Fallback path:"` 라인도 동일.
3. **빌드**:
   - `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 성공.
4. **스모크**:
   - 기존 `Reimport All` (메뉴) 경로가 동기로 작동하는지 확인 (본 Phase는 Warmup에 비동기를 도입하지 않음 — Phase 2/3에서 도입).

---

## 리뷰 체크리스트

- [ ] `PlanTextureCompression`이 순수 함수인가? (멤버/정적 mutable 상태 접근 없음)
- [ ] `CompressTextureBackground`가 `_gpuCompressor`에 접근하지 않는가?
- [ ] `FinalizeTextureOnMain` 진입부에 `ThreadGuard.CheckMainThread` 존재하는가?
- [ ] `WriteTexture`에서 `CompressTexture` 호출이 제거되었는가?
- [ ] `StoreTexturePrecompressed`가 `texture._mipData`/`_gpuFormat`을 올바르게 설정하는가?
- [ ] 기존 `StoreTexture(string, Texture2D, RoseMetadata)` 시그니처가 유지되는가?
- [ ] BC1→BC3 런타임 폴백이 Plan/Result/Finalize 경로에서 일관되게 처리되는가?
- [ ] HDR 경로가 깨지지 않는가? (분리 옵션 미적용 시 기존 inline 블록 그대로 동작)
- [ ] 모든 로그 메시지가 기존 형식을 유지하여 log grep이 계속 동작하는가?

---

## 미결 사항 (Phase 1 한정)

1. HDR/BC6H 경로도 분리할지 여부. Phase 1에서 같이 하면 Phase 3에서 warmup handoff 일관성이 좋아짐. 하지만 BC6H는 warmup 로그에 등장하지 않아 우선순위 낮음. → aca-archi가 최종 결정.
2. `TextureCompressionPlan.InitialVeldridFormat`에 sRGB variant 재도입 가능성. 현재 FormatVersion 12가 UNorm variant만 사용하므로 `Resolver.VeldridFormat`을 그대로 쓰면 됨. 미래 변경 대비 필드명을 `PreferredVeldridFormat`로 둬도 무방.
3. `CompressTextureBackground`의 SourceTag enum 여부. 문자열 대신 enum으로 강타입화. → aca-archi 재량.

---

## 다음 단계

Phase 1 머지 후 → Phase 2 ([warmup-texture-background-async-phase2-warmup-manager.md](warmup-texture-background-async-phase2-warmup-manager.md)) 진행.
