# GLB Sub-Asset 시스템 구현 계획

## Context

현재 IronRose 엔진은 GLB 파일 하나당 GUID 하나만 부여하고, 내부의 모든 mesh를 하나로 병합하며, material은 첫 번째 것만 사용합니다. GLB 내부의 개별 mesh, material, texture를 각각 독립된 sub-asset으로 관리하고, 고유 GUID를 부여하여 개별적으로 참조/로드할 수 있도록 합니다.

**핵심 원칙: GUID 우선 참조 (Unity 방식)**
- 에셋 참조의 1순위는 GUID. Scene 직렬화, 컴포넌트 참조, 드래그-드롭 모두 GUID로 연결
- 파일 경로 기반 로드(`Load<T>(path)`)는 내부/폴백용으로만 사용
- 에디터에서 에셋을 끌어서 연결하면 GUID가 저장됨 (파일 이동해도 참조 유지)

## 수정 대상 파일 (10개)

| 파일 | 변경 내용 |
|------|----------|
| `RoseMetadata.cs` | SubAssetEntry 클래스 + sub_assets TOML 직렬화 |
| `MeshImporter.cs` | MeshImportResult 확장 + 메시 병합 제거 |
| `AssetDatabase.cs` | GUID 기반 로드, Sub-asset GUID 등록 |
| `IAssetDatabase.cs` | LoadByGuid<T> 추가, GetSubAssetPaths 추가 |
| `RoseCache.cs` | 바이너리 포맷 v6 (다중 메시) |
| `SceneSerializer.cs` | GUID 기반 에셋 참조 직렬화 |
| `AssetSpawner.cs` | 멀티 메시 스폰 (parent+children) |
| `ImGuiProjectPanel.cs` | Sub-asset 트리 표시 |
| `ImGuiInspectorPanel.cs` | Sub-asset 목록 테이블 |
| (신규) `SubAssetPath.cs` | 경로 파싱 유틸리티 |

---

## Phase 1: 데이터 모델 (기반 작업)

### 1-1. SubAssetEntry + RoseMetadata 확장
**파일:** `src/IronRose.Engine/AssetPipeline/RoseMetadata.cs`

- `SubAssetEntry` 클래스 추가: `name`, `type`(Mesh/Material/Texture2D), `index`, `guid`
- `RoseMetadata.subAssets` 리스트 추가
- `ToToml()`에서 `[[sub_assets]]` TOML 테이블 배열로 직렬화
- `FromToml()`에서 역직렬화 (기존 .rose 파일은 sub_assets 없이 정상 로드)
- `GetOrCreateSubAsset(name, type, index)` — 이름+타입으로 기존 엔트리 매칭하거나 새 GUID 생성

**.rose 파일 포맷 v2 예시:**
```toml
guid = "b37cb16c-ac66-4f37-8efb-e56e7421c51a"
version = 2
[importer]
type = "MeshImporter"
scale = 1.0

[[sub_assets]]
name = "BookMesh"
type = "Mesh"
index = 0
guid = "aaaa-bbbb-..."

[[sub_assets]]
name = "BookMaterial_0"
type = "Material"
index = 0
guid = "cccc-dddd-..."

[[sub_assets]]
name = "BookTexture_Diffuse"
type = "Texture2D"
index = 0
guid = "eeee-ffff-..."
```

### 1-2. SubAssetPath 유틸리티
**신규 파일:** `src/IronRose.Engine/AssetPipeline/SubAssetPath.cs`

- 경로 형식: `"Assets/Model.glb#Mesh:0"`, `"Assets/Model.glb#Material:1"`
- `TryParse(fullPath) → (filePath, type, index)`
- `Build(filePath, type, index) → subAssetPath`
- `IsSubAssetPath(path) → bool`

### 1-3. MeshImportResult 확장
**파일:** `src/IronRose.Engine/AssetPipeline/MeshImporter.cs`

```csharp
public class MeshImportResult
{
    public NamedMesh[] Meshes { get; set; } = [];       // 개별 메시 배열
    public Material[] Materials { get; set; } = [];
    public Texture2D[] Textures { get; set; } = [];     // 임베디드 텍스처
    public MipMesh?[] MipMeshes { get; set; } = [];     // 메시별 LOD

    // 하위 호환: 첫 번째 요소 반환
    public Mesh? Mesh => Meshes.Length > 0 ? Meshes[0].Mesh : null;
    public MipMesh? MipMesh => MipMeshes.Length > 0 ? MipMeshes[0] : null;
}

public struct NamedMesh
{
    public string Name;
    public Mesh Mesh;
    public int MaterialIndex;  // Materials[] 인덱스
}
```

---

## Phase 2: MeshImporter — 메시 병합 제거

**파일:** `src/IronRose.Engine/AssetPipeline/MeshImporter.cs`

