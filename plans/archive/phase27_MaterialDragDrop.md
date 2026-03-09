# Phase 27 — Material Drag-Drop to Scene View with Hover Preview

## Context
Material을 Project Panel에서 Scene View에 직접 드래그 앤 드롭하여 대상 오브젝트에 Material 할당. 호버링 중 미리보기(outline + material preview) 표시.

## 핵심 메커니즘
- `ImGui.AcceptDragDropPayload("ASSET_PATH", AcceptPeekOnly)` — 매 프레임 호버 감지 (드롭 소비 없이)
- GPU Pick (`RequestPick`) — 호버 중 커서 아래 오브젝트 식별
- `SceneViewRenderer` material override — 컴포넌트 수정 없이 렌더링 시 임시 Material 교체
- Cyan outline (`0.3, 0.7, 1.0`) — 대상 오브젝트 하이라이트 (선택 주황색과 구분)

## 수정 대상 파일 (3개, 신규 파일 없음)

### 1. `ImGuiSceneViewPanel.cs` — 호버 감지
- `_isMaterialDragHovering`, `_hoveringMaterialPath`, `_hoveringScreenPos` 필드 추가
- `IsMaterialDragHovering`, `HoveringMaterialPath`, `HoveringScreenPos` 프로퍼티 추가
- `IsMaterialAsset(path)` static 헬퍼 추가 (`.mat` 확장자 또는 `SubAssetPath`의 "Material" 타입)
- `Draw()`의 `BeginDragDropTarget` 블록 수정:
  - 매 프레임 `_isMaterialDragHovering = false` 리셋
  - `AcceptDragDropPayload("ASSET_PATH", AcceptPeekOnly)` 로 호버 감지 (드롭 소비 없이)
  - Material이면 hover state 설정
  - 기존 delivery 패스 그대로 유지

### 2. `SceneViewRenderer.cs` — Material override + outline 색상
- `_materialOverrideObjectId`, `_materialOverride` 필드 추가
- `SetMaterialOverride(int, Material)` / `ClearMaterialOverride()` / `MaterialOverrideObjectId` 추가
- `Render()`: DiffuseOnly 모드에서 override 대상 오브젝트는 `_materialOverride`로 렌더링
- `DrawSelectionOutline()`: `Vector4? outlineColor = null` 파라미터 추가 (기본값 = 기존 주황색)
- `Render()` 끝: selection outline 이후 hover outline (cyan `0.3, 0.7, 1.0, 1`) 추가
- `RenderOverlays()`: camPos 계산을 조건문 밖으로 이동, hover outline 추가

### 3. `ImGuiOverlay.cs` — 호버 처리 + Material drop
- `_materialHoverObjectId`, `_materialHoverPreview`, `_lastMaterialHoverPath` 필드 추가
- `HandleMaterialDragHover()` 신규 메서드:
  - 호버 중이 아니면 override 정리 후 리턴
  - 경로 변경 시 `AssetDatabase.Load<Material>()` 로 로드
  - 화면좌표 → pick 좌표 변환 후 `RequestPick()` 호출
  - pick 콜백: MeshRenderer 있으면 `SetMaterialOverride()`, 없으면 `ClearMaterialOverride()`
- `Update()`에서 호출 순서: `UpdateSceneViewInput` → **`HandleMaterialDragHover`** → `HandleSceneViewAssetDrop`
- `HandleSceneViewAssetDrop()` 수정: `IsMaterialAsset()` 이면 `HandleMaterialDrop()` 호출 후 return
- `HandleMaterialDrop()` 신규 메서드:
  - `_materialHoverObjectId`로 대상 MeshRenderer 찾기
  - `meshRenderer.material = newMat` 할당
  - `SetPropertyAction` 으로 undo 기록 (componentTypeName: `typeof(MeshRenderer).Name`)
  - scene dirty 표시 + hover state 정리
- `ClearMaterialHoverState()` 헬퍼

## 프레임 플로우
```
1. ImGuiSceneViewPanel.Draw()  → AcceptPeekOnly로 호버 감지, hover state 설정
2. ImGuiOverlay.Update()       → HandleMaterialDragHover() → RequestPick()
3. ImGuiOverlay.RenderSceneView() → Render (material override 적용) → ExecutePendingPick (콜백으로 다음 프레임 override 설정)
```
- 1프레임 레이턴시 (커서 이동 → 프리뷰 표시): 체감 불가

## 모드별 동작
| 렌더 모드 | Material 프리뷰 | Hover Outline |
|-----------|----------------|---------------|
| DiffuseOnly | O (색상+텍스처) | O (cyan) |
| MatCap | X (고정 쉐이딩) | O (cyan) |
| Wireframe | X (고정 쉐이딩) | O (cyan) |
| Rendered | X (RenderSystem 별도) | O (cyan) |

## 참고: 기존 패턴 재사용
- Drag payload: `ImGuiProjectPanel._draggedAssetPath` (static field)
- Material 로딩: `AssetDatabase.Load<Material>(path)` (Inspector와 동일)
- Undo: `SetPropertyAction` (Inspector의 material drop 패턴, line 2397)
- GPU Pick: `SceneViewRenderer.RequestPick()` (click-to-select와 동일)
- Outline: `DrawSelectionOutline()` (selection outline 파라미터화)

## 검증 방법
1. 빌드 확인: `dotnet build`
2. 런타임 테스트:
   - Project Panel에서 `.mat` 파일을 Scene View의 오브젝트 위로 드래그 → 시안 outline 표시 확인
   - DiffuseOnly 모드에서 Material 색상/텍스처 미리보기 확인
   - 드롭 → Material 할당 + Inspector 반영 확인
   - Ctrl+Z → undo 동작 확인
   - 빈 공간에 드롭 → 무시됨 확인
   - MeshRenderer 없는 오브젝트 위 드래그 → outline 없음 확인
