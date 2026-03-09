# Phase 17: MipMesh — 메시 LOD 자동 생성 + 화면 픽셀 기반 LOD 전환

## 목표

텍스처의 Mipmap과 동일한 개념을 메시에 적용한다.
에셋 임포트 시 `generate_mipmesh` 옵션이 켜져 있으면 **meshoptimizer**를 이용해 원본 메시로부터 1/2, 1/4, 1/8, ... 단계로 단순화된 LOD 체인을 자동 생성하고, 런타임에 바운딩 박스의 화면 픽셀 크기를 기준으로 적절한 LOD를 자동 선택한다.

```
MipMesh = Mipmap for Meshes

LOD 0:  원본         10000 tri
LOD 1:  원본 × 1/2    5000 tri   ← SimplifyWithAttributes(원본, 1/2)
LOD 2:  원본 × 1/4    2500 tri   ← SimplifyWithAttributes(원본, 1/4)
LOD 3:  원본 × 1/8    1250 tri   ← SimplifyWithAttributes(원본, 1/8)
LOD 4:  원본 × 1/16    625 tri   ← SimplifyWithAttributes(원본, 1/16)
LOD 5:  원본 × 1/32    312 tri   ← ≤500 이므로 종료
```

모든 LOD는 **항상 원본(LOD 0)으로부터** 생성한다 (체이닝 금지 — 오차 누적 방지).

---

## 구현 상태

> **Phase 17 구현 완료** — 2026-02-19

- [x] meshoptimizer NuGet + P/Invoke 바인딩
- [x] MipMesh 데이터 클래스
- [x] MipMeshGenerator (Vertex Welding + SimplifyWithAttributes)
- [x] RoseMetadata 임포트 옵션 (`generate_mipmesh`, `mipmesh_min_triangles`, `mipmesh_target_error`)
- [x] AssetDatabase MipMesh 생성/로드 통합
- [x] RoseCache v5 MipMesh 직렬화
- [x] MipMeshFilter 컴포넌트
- [x] MipMeshSystem 매 프레임 LOD 선택
- [x] EngineCore 메인 루프 통합
- [x] Inspector UI (import settings + 에셋 참조)
- [x] AssetImportDemo 적용
- [x] Reimport 대응

---

## 아키텍처

### 컴포지션 방식 (MeshRenderer 상속 X)

기존 `MeshRenderer`와 렌더 루프를 수정하지 않는다.
새로운 `MipMeshFilter` 컴포넌트가 LOD 선택 후 `MeshFilter.mesh`에 현재 LOD 메시를 반영한다.

```
[GameObject]
  ├── MeshFilter          ← 기존 그대로. mesh 프로퍼티가 현재 LOD를 가리킴
  ├── MeshRenderer        ← 기존 그대로. 변경 없음
  └── MipMeshFilter (신규) ← LOD 배열 보유, 매 프레임 LOD 선택 → MeshFilter.mesh 갱신
```

장점:
- `RenderSystem.Draw` 변경 불필요
- 기존 `MeshFilter + MeshRenderer` 조합에 영향 없음
- `MipMeshFilter` 없는 오브젝트는 기존과 동일하게 동작

### 데이터 흐름

```
AssetDatabase.ImportMesh()
  → MeshImporter.Import()           : Assimp으로 GLB/FBX 로드
  → MipMeshGenerator.Generate()     : meshoptimizer로 LOD 체인 생성
    → WeldVertices()                : 동일 위치 정점 병합 (generateVertexRemap)
    → SimplifyWithAttributes() × N  : LOD 1, 2, 3, ... 생성
    → OptimizeVertexCache() × N     : 각 LOD 인덱스 캐시 최적화
  → RoseCache.StoreMesh()           : 디스크 캐시 v5 포맷
  → MeshImportResult { Mesh, Materials[], MipMesh? }

런타임 매 프레임:
  MipMeshSystem.UpdateAllLods()
    → 카메라 거리 + 바운딩 스피어 → 화면 픽셀 크기
    → log2(screenHeight / screenPixels) + mipBias → LOD 레벨
    → MeshFilter.mesh = mipMesh.lodMeshes[selectedLod]
  RenderSystem.DrawOpaqueRenderers()
    → MeshFilter.mesh 읽기 (이미 올바른 LOD가 설정됨)
```

---

## 구현 세부 사항

### 1. meshoptimizer P/Invoke 바인딩

