# Phase 4: 기본값 자동 감지 (알파 채널 기반 Color vs ColorWithAlpha)

## 목표
- 신규 텍스처 임포트 시, 파일의 **알파 채널 유무를 감지**하여 `.rose`의 `texture_type` 기본값을 `Color` 또는 `ColorWithAlpha`로 분기.
- 기존 `.rose`는 변경하지 않는다(이미 저장된 것은 유저 의도 존중).
- 이 Phase 완료 시: 새 PNG/TGA 파일을 프로젝트에 드롭하면, 알파 없는 이미지는 `Color` + BC1(Low 시), 알파 있는 이미지는 `ColorWithAlpha` + BC3(Low 시)로 임포트된다.

## 선행 조건
- Phase 1 (스키마 확장, `ColorWithAlpha` 허용) 완료.
- Phase 2, 3 완료가 권장되지만 필수 아님 (이 Phase는 기본값 감지 로직만 추가).

## 수정할 파일

### `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`

#### 변경 1: `InferImporter(string assetPath)` (line 113–186)
- **위치**: `.png / .jpg / .jpeg / .tga / .bmp` 분기 (line 141–152).
- **변경 내용**:
  - 현재 `["texture_type"] = "Color"` 고정값을 `DetectDefaultTextureType(assetPath, ext)` 호출 결과로 대체.
  - `DetectDefaultTextureType`은 확장자와 파일 내용을 기반으로 `"Color"` 또는 `"ColorWithAlpha"` 반환.

#### 변경 2: 신규 static 메서드 `DetectDefaultTextureType`
- **위치**: `RoseMetadata` 클래스 내부, `InferImporter` 뒤.
- **시그니처**:
  ```csharp
  private static string DetectDefaultTextureType(string assetPath, string extLower)
  ```
- **동작**:
  1. `.jpg / .jpeg / .bmp` → 항상 `"Color"` 반환 (JPEG/BMP는 알파 없음).
  2. `.png / .tga` → 파일 헤더를 읽어 알파 채널 유무 판단:
     - **PNG**: 파일 바이트 25 위치(IHDR chunk의 color type) 확인. `color type & 4 != 0`이면 알파 존재 (2=RGB, 6=RGBA, 3=Palette+알파 가능, 4=Grayscale+Alpha).
       - 구체적 오프셋: magic 8 + IHDR length 4 + IHDR type 4 + width 4 + height 4 + bitDepth 1 = byte 25에 colorType.
       - colorType ∈ {4, 6} → `ColorWithAlpha`. colorType=3(palette)은 tRNS 청크 존재 여부로 판단하지만 **단순화 위해 palette는 ColorWithAlpha로 처리**(보수적).
     - **TGA**: byte 16 = pixel depth. 32비트이면 알파 있음(`ColorWithAlpha`), 24비트이면 알파 없음(`Color`). 16/15비트는 드물므로 `Color`로 처리.
  3. 파일 읽기 실패(파일 존재하지 않음, 읽기 권한 등) → `"Color"` fallback.
  4. 파일명 힌트(`_normal`, `_n`, `_nrm` 접미사)로 NormalMap 감지는 **이 Phase 범위 밖**(기존 로직이 있다면 유지, 없으면 추후).

- **구현 힌트**:
  ```csharp
  using var fs = new FileStream(assetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
  Span<byte> header = stackalloc byte[32];
  int read = fs.Read(header);
  if (read < 26) return "Color";

  if (extLower == ".png")
  {
      // PNG magic: 89 50 4E 47 0D 0A 1A 0A
      if (header[0] != 0x89 || header[1] != 0x50) return "Color";
      byte colorType = header[25];
      bool hasAlpha = colorType == 4 || colorType == 6 || colorType == 3;
      return hasAlpha ? "ColorWithAlpha" : "Color";
  }
  else if (extLower == ".tga")
  {
      if (read < 18) return "Color";
      byte pixelDepth = header[16];
      return pixelDepth == 32 ? "ColorWithAlpha" : "Color";
  }
  return "Color";
  ```
  - `try/catch (IOException)` 및 일반 `Exception`으로 감싸 안전하게 fallback.
  - `EditorDebug.LogWarning`으로 실패 시 경고 (선택).

## 엣지 케이스 / 기존 로직 상호작용
- **기존 `.rose`가 이미 존재**: `LoadOrCreate`는 파일이 있으면 `FromToml`로 로드하므로 `InferImporter`가 호출되지 않는다. 따라서 기존 에셋의 `texture_type`은 변경되지 않는다. **의도된 동작**.
- **사용자가 Color로 지정했는데 알파가 있는 PNG**: 사용자 선택 존중(기존 `.rose` 로드 경로). 알파 채널은 압축 시 무시된다(BC1은 3채널).
- **사용자가 ColorWithAlpha로 지정했는데 알파 없는 PNG**: 정상 동작. BC3가 알파 부분을 모두 1로 저장. 약간의 낭비지만 기능상 문제 없음.
- **PNG palette + tRNS 조합**: 보수적으로 `ColorWithAlpha` 취급(약간 과보호). 실 프로젝트에서 palette PNG 사용 빈도 낮음.
- **TGA RLE 압축**: 헤더 구조는 동일하므로 pixel depth만 보면 됨. 영향 없음.
- **`.jpg`의 경우**: 항상 Color. JPEG는 알파 미지원(표준).
- **`.bmp`의 경우**: 32비트 BMP는 이론상 알파 가능하지만 렌더링 관점에서 거의 쓰이지 않음. Color로 처리.
- **파일이 아직 쓰이는 중인 경우**(import watcher 레이스 컨디션): 파일 읽기 실패 → `Color` fallback. 사용자가 수동으로 재임포트하거나 `.rose`에서 바꿀 수 있음.

## 검증 기준
- [ ] `dotnet build` 성공.
- [ ] 알파 없는 PNG(RGB, colorType=2) 새로 드롭 → `.rose` 자동 생성 시 `texture_type = "Color"`.
- [ ] 알파 있는 PNG(RGBA, colorType=6) 새로 드롭 → `texture_type = "ColorWithAlpha"`.
- [ ] JPG 드롭 → `"Color"`.
- [ ] 32비트 TGA 드롭 → `"ColorWithAlpha"`.
- [ ] 24비트 TGA 드롭 → `"Color"`.
- [ ] 기존에 `texture_type = "Color"`로 저장된 `.rose`가 있는 알파 PNG → 재임포트 시에도 `"Color"` 유지(기존 메타 존중).

## 단위 테스트 대상
- 없음. 수동 검증(다양한 포맷 테스트 이미지로).

## 참고
- 관련 플랜 §2 "Texture Type 자동 감지" 참조.
- 알파 감지는 **헤더만** 읽는다. 풀 디코드 불필요. `System.IO.FileStream` + `Read`만 사용.
- 이 Phase는 UI/파이프라인과 독립적이므로 Phase 2/3 머지 이후 별도 머지 가능.
