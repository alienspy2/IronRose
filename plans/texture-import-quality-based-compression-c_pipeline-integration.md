# Phase 3: 파이프라인 통합 (RoseCache.cs)

## 목표
- `RoseCache.CompressTexture()` 및 `StoreTexture()`에서 **구 `compression` 메타 키 의존 제거**.
- 모든 포맷 결정을 `TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb)`로 일원화.
- **NoCompression 경로** 구현: LDR → `R8G8B8A8_UNorm(_SRgb)`, HDR → `R16_G16_B16_A16_Float`.
- **BC1 경로** 지원: Compressonator CLI `BC1` 호출, CPU 폴백은 `BCnEncoder.NET`의 `CompressionFormat.Bc1` / `Bc1WithAlpha`.
- Compressonator `-Quality` 매핑 갱신(BC7만 quality 반영, 나머지 1.0 고정).
- `FormatVersion`을 11로 bump하여 기존 캐시 자동 무효화.
- 이 Phase 완료 시: UI에서 선택한 조합이 실제 임포트 파이프라인에서 정확한 포맷으로 반영된다.

## 선행 조건
- Phase 1 (Resolver) 완료.
- Phase 2 (UI) 완료 권장. Phase 2 단독 머지 시 불일치 기간 존재.

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

#### 변경 1: FormatVersion bump
- **위치**: line 49.
- **변경**: `private const int FormatVersion = 10;` → `private const int FormatVersion = 11;`.
- **주석**: `// v11: texture_type+quality resolver 도입, BC1/NoCompression 지원`.
- **이유**: 기존 캐시(v10)는 구 compression 매핑 기반이므로 자동 무효화 후 재임포트 유도.

#### 변경 2: `StoreTexture()` (line 103–140)
- **위치**: line 110–118.
- **변경**:
  - `var compression = GetMetaString(meta, "compression", "BC7");` **삭제**.
  - `quality == "Low" && ...` 강제 BC3 블록(line 115–118) **삭제**.
  - `var textureType = GetMetaString(meta, "texture_type", "Color");` 유지.
  - `var quality = GetMetaString(meta, "quality", "High");` 유지.
  - `var isSrgb = GetMetaBool(meta, "srgb", false);` 추가.
  - `var genMips = GetMetaBool(meta, "generate_mipmaps", false);` 유지.
  - `WriteTexture(writer, texture, compression, textureType, genMips, quality);` 호출을 아래로 변경:
    - `WriteTexture(writer, texture, textureType, quality, isSrgb, genMips);` (시그니처 변경, 변경 5 참조).
  - 로그 문자열에서 `compression` 필드 제거: `"(q={quality}, type={textureType}, srgb={isSrgb}, mips={genMips})"`.

#### 변경 3: `CompressTexture()` 시그니처 및 본문 (line 427–547)
- **위치**: 메서드 전체 재작성.
- **새 시그니처**:
  ```csharp
  private static (byte[][] mipData, Veldrid.PixelFormat format) CompressTexture(
      byte[] rgbaData, int width, int height,
      string textureType, string quality, bool isSrgb,
      bool generateMipmaps = true)
  ```
  - `compression` 매개변수 삭제.
  - `isSrgb` 매개변수 추가.
- **본문 로직**:
  1. `var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);`
  2. `if (resolution.IsUncompressed || RoseConfig.DontUseCompressTexture)` → 바로 uncompressed 경로:
     - LDR: `return (new[] { rgbaData }, resolution.VeldridFormat);` (`VeldridFormat`은 `R8_G8_B8_A8_UNorm` 또는 `_SRgb`).
     - HDR NoCompression: HDR 경로는 별도 처리되므로 여기서는 LDR만. HDR은 `WriteTexture`의 `_hdrPixelData` 블록에서 분기.
  3. 압축 경로:
     - `var cliFormat = resolution.CompressonatorFormat;` (항상 non-null 보장됨, IsUncompressed=false이므로).
     - `var cliQuality = TextureCompressionFormatResolver.GetCompressonatorQuality(cliFormat, quality);`
     - `var veldridFormat = resolution.VeldridFormat;`
     - 압축 시도 순서:
       1. **CLI**: `CompressWithCompressonator(rgbaData, w, h, cliFormat, cliQuality)`.
       2. **GPU** (`_gpuCompressor != null`): 단, GPU 경로는 현재 BC7/BC5만 지원. **BC1/BC3는 GPU 건너뛰고 CPU로**. 조건: `cliFormat == "BC7" || cliFormat == "BC5"`.
       3. **CPU 폴백**: `CompressWithCpuFallback(rgbaData, w, h, cliFormat, isSrgb)` (시그니처 변경, 변경 6 참조).
  4. Mip 체인 처리는 기존 로직 유지. GPU 분기는 `cliFormat`에 따라 `CompressBC5` / `CompressBC7` 호출. BC1/BC3는 GPU 분기 진입 금지.
  5. 로그에 `resolution.DisplayLabel` 포함.
