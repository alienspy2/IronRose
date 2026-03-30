# Phase 46d: 확장 명령 세트

## 목표
- CLI 브릿지의 명령을 대폭 확장하여 에디터의 거의 모든 기능을 CLI로 조작 가능하게 한다.
- 기존 엔진 API를 최대한 활용하여 구현한다.

## 선행 조건
- Phase 46a~46c 완료

## 명령 포맷
- 요청: 평문 (`command arg1 arg2 ...`), 공백 포함 인자는 쌍따옴표
- 응답: JSON (`{ "ok": true, "data": { ... } }`)

---

## 1. GameObject 생성/삭제/관리 (go.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `go.create` | `<name>` | 빈 GO 생성 | `{ "id": int, "name": "..." }` |
| `go.create_primitive` | `<type>` | Primitive 생성 (Cube/Sphere/Capsule/Cylinder/Plane/Quad) | `{ "id": int, "name": "..." }` |
| `go.destroy` | `<id>` | GO 삭제 | `{ "ok": true }` |
| `go.rename` | `<id> <name>` | GO 이름 변경 | `{ "ok": true }` |
| `go.duplicate` | `<id>` | GO 복제 (Instantiate) | `{ "id": int, "name": "..." }` |

### 참고 API
- `new GameObject(name)` — 빈 GO 생성
- `GameObject.CreatePrimitive(PrimitiveType)` — Primitive 생성
- `Object.DestroyImmediate(obj)` — 즉시 삭제
- `Object.Instantiate(go)` — 복제

---

## 2. Transform (transform.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `transform.get` | `<id>` | position/rotation/scale 한번에 조회 | `{ "position": "...", "rotation": "...", "localScale": "..." }` |
| `transform.set_position` | `<id> <x,y,z>` | 월드 위치 설정 | `{ "ok": true }` |
| `transform.set_rotation` | `<id> <x,y,z>` | 오일러 회전 설정 | `{ "ok": true }` |
| `transform.set_scale` | `<id> <x,y,z>` | 로컬 스케일 설정 | `{ "ok": true }` |
| `transform.set_local_position` | `<id> <x,y,z>` | 로컬 위치 설정 | `{ "ok": true }` |
| `transform.translate` | `<id> <x,y,z>` | 상대 이동 | `{ "ok": true }` |
| `transform.rotate` | `<id> <x,y,z>` | 상대 회전 | `{ "ok": true }` |
| `transform.look_at` | `<id> <targetId>` | 타겟을 바라봄 | `{ "ok": true }` |
| `transform.set_parent` | `<id> <parentId\|none>` | 부모 변경 | `{ "ok": true }` |
| `transform.get_children` | `<id>` | 자식 목록 조회 | `{ "children": [{ "id": int, "name": "..." }] }` |

### 참고 API
- `transform.position`, `transform.localPosition`, `transform.eulerAngles`, `transform.localScale`
- `transform.Translate()`, `transform.Rotate()`, `transform.LookAt()`
- `transform.SetParent()`, `transform.GetChild()`, `transform.childCount`

---

## 3. Component 관리 (component.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `component.add` | `<goId> <typeName>` | 컴포넌트 추가 | `{ "ok": true, "typeName": "..." }` |
| `component.remove` | `<goId> <typeName>` | 컴포넌트 제거 | `{ "ok": true }` |
| `component.list` | `<goId>` | GO의 모든 컴포넌트 목록 | `{ "components": [{ "typeName": "...", "fields": [...] }] }` |

### 타입 해석 순서 (component.add)
`typeName` 문자열로부터 `Type`을 찾는 순서:
1. 엔진 내장 타입 — `RoseEngine` 네임스페이스 (MeshRenderer, Light, Camera, Rigidbody 등)
2. FrozenCode 어셈블리 — 프로젝트의 안정 스크립트
3. LiveCode 어셈블리 — `ScriptDomain.GetLoadedTypes()` (핫 리로드 스크립트)

이 순서로 `Type.Name == typeName`인 첫 매칭을 사용한다.

