# Phase 1: 스키마 확장 및 Compression Format Resolver 도입

## 목표
- `texture_type` 열거에 `ColorWithAlpha` 추가.
- `quality` 열거에 `NoCompression` 추가.
- `texture_type + quality + HDR 여부`를 입력으로 받아 최종 압축 포맷/업로드 포맷을 결정하는 **순수 함수 Resolver**를 신설.
- 메타데이터(`.rose`) 로드 시점에 구버전 `compression` 키를 정리하는 **마이그레이션 로직**을 도입.
- 이 Phase 완료 시: 스키마/Resolver가 독립 유닛으로 동작한다. UI/파이프라인은 Phase 2/3에서 통합한다. 빌드는 통과해야 한다.

## 선행 조건
- 없음. 이 plan의 첫 Phase이며 독립적으로 빌드 성공해야 한다.
- 이후 Phase(2~5)가 이 Phase의 Resolver/마이그레이션 로직에 의존한다.

## 생성할 파일

### `src/IronRose.Engine/AssetPipeline/TextureCompressionFormatResolver.cs`
- **역할**: `texture_type + quality + (HDR 여부) + (sRGB 여부)` 조합을 받아, GPU 업로드 포맷(`Veldrid.PixelFormat` 또는 BC6H 가상 ID), Compressonator CLI 포맷 문자열, 사용자에게 보여줄 프리뷰 라벨, bits-per-pixel을 반환한다.
- **네임스페이스**: `IronRose.AssetPipeline`
- **타입**:
  - `public static class TextureCompressionFormatResolver`
  - `public readonly record struct TextureFormatResolution(Veldrid.PixelFormat VeldridFormat, int BC6HVirtualId, string? CompressonatorFormat, string DisplayLabel, int BitsPerPixel, bool IsUncompressed, bool IsHdr)`
    - `CompressonatorFormat`이 `null`이면 압축 파이프라인을 거치지 않는다(NoCompression).
    - `BC6HVirtualId`는 BC6H 경로에만 사용하는 정수(ID=1000). 그 외 경로에서는 0.
- **주요 멤버**:
  - `public static TextureFormatResolution Resolve(string textureType, string quality, bool isSrgb)` — 아래 매핑표에 따라 결정.
  - `public static bool IsHdrType(string textureType)` — `textureType ∈ {"HDR","Panoramic"}`.
  - `public static IReadOnlyList<string> AllTextureTypes { get; }` — `{ "Color", "ColorWithAlpha", "NormalMap", "Sprite", "HDR", "Panoramic" }`.
  - `public static IReadOnlyList<string> AllQualities { get; }` — `{ "High", "Medium", "Low", "NoCompression" }`.
  - `public static double GetCompressonatorQuality(string compressonatorFormat, string quality)` — `-Quality` 인자 값. BC7은 High=1.0/Medium=0.6/Low=해당 없음, 그 외 포맷은 1.0 고정.
- **매핑 규칙 (구현 그대로 반영)**:

  LDR 경로:
  | textureType | High | Medium | Low | NoCompression |
  |---|---|---|---|---|
  | `Color` | BC7 | BC7 | **BC1** | R8G8B8A8_UNorm |
  | `ColorWithAlpha` | BC7 | BC7 | **BC3** | R8G8B8A8_UNorm |
  | `NormalMap` | BC5 | BC5 | BC5 | R8G8B8A8_UNorm |
  | `Sprite` | BC7 | BC7 | BC3 | R8G8B8A8_UNorm |

  HDR 경로 (`HDR`, `Panoramic`):
  - High/Medium/Low → BC6H (virtual id 1000), CompressonatorFormat = `"BC6H"`.
  - NoCompression → `Veldrid.PixelFormat.R16_G16_B16_A16_Float`, 업로드 시 float16로 저장.

- **sRGB 반영**:
  - sRGB=true + LDR + 압축 → `BC7_UNorm_SRgb`, `BC3_UNorm_SRgb`, `BC1_Rgba_UNorm_SRgb` 사용.
  - sRGB=true + LDR + NoCompression → `R8_G8_B8_A8_UNorm_SRgb`.
  - NormalMap / HDR / Panoramic은 sRGB 무시(linear).
- **DisplayLabel 형식 예**:
  - `"BC7 (8 bpp)"`, `"BC1 (4 bpp)"`, `"BC3 (8 bpp)"`, `"BC5 (8 bpp)"`, `"BC6H (8 bpp, HDR)"`, `"R8G8B8A8 (32 bpp, Uncompressed)"`, `"RGBA16F (64 bpp, HDR Uncompressed)"`.
