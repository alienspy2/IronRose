# Phase 24: 3D Physics Collider Pipeline + Edit Collider Mode + Rectangle Selection

## Context

Phase 23(PBR Material Pipeline + Gizmo API + 에디터 UX) 이후, 3D 물리 시스템의 콜라이더 파이프라인을 대폭 강화하고, 에디터에서 콜라이더를 시각적으로 편집할 수 있는 Edit Collider 모드와 씬 뷰 Rectangle Selection 도구를 추가했습니다.

**핵심 변경 영역:**
1. Physics Collider 시스템 강화 (lossyScale 적용, CylinderCollider, per-body Gravity 제어)
2. Collider Gizmo 시각화 (Box/Sphere/Capsule/Cylinder 와이어프레임)
3. Edit Collider 모드 (인터랙티브 핸들 드래그로 콜라이더 리사이즈)
4. Rectangle Selection 도구 (LMB 드래그로 다중 오브젝트 선택)
5. Cylinder Primitive (메시 생성 + 콜라이더 + 물리 Shape)
6. GizmoRenderer 버퍼 통합 (use-after-free 버그 수정)
7. 에디터 UX 개선 (Play/Stop 버튼 통합, 검색 클리어, 프로퍼티 반영 개선)

---

## 1. Physics Collider 시스템 강화

### 배경
기존 콜라이더는 Transform의 `lossyScale`을 무시하여 스케일된 오브젝트의 물리 Shape 크기가 맞지 않았음. 또한 `center` 오프셋이 회전/스케일 미반영.

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `Collider.cs` | `GetWorldPosition()` — `TransformPoint(center)`로 변경 (lossyScale + rotation 적용) |
| `BoxCollider.cs` | `RegisterAsStatic()` — size × lossyScale 적용 |
| `SphereCollider.cs` | `RegisterAsStatic()` — radius × max(scaleXYZ) 적용 |
| `CapsuleCollider.cs` | `RegisterAsStatic()` — radius × max(scaleX,Z), height × scaleY 적용 |
| `Rigidbody.cs` | Dynamic/Kinematic 모든 Shape에 lossyScale 적용, CylinderCollider 지원 추가 |
| `PhysicsComponent.cs` | `EnsureRegistered()` 순서 수정 — `RegisterWithPhysics()` 먼저 호출 후 `_registered = true` |
| `PhysicsManager.cs` | `EnsureRigidbodies()` — 스텝 전 모든 Rigidbody 지연 등록 보장 |

### per-body Gravity 제어

| 파일 | 변경 내용 |
|------|----------|
| `Rigidbody.cs` | `useGravity` setter → `PhysicsWorld3D.SetBodyUseGravity()` 호출 |
| `PhysicsWorld3D.cs` | `_noGravityBodies` HashSet + `SetBodyUseGravity()` API |
| `PoseIntegratorCallbacks` | `IntegrateVelocity()` — per-body gravity mask 적용 (SIMD Vector 연산) |

```csharp
// per-body gravity 비활성화 — SIMD 마스킹
var handle = _simulation.Bodies.ActiveSet.IndexToHandle[idx];
maskValues[i] = _noGravityBodies.Contains(handle.Value) ? 0f : 1f;
velocity.Linear.Y += _gravityDtWide.Y * mask;
```

---

## 2. Collider Gizmo 시각화

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| `BoxCollider.cs` | `OnDrawGizmosSelected()` — 와이어 큐브 렌더링 |
| `SphereCollider.cs` | `OnDrawGizmosSelected()` — 와이어 스피어 렌더링 |
| `CapsuleCollider.cs` | `OnDrawGizmosSelected()` — 와이어 캡슐 렌더링 |
| `CylinderCollider.cs` | `OnDrawGizmosSelected()` — 와이어 실린더 렌더링 |
| `Gizmos.cs` | `DrawWireCapsule()`, `DrawWireCylinder()` 정적 API 추가 |
| `IGizmoBackend.cs` | 인터페이스에 `DrawWireCapsule()`, `DrawWireCylinder()` 추가 |
| `GizmoRendererBackend.cs` | 백엔드 포워딩 구현 |
| `GizmoRenderer.cs` | `DrawWireCapsule()` — 원형 equator × 2 + 반구 arc × 4 + 수직선 × 4 |
| `GizmoRenderer.cs` | `DrawWireCylinder()` — 원형 top/bottom × 2 + 수직선 × 4 |

### 캡슐 와이어프레임 구조

```
  ╭───╮  ← top hemisphere arcs (2 orthogonal half-circles)
  │   │  ← 4 vertical lines + 2 equator circles
  ╰───╯  ← bottom hemisphere arcs
```

---

## 3. Edit Collider 모드

### 신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `ColliderGizmoEditor.cs` | 인터랙티브 콜라이더 핸들 에디터 (537줄) |
| `EditorState.cs` | `IsEditingCollider` 프로퍼티 추가 |
| `ImGuiInspectorPanel.cs` | Collider 컴포넌트에 "Edit Collider" 토글 버튼 추가 |
| `ImGuiOverlay.cs` | Edit Collider 모드 시 TransformGizmo 대신 ColliderGizmoEditor 사용 |