### 참고 API
- `go.AddComponent(Type)` — Type 객체로 컴포넌트 추가. MonoBehaviour이면 자동 등록.
- `go.RemoveComponent(comp)`
- `go.InternalComponents`
- `ScriptDomain.GetLoadedTypes()` — LiveCode에서 컴파일된 타입 목록
- FrozenCode 타입은 `AppDomain.CurrentDomain.GetAssemblies()`에서 FrozenCode 어셈블리를 찾아 조회

---

## 4. 프리팹 (prefab.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `prefab.instantiate` | `<guid> [x,y,z]` | 프리팹 인스턴스 생성 | `{ "id": int, "name": "..." }` |
| `prefab.save` | `<goId> <path>` | GO를 프리팹으로 저장 | `{ "saved": true, "path": "..." }` |
| `prefab.create_variant` | `<baseGuid> <path>` | 프리팹 Variant 생성 | `{ "created": true, "path": "..." }` |
| `prefab.is_instance` | `<goId>` | 프리팹 인스턴스 여부 | `{ "isPrefab": bool, "guid": "..." }` |
| `prefab.unpack` | `<goId>` | 프리팹 인스턴스 언팩 | `{ "ok": true }` |

### 참고 API
- `PrefabUtility.InstantiatePrefab(guid)` / `InstantiatePrefab(guid, pos, rot)`
- `PrefabUtility.SaveAsPrefab(go, path)` / `SceneSerializer.SavePrefab(go, path)`
- `PrefabUtility.CreateVariant(baseGuid, path)`
- `PrefabUtility.IsPrefabInstance(go)` / `GetPrefabGuid(go)`
- `PrefabUtility.UnpackPrefabInstance(go)`

---

## 5. 에셋 (asset.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `asset.list` | `[path]` | 에셋 폴더 탐색 (기본 Assets/) | `{ "assets": [{ "path": "...", "guid": "...", "type": "..." }] }` |
| `asset.find` | `<name>` | 이름으로 에셋 검색 | `{ "assets": [...] }` |
| `asset.guid` | `<path>` | 경로에서 GUID 조회 | `{ "guid": "..." }` |
| `asset.path` | `<guid>` | GUID에서 경로 조회 | `{ "path": "..." }` |
| `asset.import` | `<path>` | 에셋 임포트/리임포트 트리거 | `{ "ok": true }` |
| `asset.scan` | `[path]` | 에셋 스캔 실행 | `{ "count": int }` |

### 참고 API
- `AssetDatabase.GetGuidFromPath()` / `GetPathFromGuid()`
- `AssetDatabase.ScanAssets()`
- `AssetDatabase.Load<T>(path)` / `LoadByGuid<T>(guid)`
- `AssetDatabase.AssetCount`

---

## 6. 에디터 (editor.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `editor.undo` | — | 실행취소 | `{ "ok": true, "description": "..." }` |
| `editor.redo` | — | 다시실행 | `{ "ok": true, "description": "..." }` |
| `editor.undo_history` | — | Undo/Redo 스택 설명 조회 | `{ "undo": "...", "redo": "..." }` |
| `editor.screenshot` | `[path]` | 현재 화면 캡처 | `{ "saved": true, "path": "..." }` |
| `editor.copy` | `<goId>` | GO 복사 (클립보드) | `{ "ok": true }` |
| `editor.paste` | — | 클립보드에서 붙여넣기 | `{ "id": int, "name": "..." }` |
| `editor.select_all` | — | 모든 GO 선택 | `{ "count": int }` |

### 참고 API
- `UndoSystem.PerformUndo()` / `PerformRedo()`
- `UndoSystem.UndoDescription` / `RedoDescription`
- `EditorClipboard` — Copy/Paste
- `EditorSelection`

---

## 7. 카메라 (camera.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `camera.info` | `[id]` | 카메라 정보 (기본: main) | `{ "fov": float, "near": float, "far": float, "clearFlags": "..." }` |
| `camera.set_fov` | `<id> <fov>` | FOV 설정 | `{ "ok": true }` |
| `camera.set_clip` | `<id> <near> <far>` | 클리핑 설정 | `{ "ok": true }` |

### 참고 API
- `Camera.main`, `camera.fieldOfView`, `camera.nearClipPlane`, `camera.farClipPlane`

---