- **BitsPerPixel 참고값**: BC1=4, BC3=8, BC5=8, BC6H=8, BC7=8, RGBA8=32, RGBA16F=64.
- **구현 힌트**:
  - 단일 `switch` 표현식으로 LDR 경로를 분기. HDR 경로는 `IsHdrType(...)` 선분기.
  - 알 수 없는 `textureType`은 `Color`로 fallback. 알 수 없는 `quality`는 `High`로 fallback.
  - `BC6HVirtualId` 상수는 `RoseCache.cs`의 `FormatBC6H_UFloat = 1000`과 동일하게 맞춘다. 상수 중복이 부담스러우면 `RoseCache`에서 `internal const int` 로 공개하고 참조. **권장: 이 파일 안에 `public const int BC6HVirtualId = 1000;` 를 정의하고 Phase 3에서 RoseCache가 이 상수를 참조하도록 리팩터링**.
  - 파일 상단에 `// @file` 주석 블록(프로젝트 관례)을 넣을 것.

### `src/IronRose.Engine/AssetPipeline/TextureMetadataMigration.cs`
- **역할**: `.rose` TOML 로드 직후 importer 섹션의 구버전 키 마이그레이션.
- **네임스페이스**: `IronRose.AssetPipeline`
- **타입**: `internal static class TextureMetadataMigration`
- **주요 멤버**:
  - `public static bool Apply(Tomlyn.Model.TomlTable importer)` — 변경이 있었으면 `true`. 호출 측이 true일 때만 저장/플래그 설정.
- **동작**:
  1. `importer["type"]`이 `"TextureImporter"`가 아니면 즉시 `false` 반환.
  2. `compression` 키가 존재하면:
     - 값이 `"none"`이면 `quality = "NoCompression"`으로 세팅(기존 `quality`가 있어도 덮어쓴다, 단 `quality`가 이미 `NoCompression`이면 스킵).
     - 그 외 값은 단순 제거만 수행. `quality`는 건드리지 않는다(있으면 유지, 없으면 그대로 두고 Resolver fallback이 High로 처리).
     - 마지막에 `importer.Remove("compression")`.
  3. `texture_type`이 없고 파일 확장자로 추정 불가한 경우는 이 함수의 범위 밖(LoadOrCreate에서 처리).
  4. 변경이 한 번이라도 발생하면 `true` 반환.