- 현재: `foreach (var assimpMesh in scene.Meshes)` → 하나의 allVertices/allIndices로 병합
- 변경: 각 `scene.Meshes[i]`를 별도 `NamedMesh`로 보존
  - 이름: `assimpMesh.Name` 또는 `$"Mesh_{i}"` 폴백
  - `MaterialIndex`: `assimpMesh.MaterialIndex` 보존
- `ExtractMaterials`에서 material 이름 부여: `assimpMat.Name` 또는 `$"Material_{i}"`
- 임베디드 텍스처를 별도 `Textures[]` 배열로 수집 (Material에도 할당하되, 별도 참조 가능하게)

---

## Phase 3: AssetDatabase — GUID 기반 로드 + Sub-asset 등록

**파일:** `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`

### 3-1. 핵심 API: `LoadByGuid<T>(guid)` (1순위)
```csharp
// 1순위: GUID로 로드 (에디터 참조, Scene 직렬화에서 사용)
db.LoadByGuid<Mesh>("aaaa-bbbb-cccc-...")       → 개별 메시
db.LoadByGuid<Material>("dddd-eeee-ffff-...")    → 개별 머터리얼

// 2순위 (내부/폴백): 경로로 로드
db.Load<Mesh>("Assets/Model.glb")               → 첫 번째 메시 (하위 호환)
db.Load<Mesh>("Assets/Model.glb#Mesh:0")        → sub-asset 경로 (내부용)
```
- `LoadByGuid<T>(guid)`: `_guidToPath`에서 경로 조회 → `Load<T>(path)` 위임
- GUID가 sub-asset이면 부모 파일 임포트 후 sub-asset 캐시에서 반환
- **모든 외부 참조(Scene, 컴포넌트)는 GUID 사용. 경로는 내부 구현 디테일**

### 3-2. ScanAssets 확장
- .rose 파일 로드 시 메인 GUID + sub_assets의 GUID 모두 `_guidToPath`에 등록
- `_guidToPath["main-guid"] = "Assets/Model.glb"`
- `_guidToPath["sub-guid"] = "Assets/Model.glb#Mesh:0"`

### 3-3. Load<T> sub-asset 경로 지원 (내부용)
- `SubAssetPath.TryParse`로 `#` 감지 → 부모 파일 임포트 → sub-asset 캐시에서 반환
- Sub-asset 미스 시 부모 파일 임포트 → CacheSubAssets → 재시도

### 3-4. RegisterSubAssets — 임포트 후 .rose 업데이트
- 임포트 결과의 mesh/material/texture를 순회
- `meta.GetOrCreateSubAsset(name, type, index)`로 안정 GUID 확보
- `_guidToPath`에 등록 + .rose 파일 저장

### 3-5. CacheSubAssets — 메모리 캐시에 개별 등록
- `_loadedAssets["path#Mesh:0"] = mesh` 등 (내부 캐시 키는 경로)
- GUID → 경로 → 캐시 조회 체인

### 3-6. FindGuidForMesh — 메시로부터 GUID 역검색
- 메시 인스턴스 → sub-asset GUID 반환 (AssetDatabase에 등록된 경우)
- **GUID를 찾을 수 없으면 null 반환** → Scene 직렬화 시 해당 컴포넌트 무시
- 코드에서 파일 경로로 직접 로드한 메시도 AssetDatabase에 있으면 GUID 매칭됨
```csharp
public string? FindGuidForMesh(Mesh mesh);      // null이면 직렬화 무시
public string? FindGuidForMaterial(Material material);
```

### 3-7. Reimport 업데이트
- `ReplaceMeshInScene`에서 이름 기반으로 개별 메시 교체

### 3-8. IAssetDatabase 인터페이스 추가
```csharp
T? LoadByGuid<T>(string guid) where T : class;
string? FindGuidForMesh(Mesh mesh);
string? FindGuidForMaterial(Material material);
IReadOnlyList<string> GetSubAssetPaths(string filePath);
```

---

## Phase 4: RoseCache — 바이너리 포맷 v6

**파일:** `src/IronRose.Engine/AssetPipeline/RoseCache.cs`

- `FormatVersion = 6`
- StoreMesh: `meshCount` → 각 메시별 (name, materialIndex, vertices, indices, mipMesh)
- Material에도 name 필드 추가
- TryLoadMesh: v6 포맷 읽기 + v5 폴백 (단일 메시 → 배열로 래핑)
- 기존 .rosecache는 버전 불일치로 자동 무효화/재생성

---

## Phase 5: MipMesh 생성 — 메시별 LOD

**파일:** `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` (ImportMesh 메서드)

- 현재: `result.Mesh` 하나에 대해 MipMesh 생성
- 변경: `result.Meshes[]` 각각에 대해 MipMesh 생성 → `result.MipMeshes[i]`에 저장

---

## Phase 6: Scene 직렬화 — GUID 기반 참조

**파일:** `src/IronRose.Engine/Editor/SceneSerializer.cs`