- **기존 `isNormalMap`/`isBc3` 변수 완전 제거**. 모든 분기는 `cliFormat` 문자열("BC1"/"BC3"/"BC5"/"BC7") 기반으로 전환.

#### 변경 4: CPU 폴백 (`CompressWithCpuFallback`, line 729–742)
- **새 시그니처**:
  ```csharp
  private static byte[][] CompressWithCpuFallback(byte[] rgbaData, int width, int height, string format, bool isSrgb)
  ```
- **본문**:
  - switch에 BC1 케이스 추가:
    ```csharp
    "BC1" => CompressionFormat.Bc1WithAlpha, // 알파 1비트 지원 위해 Bc1WithAlpha 사용 (Color Low 경로는 알파 무시되지만 안전 기본)
    ```
  - BCnEncoder.NET에 BC1이 있다면 그대로 사용. **확인 필요**(Phase 5에서 재검증). 없거나 예외 발생 시 → `Debug.LogWarning`하고 `CompressionFormat.Bc3`로 폴백. 경고 메시지: `"[RoseCache] BC1 CPU encoder not available, falling back to BC3 (higher size)"`.
  - `isSrgb` 매개변수는 현재 시점에서 BCnEncoder.NET에 직접 전달할 수단이 없음(raw bytes 인코딩). sRGB는 업로드 시 Veldrid 포맷 선택으로만 반영되므로 인코딩 단계에서는 무시. 매개변수는 향후 확장을 위해 받아두되 현재는 사용 안 함(주석 기록).

#### 변경 5: `WriteTexture()` 시그니처 (line 746–822)
- **새 시그니처**:
  ```csharp
  private static void WriteTexture(BinaryWriter writer, Texture2D? tex,
      string textureType, string quality, bool isSrgb, bool generateMipmaps = true)
  ```
  - `compression` 매개변수 삭제. `textureType`, `quality`, `isSrgb`로 교체.
- **본문 변경**:
  - HDR 블록(line 756–783):
    - `if (tex._hdrPixelData != null)` 내부 분기:
      - `var resolution = TextureCompressionFormatResolver.Resolve(textureType, quality, isSrgb);`
      - `if (!resolution.IsUncompressed && !RoseConfig.DontUseCompressTexture)` → BC6H 경로 (기존 `Bc6hEncoder.Encode` 사용).
      - else → float16 경로 (기존 `ConvertFloatToHalfBytes`).
  - LDR 블록(line 788–806):
    - `CompressTexture` 호출을 새 시그니처로 변경: `CompressTexture(tex._pixelData, tex.width, tex.height, textureType, quality, isSrgb, generateMipmaps)`.
- **주의**: `WriteMaterial` 내부에서 `WriteTexture`를 호출하는 부분(line 878–880)도 함께 수정:
  ```csharp
  WriteTexture(writer, mat.mainTexture, "Color", "High", true);
  WriteTexture(writer, mat.normalMap, "NormalMap", "High", false);
  WriteTexture(writer, mat.MROMap, "Color", "High", false); // MRO: linear color, 알파 무시
  ```
  - `MRO`는 기존에 `"MRO"` 타입으로 전달했으나 Resolver에 없는 타입. `Color`로 처리(fallback 동작과 동일). 또는 Resolver에 `"MRO"` 분기를 추가할지 결정 — **결정: 단순화 위해 `Color` 사용**.

#### 변경 6: 헤더 주석 블록 업데이트 (line 1–27)
- `@note`의 `Texture quality (meta): High=BC7 Q1.0, Medium=BC7 Q0.6, Low=BC3(자체가 빠름).` 블록을 갱신:
  ```
  // FormatVersion 11: texture_type + quality → TextureCompressionFormatResolver로 포맷 결정.
  //   Color/High,Med=BC7, Color/Low=BC1, ColorWithAlpha/Low=BC3, NormalMap=BC5,
  //   Sprite/Low=BC3, HDR/Panoramic=BC6H. NoCompression=R8G8B8A8(LDR)/RGBA16F(HDR).
  // 압축 우선순위: Compressonator CLI → GPU Vulkan (BC7/BC5만) → CPU BCnEncoder.NET.
  ```

## 추가/삭제할 시그니처

