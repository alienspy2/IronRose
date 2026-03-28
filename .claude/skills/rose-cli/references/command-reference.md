# IronRose CLI 전체 명령 레퍼런스

## 목차
- [Core](#core)
- [Scene](#scene)
- [GameObject](#gameobject)
- [Transform](#transform)
- [Component](#component)
- [Material](#material)
- [Light](#light)
- [Camera](#camera)
- [Render](#render)
- [Play Mode](#play-mode)
- [Prefab](#prefab)
- [Asset](#asset)
- [Editor](#editor)
- [System](#system)

---

## Core

### `ping`
연결 테스트. 백그라운드 스레드에서 즉시 응답.
```
ping
```
반환: `{ pong: true, project: "ProjectName" }`

---

## Scene

### `scene.info`
현재 활성 씬 정보 조회.
```
scene.info
```
반환: `{ name, path, gameObjectCount, isDirty }`

### `scene.list`
씬 내 모든 GameObject 목록.
```
scene.list
```
반환: `{ gameObjects: [{ id, name }, ...] }`

### `scene.tree`
부모-자식 계층 트리 (재귀 구조).
```
scene.tree
```
반환: 트리 구조 JSON

### `scene.save`
현재 씬 저장. 경로를 지정하면 다른 이름으로 저장.
```
scene.save [path]
```

### `scene.load`
씬 파일 로드.
```
scene.load <path>
```

### `scene.new`
새 빈 씬 생성 (기존 씬 Clear + 새 Scene 객체).
```
scene.new
```

### `scene.clear`
씬 내 모든 GO 삭제 (Scene 객체 자체는 유지, name/path 보존).
```
scene.clear
```

---

## GameObject

### `go.get`
GO 상세 정보 조회 (컴포넌트, 필드 포함).
```
go.get <id|name>
```
반환: `{ id, name, active, parentId, components: [{ typeName, fields: [{ name, typeName, value }] }] }`

### `go.find`
이름으로 GO 검색 (정확 매칭).
```
go.find <name>
```
반환: `{ gameObjects: [{ id, name }, ...] }`

### `go.create`
빈 GO 생성.
```
go.create [name]
```
- `name` 생략 시 "GameObject"
반환: `{ id, name }`

### `go.create_primitive`
프리미티브 메시 GO 생성.
```
go.create_primitive <type> [materialGuid|materialPath]
```
- `type`: Cube, Sphere, Capsule, Cylinder, Plane, Quad
- 머티리얼 GUID 또는 경로를 지정하면 해당 머티리얼 적용
반환: `{ id, name }`

### `go.destroy`
GO 즉시 삭제.
```
go.destroy <id>
```

### `go.rename`
GO 이름 변경.
```
go.rename <id> <name>
```

### `go.duplicate`
GO 복제. 이름에 "_copy" 접미사 추가. 같은 부모 아래 배치.
```
go.duplicate <id>
```
반환: `{ id, name }`

### `go.set_active`
GO 활성/비활성 설정.
```
go.set_active <id> <true|false>
```

### `go.set_field`
리플렉션으로 컴포넌트 필드 값 수정.
```
go.set_field <id> <ComponentTypeName> <fieldName> <value>
```
- 지원 타입: float, int, bool, string, Vector3(`x,y,z`), Color(`r,g,b,a`), enum

---

## Transform

### `transform.get`
GO의 Transform 정보 조회.
```
transform.get <id>
```
반환: `{ position, rotation, scale, localPosition, localRotation, localScale }`

### `transform.set_position`
월드 위치 설정.
```
transform.set_position <id> <x,y,z>
```

### `transform.set_local_position`
로컬 위치 설정.
```
transform.set_local_position <id> <x,y,z>
```

### `transform.set_rotation`
월드 회전 설정 (오일러 각도).
```
transform.set_rotation <id> <x,y,z>
```

### `transform.set_scale`
로컬 스케일 설정.
```
transform.set_scale <id> <x,y,z>
```

### `transform.set_parent`
부모 GO 설정. "none"이면 루트로 이동.
```
transform.set_parent <id> <parentId|none>
```

### `transform.translate`
상대 이동 (월드 좌표).
```
transform.translate <id> <x,y,z>
```

### `transform.rotate`
상대 회전 (로컬, 오일러).
```
transform.rotate <id> <x,y,z>
```

### `transform.look_at`
다른 GO를 바라보도록 회전.
```
transform.look_at <id> <targetId>
```

### `transform.get_children`
직접 자식 GO 목록 조회.
```
transform.get_children <id>
```
반환: `{ children: [{ id, name }, ...] }`

---

## Component

### `component.add`
GO에 컴포넌트 추가.
```
component.add <goId> <typeName>
```
- `typeName`: 컴포넌트 타입의 전체 이름 또는 짧은 이름

### `component.remove`
GO에서 컴포넌트 제거.
```
component.remove <goId> <typeName>
```

### `component.list`
GO의 모든 컴포넌트 목록 (필드 포함).
```
component.list <goId>
```
반환: `{ components: [{ typeName, fields: [{ name, typeName, value }] }] }`

---

## Material

### `material.info`
GO의 MeshRenderer 머티리얼 정보 조회.
```
material.info <goId>
```
반환: `{ materialName, color, metallic, roughness, ... }`

### `material.set_color`
머티리얼 색상 변경.
```
material.set_color <goId> <r,g,b,a>
```

### `material.set_metallic`
metallic 값 변경 (0~1).
```
material.set_metallic <goId> <value>
```

### `material.set_roughness`
roughness 값 변경 (0~1).
```
material.set_roughness <goId> <value>
```

### `material.create`
새 머티리얼 파일 생성 후 AssetDatabase에 등록.
```
material.create <name> <dirPath> [r,g,b,a]
```
- 색상 미지정 시 기본 흰색
반환: `{ created: true, path, guid }`

### `material.apply`
GO의 MeshRenderer에 머티리얼 적용.
```
material.apply <goId> <materialGuid|materialPath>
```
- GUID 또는 파일 경로 모두 가능

---

## Light

### `light.info`
Light 컴포넌트 정보 조회.
```
light.info <goId>
```
반환: `{ type, color, intensity, range, shadows, ... }`

### `light.set_color`
라이트 색상 변경.
```
light.set_color <goId> <r,g,b,a>
```

### `light.set_intensity`
라이트 강도 변경.
```
light.set_intensity <goId> <value>
```

### `light.set_type`
라이트 타입 변경.
```
light.set_type <goId> <Directional|Point|Spot>
```

### `light.set_range`
라이트 범위 변경 (Point/Spot).
```
light.set_range <goId> <value>
```

### `light.set_shadows`
그림자 on/off.
```
light.set_shadows <goId> <true|false>
```

---

## Camera

### `camera.info`
카메라 정보 조회. ID 미지정 시 Camera.main.
```
camera.info [goId]
```
반환: `{ fov, nearClip, farClip, ... }`

### `camera.set_fov`
FOV 설정.
```
camera.set_fov <goId> <value>
```

### `camera.set_clip`
클리핑 near/far 설정.
```
camera.set_clip <goId> <near> <far>
```

---

## Render

### `render.info`
전역 렌더 설정 조회 (ambient, skybox, FSR, SSIL 등).
```
render.info
```

### `render.set_ambient`
앰비언트 라이트 색상 변경.
```
render.set_ambient <r,g,b,a>
```

### `render.set_skybox_exposure`
스카이박스 노출 변경.
```
render.set_skybox_exposure <value>
```

---

## Play Mode

### `play.enter`
Play 모드 진입.
```
play.enter
```

### `play.stop`
Play 모드 종료.
```
play.stop
```

### `play.pause`
일시정지.
```
play.pause
```

### `play.resume`
재개.
```
play.resume
```

### `play.state`
현재 Play 상태 조회.
```
play.state
```
반환: `{ state: "Stopped"|"Playing"|"Paused" }`

---

## Prefab

### `prefab.instantiate`
GUID로 프리팹 인스턴스 생성.
```
prefab.instantiate <guid> [x,y,z]
```
반환: `{ id, name }`

### `prefab.save`
GO를 프리팹 파일로 저장.
```
prefab.save <goId> <path>
```
반환: `{ saved: true, path, guid }`

### `prefab.create_variant`
Variant 프리팹 생성.
```
prefab.create_variant <goId> <path>
```

### `prefab.is_instance`
프리팹 인스턴스 여부 확인.
```
prefab.is_instance <goId>
```

### `prefab.unpack`
프리팹 인스턴스 언팩 (독립 GO로 변환).
```
prefab.unpack <goId>
```

---

## Asset

### `asset.list`
에셋 목록 조회. 경로 지정 시 해당 폴더 내 에셋만 필터링 (Contains 부분 매칭).
```
asset.list [filterPath]
```

### `asset.find`
이름으로 에셋 검색 (case-insensitive 부분 매칭).
```
asset.find <name>
```

### `asset.guid`
파일 경로에서 GUID 조회.
```
asset.guid <path>
```

### `asset.path`
GUID에서 파일 경로 조회.
```
asset.path <guid>
```

### `asset.import`
에셋 임포트/리임포트 (ScanAssets 호출).
```
asset.import <path>
```

### `asset.scan`
에셋 스캔 실행.
```
asset.scan [path]
```

---

## Editor

### `select`
에디터에서 GO 선택. "none"이면 선택 해제.
```
select <id|none>
```

### `select.get`
현재 선택된 GO 조회.
```
select.get
```

### `editor.undo`
실행취소.
```
editor.undo
```

### `editor.redo`
다시실행.
```
editor.redo
```

### `editor.screenshot`
화면 캡처. 비동기 — 파일은 다음 프레임 이후 생성.
```
editor.screenshot <path>
```

### `editor.copy`
선택된 GO 복사 (클립보드). 내부적으로 선택 상태를 변경하는 사이드 이펙트 있음.
```
editor.copy <goId>
```

### `editor.paste`
클립보드에서 붙여넣기.
```
editor.paste
```

### `editor.select_all`
모든 GO 선택.
```
editor.select_all
```

### `editor.undo_history`
Undo/Redo 스택 설명 조회.
```
editor.undo_history
```

---

## System

### `log.recent`
최근 로그 조회 (스레드 안전, 백그라운드 실행). 최대 1000개 링 버퍼.
```
log.recent [count]
```

### `screen.info`
화면 정보 조회.
```
screen.info
```
반환: `{ width, height, dpi }`

### `assembly.info`
로드된 어셈블리 정보 및 Component 타입 목록 조회.
```
assembly.info
```
반환: `{ totalAssemblies, assemblies: [{ name, componentCount, components }], liveCodeDemoTypes, liveCodeDemoCount }`