## 8. 라이트 (light.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `light.info` | `<id>` | 라이트 정보 | `{ "type": "...", "color": "...", "intensity": float, ... }` |
| `light.set_type` | `<id> <type>` | 타입 변경 (Directional/Point/Spot) | `{ "ok": true }` |
| `light.set_color` | `<id> <r,g,b,a>` | 색상 변경 | `{ "ok": true }` |
| `light.set_intensity` | `<id> <value>` | 강도 변경 | `{ "ok": true }` |
| `light.set_range` | `<id> <value>` | 범위 변경 | `{ "ok": true }` |
| `light.set_shadows` | `<id> <true\|false>` | 그림자 on/off | `{ "ok": true }` |

### 참고 API
- `Light` 컴포넌트 프로퍼티: `type`, `color`, `intensity`, `range`, `spotAngle`, `shadows`

---

## 9. 머티리얼 (material.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `material.info` | `<goId>` | GO의 MeshRenderer 머티리얼 정보 | `{ "color": "...", "metallic": float, "roughness": float, ... }` |
| `material.set_color` | `<goId> <r,g,b,a>` | 머티리얼 색상 변경 | `{ "ok": true }` |
| `material.set_metallic` | `<goId> <value>` | metallic 변경 | `{ "ok": true }` |
| `material.set_roughness` | `<goId> <value>` | roughness 변경 | `{ "ok": true }` |

### 참고 API
- `MeshRenderer.material` — `Material` 객체
- `Material.color`, `Material.metallic`, `Material.roughness`

---

## 10. 렌더 설정 (render.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `render.info` | — | 현재 렌더 설정 | `{ "ambientColor": "...", "skyboxExposure": float, ... }` |
| `render.set_ambient` | `<r,g,b>` | 앰비언트 색상 | `{ "ok": true }` |
| `render.set_skybox_exposure` | `<value>` | 스카이박스 노출 | `{ "ok": true }` |

### 참고 API
- `RenderSettings.ambientColor`, `RenderSettings.skyboxExposure`
- `SetRenderSettingsCommand`

---

## 11. 스크린 (screen.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `screen.info` | — | 화면 정보 | `{ "width": int, "height": int, "dpi": float }` |

### 참고 API
- `Screen.width`, `Screen.height`, `Screen.dpi`

---

## 12. 씬 확장 (scene.*)

| 명령 | 인자 | 설명 | 응답 data |
|------|------|------|-----------|
| `scene.new` | — | 새 빈 씬 생성 | `{ "ok": true }` |
| `scene.tree` | — | 계층 트리 (부모-자식 구조) | `{ "tree": [{ "id": int, "name": "...", "children": [...] }] }` |
| `scene.clear` | — | 씬 내 모든 GO 삭제 | `{ "ok": true }` |

### 참고 API
- `SceneManager.Clear()`
- `transform.childCount`, `transform.GetChild()`

---

## 구현 우선순위

### Wave 1 (핵심 — 씬 구성에 필수)
- `go.create`, `go.create_primitive`, `go.destroy`, `go.rename`, `go.duplicate`
- `transform.get`, `transform.set_position`, `transform.set_rotation`, `transform.set_scale`, `transform.set_parent`
- `component.add`, `component.remove`, `component.list`
- `editor.undo`, `editor.redo`

### Wave 2 (에셋/프리팹 — 실용적 워크플로우)
- `prefab.instantiate`, `prefab.save`
- `asset.list`, `asset.find`, `asset.guid`, `asset.path`
- `scene.tree`, `scene.new`

### Wave 3 (렌더링/비주얼 — 시각적 조작)
- `material.info`, `material.set_color`, `material.set_metallic`, `material.set_roughness`
- `light.info`, `light.set_color`, `light.set_intensity`
- `camera.info`, `camera.set_fov`
- `render.info`, `render.set_ambient`

### Wave 4 (편의 기능)
- `transform.translate`, `transform.rotate`, `transform.look_at`, `transform.get_children`
- `transform.set_local_position`
- `prefab.create_variant`, `prefab.is_instance`, `prefab.unpack`
- `asset.import`, `asset.scan`
- `editor.screenshot`, `editor.copy`, `editor.paste`, `editor.select_all`
- `editor.undo_history`
- `screen.info`
- `scene.clear`
- `camera.set_clip`
- `light.set_type`, `light.set_range`, `light.set_shadows`
- `render.set_skybox_exposure`