### 삭제
- `CompressTexture(byte[], int, int, string compression, string textureType, bool, string quality)` — 구 시그니처.
- `WriteTexture(BinaryWriter, Texture2D?, string compression, string textureType, bool, string quality)` — 구 시그니처.

### 신규/변경
- `CompressTexture(byte[], int, int, string textureType, string quality, bool isSrgb, bool generateMipmaps)` — 새 시그니처.
- `WriteTexture(BinaryWriter, Texture2D?, string textureType, string quality, bool isSrgb, bool generateMipmaps)` — 새 시그니처.
- `CompressWithCpuFallback(byte[], int, int, string format, bool isSrgb)` — `isSrgb` 추가.

## 엣지 케이스 / 기존 로직 상호작용
- **메타에 `quality`가 없는 경우**: `GetMetaString(meta, "quality", "High")`로 기본값 `High` 사용. Resolver가 BC7 반환.
- **메타에 `texture_type`이 없는 경우**: 기본 `Color`. Resolver가 LDR BC7 반환.
- **Compressonator CLI가 없는 환경 + BC1**: CLI 실패 → GPU 스킵(BC1 미지원) → CPU 폴백에서 `Bc1WithAlpha` 시도. BCnEncoder.NET이 실제로 지원하는지는 Phase 5에서 검증. 미지원 시 BC3로 폴백(크기 2배 증가하지만 기능 유지).
- **기존 v10 캐시**: FormatVersion 11로 bump했으므로 `ValidateHeader`가 false 반환 → 재임포트 자동 유도.
- **Material 내부 텍스처**(`mainTexture`, `normalMap`, `MROMap`): Material은 메타를 따로 가지지 않으므로 고정값(`"Color"/"High"/true` 등) 사용. 사용자 노출 대상 아님.
- **HDR `_hdrPixelData` 경로**: NoCompression 선택 시 RGBA16F로 저장. 기존 BC6H 경로를 NoCompression으로 건너뛸 수 있음.
- **`RoseConfig.DontUseCompressTexture`** 플래그: 기존 동작(강제 uncompressed)을 유지. Resolver 결과와 무관하게 R8G8B8A8/RGBA16F로 폴백.
- **sRGB + 압축**: Resolver가 `BC7_UNorm_SRgb` 등을 반환. 기존 코드는 `BC7_UNorm`만 사용했으므로 Veldrid 포맷 비교 코드(`format == Veldrid.PixelFormat.R8_G8_B8_A8_UNorm` 같은)가 있다면 `_SRgb` 변종도 고려해야 함. `WriteTexture` line 801–805의 `if (format != R8_G8_B8_A8_UNorm)`은 sRGB 변종을 놓친다.
  - **수정**: `resolution.IsUncompressed`로 판정하도록 변경. `if (!resolution.IsUncompressed) { tex._mipData = mipData; tex._gpuFormat = format; }`. Resolver 결과를 활용하여 일관성 확보.

## 검증 기준
- [ ] `dotnet build` 성공.
- [ ] 에디터 실행 후 PNG(알파 없음) 임포트, texture_type=Color, quality=Low 설정 → `.rosecache` 헤더의 포맷이 `BC1_Rgba_UNorm` 또는 sRGB 변종. 로그에 `BC1` 표시.
- [ ] PNG(알파 있음), texture_type=ColorWithAlpha, quality=Low → 포맷 `BC3_UNorm_SRgb`.
- [ ] quality=NoCompression → 포맷 `R8_G8_B8_A8_UNorm(_SRgb)`. 캐시 파일 크기가 크게 증가(압축 미적용).
- [ ] HDR 파일, quality=NoCompression → 포맷 `R16_G16_B16_A16_Float`.
- [ ] 기존 v10 캐시 파일이 있는 상태에서 실행 → 자동 재임포트되어 v11로 갱신.
- [ ] Compressonator CLI가 없는 환경(externalTools 임시 리네임 테스트)에서 Color/Low 임포트 → 경고 로그 후 BC1(CPU) 또는 BC3(fallback)로 저장, 크래시 없음.

## 단위 테스트 대상
- 없음. 실제 에셋 임포트로 E2E 검증.

## 참고
- 관련 플랜 §4 "파이프라인 변경" 참조.
- Phase 2와 반드시 함께 적용해야 UI ↔ 파이프라인 일관성 확보.
- 미결: `RoseConfig.DontUseCompressTexture` 활성 상태에서 `_SRgb` 여부. 현재는 `R8_G8_B8_A8_UNorm` (non-sRGB) 사용 중. **결정: 기존 동작 유지** (sRGB는 압축 경로에서만 반영).