### Save (직렬화)
- MeshFilter: `db.FindGuidForMesh(mf.mesh)` → GUID가 있으면 `assetGuid` 저장
- MeshRenderer: `db.FindGuidForMaterial(mr.material)` → GUID가 있으면 `materialGuid` 저장
- MipMeshFilter: 동일하게 GUID 저장
- **GUID를 찾을 수 없는 메시/머터리얼 (코드에서 직접 생성 등)은 직렬화에서 무시 (저장 안 함)**
- primitiveType 메시는 기존처럼 `primitiveType` 필드로 저장

**.scene 파일 예시 (변경 후):**
```toml
[[gameObjects.components]]
type = "MeshFilter"
[gameObjects.components.fields]
assetGuid = "aaaa-bbbb-cccc-dddd"   # sub-asset GUID

[[gameObjects.components]]
type = "MeshRenderer"
[gameObjects.components.fields]
materialGuid = "eeee-ffff-1111-2222"  # material sub-asset GUID
color = [1.0, 1.0, 1.0, 1.0]
metallic = 0.0
roughness = 0.5
```

### Load (역직렬화)
- `assetGuid` 필드 → `db.LoadByGuid<Mesh>(guid)` 로 로드
- `materialGuid` 필드 → `db.LoadByGuid<Material>(guid)` 로 로드
- `primitiveType` 필드 → 기존 PrimitiveGenerator 로직

---

## Phase 7: AssetSpawner — GUID 기반 멀티 메시 스폰

**파일:** `src/IronRose.Engine/Editor/AssetSpawner.cs`

- **드래그-드롭 payload 변경**: `_draggedAssetPath` → `_draggedAssetGuid` (GUID 전달)
- GLB(메인 GUID) 드래그 시: sub-asset가 1개면 단일 GO, 2개 이상이면 parent GO + child GO per mesh
  - 각 child의 mesh/material은 sub-asset GUID로 로드: `db.LoadByGuid<Mesh>(subGuid)`
- 개별 sub-asset(sub GUID) 드래그 시: 해당 메시만 단일 GO로 생성
- child GO마다 MeshFilter + MeshRenderer (NamedMesh.MaterialIndex로 올바른 Material 연결)
- Undo 액션도 GUID 저장 (파일 이동해도 Redo 동작)

---

## Phase 8: Project 패널 — Sub-asset 트리 표시

**파일:** `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`

- `AssetEntry`에 `List<SubAssetDisplay>` 추가 (각 항목에 GUID 포함)
- RebuildTree 시 .rose의 sub_assets 정보 로드
- Sub-asset이 있는 에셋은 TreeNode로 렌더링 (펼치면 하위 항목 표시)
- 하위 항목 클릭 시 선택 상태 업데이트
- **드래그-드롭 payload**: 메인 에셋이든 sub-asset이든 GUID를 전달
  - `_draggedAssetGuid = sub.guid` (ImGui payload로 GUID 전달)
- Inspector 연동: 선택된 sub-asset의 GUID 전달

---

## Phase 9: Inspector 패널 — Sub-asset 정보 표시

**파일:** `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`

- MeshImporter 에셋 선택 시 "Sub-Assets" 섹션 추가
- Type | Name | Index | GUID 테이블 렌더링
- Sub-asset 이름 클릭 시 해당 sub-asset 상세 정보 (vertex/triangle 수 등)

---

## 하위 호환성

**엔진이 궤도에 오르기 전이므로 하위 호환성은 무시한다.**
- 기존 .scene, .rosecache, .rose 파일은 삭제 후 재생성
- `Load<T>(path)` 폴백 불필요 — GUID 기반만 지원
- 레거시 `assetPath` 필드 처리 불필요

## 구현 순서

```
Phase 1 (데이터 모델) ← 행동 변화 없음, 안전한 기반
  ↓
Phase 2 (MeshImporter) ← 개별 메시 추출
  ↓
Phase 3 (AssetDatabase) ← 핵심: sub-asset 등록/로드
  ↓
Phase 4 (RoseCache) + Phase 5 (MipMesh) ← 병렬 가능
  ↓
Phase 6 (SceneSerializer) ← Phase 3의 FindPathForMesh 의존
  ↓
Phase 7 (AssetSpawner) ← Phase 3 + 6 의존
  ↓
Phase 8 (ProjectPanel) + Phase 9 (InspectorPanel) ← 병렬 가능
```

## 검증 방법

1. AntiqueBook.glb 임포트 후 `.glb.rose` 파일에 `[[sub_assets]]` 엔트리 확인
2. Project 패널에서 GLB 펼쳐서 개별 mesh/material/texture 표시 확인
3. 개별 sub-asset 드래그-드롭으로 Scene View에 배치 확인
4. Scene 저장 후 .scene 파일에 `assetGuid` 필드로 GUID 저장 확인
5. Scene 로드 후 GUID 기반으로 에셋 복원 확인
6. Reimport 후 GUID 안정성 확인 (동일 이름 → 동일 GUID)
7. 에셋 파일 이동 후에도 GUID 참조가 유효한지 확인