**Meshoptimizer.NET** (BoyBaykiller v1.0.7) NuGet 패키지 사용.
단, 이 패키지는 `SimplifyWithAttributes` / `Simplify` API를 C# 래퍼로 노출하지 않으므로,
네이티브 라이브러리에 직접 P/Invoke 바인딩을 추가.

**파일**: `src/IronRose.Engine/AssetPipeline/MeshoptNative.cs`

번들 네이티브 라이브러리는 **meshoptimizer v0.21+** (vertex_lock 파라미터 포함).

```csharp
// SimplifyWithAttributes (v0.21+ — 15 params, vertex_lock 포함)
[DllImport("meshoptimizer", EntryPoint = "meshopt_simplifyWithAttributes")]
public static extern nuint SimplifyWithAttributes(
    uint[] destination, uint[] indices, nuint indexCount,
    float[] vertexPositions, nuint vertexCount, nuint vertexPositionsStride,
    float[] vertexAttributes, nuint vertexAttributesStride,
    float[] attributeWeights, nuint attributeCount,
    IntPtr vertexLock,  // IntPtr.Zero = 잠금 없음
    nuint targetIndexCount, float targetError, uint options,
    out float resultError);

// Vertex welding용 추가 바인딩
[DllImport("meshoptimizer")] GenerateVertexRemap(...)
[DllImport("meshoptimizer")] RemapIndexBuffer(...)
[DllImport("meshoptimizer")] RemapVertexBuffer(...)
```

> **주의**: `vertex_lock`은 `byte[]?` 대신 `IntPtr`로 선언.
> `byte[]?` + `null`은 .NET 마샬러에서 NULL 포인터로 변환되어야 하지만,
> `IntPtr.Zero`가 더 명시적이고 안전함.

---

### 2. MipMesh 데이터 클래스

**파일**: `src/IronRose.Engine/RoseEngine/MipMesh.cs`

```csharp
public class MipMesh
{
    public Mesh[] lodMeshes = [];
    public int LodCount => lodMeshes.Length;

    public void Dispose()
    {
        // LOD 0은 MeshImportResult.Mesh와 공유 — LOD 1부터만 해제
        for (int i = 1; i < lodMeshes.Length; i++)
            lodMeshes[i].Dispose();
    }
}
```

---

### 3. MipMeshGenerator — Vertex Welding + SimplifyWithAttributes

**파일**: `src/IronRose.Engine/AssetPipeline/MipMeshGenerator.cs`

```csharp
public static MipMesh Generate(Mesh originalMesh,
    int minTriangles = 500, float targetError = 0.02f)
```

#### Vertex Welding (핵심 전처리)

GLB/FBX 메시가 unindexed (120000 verts / 40000 tris = 정확히 3:1)인 경우,
정점이 삼각형 간 공유되지 않아 simplifier가 토폴로지를 인식하지 못함.

**해결**: `meshopt_generateVertexRemap`으로 동일 **위치(Position)** 기준 정점 병합 후,
welded positions/attributes/indices를 simplifier에 전달.

```
AntiqueBook 예시:
  원본:  120000 verts (unindexed)
  Weld:  120000 → 19992 unique position verts
  LOD 1: 20000 tri (1/2,  error=0.0020)
  LOD 2:  9998 tri (1/4,  error=0.0053)
  LOD 3:  5000 tri (1/8,  error=0.0129)
  LOD 4:  3374 tri (1/16, error=0.0199) ← targetError=0.02 한계
  LOD 5:  3352 tri (1/32, error=0.0199)
  총 6 LODs in 70ms
```

#### LOD 생성 루프

```
level = 1, 2, 3, ...
  targetTriCount = originalTriCount >> level  (원본 / 2^level)
  if targetTriCount < minTriangles → 종료
  SimplifyWithAttributes(welded, target, targetError)
  if 축소 안 됨 → 종료 (error 한계 도달)
  OptimizeVertexCache(result)  ← GPU 캐시 효율 최적화
  lodMeshes[level] = { vertices: 원본 공유, indices: 단순화된 인덱스 }
```

---

### 4. 임포트 옵션

**RoseMetadata.cs** — MeshImporter 기본값:

```toml
[importer]
type = "MeshImporter"
scale = 1.0
generate_normals = true
flip_uvs = true
triangulate = true
generate_mipmesh = false           # MipMesh LOD 생성
mipmesh_min_triangles = 500        # LOD 체인 최소 삼각형 수
mipmesh_target_error = 0.02        # 단순화 최대 허용 오차 (메시 크기 대비 비율)
```

- `mipmesh_target_error`를 높이면 (예: 0.1) 더 낮은 LOD까지 생성 가능
- 기존 `.rose` 파일에 키가 없으면 기본값으로 폴백 — 하위 호환성 유지

