# Phase 23: PBR Material Pipeline + glTF 임포터 + Gizmo API + 에디터 UX 개선

## Context

Phase 22(멀티셀렉션/멀티에디트) 이후, PBR 렌더링 파이프라인의 머티리얼 시스템을 대폭 강화하고, glTF 임포터를 전면 교체하며, Gizmo API와 에디터 UX를 전반적으로 개선했습니다.

**핵심 변경 영역:**
1. SharpGLTF 기반 glTF/GLB 임포터 + 비동기 재임포트
2. 우클릭 컨텍스트 메뉴 + Inspector 싱글클릭 편집 + 프리미티브 생성
3. Gizmo API + Light 컴포넌트 리팩토링 + SceneView 렌더 스타일
4. PBR Material 에셋 시스템 (`.mat` TOML 파일, Normal Map, MRO Map, Texture Tiling/Offset)
5. Texture Tool 패널 (채널 리믹스, 프로시저럴 텍스처 생성)
6. Project 패널 고도화 (폴더/에셋 리네임, Material 생성, 에셋 리네이밍)
7. Hierarchy 리네이밍 (F2/더블클릭) + 드래그 앤 드롭 순서 보존

---

## 커밋 이력

| 커밋 | 설명 |
|------|------|
| `85e0933` | SharpGLTF glTF/GLB 임포터 + 비동기 재임포트 시스템 + 백그라운드 워밍업 |
| `4594eb0` | 우클릭 컨텍스트 메뉴 GameObject 생성 + Inspector 싱글클릭 편집 + 빌드 버전 표시 |
| `e5dad76` | Gizmo API + Light 컴포넌트 리팩토링 + 에디터 UI 스케일/폰트/스냅 설정 + SceneView 렌더 스타일 |
| (WIP) | PBR Material Pipeline + Texture Tool + Normal/MRO Map + Project 패널 고도화 |

---

## 1. SharpGLTF glTF/GLB 임포터

### 배경
AssimpNet 4.1.0 번들이 glTF 2.0을 제대로 지원하지 않아 SharpGLTF.Core로 전면 교체.

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `GltfMeshImporter.cs` | SharpGLTF 기반 메시/머티리얼/텍스처 임포트, 텍스처 중복 제거(캐시) |
| `AssetDatabase.cs` | glb/gltf 확장자 분기, 비동기 재임포트 + 진행 오버레이 UI, 실패 캐시 |
| `MeshImporter.cs` | Assimp 임포터는 OBJ/FBX용으로 유지 |
| `RoseMetadata.cs` | Sub-asset GUID 안정성: 타입+인덱스 폴백으로 GUID 보존 |
| `RoseCache.cs` | 캐시 포맷 v7 (BC6H HDR) → v8 (normalMapStrength 추가) |
| `AssetWarmupManager.cs` | 백그라운드 스레드 워밍업 처리 |

### 핵심 설계

```
.glb/.gltf → SharpGLTF (GltfMeshImporter)
.obj/.fbx  → AssimpNet (MeshImporter)
```

- **텍스처 중복 제거:** `GetOrLoadTexture()` — `GltfTexture.LogicalIndex` 기반 캐시로 동일 텍스처 인스턴스 공유
- **UV flip 수정:** glTF 2.0은 UV 원점이 좌상단(Vulkan/Veldrid와 동일)이므로 `flipUVs=false`
- **비동기 재임포트:** 백그라운드 임포트 + 진행 오버레이 UI
- **임포트 실패 캐시:** `_failedImports` HashSet으로 매 프레임 재시도 방지

---

## 2. 우클릭 컨텍스트 메뉴 + Inspector 싱글클릭 편집

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `GameObjectFactory.cs` | Empty, Camera, Light(Dir/Point/Spot), Primitive(Cube/Sphere/Capsule/Plane/Quad) 생성 팩토리 |
| (신규) `CreateGameObjectAction.cs` | 생성 Undo 액션 |
| `ImGuiHierarchyPanel.cs` | 우클릭 컨텍스트 메뉴 (Empty, 3D Object, Light, Camera) |
| `ImGuiSceneViewPanel.cs` | SceneView 우클릭 메뉴에서도 동일 생성 |
| `ImGuiInspectorPanel.cs` | DragFloat/DragInt/DragFloat3 싱글클릭 텍스트 편집 모드 진입 |
| `ImGuiRenderSettingsPanel.cs` | Slider + InputField 조합 UI, Enum/IntDropdown 가운데 정렬 |
| `EditorSelection.cs` | 동일 오브젝트 재선택 시 `SelectionVersion++` (Inspector 모드 전환 트리거) |
| (신규) `BuildVersion.cs` | About 다이얼로그에 환경/빌드시각 표시 |
| (신규) `Attributes.cs` | `IntDropdown` 어트리뷰트 추가 |

