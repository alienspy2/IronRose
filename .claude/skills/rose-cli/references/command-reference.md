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
- [Sprite](#sprite)
- [UI 편의 생성](#ui-편의-생성)
- [UI 트리/조회](#ui-트리조회)
- [UI RectTransform](#ui-recttransform)
- [UI Canvas](#ui-canvas)
- [UI Text](#ui-text)
- [UI Image](#ui-image)
- [UI Panel](#ui-panel)
- [UI Button](#ui-button)
- [UI Toggle](#ui-toggle)
- [UI Slider](#ui-slider)
- [UI InputField](#ui-inputfield)
- [UI LayoutGroup](#ui-layoutgroup)
- [UI ScrollView](#ui-scrollview)
- [UI 디버깅](#ui-디버깅)
- [UI 정렬/분배](#ui-정렬분배)
- [UI 테마](#ui-테마)
- [UI 프리팹](#ui-프리팹)

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

---

## Sprite

### `sprite.info`
텍스처/스프라이트 에셋의 임포트 설정 조회.
```
sprite.info <assetPath|guid>
```
반환: `{ path, guid, textureType, isSprite, maxSize, filterMode, wrapMode, srgb, spriteMode, pixelsPerUnit, pivot, border, slices }`

### `sprite.set_type`
텍스처를 스프라이트로 전환 (또는 반대). 스프라이트 전환 시 기본 설정 자동 적용 (mipmap off, Clamp, Single 모드).
```
sprite.set_type <assetPath|guid> <Sprite|Color>
```
반환: `{ path, textureType }`

### `sprite.set_border`
9-slice border 설정 (픽셀 단위).
```
sprite.set_border <assetPath|guid> <left,bottom,right,top>
```
- 예: `sprite.set_border Assets/UI/panel.png 16,16,16,16`
반환: `{ path, border }`

### `sprite.set_pivot`
피벗 설정 (0~1 정규화 좌표).
```
sprite.set_pivot <assetPath|guid> <x,y>
```
- 예: `sprite.set_pivot Assets/UI/panel.png 0.5,0.5`
반환: `{ path, pivot }`

### `sprite.set_ppu`
Pixels Per Unit 설정.
```
sprite.set_ppu <assetPath|guid> <value>
```
반환: `{ path, pixelsPerUnit }`

### `sprite.set_mode`
스프라이트 모드 전환.
```
sprite.set_mode <assetPath|guid> <Single|Multiple>
```
반환: `{ path, spriteMode }`

### `sprite.set_filter`
텍스처 필터 모드 설정.
```
sprite.set_filter <assetPath|guid> <Point|Bilinear|Trilinear>
```
- 픽셀아트 스프라이트는 `Point`, 일반 스프라이트는 `Bilinear` 권장
반환: `{ path, filterMode }`

---

## UI 편의 생성

### `ui.create_canvas`
Canvas + RectTransform GO 생성.
```
ui.create_canvas [name]
```
- `name` 생략 시 "Canvas"
반환: `{ id, name }`

### `ui.create_text`
부모 하위에 UIText GO 생성 (RectTransform 자동).
```
ui.create_text <parentId> [text] [fontSize]
```
반환: `{ id, name }`

### `ui.create_image`
부모 하위에 UIImage GO 생성 (RectTransform 자동).
```
ui.create_image <parentId>
```
반환: `{ id, name }`

### `ui.create_panel`
부모 하위에 UIPanel GO 생성. 색상 지정 가능.
```
ui.create_panel <parentId> [r,g,b,a]
```
반환: `{ id, name }`

### `ui.create_button`
부모 하위에 UIButton + UIImage + 자식(UIText) GO 복합 생성.
```
ui.create_button <parentId> [label]
```
반환: `{ id, name, labelId }`

### `ui.create_toggle`
부모 하위에 UIToggle GO 생성.
```
ui.create_toggle <parentId>
```
반환: `{ id, name }`

### `ui.create_slider`
부모 하위에 UISlider GO 생성.
```
ui.create_slider <parentId>
```
반환: `{ id, name }`

### `ui.create_input`
부모 하위에 UIInputField GO 생성.
```
ui.create_input <parentId> [placeholder]
```
반환: `{ id, name }`

### `ui.create_layout`
부모 하위에 UILayoutGroup GO 생성.
```
ui.create_layout <parentId> [Horizontal|Vertical]
```
- 기본값: `Vertical`
반환: `{ id, name }`

### `ui.create_scroll`
부모 하위에 UIScrollView GO 생성. Content 자식도 함께 생성.
```
ui.create_scroll <parentId>
```
반환: `{ id, name, contentId }`

---

## UI 트리/조회

### `ui.tree`
모든 Canvas(또는 특정 Canvas)의 UI 계층 트리 조회. 각 노드에 RectTransform 정보와 UI 컴포넌트 타입 표시.
```
ui.tree [canvasId]
```
반환: `{ canvases: [{ id, name, renderMode, sortingOrder, tree: { id, name, active, rect, uiComponents, children } }] }`

### `ui.list`
모든 Canvas(또는 특정 Canvas) 하위의 UI 요소 flat 목록 조회.
```
ui.list [canvasId]
```

### `ui.find`
UI 컴포넌트가 있는 GO 중 이름으로 검색.
```
ui.find <name>
```

### `ui.canvas.list`
씬 내 모든 Canvas 목록.
```
ui.canvas.list
```
반환: `{ canvases: [{ id, name, renderMode, sortingOrder }] }`

---

## UI RectTransform

### `ui.rect.get`
RectTransform 전체 정보 조회.
```
ui.rect.get <goId>
```
반환: `{ anchorMin, anchorMax, anchoredPosition, sizeDelta, pivot, offsetMin, offsetMax, rect, lastScreenRect }`

### `ui.rect.set_anchors`
anchorMin, anchorMax 설정.
```
ui.rect.set_anchors <goId> <minX,minY> <maxX,maxY>
```

### `ui.rect.set_position`
anchoredPosition 설정.
```
ui.rect.set_position <goId> <x,y>
```

### `ui.rect.set_size`
sizeDelta 설정.
```
ui.rect.set_size <goId> <w,h>
```

### `ui.rect.set_pivot`
pivot 설정.
```
ui.rect.set_pivot <goId> <x,y>
```

### `ui.rect.set_offsets`
offsetMin, offsetMax 설정 (stretch 모드에서 유용).
```
ui.rect.set_offsets <goId> <minX,minY> <maxX,maxY>
```

### `ui.rect.set_preset`
AnchorPreset 적용.
```
ui.rect.set_preset <goId> <preset> [keepVisual]
```
- `preset`: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight, TopStretch, MiddleStretch, BottomStretch, StretchLeft, StretchCenter, StretchRight, StretchAll
- `keepVisual`: `true`면 시각적 위치 유지 (기본 `false`)

### `ui.rect.get_world_rect`
마지막 렌더링된 스크린 좌표 Rect 조회.
```
ui.rect.get_world_rect <goId>
```

---

## UI Canvas

### `ui.canvas.info`
Canvas 컴포넌트 상세 정보 조회.
```
ui.canvas.info <goId>
```
반환: `{ renderMode, sortingOrder, referenceResolution, scaleMode, matchWidthOrHeight }`

### `ui.canvas.set_render_mode`
renderMode 변경.
```
ui.canvas.set_render_mode <goId> <ScreenSpaceOverlay|ScreenSpaceCamera|WorldSpace>
```

### `ui.canvas.set_sorting_order`
sortingOrder 변경.
```
ui.canvas.set_sorting_order <goId> <order>
```

### `ui.canvas.set_reference_resolution`
referenceResolution 변경.
```
ui.canvas.set_reference_resolution <goId> <w,h>
```

### `ui.canvas.set_scale_mode`
scaleMode 변경.
```
ui.canvas.set_scale_mode <goId> <ConstantPixelSize|ScaleWithScreenSize>
```

### `ui.canvas.set_match`
matchWidthOrHeight 변경 (0~1).
```
ui.canvas.set_match <goId> <value>
```

---

## UI Text

### `ui.text.info`
UIText 컴포넌트 정보 조회.
```
ui.text.info <goId>
```
반환: `{ text, fontSize, color, alignment, overflow, hasFont }`

### `ui.text.set_text`
텍스트 내용 변경.
```
ui.text.set_text <goId> <text>
```

### `ui.text.set_font_size`
fontSize 변경.
```
ui.text.set_font_size <goId> <size>
```

### `ui.text.set_color`
텍스트 색상 변경.
```
ui.text.set_color <goId> <r,g,b,a>
```

### `ui.text.set_alignment`
alignment 변경.
```
ui.text.set_alignment <goId> <UpperLeft|UpperCenter|UpperRight|MiddleLeft|MiddleCenter|MiddleRight|LowerLeft|LowerCenter|LowerRight>
```

### `ui.text.set_font`
폰트 에셋 설정. GUID 또는 에셋 경로로 지정.
```
ui.text.set_font <goId> <fontGuid|fontPath>
```

### `ui.text.set_overflow`
overflow 변경.
```
ui.text.set_overflow <goId> <Wrap|Overflow|Ellipsis>
```

---

## UI Image

### `ui.image.info`
UIImage 컴포넌트 정보 조회.
```
ui.image.info <goId>
```

### `ui.image.set_color`
이미지 틴트 색상 변경.
```
ui.image.set_color <goId> <r,g,b,a>
```

### `ui.image.set_type`
imageType 변경.
```
ui.image.set_type <goId> <Simple|Sliced|Tiled|Filled>
```

### `ui.image.set_sprite`
스프라이트 변경.
```
ui.image.set_sprite <goId> <spriteGuid|spritePath>
```

### `ui.image.set_preserve_aspect`
preserveAspect 변경.
```
ui.image.set_preserve_aspect <goId> <true|false>
```

---

## UI Panel

### `ui.panel.info`
UIPanel 컴포넌트 정보 조회.
```
ui.panel.info <goId>
```

### `ui.panel.set_color`
패널 배경 색상 변경.
```
ui.panel.set_color <goId> <r,g,b,a>
```

### `ui.panel.set_sprite`
패널 배경 스프라이트 변경.
```
ui.panel.set_sprite <goId> <spriteGuid|spritePath>
```

### `ui.panel.set_type`
imageType 변경.
```
ui.panel.set_type <goId> <Simple|Sliced>
```

---

## UI Button

### `ui.button.info`
UIButton 컴포넌트 정보 조회.
```
ui.button.info <goId>
```
반환: `{ interactable, normalColor, hoverColor, pressedColor, disabledColor, transition }`

### `ui.button.set_interactable`
interactable 변경.
```
ui.button.set_interactable <goId> <true|false>
```

### `ui.button.set_colors`
4가지 상태 색상 일괄 설정.
```
ui.button.set_colors <goId> <normal> <hover> <pressed> <disabled>
```
- 각 색상은 `r,g,b,a` 형식
- 예: `ui.button.set_colors 44 1,1,1,1 0.9,0.9,0.9,1 0.7,0.7,0.7,1 0.5,0.5,0.5,0.5`

### `ui.button.set_transition`
transition 변경.
```
ui.button.set_transition <goId> <ColorTint|SpriteSwap>
```

---

## UI Toggle

### `ui.toggle.info`
UIToggle 컴포넌트 정보 조회.
```
ui.toggle.info <goId>
```

### `ui.toggle.set_on`
isOn 변경.
```
ui.toggle.set_on <goId> <true|false>
```

### `ui.toggle.set_interactable`
interactable 변경.
```
ui.toggle.set_interactable <goId> <true|false>
```

### `ui.toggle.set_colors`
배경/체크마크 색상 설정.
```
ui.toggle.set_colors <goId> <bgColor> <checkColor>
```

---

## UI Slider

### `ui.slider.info`
UISlider 컴포넌트 정보 조회.
```
ui.slider.info <goId>
```
반환: `{ value, minValue, maxValue, wholeNumbers, direction, interactable, backgroundColor, fillColor, handleColor }`

### `ui.slider.set_value`
value 변경.
```
ui.slider.set_value <goId> <value>
```

### `ui.slider.set_range`
minValue, maxValue 설정.
```
ui.slider.set_range <goId> <min> <max>
```

### `ui.slider.set_direction`
direction 변경.
```
ui.slider.set_direction <goId> <LeftToRight|RightToLeft|BottomToTop|TopToBottom>
```

### `ui.slider.set_whole_numbers`
wholeNumbers 변경.
```
ui.slider.set_whole_numbers <goId> <true|false>
```

### `ui.slider.set_interactable`
interactable 변경.
```
ui.slider.set_interactable <goId> <true|false>
```

### `ui.slider.set_colors`
3가지 색상 일괄 설정.
```
ui.slider.set_colors <goId> <bgColor> <fillColor> <handleColor>
```

---

## UI InputField

### `ui.input.info`
UIInputField 컴포넌트 정보 조회.
```
ui.input.info <goId>
```

### `ui.input.set_text`
text 변경.
```
ui.input.set_text <goId> <text>
```

### `ui.input.set_placeholder`
placeholder 변경.
```
ui.input.set_placeholder <goId> <text>
```

### `ui.input.set_font_size`
fontSize 변경.
```
ui.input.set_font_size <goId> <size>
```

### `ui.input.set_max_length`
maxLength 변경.
```
ui.input.set_max_length <goId> <length>
```

### `ui.input.set_content_type`
contentType 변경.
```
ui.input.set_content_type <goId> <Standard|IntegerNumber|DecimalNumber|Alphanumeric|Password>
```

### `ui.input.set_interactable`
interactable 변경.
```
ui.input.set_interactable <goId> <true|false>
```

### `ui.input.set_read_only`
readOnly 변경.
```
ui.input.set_read_only <goId> <true|false>
```

---

## UI LayoutGroup

### `ui.layout.info`
UILayoutGroup 컴포넌트 정보 조회.
```
ui.layout.info <goId>
```
반환: `{ direction, spacing, padding, childAlignment, childForceExpandWidth, childForceExpandHeight }`

### `ui.layout.set_direction`
direction 변경.
```
ui.layout.set_direction <goId> <Horizontal|Vertical>
```

### `ui.layout.set_spacing`
spacing 변경.
```
ui.layout.set_spacing <goId> <value>
```

### `ui.layout.set_padding`
padding 변경 (Vector4 형식).
```
ui.layout.set_padding <goId> <left,bottom,right,top>
```

### `ui.layout.set_child_alignment`
childAlignment 변경.
```
ui.layout.set_child_alignment <goId> <alignment>
```

### `ui.layout.set_force_expand`
childForceExpandWidth, childForceExpandHeight 설정.
```
ui.layout.set_force_expand <goId> <width:true|false> <height:true|false>
```

---

## UI ScrollView

### `ui.scroll.info`
UIScrollView 컴포넌트 정보 조회.
```
ui.scroll.info <goId>
```

### `ui.scroll.set_scroll_position`
scrollPosition 변경.
```
ui.scroll.set_scroll_position <goId> <x,y>
```

### `ui.scroll.set_content_size`
contentSize 변경.
```
ui.scroll.set_content_size <goId> <w,h>
```

### `ui.scroll.set_direction`
horizontal, vertical 설정.
```
ui.scroll.set_direction <goId> <horizontal:true|false> <vertical:true|false>
```

### `ui.scroll.set_sensitivity`
scrollSensitivity 변경.
```
ui.scroll.set_sensitivity <goId> <value>
```

---

## UI 디버깅

### `ui.debug.rects`
CanvasRenderer.DebugDrawRects 토글. 모든 RectTransform의 아웃라인을 게임 뷰에 표시.
```
ui.debug.rects <true|false>
```

### `ui.debug.overlap`
겹치는 UI 요소 쌍을 검출하여 목록 반환.
```
ui.debug.overlap [canvasId]
```
반환: `{ overlaps: [{ a: { id, name }, b: { id, name }, intersection }], count }`

### `ui.debug.hit_test`
지정 스크린 좌표에서 hit test 수행. 최상위 UI GO 반환.
```
ui.debug.hit_test <screenX> <screenY>
```

---

## UI 정렬/분배

### `ui.align`
여러 UI 요소를 지정 방향으로 정렬. 첫 번째 GO의 edge를 기준으로 나머지를 맞춤.
```
ui.align <edge> <goId1> <goId2> [goId3...]
```
- `edge`: `left`, `right`, `top`, `bottom`, `center_h`, `center_v`
- 최소 2개의 GO ID 필요
반환: `{ ok: true, aligned: <count> }`

### `ui.distribute`
여러 UI 요소를 균등 분배. 첫 번째와 마지막 요소 사이를 균등 배분.
```
ui.distribute <axis> <goId1> <goId2> [goId3...]
```
- `axis`: `horizontal`, `vertical`
- 최소 3개의 GO ID 필요
반환: `{ ok: true, distributed: <count> }`

---

## UI 테마

### `ui.theme.apply_color`
Canvas 하위의 모든 지정 컴포넌트 타입의 지정 색상 필드를 일괄 변경.
```
ui.theme.apply_color <canvasId> <componentType> <field> <r,g,b,a>
```
- `componentType`: `UIText`, `UIImage`, `UIPanel`, `UIButton` 등
- `field`: `color`, `textColor`, `backgroundColor`, `normalColor` 등
- 예: `ui.theme.apply_color 42 UIText color 1,1,1,1`
반환: `{ ok: true, affected: <count> }`

### `ui.theme.apply_font_size`
Canvas 하위의 모든 UIText의 fontSize를 일괄 변경.
```
ui.theme.apply_font_size <canvasId> <size>
```
반환: `{ ok: true, affected: <count> }`

---

## UI 프리팹

### `ui.prefab.save`
UI GO 서브트리를 프리팹으로 저장.
```
ui.prefab.save <goId> <path>
```
반환: `{ saved: true, path, guid }`

### `ui.prefab.instantiate`
UI 프리팹을 특정 부모 하위에 인스턴스화. 일반 `prefab.instantiate`와 달리 부모를 즉시 설정.
```
ui.prefab.instantiate <guid> <parentId>
```
반환: `{ id, name }`