---

### 5. AssetDatabase 통합

**AssetDatabase.ImportMesh**:
```csharp
if (generateMipMesh)
{
    result.MipMesh = MipMeshGenerator.Generate(result.Mesh, minTriangles, targetError);
}
```

**AssetDatabase.Load\<T\>**:
- `Load<MipMesh>(path)` → MeshImportResult에서 MipMesh 추출 (캐시 히트/미스 모두 대응)

**ReplaceMeshInScene**:
- Reimport 시 MipMeshFilter.mipMesh도 함께 교체

---

### 6. RoseCache v5 — MipMesh 직렬화

캐시 포맷 버전 4 → 5.

```
[ROSE][v5][validation header]
[assetType = 1 (Mesh)]
[vertexCount][vertices]
[indexCount][indices]
[hasMipMesh: bool]
if hasMipMesh:
  [lodCount: int32]
  for LOD 1..N:             ← LOD 0 = 원본이므로 건너뜀
    [indexCount: int32]
    [indices: uint32[]]     ← 정점은 원본과 공유 — 인덱스만 저장
[materialCount][materials]
```

정점 버퍼는 원본 한 번만 저장. 각 LOD는 인덱스만 추가 저장.

---

### 7. MipMeshFilter 컴포넌트

**파일**: `src/IronRose.Engine/RoseEngine/MipMeshFilter.cs`

```csharp
public class MipMeshFilter : Component
{
    public MipMesh? mipMesh { get; set; }

    [Range(-3f, 3f)]
    [Tooltip("LOD bias. Negative = higher quality, Positive = better performance")]
    public float mipBias = 0f;

    [HideInInspector]
    public int currentLod;  // plain field (HideInInspector은 Field만 타겟)

    internal static readonly List<MipMeshFilter> _allMipMeshFilters = new();
    // OnAddedToGameObject / OnComponentDestroy에서 등록/해제
}
```

---

### 8. MipMeshSystem — 매 프레임 LOD 선택

**파일**: `src/IronRose.Engine/RoseEngine/MipMeshSystem.cs`

`EngineCore`의 렌더 루프에서 `DrawOpaqueRenderers` **직전**에 호출:

```csharp
MipMeshSystem.UpdateAllLods();
```

LOD 선택 공식:
```
screenPixels = (worldRadius / (distance × tan(fov/2))) × screenHeight

LOD 레벨 = floor(log2(screenHeight / screenPixels) + mipBias)

예시 (screenHeight = 1080):
  화면 가득 (1080px) → log2(1) + 0 = 0  → LOD 0
  540px             → log2(2) + 0 = 1  → LOD 1
  270px             → log2(4) + 0 = 2  → LOD 2
  135px             → log2(8) + 0 = 3  → LOD 3

mipBias = -1 → 한 단계 높은 LOD (품질 우선)
mipBias = +1 → 한 단계 낮은 LOD (성능 우선)
```

---

### 9. Inspector UI

**Asset Inspector** — `DrawMeshImporterSettings`:
- `generate_mipmesh` bool 토글
- `mipmesh_min_triangles` int 드래그 (50~5000)
- `mipmesh_target_error` float 드래그 (step=0.001)

**GameObject Inspector**:
- `MipMesh` 타입은 `SkipPropertyTypes`에 추가 (직접 표시 제외)
- `AssetNameExtractors`에 MipMesh → `"name (N LODs)"` 표시

---

### 10. AssetImportDemo 적용

**파일**: `src/IronRose.Demo/FrozenCode/AssetImportDemo.cs`

메시 로드 후 MipMesh가 있으면 MipMeshFilter 컴포넌트를 자동 부착:

```csharp
var mipMesh = Resources.Load<MipMesh>(glbPath);
if (mipMesh != null && mipMesh.LodCount > 1)
{
    var mipFilter = _meshObj.AddComponent<MipMeshFilter>();
    mipFilter.mipMesh = mipMesh;
}
```

---

## 파일 변경 목록