### 싱글클릭 텍스트 편집

DragFloat 위젯을 클릭 시 InputText 모드로 전환:
1. `_float3FocusAxis` 딕셔너리로 클릭된 축(X/Y/Z) 추적
2. 다음 프레임에서 `SetKeyboardFocusHere(axis)` 호출
3. Deactivate 시 원래 Drag 모드로 복귀

---

## 3. Gizmo API + Light 리팩토링 + SceneView 렌더 스타일

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `Gizmos.cs` | `Gizmos.DrawWireSphere()`, `DrawLine()`, `DrawRay()` 등 정적 API |
| (신규) `IGizmoBackend.cs` | Gizmo 렌더링 백엔드 인터페이스 |
| (신규) `GizmoRendererBackend.cs` | 실제 GPU 드로우콜 실행 |
| (신규) `GizmoCallbackRunner.cs` | 컴포넌트의 `OnDrawGizmos()` 콜백 실행 |
| `GizmoRenderer.cs` | GizmoMeshBuilder 기반 와이어프레임 렌더링 확장 |
| `Light.cs` | Directional/Point/Spot 타입별 프로퍼티 확장, `OnDrawGizmos()` 구현 |
| `TransformGizmo.cs` | 스케일/회전 모드 확장 |
| `EditorCamera.cs` | 스냅 설정 연동 |
| (신규) `EditorState.cs` | UI 스케일, 폰트 선택, 스냅 설정 영속화 (TOML) |
| `SceneViewRenderer.cs` | Wireframe/Matcap/DiffuseOnly/Rendered 렌더 스타일 |
| `ImGuiOverlay.cs` | 메뉴바 확장, 패널 관리 개선 |
| `ImGuiProjectPanel.cs` | Scene 에셋 더블클릭 열기, Reimport All |

### Gizmo API 설계

```csharp
public static class Gizmos
{
    public static Color color { get; set; }
    public static void DrawWireSphere(Vector3 center, float radius);
    public static void DrawLine(Vector3 from, Vector3 to);
    public static void DrawRay(Vector3 from, Vector3 direction);
    // ...
}
```

- `Component.OnDrawGizmos()` 가상 메서드 → `GizmoCallbackRunner`가 매 프레임 호출
- Light 컴포넌트에서 타입별 Gizmo 자동 렌더링 (방향, 범위, 콘 등)

### SceneView 렌더 스타일

| 스타일 | 설명 |
|--------|------|
| Rendered | 풀 PBR + 라이팅 |
| Matcap | 파일 기반 MatCap 텍스처 |
| Diffuse Only | 라이팅 없이 알베도만 |
| Wireframe | 와이어프레임 오버레이 |

---

## 4. PBR Material 에셋 시스템 (`.mat` TOML)

### 신규/수정 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `MaterialImporter.cs` | `.mat` TOML → Material 인스턴스 임포트, 텍스처 GUID 참조 해석 |
| (신규) `MaterialPropertyUndoAction.cs` | .mat 파일 TOML 스냅샷 기반 Undo/Redo |
| `AssetDatabase.cs` | `.mat` 확장자 등록, MaterialImporter 호출, `_textureToGuid` 역참조맵, `RenameAsset()`, `ReplaceMaterialInScene()` |
| `IAssetDatabase.cs` | `FindGuidForTexture()` 인터페이스 추가 |
| `RoseMetadata.cs` | `.mat` → `MaterialImporter` 매핑 |
| `ImGuiInspectorPanel.cs` | Material 에셋 편집 UI (색상, 텍스처 슬롯, PBR 파라미터, 드래그 앤 드롭) |

### `.mat` TOML 형식

```toml
name = "MyMaterial"

[color]
r = 1.0
g = 1.0
b = 1.0
a = 1.0

[emission]
r = 0.0
g = 0.0
b = 0.0
a = 1.0

metallic = 0.0
roughness = 0.5
occlusion = 1.0
normalMapStrength = 1.0
textureScaleX = 1.0
textureScaleY = 1.0
textureOffsetX = 0.0
textureOffsetY = 0.0
mainTextureGuid = "abc123..."
normalMapGuid = "def456..."
MROMapGuid = "ghi789..."
```

### Material Inspector UI

- **Base Surface:** Main Texture (드래그 앤 드롭), Tiling, Offset, Color
- **Normal:** Normal Map 슬롯, Normal Map Strength
- **MRO:** MRO Map 슬롯, Metallic, Roughness, Occlusion 개별 슬라이더
- **Emission:** Emission Color
- 모든 편집은 **즉시 적용** (TOML 저장 → 재임포트) + Undo/Redo 지원
- GLB sub-asset Material은 **Read-only** Inspector 표시

### MeshRenderer 머티리얼 드래그 앤 드롭