### 핵심 설계

- **6방향 핸들:** PosX, NegX, PosY, NegY, PosZ, NegZ — 콜라이더 표면에 다이아몬드 마커 표시
- **핸들 Hit Test:** 월드 좌표 → 스크린 좌표 투영, 8px 반경 내 클릭 감지
- **드래그:** 스크린 레이를 축에 투영, 로컬 공간 delta 계산 후 size/radius/height 조정
- **Undo:** `SetPropertyAction` + `CompoundUndoAction` (BoxCollider는 size + center 동시 기록)
- **지원 타입:** BoxCollider (6핸들, size+center), SphereCollider (6핸들, radius), CapsuleCollider (4핸들, height/radius), CylinderCollider (4핸들, height/radius)

### 에디터 동작

1. Inspector에서 Collider 컴포넌트의 "Edit Collider" 버튼 클릭 (활성화 시 초록색)
2. TransformGizmo가 비활성화되고 콜라이더 핸들이 표시됨
3. 핸들 드래그로 콜라이더 크기/반경/높이 조절
4. 선택 해제 또는 콜라이더 없는 오브젝트 선택 시 자동 비활성화

---

## 4. Rectangle Selection 도구

### 신규 파일

| 파일 | 변경 내용 |
|------|----------|
| (신규) `RectSelectionTool.cs` | LMB 드래그 사각형 선택 도구 (197줄) |
| `ImGuiOverlay.cs` | 기존 클릭-투-셀렉트 로직을 RectSelectionTool으로 통합 |

### 핵심 설계

- **드래그 감지:** LMB 다운 시 추적 시작, 4px 이상 이동 시 사각형 활성화
- **AABB 스크린 투영:** 각 GameObject의 `MeshFilter.mesh.bounds` 8개 코너를 ViewProjection으로 스크린 좌표 변환
- **오버랩 테스트:** 투영된 2D AABB와 선택 사각형의 겹침 판정
- **수식어 키:** Ctrl → 토글 선택, Shift → 추가 선택, 없음 → 교체 선택
- **시각화:** ImGui ForegroundDrawList에 반투명 파란색 사각형 + 테두리 오버레이
- **폴백:** 드래그 없이 클릭 시 기존 GPU 피킹 로직으로 처리

---

## 5. Cylinder Primitive

### 수정/신규 파일

| 파일 | 변경 내용 |
|------|----------|
| `PrimitiveGenerator.cs` | `CreateCylinder()` — 24 세그먼트, Y축 정렬, height=2, radius=0.5 |
| (신규) `CylinderCollider.cs` | radius/height 프로퍼티, static 등록, Gizmo 렌더링 |
| `PhysicsWorld3D.cs` | `AddDynamicCylinder()`, `AddStaticCylinder()` — BepuPhysics Cylinder shape |
| `GameObject.cs` | `CreatePrimitive()` — Cylinder 지원 + 모든 Primitive에 매칭 Collider 자동 부착 |
| `GameObjectFactory.cs` | `CreateGameObjectType.Cylinder` 열거형 추가 |
| `ImGuiHierarchyPanel.cs` | 3D Object 메뉴에 "Cylinder" 항목 추가 |
| `SceneSerializer.cs` | Cylinder 프리미티브 직렬화/역직렬화 지원 |

### Cylinder 메시 구조

```
Top cap (center + ring, fan triangles)
Bottom cap (center + ring, fan triangles, reversed winding)
Side wall (top ring + bottom ring, quad strips with outward normals)
```

### Primitive 자동 Collider 부착

`GameObject.CreatePrimitive()` 호출 시 타입별 Collider 자동 추가:
- Cube → BoxCollider
- Sphere → SphereCollider
- Capsule → CapsuleCollider
- Cylinder → CylinderCollider
- Plane → BoxCollider (10×0.01×10)
- Quad → BoxCollider (1×1×0.01)

---

## 6. GizmoRenderer 버퍼 통합

### 배경
기존 배치별 개별 GPU 업로드 방식에서 `Mesh.UploadToGPU()`가 이전 버퍼를 Dispose하여 후속 배치에서 use-after-free 발생.

### 수정 파일

| 파일 | 변경 내용 |
|------|----------|
| `GizmoRenderer.cs` | `Render()` — 모든 배치를 단일 버텍스/인덱스 버퍼로 병합 후 1회 업로드, `DrawIndexed(indexCount, 1, indexStart, 0, 0)` |
| `GizmoRenderer.cs` | `RenderPickPass()` — 동일한 단일 버퍼 병합 수정 |
| `SceneViewRenderer.cs` | `DrawGizmoLines()` 오버로드 추가 — `indexStart`, `indexCount` 파라미터 |

---

## 7. 에디터 UX 개선

