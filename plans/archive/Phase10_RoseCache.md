# Phase 10: RoseCache — 에셋 임포트 디스크 캐시 + BC 텍스처 압축

## Context

IronRose 엔진은 에셋 로드 시 AssimpNet(메시)과 ImageSharp(텍스처)로 매번 원본을 파싱/디코딩합니다.
또한 텍스처가 비압축 RGBA8로 GPU에 올라가 메모리 낭비가 심합니다 (2048x2048 = 16MB/장).

`RoseCache/` 폴더에 처리 완료된 바이너리를 캐시하되, 텍스처는 **BC7/BC5 압축**된 상태로 저장하여:
- 두 번째 실행부터 파싱/압축 건너뜀 (로딩 속도 향상)
- GPU 메모리 ~75% 절감 (BC7: 4MB/장)

## 변경 파일

| 파일 | 작업 |
|------|------|
| `src/IronRose.Engine/AssetPipeline/RoseCache.cs` | **신규** — 디스크 캐시 + BC 압축 파이프라인 |
| `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` | **수정** — Load<T>()에 캐시 연동 |
| `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs` | **수정** — TextureImporter 기본값 `compression = "BC7"` |
| `src/IronRose.Engine/RoseEngine/Texture2D.cs` | **수정** — BC 압축 포맷 지원 |
| `src/IronRose.Engine/IronRose.Engine.csproj` | **수정** — BCnEncoder.NET 패키지 추가 |
| `.gitignore` | **수정** — `RoseCache/` 추가 |

---

## 1. Import Options (.rose 메타데이터)

### TextureImporter 기본값 변경 (`RoseMetadata.cs`)

```toml
[importer]
type = "TextureImporter"
max_size = 2048
compression = "BC7"          # 변경: "none" → "BC7"
texture_type = "Color"       # 추가: "Color" | "NormalMap" | "MRO"
srgb = true
filter_mode = "Bilinear"
wrap_mode = "Repeat"
generate_mipmaps = true
```

### 압축 포맷 결정 규칙

| texture_type | compression | GPU 포맷 | 비고 |
|-------------|-------------|----------|------|
| Color | BC7 | `BC7_UNorm` | sRGB 컬러, 최고 품질 |
| NormalMap | BC5 | `BC5_UNorm` | RG 2채널, 탄젠트 노멀 |
| MRO | BC7 | `BC7_UNorm` | Metallic/Roughness/Occlusion |
| (any) | none | `R8_G8_B8_A8_UNorm` | 비압축 폴백 |

### MeshImporter 내장 텍스처 (GLB/glTF/FBX)

GLB 등 메시 파일에 임베디드된 텍스처도 동일하게 BC 압축 캐시 대상.
`.rose` 메타데이터가 없으므로 **머티리얼 슬롯**에 따라 자동 결정:
- `mainTexture` → BC7 (Color)
- `normalMap` → BC5 (NormalMap)
- `MROMap` → BC7 (MRO)

`StoreMesh()` 시 각 Material의 텍스처를 슬롯별 포맷으로 압축 후 저장.
`TryLoadMesh()` 시 이미 BC 압축된 텍스처가 Material에 포함되어 반환.

---

## 2. Texture2D 변경

### 필드 추가
```csharp
internal PixelFormat _gpuFormat = PixelFormat.R8_G8_B8_A8_UNorm;  // 기본값 유지
```

### UploadToGPU() 수정
- `_gpuFormat`을 사용하여 텍스처 생성 (`R8_G8_B8_A8_UNorm` 또는 `BC7_UNorm` 등)
- BC 포맷일 때: `GenerateMipmaps` 스킵 (이미 캐시에 mip별 압축 데이터 포함)
- `_pixelData`가 BC 블록 데이터인 경우 그대로 업로드

---

## 3. RoseCache.cs

위치: `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

### 핵심 구조
```
RoseCache
├── 생성자(cacheRoot) — RoseCache/ 디렉토리 생성
├── TryLoadMesh(assetPath) → MeshImportResult?
├── StoreMesh(assetPath, MeshImportResult)
├── TryLoadTexture(assetPath, RoseMetadata) → Texture2D?
├── StoreTexture(assetPath, Texture2D, RoseMetadata)
├── ClearAll()
└── private helpers
    ├── GetCachePath(assetPath) — 캐시 파일 경로 결정
    ├── ValidateHeader / WriteValidationHeader — 유효성 검증
    ├── CompressToBC7 / CompressToBC5 — BCnEncoder.NET 호출
    ├── WriteVertex / ReadVertex
    ├── WriteColor / ReadColor
    ├── WriteTexture / ReadTexture — BC 압축 데이터 포함
    └── WriteMaterial / ReadMaterial
```

### 텍스처 캐시 파이프라인 (독립 텍스처)

```
[첫 실행 - 캐시 미스]
원본 PNG/JPG
  → ImageSharp 디코딩 (RGBA8)
  → max_size 초과 시 리사이즈
  → BCnEncoder.NET으로 BC7/BC5 압축 (mip 레벨별)
  → RoseCache에 바이너리 저장
  → Texture2D(_pixelData = BC블록, _gpuFormat = BC7_UNorm)
  → GPU 업로드

