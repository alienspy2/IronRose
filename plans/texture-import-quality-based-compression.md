# Texture Import Quality-Based Compression

## 배경

현재 텍스처 임포트 옵션은 `compression`(BC7/BC5/BC3/none)과 `quality`(High/Medium/Low) 두 가지 드롭다운이 공존하며, 서로를 덮어쓰는 암묵적 우선순위 로직이 섞여 있다.

- UI: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`
  - `DrawTextureImporterSettings()` (line 2596~)
  - compression 드롭다운: line 2648 (`new[] { "BC7", "BC5", "BC3", "none" }`)
  - quality 드롭다운: line 2672 (`new[] { "High", "Medium", "Low" }`)
  - quality→compression 자동 덮어쓰기 로직: line 2674–2695
    - `quality == "Low"` → `compression`이 BC3/none이 아니면 강제로 BC3로 덮어씀
    - `quality == "High" | "Medium"` → `compression == "BC3"`이면 BC7로 복원
  - NormalMap은 BC5, HDR/Panoramic은 BC6H로 이미 고정 (line 2610, 2615, 2643)
- 파이프라인: `src/IronRose.Engine/AssetPipeline/RoseCache.cs`
  - 포맷 결정 (line 440–454)
  - Compressonator `-Quality` 파라미터 (line 457–464) — High=1.0, Medium=0.6, Low=0.6
  - 압축 우선순위 (line 22–23): Compressonator CLI → GPU 컴퓨트 → CPU BCnEncoder.NET
- 메타데이터 기본값: `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` (line 130–152)

**문제점**:
1. 사용자가 `compression`에서 BC7을 직접 선택해도 `quality == "Low"`면 같은 프레임에 BC3로 되돌아간다. 결과적으로 **"BC7이 선택 안 된다"**는 버그로 체감된다.
2. 두 드롭다운이 중복 개념(압축 품질)을 표현한다. 사용자가 포맷을 직접 알 필요가 없어야 한다.
3. `Color`와 `Color with Alpha`의 구분이 없어, 알파 없는 텍스처도 알파 채널을 저장하는 포맷으로 압축된다(대역폭/용량 낭비).

## 목표

1. Inspector에서 **compression 드롭다운 제거**. 사용자는 `quality`만 선택한다.
2. `quality`에 **NoCompression** 옵션 추가 → `High / Medium / Low / NoCompression` 4단계.
3. Texture Type에 **Color with Alpha** 추가.
4. compression 포맷은 `texture_type` + `quality` 조합으로 **내부 자동 결정**.

## 최종 매핑

### 포맷

| Texture Type | High | Medium | Low | NoCompression |
|--------------|------|--------|-----|---------------|
| **Color** (RGB, 알파 무시) | BC7 | BC7 | **BC1** | none |
| **Color with Alpha** (RGBA) | BC7 | BC7 | **BC3** (DXT5) | none |
| **NormalMap** | BC5 | BC5 | BC5 | none |
| **Sprite** (알파 필수) | BC7 | BC7 | BC3 | none |
| **HDR** (.hdr/.exr, 2D) | BC6H | BC6H | BC6H | none (float16) |
| **Panoramic** (HDR equirect) | BC6H | BC6H | BC6H | none (float16) |

### Compressonator `-Quality`

| 포맷 | High | Medium | Low |
|------|------|--------|-----|
| BC7 | 1.0 | 0.6 | — |
| BC1 / BC3 / BC5 / BC6H | 1.0 | 1.0 | 1.0 |

- `-Quality`는 **BC7에만 실질적 영향** (Mode 0~7 탐색 깊이). 그 외 포맷은 값과 무관하게 결과/속도 동일.
- NoCompression은 압축 파이프라인을 거치지 않고 R8G8B8A8_UNorm(LDR) 또는 RGBA16F(HDR)로 원본 저장.

## 설계

### 1. 메타데이터 스키마 변화

- `compression` 키는 **사용자가 직접 수정하지 않는 파생 값**으로 강등. 로드 시 `texture_type + quality`로 재계산하여 내부 파이프라인에 전달.
- 저장된 메타 TOML에서 `compression` 키는 **유지하지 않는다** (쓰지 않고, 있어도 무시). 마이그레이션 시 자동 제거.
- `texture_type` 허용값 확장: `Color`, `ColorWithAlpha`, `NormalMap`, `Sprite`, `HDR`, `Panoramic`.
- `quality` 허용값 확장: `High`, `Medium`, `Low`, `NoCompression`.

### 2. Texture Type 자동 감지 (기본값)

`RoseMetadata.cs`의 확장자 기반 기본값 로직에 알파 채널 감지 추가:

- `.hdr`, `.exr` → `HDR`
- `.png`, `.tga` → 알파 채널 존재 여부로 `Color` vs `ColorWithAlpha` 자동 결정
- `.jpg`, `.jpeg`, `.bmp` → `Color` (알파 없음)
- 파일명에 `_normal`, `_n`, `_nrm` 힌트가 있으면 `NormalMap` 힌트 (기존 로직 유지)

알파 감지는 이미지 헤더만 읽고(풀 디코드 불필요), `System.Drawing` 또는 기존 이미지 로더의 채널 수 조회로 처리.

### 3. UI 변경 (ImGuiInspectorPanel.cs)

- `DrawTextureImporterSettings()`에서 **compression 드롭다운 전체 제거**. 사용자가 선택 불가.
- texture_type 드롭다운 옵션: `Color`, `ColorWithAlpha`, `NormalMap`, `Sprite`, `Panoramic`.
- quality 드롭다운: `High`, `Medium`, `Low`, `NoCompression`. NormalMap/HDR/Panoramic은 노출하되 의미상 High/Med/Low가 동일 결과.
- HDR 전용(파일이 `.hdr`/`.exr`)은 `HDR ↔ Panoramic` 토글만 허용 (기존 로직 유지).
- quality→compression 자동 덮어쓰기 로직(line 2674–2695) 전부 제거.
- **Compression Format 프리뷰 표시**: quality 드롭다운 바로 아래 또는 옆에 read-only 라벨로 resolver 결과 포맷을 표시(`"Format: BC7"`, `"Format: BC1"`, `"Format: R8G8B8A8 (Uncompressed)"` 등). `ImGui.BeginDisabled()` 블록 또는 단순 `ImGui.Text`로 렌더. 사용자가 texture_type/quality를 바꾸면 실시간으로 갱신된다.
  - 포맷 문자열은 `ResolveCompressionFormat()` (Phase 1)이 반환하는 값을 그대로 사용.
  - 크기 정보도 함께 노출하면 유용: `"Format: BC7 (8 bpp)"`, `"Format: BC1 (4 bpp)"`, `"Format: R8G8B8A8 (32 bpp)"`.

### 4. 파이프라인 변경 (RoseCache.cs)

- `compression` 필드 읽기를 **제거**하고, `texture_type + quality`로부터 런타임에 포맷 결정 함수 도입:
  ```
  ResolveCompressionFormat(textureType, quality) → (veldridFormat, cliFormat)
  ```
- 기존 `isNormalMap`/`isBc3` 분기 제거, 신규 resolver로 통합.
- NoCompression 경로:
  - LDR → R8G8B8A8_UNorm (sRGB 플래그 존중)
  - HDR → RGBA16F (float16)
- `-Quality` 값: BC7이면 High=1.0, Medium=0.6, 그 외 포맷은 1.0.

### 5. 폴백 경로 (BC1)

Compressonator CLI가 없는 환경에서 `Color Low` = BC1을 처리하려면 인코더 필요.

- **우선 확인**: 기존 `BCnEncoder.NET` 패키지가 BC1 지원 여부. 지원하면 포맷 분기만 추가.
- 지원 안 하면 1차 범위에서는 **Compressonator 없을 때 BC3로 폴백**하고 경고 로그 출력. BC1 인코더 추가는 후속 작업으로 분리.
- GPU 컴퓨트 쉐이더 BC1 구현은 본 플랜 **범위 밖**.

### 6. 마이그레이션

기존 프로젝트의 메타 TOML에 저장된 `compression` 값 처리:
- 메타 로드 시 `compression` 키가 있으면 무시하고 제거.
- 예외: `compression == "none"`이 저장돼 있었다면 `quality = "NoCompression"`으로 자동 변환 (사용자 의도 보존).
- 저장 시 `compression` 키를 쓰지 않음.

## Phase 분할 (후속 `aca-archi`에서 상세화)

### Phase 1 — 스키마/Resolver 도입
- `texture_type`에 `ColorWithAlpha` 추가
- `quality`에 `NoCompression` 추가
- `ResolveCompressionFormat()` 함수 구현 및 단위 테스트
- 기존 `compression` 필드 마이그레이션 (로드 시 제거)

### Phase 2 — Inspector UI 재작성
- compression 드롭다운 제거
- texture_type 드롭다운에 `ColorWithAlpha` 추가
- quality 드롭다운에 `NoCompression` 추가
- quality→compression 덮어쓰기 로직 제거
- Resolver 결과를 read-only 라벨로 프리뷰 표시 (포맷명 + bpp)

### Phase 3 — 파이프라인 통합
- `RoseCache.cs` 포맷 결정 로직을 resolver로 일원화
- NoCompression 경로 구현 (LDR / HDR 분기)
- Compressonator `-Quality` 파라미터 갱신

### Phase 4 — 기본값 자동 감지 강화
- `RoseMetadata.cs`: 알파 채널 감지로 `Color` vs `ColorWithAlpha` 기본값 분기
- 신규 임포트 시 적용, 기존 메타는 변경하지 않음

### Phase 5 — 폴백 검증 및 로깅
- BCnEncoder.NET의 BC1 지원 여부 확인
- 미지원 시 BC3 폴백 + 경고 로그
- 전체 매핑표 기반 통합 테스트 (각 texture_type × quality 조합 스냅샷)

## 범위 밖

- BC1 GPU 컴퓨트 쉐이더 구현 (후속 작업)
- BC7 컴퓨트 쉐이더의 quality 튜닝 (Compressonator가 주 파이프라인이므로 불필요)
- HDR의 NoCompression을 float32 원본 유지 (RGBA16F로 충분)
- 기존 에셋의 일괄 재임포트 자동화 (사용자가 필요 시 수동)