| 항목 | 설명 |
|------|------|
| **Play/Stop 버튼 통합** | 3버튼(Play/Pause/Stop) → 2버튼(Play↔Stop, Pause)으로 간소화 |
| **디버그 오버레이 단축키 제거** | F2/F3 글로벌 단축키 삭제, 메뉴 전용 접근 (G-Buffer/ShadowMap) |
| **Hierarchy 검색 클리어** | 검색어 입력 시 "X" 클리어 버튼 표시 |
| **Project 검색 클리어** | 동일한 "X" 클리어 버튼 |
| **Inspector 프로퍼티 반영** | `DeclaredOnly` → 전체 `Public|Instance`로 변경, 상속 프로퍼티 노출 |
| **Add Component 퍼지 검색** | 스페이스 구분 멀티토큰 매칭 (예: "box col" → BoxCollider) |
| **glTF Texture2D GUID** | `.glb.rose` 메타데이터에 Texture2D sub-asset GUID 등록 |

---

## 신규 에셋

| 파일 | 설명 |
|------|------|
| `Assets/Scenes/cornellBox.scene` | Cornell Box 테스트 씬 |
| `Assets/Scenes/physics_test.scene` | 물리 테스트 씬 |
| `Assets/_test/mat_bluePlastic.mat` | PBR 파란 플라스틱 머티리얼 |
| `Assets/_test/mat_greenPlastic.mat` | PBR 초록 플라스틱 머티리얼 |
| `Assets/_test/mat_redPlastic.mat` | PBR 빨간 플라스틱 머티리얼 |
| `Assets/_test/mat_illumination.mat` | Emission 발광 머티리얼 |

---

## 수정 파일 요약 (전체)

### 신규 파일 (3개)

| 파일 | 설명 |
|------|------|
| `ColliderGizmoEditor.cs` | 인터랙티브 콜라이더 핸들 에디터 |
| `RectSelectionTool.cs` | 사각형 드래그 선택 도구 |
| `CylinderCollider.cs` | 실린더 콜라이더 컴포넌트 |

### 주요 수정 파일 (25개)

| 파일 | 변경 요약 |
|------|----------|
| `Collider.cs` | center → TransformPoint 월드 좌표 변환 |
| `BoxCollider.cs` | lossyScale 적용, OnDrawGizmosSelected |
| `SphereCollider.cs` | lossyScale 적용, OnDrawGizmosSelected |
| `CapsuleCollider.cs` | lossyScale 적용, OnDrawGizmosSelected |
| `Rigidbody.cs` | lossyScale 전면 적용, CylinderCollider, useGravity 런타임 제어 |
| `PhysicsComponent.cs` | EnsureRegistered 순서 수정 |
| `PhysicsManager.cs` | EnsureRigidbodies 지연 등록, 디버그 로깅 |
| `PhysicsWorld3D.cs` | Cylinder shape, per-body gravity, PoseIntegratorCallbacks 개선 |
| `Gizmos.cs` | DrawWireCapsule, DrawWireCylinder API |
| `IGizmoBackend.cs` | 인터페이스 확장 |
| `GizmoRendererBackend.cs` | 포워딩 추가 |
| `GizmoRenderer.cs` | 캡슐/실린더 와이어프레임, 단일 버퍼 병합 |
| `SceneViewRenderer.cs` | DrawGizmoLines 인덱스 오프셋 오버로드 |
| `PrimitiveGenerator.cs` | CreateCylinder 메시 생성 |
| `GameObject.cs` | Cylinder + Primitive 자동 Collider 부착 |
| `GameObjectFactory.cs` | Cylinder 타입 추가 |
| `SceneSerializer.cs` | Cylinder 직렬화 |
| `EditorState.cs` | IsEditingCollider 상태 |
| `ImGuiOverlay.cs` | ColliderGizmoEditor/RectSelection 통합, Play/Stop 버튼 간소화 |
| `ImGuiHierarchyPanel.cs` | Cylinder 메뉴, 검색 클리어 버튼 |
| `ImGuiInspectorPanel.cs` | Edit Collider 버튼, 프로퍼티 반영 개선, 퍼지 검색 |
| `ImGuiProjectPanel.cs` | 검색 클리어 버튼 |
| `DemoLauncher.cs` | F2 디버그 오버레이 단축키 제거 |
| `EngineCore.cs` | F2/F3 글로벌 단축키 제거 |
| `Light.cs` | 주석 수정 |

**변경 통계:** 30개 파일, +694줄 / -131줄

---

## 구현 순서

```
Phase 23 (PBR Material Pipeline)
  ↓
Step 1: Collider lossyScale 적용 + center 월드 좌표 변환
  ↓
Step 2: CylinderCollider + Cylinder Primitive 메시 생성
  ↓
Step 3: Gizmo API 확장 (DrawWireCapsule/Cylinder) + Collider OnDrawGizmosSelected
  ↓
Step 4: GizmoRenderer 배치 버퍼 통합 (use-after-free 수정)
  ↓
Step 5: Edit Collider 모드 (ColliderGizmoEditor + Inspector 토글)
  ↓
Step 6: Rectangle Selection 도구 (RectSelectionTool)
  ↓
Step 7: per-body Gravity 제어 (PhysicsWorld3D + PoseIntegratorCallbacks)
  ↓
Step 8: 에디터 UX 개선 (Play/Stop 통합, 검색 클리어, 프로퍼티 반영 등)
```