[이후 실행 - 캐시 히트]
RoseCache 바이너리
  → 헤더 검증 (mtime + size)
  → BC 블록 데이터 직접 로드
  → Texture2D(_pixelData = BC블록, _gpuFormat = BC7_UNorm)
  → GPU 업로드 (파싱/압축 전부 스킵)
```

### 메시 내장 텍스처 캐시 파이프라인 (GLB/glTF/FBX)

```
[첫 실행 - 캐시 미스]
GLB 파일
  → AssimpNet 파싱 (메시 + 머티리얼 + 임베디드 텍스처)
  → Vertex/Index 데이터 추출
  → 각 Material의 텍스처 슬롯별 BC 압축:
      mainTexture  → BC7 (Color)
      normalMap    → BC5 (NormalMap)
      MROMap       → BC7 (MRO)
  → RoseCache에 메시 + 머티리얼(BC 텍스처 포함) 통째로 저장
  → GPU 업로드

[이후 실행 - 캐시 히트]
RoseCache 바이너리
  → 헤더 검증 (GLB 파일 mtime + size)
  → Vertex/Index + Material(BC 텍스처 포함) 직접 로드
  → AssimpNet 파싱 완전 스킵, 텍스처 디코딩/압축도 스킵
  → GPU 업로드
```

### 바이너리 포맷

```
[Validation Header]
  4B  매직넘버 "ROSE" (0x45534F52)
  4B  포맷 버전 (1)
  8B  원본 파일 mtime (ticks)
  8B  원본 파일 크기
  1B  .rose 파일 존재 여부
  8B? .rose 파일 mtime (존재 시만)

[Asset Type]
  1B  타입 (1=Mesh, 2=Texture)

[Mesh Payload]
  4B  vertexCount
  N×32B  Vertex[] (Position:3f + Normal:3f + UV:2f)
  4B  indexCount
  N×4B  uint[]
  4B  materialCount
  N×  Material:
    16B  color (4f)
    16B  emission (4f)
    4B   metallic (f)
    4B   roughness (f)
    4B   occlusion (f)
    mainTexture  → Texture (BC7 압축, 아래 Texture Payload 포맷)
    normalMap    → Texture (BC5 압축, 아래 Texture Payload 포맷)
    MROMap       → Texture (BC7 압축, 아래 Texture Payload 포맷)

[Texture Payload]
  4B  width
  4B  height
  4B  gpuFormat (PixelFormat enum 값)
  4B  mipCount
  per mip:
    4B  dataLength
    N×B  블록 데이터 (BC 압축 또는 RGBA 원본)
```

### 캐시 무효화 조건
- 원본 파일의 mtime 또는 크기 변경
- `.rose` 메타데이터 파일의 mtime 변경 (import 옵션 변경 감지)
- FormatVersion 변경 (코드 업데이트 시)
- 캐시 파일 손상 → 자동 삭제 후 재임포트

### 에러 처리
- 모든 캐시 I/O는 try/catch, 실패 시 일반 임포트로 폴백
- BC 압축 실패 시 비압축 RGBA로 폴백 (compression = "none" 동작)
- 캐시 실패가 엔진 동작을 절대 방해하지 않음

---

## 4. AssetDatabase.cs 수정

### 필드 추가
```csharp
private readonly RoseCache _roseCache = new(Path.Combine(Directory.GetCurrentDirectory(), "RoseCache"));
```

### Load<T>() 수정

```
1. _loadedAssets 확인 (메모리) → 미스
2. RoseCache 확인 (디스크) → 히트면 반환
3. 미스면 임포터 호출 → BC 압축 → RoseCache에 저장
4. _loadedAssets에 저장
```

### ClearCache() 추가
```csharp
public void ClearCache() => _roseCache.ClearAll();
```

---

## 5. NuGet 패키지 추가

`IronRose.Engine.csproj`에:
```xml
<PackageReference Include="BCnEncoder.Net" Version="2.1.0" />
```

BCnEncoder.NET: 순수 C# BC1~BC7 인코더/디코더. 외부 네이티브 의존성 없음.

---

## 6. .gitignore 수정

```
# Asset import cache
RoseCache/
```

---

## 검증 방법

1. `dotnet build` — 컴파일 성공
2. 첫 실행 → `[RoseCache] Cached (mesh/texture): ...` 로그, `RoseCache/` 생성 확인
3. 두 번째 실행 → `[RoseCache] Cache hit` 로그, AssimpNet/ImageSharp 로그 없음
4. GPU 메모리 비교: 비압축 대비 ~75% 감소 확인
5. 에셋 수정 후 재실행 → `[RoseCache] Cache stale` 로그, 재임포트 + 재압축
6. `.rose` 파일에서 `compression = "none"` 으로 변경 후 재실행 → 비압축 폴백 동작