- Inspector의 `material` 필드에 .mat 또는 Material sub-asset 드래그 앤 드롭으로 교체
- Undo 지원 (`SetPropertyAction`)

---

## 5. Deferred 셰이더 Normal Map + MRO Map 지원

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `deferred_geometry.frag` | Normal Map (cotangent frame TBN), MRO Map, Texture Tiling/Offset |
| `preview_material.frag` | 3-light 스튜디오 조명 강화, IBL 근사 (반구 스카이) |
| `sceneview_matcap.frag` | MatCap UV Y 반전 수정 |
| `RoseCache.cs` | 캐시 v8: `normalMapStrength` 직렬화 |
| `Material.cs` | `normalMapStrength`, `textureScale`, `textureOffset` 프로퍼티 추가 |

### deferred_geometry.frag 주요 변경

```glsl
// MaterialData UBO 확장
float NormalMapStrength;
float HasNormalMap;
float HasMROMap;
vec2 TextureOffset;
vec2 TextureScale;

// Cotangent frame TBN (screen-space derivatives, vertex tangent 불필요)
vec3 dp1 = dFdx(fsin_WorldPos);
vec3 dp2 = dFdy(fsin_WorldPos);
// ... TBN 행렬 구성 ...

// BC5 normal map: RG만 저장, Z는 XY로부터 재구성
vec2 nXY = texture(NormalMap, uv).rg * 2.0 - 1.0;
nXY *= NormalMapStrength;
vec3 nTS = vec3(nXY, sqrt(max(1.0 - dot(nXY, nXY), 0.0)));
N = normalize(TBN * nTS);

// MRO map override
if (HasMROMap > 0.5) {
    vec3 mro = texture(MROMap, uv).rgb;
    metallic = mro.r; roughness = mro.g; occlusion = mro.b;
}
```

---

## 6. Texture Tool 패널

### 신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `ImGuiTextureToolPanel.cs` | 텍스처 채널 리믹스 + 프로시저럴 텍스처 생성 |
| `TextureImporter.cs` | Panoramic (LDR→HDR) 임포트, Height→Normal Map 변환, Equirectangular 비율 강제 |

### Channel Remix
- R/G/B/A 채널별로 **Color(고정값)** 또는 **Texture(외부 파일의 특정 채널)** 선택
- 4개 소스를 합성하여 MRO Map 등 제작

### Procedural Generation
- **Checker:** 바둑판 패턴
- **Brick:** 벽돌 패턴 (행/열/몰타르 크기)
- **Voronoi:** Worley 노이즈 기반 셀 패턴
- **Gradient:** 수평/수직/방사형 그라디언트
- **Noise:** Perlin 노이즈 (옥타브/퍼시스턴스)

### Height → Normal Map 변환

```csharp
TextureImporter.ConvertHeightToNormalMap(inputPath, outputPath, strength);
```
- Sobel 필터로 gradient 계산 → tangent-space normal 생성

---

## 7. Hierarchy 리네이밍 + 드래그 앤 드롭 순서 보존

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| `ImGuiHierarchyPanel.cs` | F2/더블클릭 리네이밍, deferred select, 드래그 순서 보존, 3D Object 서브메뉴, sibling 정렬 |
| (신규) `RenameGameObjectAction.cs` | Rename Undo 액션 |
| `Transform.cs` | `GetSiblingIndex()`, `SetSiblingIndex()` 추가 |

### 리네이밍 흐름
1. F2 키 또는 더블클릭 → `BeginRename(id)`
2. InputText 위젯으로 전환 (AutoSelectAll, 포커스 자동 설정)
3. Enter → `CommitRename()` (Undo 기록)
4. Escape → `CancelRename()`
5. 포커스 손실 → 자동 CommitRename

### 드래그 앤 드롭 순서 보존
- `_flatOrderedIds` 기반으로 드래그 대상을 트리 순서로 정렬
- Below 드롭 시 역순 처리로 올바른 삽입 순서 유지
- 멀티셀렉트 상태에서 드래그 방지를 위한 `_deferredSelectId`

---

## 8. Project 패널 고도화

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `ImGuiProjectPanel.cs` | 에셋 리네이밍(F2), Material 생성, 폴더 리네이밍/삭제, 폴더 트리 Root 표시 |
| `AssetDatabase.cs` | `RenameAsset()` — 파일 + .rose 사이드카 + GUID/캐시 일괄 갱신 |
| `NativeFileDialog.cs` | 필터 문자열 제네릭화 (Scene 전용 → 모든 확장자 대응) |

### 에셋 리네이밍 (`F2`)
- `AssetDatabase.RenameAsset()` — 물리 파일, .rose 사이드카, `_guidToPath`, `_loadedAssets` 캐시 키 일괄 갱신
- 서브에셋 경로도 자동 갱신