| 파일 | 변경 내용 |
|---|---|
| `IronRose.Engine.csproj` | `Meshoptimizer.NET` v1.0.7 NuGet 추가 |
| `AssetPipeline/MeshoptNative.cs` | **신규** — P/Invoke 바인딩 (SimplifyWithAttributes, Simplify, GenerateVertexRemap, RemapIndexBuffer, RemapVertexBuffer 등) |
| `RoseEngine/MipMesh.cs` | **신규** — MipMesh 데이터 클래스 |
| `RoseEngine/MipMeshFilter.cs` | **신규** — MipMeshFilter 컴포넌트 (전역 레지스트리, mipBias, currentLod) |
| `RoseEngine/MipMeshSystem.cs` | **신규** — 매 프레임 LOD 선택 시스템 (화면 픽셀 기반) |
| `AssetPipeline/MipMeshGenerator.cs` | **신규** — Vertex Welding + SimplifyWithAttributes LOD 체인 생성 |
| `AssetPipeline/MeshImporter.cs` | `MeshImportResult`에 `MipMesh?` 필드 추가 |
| `AssetPipeline/RoseMetadata.cs` | `InferImporter`에 `generate_mipmesh`, `mipmesh_min_triangles`, `mipmesh_target_error` 추가 |
| `AssetPipeline/AssetDatabase.cs` | `ImportMesh`에 MipMesh 생성 분기, `Load<MipMesh>` 지원, `ReplaceMeshInScene` 확장 |
| `AssetPipeline/RoseCache.cs` | FormatVersion 5, MipMesh LOD 인덱스 직렬화/역직렬화 |
| `Editor/ImGui/Panels/ImGuiInspectorPanel.cs` | Mesh Import Settings에 mipmesh 옵션 UI 3종, MipMesh 에셋 참조 표시, SkipPropertyTypes 추가 |
| `EngineCore.cs` | `MipMeshSystem.UpdateAllLods()` 호출 (렌더 직전) |
| `Demo/FrozenCode/AssetImportDemo.cs` | MipMesh 로드 + MipMeshFilter 부착 |

---

## 구현 중 발견된 이슈와 해결

### Meshoptimizer.NET API 미노출

Meshoptimizer.NET NuGet 패키지는 `BuildMeshlets`, `OptimizeVertexCache` 등 ~10개 함수만 C# 래핑.
`Simplify`, `SimplifyWithAttributes` 등은 노출하지 않음.
네이티브 라이브러리(`libmeshoptimizer.so`)에는 모든 심볼이 존재 (`nm -D`로 확인).

**해결**: `MeshoptNative.cs`에 `[DllImport("meshoptimizer")]`로 직접 P/Invoke 선언.

### vertex_lock 파라미터 버전 차이

meshoptimizer API 변천:
- v0.20: `SimplifyWithAttributes` 14 params (vertex_lock 없음)
- v0.21+: `SimplifyWithAttributes` 15 params (vertex_lock 추가)

NuGet 번들 네이티브 라이브러리가 v0.21+임을 크래시 테스트로 확인.
14파라미터로 호출 시 파라미터 시프트로 인한 SEGFAULT 발생.

**해결**: 15파라미터 시그니처 사용, `vertex_lock`을 `IntPtr` 타입으로 선언하여 `IntPtr.Zero` 전달.

### Unindexed 메시 (정점 3:1 비율)

GLB 에셋이 120000 verts / 40000 tris — 모든 삼각형이 고유 정점을 가진 unindexed 메시.
Simplifier가 공유 엣지를 찾지 못해 축소율 0% (원본 그대로 반환).

**해결**: `meshopt_generateVertexRemap`으로 동일 위치(Position) 기준 정점 용접(welding) 후 simplify.
120000 → 19992 unique position verts로 병합되어 정상 단순화 가능.

---

## 검증 결과

- [x] `generate_mipmesh = true`로 메시 임포트 시 LOD 체인 생성 및 로그 출력
- [x] 캐시된 MipMesh가 정상 로드/저장 (RoseCache v5)
- [x] 카메라 거리에 따라 LOD 자동 전환
- [x] `mipBias` 적용 (음수 = 고품질, 양수 = 고성능)
- [x] `MipMeshFilter` 없는 기존 오브젝트 정상 렌더링
- [x] Asset Inspector에서 토글 후 Reimport 시 LOD 체인 재생성
- [x] Shadow 패스에서도 현재 LOD 메시 사용 (MeshFilter.mesh 기반)
- [x] `dotnet build` 0 경고, 0 오류

---

## 다음 단계

→ meshoptimizer `OptimizeVertexFetch`를 임포트 파이프라인에 기본 적용
→ LOD별 사용되지 않는 정점 제거 (정점 범위 최소화 → GPU 메모리 절감)
→ LOD 전환 시 크로스페이드 / 디더링으로 팝핑 완화
→ Meshlet 생성 + Mesh Shader 파이프라인 (스태틱 메시 한정)
→ 에디터 씬 뷰에서 LOD 레벨 색상 시각화 디버그 모드