- **구현 힌트**:
  - `Tomlyn.Model.TomlTable` API: `TryGetValue`, `Remove`, 인덱서 할당. Null 체크 방어적으로 처리.

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`
- **변경 위치**: `FromToml(TomlTable table)` 메서드 내부의 `importer` 로드 직후 (line ~241).
- **변경 내용**:
  - `meta.importer = impTable;` 직후, `TextureMetadataMigration.Apply(meta.importer)` 호출.
  - 반환값(`changed`)이 `true`이면 `meta`에 내부 플래그를 세우고 저장을 유도하는 것이 이상적이지만, `FromToml`은 저장 경로를 모른다. 따라서 **호출 측(`LoadOrCreate`)에서 후처리**한다:
    - `LoadOrCreate`에서 `FromToml` 반환 후, `TextureMetadataMigration.Apply(meta.importer)`를 한 번 더 호출하는 대신, `FromToml` 내부에서 호출하고 결과를 `meta`의 새 필드 `internal bool _migrated;`에 기록. `LoadOrCreate`가 `_migrated == true`면 `meta.Save(rosePath)` 호출 후 로그 남김.
  - 새 필드 추가: `internal bool _migrated { get; set; }`. TOML에 직렬화되지 않는다(프로퍼티 `ToConfig`에 들어가지 않으므로 안전).
- **이유**: 구버전 메타(`compression = "BC3"` 등)에서 `compression` 키를 자동 제거하고, `compression = "none"`은 `quality = "NoCompression"`으로 이관.

### `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` (두 번째 수정)
- **변경 위치**: `InferImporter()` 내 `.png/.jpg/.jpeg/.tga/.bmp` 분기(line 141–152)와 `.hdr/.exr` 분기(line 130–140).
- **변경 내용**:
  - `.png/.jpg/.jpeg/.tga/.bmp` 분기: `["compression"] = "BC3"` 제거. `["quality"] = "Low"`는 유지(기본 저화질). `["texture_type"] = "Color"` 유지.
    - 알파 채널 자동 감지는 **Phase 4**에서 처리. 이 Phase에서는 기본값 `Color` 그대로 둔다.
  - `.hdr/.exr` 분기: `["compression"] = "BC6H"` 제거. `["texture_type"] = "HDR"`, `["quality"] = "High"` 추가.
- **이유**: `compression` 키를 메타 스키마에서 퇴출. Resolver가 `texture_type + quality`로 결정.

## NuGet 패키지
- 변경 없음.

## 엣지 케이스 / 기존 로직 상호작용
- `FromToml`에서 `impTable`이 `null`인 경우: 기존처럼 `meta.importer`는 빈 `TomlTable`. 마이그레이션도 `type` 확인 단계에서 바로 리턴.
- 사용자가 `.rose`를 수동 편집하여 `quality = "Low"` + `compression = "BC7"`로 저장해 둔 경우: 로드 시 `compression`은 제거됨. Resolver는 `Low` → `Color`면 BC1, `ColorWithAlpha`면 BC3를 반환한다. 사용자가 "BC7을 원했음"이라면 `quality`를 `High`/`Medium`으로 바꿔야 한다(문서화 필요).
- `quality = "NoCompression"`으로 마이그레이션된 후, 기존 캐시(`.rosecache`)는 FormatVersion 변화가 없으므로 여전히 유효할 수 있다. **Phase 3에서 `FormatVersion`을 11로 bump하여 강제 재임포트**. Phase 1에서는 bump하지 않아도 된다(실제 파이프라인이 아직 구 compression 값 기반이므로 무방).
- `RoseCache.cs`가 `compression = "BC7"` 같은 값을 `GetMetaString(meta, "compression", "BC7")`으로 읽는 기존 코드: Phase 1 완료 시점에 메타에 `compression`이 없으므로 기본값 `"BC7"`이 사용된다. 이 경우 기존 `StoreTexture`의 `quality == "Low"` → `BC3` 강제 로직이 그대로 동작하므로, **Phase 1 단독 적용 시점에는 기존 파이프라인 동작이 사실상 유지된다**(BC1/NoCompression은 아직 반영 안 됨). Phase 3 머지 전까지는 이 상태가 과도기로 허용된다.

## 검증 기준
- [ ] `dotnet build`가 성공한다.
- [ ] `TextureCompressionFormatResolver.Resolve("Color", "Low", false)` 가 BC1 경로를 반환 (`CompressonatorFormat == "BC1"`, `VeldridFormat == BC1_Rgba_UNorm`).
- [ ] `TextureCompressionFormatResolver.Resolve("ColorWithAlpha", "Low", false)` 는 BC3 반환.
- [ ] `TextureCompressionFormatResolver.Resolve("HDR", "NoCompression", false)` 는 `R16_G16_B16_A16_Float`, `CompressonatorFormat == null`, `IsUncompressed == true`, `IsHdr == true`.
- [ ] `TextureCompressionFormatResolver.Resolve("Color", "NoCompression", true)` 는 `R8_G8_B8_A8_UNorm_SRgb`, `IsUncompressed == true`.
- [ ] 기존 `.rose` 파일 중 `compression = "none"`이 있는 것을 읽으면 importer에서 `compression`이 사라지고 `quality = "NoCompression"`이 세팅된다. `_migrated == true`이면 `LoadOrCreate`가 자동 저장한다.
- [ ] 신규 png 임포트 시 `.rose`에는 `compression` 키가 없다.

## 단위 테스트 대상
프로젝트에 테스트 어셈블리가 없다. 다음 중 하나 선택:
1. **간단한 런타임 Assert**: `EngineCore` 부팅 경로에 `#if DEBUG` 블록으로 Resolver 결과 몇 개를 `System.Diagnostics.Debug.Assert`로 검증(프로덕션에는 영향 없음). 부담스러우면 생략.
2. **미결 사항으로 기록**: 테스트 프레임워크 도입은 별도 plan.

본 Phase에서는 **테스트 어셈블리를 추가하지 않는다**. Resolver는 순수 함수이므로, Phase 2/3에서 UI/파이프라인 통합 후 실제 에셋 임포트로 E2E 검증한다.

## 참고
- 관련 플랜 원본: `plans/texture-import-quality-based-compression.md` §1, §4, §6.
- `RoseCache.cs` line 22–23의 헤더 주석에 적힌 우선순위(Compressonator CLI → GPU → CPU BCnEncoder.NET)는 Phase 3에서 갱신한다.
- `BC6HVirtualId = 1000` 상수는 `RoseCache.cs`의 `FormatBC6H_UFloat` 와 일치해야 한다. Phase 3에서 일원화.
- 미결: BC1을 CPU 폴백에서 지원 가능한지는 Phase 5에서 확정(BCnEncoder.NET이 `CompressionFormat.Bc1` / `Bc1WithAlpha`를 가지고 있음, Phase 5 범위).