### Material 생성 (`Create > Material`)
- 우클릭 컨텍스트 메뉴에서 새 `.mat` 파일 생성
- `MaterialImporter.WriteDefault()` → 기본 PBR 값으로 초기화
- 자동으로 `.rose` 메타데이터 생성 및 프로젝트 새로고침

### 폴더 관리
- Root "Assets" 노드 기본 펼침
- 폴더 리네이밍, 삭제 (재귀적) 모달 다이얼로그

---

## 9. 기타 개선

| 항목 | 설명 |
|------|------|
| RenderSystem | Normal Map / MRO Map 바인딩 추가, Material UBO에 `NormalMapStrength`, `HasNormalMap`, `HasMROMap`, `TextureOffset`, `TextureScale` 추가 |
| PrimitiveGenerator | Capsule, Quad 프리미티브 추가 |
| SceneManager | Primitive 생성 시 자동 `MeshRenderer` + `MeshFilter` 연결 |
| SceneSerializer | Material GUID 직렬화/로드, Texture sub-asset GUID 추적 |
| RenderSettings | 추가 조명 파라미터 |
| GraphicsManager | 리소스 관리 개선 |

---

## 수정 파일 요약 (전체)

### 신규 파일 (6개)
| 파일 | 설명 |
|------|------|
| `MaterialImporter.cs` | .mat TOML → Material 인스턴스 임포트 |
| `MaterialPropertyUndoAction.cs` | Material 편집 Undo/Redo |
| `RenameGameObjectAction.cs` | GameObject 리네이밍 Undo |
| `ImGuiTextureToolPanel.cs` | Texture Tool (채널 리믹스 + 프로시저럴 생성) |
| `GameObjectFactory.cs` | GameObject 생성 팩토리 (Phase 23에서 프리미티브 확장) |
| `CreateGameObjectAction.cs` | 생성 Undo 액션 |

### 주요 수정 파일 (20+)
| 파일 | 변경 요약 |
|------|----------|
| `deferred_geometry.frag` | Normal Map + MRO Map + Texture Tiling |
| `preview_material.frag` | IBL 근사 + 조명 강화 |
| `sceneview_matcap.frag` | UV Y 반전 수정 |
| `AssetDatabase.cs` | MaterialImporter, RenameAsset, 텍스처 역참조맵 |
| `GltfMeshImporter.cs` | 텍스처 중복 제거, 다중 float 파라미터 |
| `IAssetDatabase.cs` | FindGuidForTexture 인터페이스 |
| `RoseCache.cs` | v8: normalMapStrength |
| `RoseMetadata.cs` | .mat → MaterialImporter |
| `TextureImporter.cs` | Panoramic LDR→HDR, Height→Normal 변환 |
| `ImGuiOverlay.cs` | Tools 메뉴, TextureTool 패널 관리 |
| `NativeFileDialog.cs` | 필터 제네릭화 |
| `ImGuiHierarchyPanel.cs` | F2 리네이밍, 3D Object 메뉴, 드래그 순서 |
| `ImGuiInspectorPanel.cs` | Material 에셋 편집 UI, 텍스처 메모리 표시, 드래그 앤 드롭 |
| `ImGuiProjectPanel.cs` | Material 생성, 에셋/폴더 리네이밍, 폴더 삭제 |
| `ImGuiRenderSettingsPanel.cs` | Panoramic 텍스처 설정 |
| `SceneSerializer.cs` | Material GUID 직렬화 개선 |
| `RenderSystem.cs` | Normal/MRO Map 바인딩, Material UBO 확장 |
| `Material.cs` | normalMapStrength, textureScale, textureOffset |
| `PrimitiveGenerator.cs` | Capsule, Quad 추가 |
| `SceneManager.cs` | Primitive 생성 연동 |
| `Transform.cs` | GetSiblingIndex/SetSiblingIndex |

---

## 구현 순서

```
Phase 22 (MultiSelect)
  ↓
Step 1: SharpGLTF glTF/GLB 임포터 교체 + 비동기 재임포트
  ↓
Step 2: 우클릭 컨텍스트 메뉴 + Inspector 싱글클릭 편집 + BuildVersion
  ↓
Step 3: Gizmo API + Light 리팩토링 + EditorState 영속화 + SceneView 렌더 스타일
  ↓
Step 4: PBR Material 에셋 시스템 (.mat TOML + MaterialImporter + Inspector UI)
  ↓
Step 5: Deferred 셰이더 Normal Map + MRO Map + Texture Tiling/Offset
  ↓
Step 6: Texture Tool 패널 (Channel Remix + Procedural Generation)
  ↓
Step 7: Project 패널 고도화 (에셋/폴더 리네이밍, Material 생성)
  ↓
Step 8: Hierarchy 리네이밍 + 드래그 앤 드롭 순서 보존 + 프리미티브 서브메뉴
```
