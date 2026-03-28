# CLI UI 명령 추가

## 배경

IronRose 에디터의 CLI 브릿지(rose-cli)는 현재 scene, go, transform, component, material, light, camera, render, play, prefab, asset, editor, system 카테고리의 60여 개 명령을 지원한다. 그러나 UI 시스템(Canvas, RectTransform, UIText, UIImage, UIPanel, UIButton, UIToggle, UISlider, UIInputField, UILayoutGroup, UIScrollView)을 조작하는 전용 명령이 없어, CLI만으로 UI를 구성하거나 디버깅하기 어렵다.

기존에는 `go.set_field`와 `component.add`를 조합하여 UI 필드를 하나씩 수정할 수 있었지만, UI 작업의 특성상 계층 구조 생성, RectTransform 앵커/피벗 설정, 편의 생성(Canvas + RectTransform 자동 추가), UI 트리 조회, 정렬/분배 등의 고수준 명령이 필요하다.

## 목표

1. UI 요소의 편의 생성 명령 제공 (Canvas, Text, Image, Button 등을 한 번에 생성)
2. RectTransform 전용 조회/수정 명령 (`ui.rect.*`)
3. 각 UI 컴포넌트별 조회/수정 명령 (`ui.*`)
4. UI 디버깅/워크플로우 명령 (트리 조회, 정렬/분배, 오버랩 감지 등)
5. 기존 CLI 패턴과 완전히 일관된 네이밍, 응답 포맷, 구현 구조

## 현재 상태

### CLI 아키텍처

```
Python CLI (ironrose_cli.py)
  └─ Named Pipe (Unix Domain Socket)
       └─ C# CliPipeServer (백그라운드 스레드)
            └─ CliCommandDispatcher.Dispatch(requestLine)
                 └─ _handlers["command.name"](args) → JSON 응답
```

- **Python 클라이언트**: 명령을 해석하지 않고 그대로 릴레이 (새 명령 추가 시 Python 수정 불필요)
- **C# CliCommandDispatcher**: 단일 파일에 모든 핸들러가 `RegisterHandlers()` 메서드 내에 람다로 등록
  - 핸들러 시그니처: `Func<string[], string>` (args → JSON 응답 문자열)
  - 메인 스레드 필요 시 `ExecuteOnMainThread()` 사용 (5초 타임아웃)
  - 헬퍼: `FindGameObject()`, `FindGameObjectById()`, `ParseVector3()`, `ParseColor()`, `ParseFieldValue()`, `FormatVector3()`, `FormatColor()`, `JsonOk()`, `JsonError()`
- **파일**: `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` (현재 약 2,350줄)

### UI 시스템 클래스 구조

| 클래스 | 위치 | 주요 필드 |
|--------|------|-----------|
| `Canvas` | `RoseEngine/Canvas.cs` | `renderMode` (enum CanvasRenderMode), `sortingOrder` (int), `referenceResolution` (Vector2), `scaleMode` (enum CanvasScaleMode), `matchWidthOrHeight` (float) |
| `RectTransform` | `RoseEngine/RectTransform.cs` | `anchorMin` (Vector2), `anchorMax` (Vector2), `anchoredPosition` (Vector2), `sizeDelta` (Vector2), `pivot` (Vector2). 파생: `offsetMin`, `offsetMax`, `rect`. 메서드: `SetAnchorPreset()`, `SetAnchorPresetKeepVisual()`, `SetSizeWithCurrentAnchors()`, `SetInsetAndSizeFromParentEdge()`, `GetWorldRect()`, `GetLocalCorners()` |
| `UIText` | `RoseEngine/UI/UIText.cs` | `text` (string), `fontSize` (float), `color` (Color), `alignment` (enum TextAnchor), `overflow` (enum TextOverflow), `font` (Font) |
| `UIImage` | `RoseEngine/UI/UIImage.cs` | `sprite` (Sprite), `color` (Color), `imageType` (enum ImageType), `preserveAspect` (bool) |
| `UIPanel` | `RoseEngine/UI/UIPanel.cs` | `color` (Color), `sprite` (Sprite), `imageType` (enum ImageType) |
| `UIButton` | `RoseEngine/UI/UIButton.cs` | `interactable` (bool), `normalColor`, `hoverColor`, `pressedColor`, `disabledColor` (Color), `transition` (enum ButtonTransition) |
| `UIToggle` | `RoseEngine/UI/UIToggle.cs` | `isOn` (bool), `interactable` (bool), `backgroundColor`, `checkmarkColor` (Color) |
| `UISlider` | `RoseEngine/UI/UISlider.cs` | `value`, `minValue`, `maxValue` (float), `wholeNumbers` (bool), `direction` (enum SliderDirection), `backgroundColor`, `fillColor`, `handleColor` (Color), `handleSize` (float), `interactable` (bool) |
| `UIInputField` | `RoseEngine/UI/UIInputField.cs` | `text`, `placeholder` (string), `fontSize` (float), `maxLength` (int), `contentType` (enum InputFieldContentType), `textColor`, `placeholderColor`, `backgroundColor`, `focusedBorderColor`, `borderColor`, `selectionColor` (Color), `padding` (float), `interactable` (bool), `readOnly` (bool), `font` (Font) |
| `UILayoutGroup` | `RoseEngine/UI/UILayoutGroup.cs` | `direction` (enum LayoutDirection), `spacing` (float), `padding` (Vector4), `childAlignment` (enum LayoutChildAlignment), `childForceExpandWidth`, `childForceExpandHeight` (bool) |
| `UIScrollView` | `RoseEngine/UI/UIScrollView.cs` | `horizontal`, `vertical` (bool), `scrollPosition`, `contentSize` (Vector2), `scrollSensitivity` (float), `scrollbarColor`, `scrollbarHoverColor` (Color), `scrollbarWidth` (float) |
| `CanvasRenderer` | `RoseEngine/CanvasRenderer.cs` | (정적 유틸) `DebugDrawRects` (bool), `HitTest()`, `CollectHitsInRect()`, `GetCanvasScaleFor()` |

### 네이밍 컨벤션

기존 명령은 `카테고리.동사` 또는 `카테고리.동사_수식어` 패턴:
- `go.create`, `go.create_primitive`, `go.set_field`, `go.set_active`
- `transform.set_position`, `transform.set_local_position`, `transform.get_children`
- `material.info`, `material.set_color`, `material.create`, `material.apply`
- `scene.tree`, `scene.info`, `scene.list`

### 응답 포맷

- 성공: `{ "ok": true, "data": { ... } }`
- 실패: `{ "ok": false, "error": "..." }`
- 생성 명령: `{ id, name }` 반환
- 정보 조회: 관련 필드를 flat object로 반환
- 수정 명령: `{ ok: true }` 반환

---

## 설계

### 개요

새로운 UI 명령을 3개 카테고리로 분류한다:

1. **`ui.*`** -- UI 편의 생성, 트리 조회, 디버깅, 정렬/분배, 테마 등 고수준 명령
2. **`ui.rect.*`** -- RectTransform 전용 조회/수정 (요구사항에 따라 별도 카테고리)
3. **`ui.<component>.*`** -- 개별 UI 컴포넌트(text, image, panel, button, toggle, slider, input, layout, scroll) 필드 조회/수정

모든 명령은 기존 `CliCommandDispatcher` 파일이 이미 2,350줄로 매우 크므로, **별도 파일로 핸들러를 분리**하는 리팩토링을 함께 진행한다.

### 상세 설계

---

#### 1. 아키텍처 리팩토링: 핸들러 분리

현재 `CliCommandDispatcher.RegisterHandlers()`에 모든 핸들러가 있어 파일이 비대하다. UI 명령 추가 시 더 커지므로, 핸들러 등록을 카테고리별 메서드로 분리한다.

**파일**: `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

```csharp
private void RegisterHandlers()
{
    RegisterCoreHandlers();       // ping, scene.*, go.*, select.*, play.*, log.*
    RegisterTransformHandlers();  // transform.*
    RegisterComponentHandlers();  // component.*
    RegisterMaterialHandlers();   // material.*
    RegisterLightHandlers();      // light.*
    RegisterCameraHandlers();     // camera.*
    RegisterRenderHandlers();     // render.*
    RegisterPrefabHandlers();     // prefab.*
    RegisterAssetHandlers();      // asset.*
    RegisterEditorHandlers();     // editor.*, screen.*, assembly.*
    RegisterUIHandlers();         // ui.* (신규)
}
```

**새 파일**: `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs` (partial class)

```csharp
namespace IronRose.Engine.Cli
{
    public partial class CliCommandDispatcher
    {
        private void RegisterUIHandlers()
        {
            // 모든 ui.* 핸들러 등록
        }
    }
}
```

> **주의**: 기존 `CliCommandDispatcher`를 `partial class`로 변경해야 한다. 기존 코드의 클래스 선언을 `public partial class CliCommandDispatcher`로 수정.

---

#### 2. 새 헬퍼 메서드

`CliCommandDispatcher.UI.cs`에 다음 헬퍼를 추가한다:

```csharp
/// <summary>"x,y" 형식의 문자열을 Vector2로 파싱한다.</summary>
private static Vector2 ParseVector2(string raw)
{
    var cleaned = raw.Trim('(', ')', ' ');
    var parts = cleaned.Split(',');
    return new Vector2(
        float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
        float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
}

/// <summary>Vector2를 "x, y" 형식으로 포맷한다.</summary>
private static string FormatVector2(Vector2 v)
{
    return $"{v.x.ToString(CultureInfo.InvariantCulture)}, {v.y.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Vector4를 "x, y, z, w" 형식으로 포맷한다.</summary>
private static string FormatVector4(Vector4 v)
{
    return $"{v.x.ToString(CultureInfo.InvariantCulture)}, {v.y.ToString(CultureInfo.InvariantCulture)}, {v.z.ToString(CultureInfo.InvariantCulture)}, {v.w.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>"x,y,z,w" 형식을 Vector4로 파싱한다.</summary>
private static Vector4 ParseVector4(string raw)
{
    var cleaned = raw.Trim('(', ')', ' ');
    var parts = cleaned.Split(',');
    return new Vector4(
        float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
        float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
        float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
        float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture));
}

/// <summary>GO의 조상 Canvas를 찾는다. Canvas가 없으면 null.</summary>
private static Canvas? FindParentCanvas(GameObject go)
{
    var current = go.transform;
    while (current != null)
    {
        var canvas = current.gameObject.GetComponent<Canvas>();
        if (canvas != null) return canvas;
        current = current.parent;
    }
    return null;
}

/// <summary>Canvas 하위의 UI 트리를 재귀적으로 구축한다. RectTransform 정보 포함.</summary>
private static object BuildUITreeNode(GameObject go)
{
    var rt = go.GetComponent<RectTransform>();
    var children = new List<object>();
    for (int i = 0; i < go.transform.childCount; i++)
    {
        var child = go.transform.GetChild(i).gameObject;
        if (!child._isDestroyed)
            children.Add(BuildUITreeNode(child));
    }

    // UI 컴포넌트 타입 수집
    var uiTypes = new List<string>();
    foreach (var comp in go.InternalComponents)
    {
        if (comp is IUIRenderable && !comp._isDestroyed)
            uiTypes.Add(comp.GetType().Name);
    }
    if (go.GetComponent<Canvas>() != null) uiTypes.Add("Canvas");
    if (go.GetComponent<UILayoutGroup>() != null) uiTypes.Add("UILayoutGroup");

    return new
    {
        id = go.GetInstanceID(),
        name = go.name,
        active = go.activeSelf,
        rect = rt != null ? new
        {
            anchoredPosition = FormatVector2(rt.anchoredPosition),
            sizeDelta = FormatVector2(rt.sizeDelta),
            anchorMin = FormatVector2(rt.anchorMin),
            anchorMax = FormatVector2(rt.anchorMax),
            pivot = FormatVector2(rt.pivot)
        } : null,
        uiComponents = uiTypes,
        children
    };
}
```

---

#### 3. 전체 명령 목록

##### 3.1 UI 편의 생성 명령

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.create_canvas` | `ui.create_canvas [name]` | Canvas + RectTransform GO 생성. Canvas는 자동으로 RectTransform을 추가함. |
| `ui.create_text` | `ui.create_text <parentId> [text] [fontSize]` | 부모 하위에 UIText GO 생성 (RectTransform 자동). |
| `ui.create_image` | `ui.create_image <parentId>` | 부모 하위에 UIImage GO 생성 (RectTransform 자동). |
| `ui.create_panel` | `ui.create_panel <parentId> [r,g,b,a]` | 부모 하위에 UIPanel GO 생성. 색상 지정 가능. |
| `ui.create_button` | `ui.create_button <parentId> [label]` | 부모 하위에 UIButton + UIImage + 자식(UIText) GO 생성. |
| `ui.create_toggle` | `ui.create_toggle <parentId>` | 부모 하위에 UIToggle GO 생성. |
| `ui.create_slider` | `ui.create_slider <parentId>` | 부모 하위에 UISlider GO 생성. |
| `ui.create_input` | `ui.create_input <parentId> [placeholder]` | 부모 하위에 UIInputField GO 생성. |
| `ui.create_layout` | `ui.create_layout <parentId> [Horizontal\|Vertical]` | 부모 하위에 UILayoutGroup GO 생성. |
| `ui.create_scroll` | `ui.create_scroll <parentId>` | 부모 하위에 UIScrollView GO 생성. Content 자식도 함께 생성. |

##### 3.2 UI 트리/조회 명령

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.tree` | `ui.tree [canvasId]` | 모든 Canvas(또는 특정 Canvas)의 UI 계층 트리 조회. 각 노드에 RectTransform 정보와 UI 컴포넌트 타입 표시. |
| `ui.list` | `ui.list [canvasId]` | 모든 Canvas(또는 특정 Canvas) 하위의 UI 요소 flat 목록 조회. |
| `ui.find` | `ui.find <name>` | UI 컴포넌트(IUIRenderable)가 있는 GO 중 이름으로 검색. |
| `ui.canvas.list` | `ui.canvas.list` | 씬 내 모든 Canvas 목록 (id, name, renderMode, sortingOrder). |

##### 3.3 RectTransform 명령 (`ui.rect.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.rect.get` | `ui.rect.get <goId>` | RectTransform 전체 정보 조회 (anchorMin, anchorMax, anchoredPosition, sizeDelta, pivot, offsetMin, offsetMax, rect). |
| `ui.rect.set_anchors` | `ui.rect.set_anchors <goId> <minX,minY> <maxX,maxY>` | anchorMin, anchorMax 설정. |
| `ui.rect.set_position` | `ui.rect.set_position <goId> <x,y>` | anchoredPosition 설정. |
| `ui.rect.set_size` | `ui.rect.set_size <goId> <w,h>` | sizeDelta 설정. |
| `ui.rect.set_pivot` | `ui.rect.set_pivot <goId> <x,y>` | pivot 설정. |
| `ui.rect.set_offsets` | `ui.rect.set_offsets <goId> <minX,minY> <maxX,maxY>` | offsetMin, offsetMax 설정 (stretch 모드에서 유용). |
| `ui.rect.set_preset` | `ui.rect.set_preset <goId> <preset> [keepVisual]` | AnchorPreset 적용. keepVisual=true면 시각적 위치 유지. |
| `ui.rect.get_world_rect` | `ui.rect.get_world_rect <goId>` | 마지막 렌더링된 스크린 좌표 Rect 조회 (lastScreenRect). |

**AnchorPreset 이름 목록**: `TopLeft`, `TopCenter`, `TopRight`, `MiddleLeft`, `MiddleCenter`, `MiddleRight`, `BottomLeft`, `BottomCenter`, `BottomRight`, `TopStretch`, `MiddleStretch`, `BottomStretch`, `StretchLeft`, `StretchCenter`, `StretchRight`, `StretchAll`

##### 3.4 Canvas 명령 (`ui.canvas.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.canvas.info` | `ui.canvas.info <goId>` | Canvas 컴포넌트 상세 정보 조회. |
| `ui.canvas.set_render_mode` | `ui.canvas.set_render_mode <goId> <mode>` | renderMode 변경 (ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace). |
| `ui.canvas.set_sorting_order` | `ui.canvas.set_sorting_order <goId> <order>` | sortingOrder 변경. |
| `ui.canvas.set_reference_resolution` | `ui.canvas.set_reference_resolution <goId> <w,h>` | referenceResolution 변경. |
| `ui.canvas.set_scale_mode` | `ui.canvas.set_scale_mode <goId> <mode>` | scaleMode 변경 (ConstantPixelSize, ScaleWithScreenSize). |
| `ui.canvas.set_match` | `ui.canvas.set_match <goId> <value>` | matchWidthOrHeight 변경 (0~1). |

##### 3.5 UIText 명령 (`ui.text.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.text.info` | `ui.text.info <goId>` | UIText 컴포넌트 정보 조회. |
| `ui.text.set_text` | `ui.text.set_text <goId> <text>` | 텍스트 내용 변경. |
| `ui.text.set_font_size` | `ui.text.set_font_size <goId> <size>` | fontSize 변경. |
| `ui.text.set_color` | `ui.text.set_color <goId> <r,g,b,a>` | 텍스트 색상 변경. |
| `ui.text.set_alignment` | `ui.text.set_alignment <goId> <alignment>` | alignment 변경 (UpperLeft, MiddleCenter 등). |
| `ui.text.set_overflow` | `ui.text.set_overflow <goId> <overflow>` | overflow 변경 (Wrap, Overflow, Ellipsis). |

##### 3.6 UIImage 명령 (`ui.image.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.image.info` | `ui.image.info <goId>` | UIImage 컴포넌트 정보 조회. |
| `ui.image.set_color` | `ui.image.set_color <goId> <r,g,b,a>` | 이미지 틴트 색상 변경. |
| `ui.image.set_type` | `ui.image.set_type <goId> <type>` | imageType 변경 (Simple, Sliced, Tiled, Filled). |
| `ui.image.set_sprite` | `ui.image.set_sprite <goId> <spriteGuid\|spritePath>` | 스프라이트 변경. |
| `ui.image.set_preserve_aspect` | `ui.image.set_preserve_aspect <goId> <true\|false>` | preserveAspect 변경. |

##### 3.7 UIPanel 명령 (`ui.panel.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.panel.info` | `ui.panel.info <goId>` | UIPanel 컴포넌트 정보 조회. |
| `ui.panel.set_color` | `ui.panel.set_color <goId> <r,g,b,a>` | 패널 배경 색상 변경. |
| `ui.panel.set_sprite` | `ui.panel.set_sprite <goId> <spriteGuid\|spritePath>` | 패널 배경 스프라이트 변경. |
| `ui.panel.set_type` | `ui.panel.set_type <goId> <type>` | imageType 변경 (Simple, Sliced). |

##### 3.8 UIButton 명령 (`ui.button.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.button.info` | `ui.button.info <goId>` | UIButton 컴포넌트 정보 조회. |
| `ui.button.set_interactable` | `ui.button.set_interactable <goId> <true\|false>` | interactable 변경. |
| `ui.button.set_colors` | `ui.button.set_colors <goId> <normal> <hover> <pressed> <disabled>` | 4가지 상태 색상 일괄 설정. 각 색상은 `r,g,b,a` 형식. |
| `ui.button.set_transition` | `ui.button.set_transition <goId> <transition>` | transition 변경 (ColorTint, SpriteSwap). |

##### 3.9 UIToggle 명령 (`ui.toggle.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.toggle.info` | `ui.toggle.info <goId>` | UIToggle 컴포넌트 정보 조회. |
| `ui.toggle.set_on` | `ui.toggle.set_on <goId> <true\|false>` | isOn 변경. |
| `ui.toggle.set_interactable` | `ui.toggle.set_interactable <goId> <true\|false>` | interactable 변경. |
| `ui.toggle.set_colors` | `ui.toggle.set_colors <goId> <bgColor> <checkColor>` | 배경/체크마크 색상 설정. |

##### 3.10 UISlider 명령 (`ui.slider.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.slider.info` | `ui.slider.info <goId>` | UISlider 컴포넌트 정보 조회. |
| `ui.slider.set_value` | `ui.slider.set_value <goId> <value>` | value 변경. |
| `ui.slider.set_range` | `ui.slider.set_range <goId> <min> <max>` | minValue, maxValue 설정. |
| `ui.slider.set_direction` | `ui.slider.set_direction <goId> <direction>` | direction 변경 (LeftToRight, RightToLeft, BottomToTop, TopToBottom). |
| `ui.slider.set_whole_numbers` | `ui.slider.set_whole_numbers <goId> <true\|false>` | wholeNumbers 변경. |
| `ui.slider.set_interactable` | `ui.slider.set_interactable <goId> <true\|false>` | interactable 변경. |
| `ui.slider.set_colors` | `ui.slider.set_colors <goId> <bgColor> <fillColor> <handleColor>` | 3가지 색상 일괄 설정. |

##### 3.11 UIInputField 명령 (`ui.input.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.input.info` | `ui.input.info <goId>` | UIInputField 컴포넌트 정보 조회. |
| `ui.input.set_text` | `ui.input.set_text <goId> <text>` | text 변경. |
| `ui.input.set_placeholder` | `ui.input.set_placeholder <goId> <text>` | placeholder 변경. |
| `ui.input.set_font_size` | `ui.input.set_font_size <goId> <size>` | fontSize 변경. |
| `ui.input.set_max_length` | `ui.input.set_max_length <goId> <length>` | maxLength 변경. |
| `ui.input.set_content_type` | `ui.input.set_content_type <goId> <type>` | contentType 변경 (Standard, IntegerNumber, DecimalNumber, Alphanumeric, Password). |
| `ui.input.set_interactable` | `ui.input.set_interactable <goId> <true\|false>` | interactable 변경. |
| `ui.input.set_read_only` | `ui.input.set_read_only <goId> <true\|false>` | readOnly 변경. |

##### 3.12 UILayoutGroup 명령 (`ui.layout.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.layout.info` | `ui.layout.info <goId>` | UILayoutGroup 컴포넌트 정보 조회. |
| `ui.layout.set_direction` | `ui.layout.set_direction <goId> <Horizontal\|Vertical>` | direction 변경. |
| `ui.layout.set_spacing` | `ui.layout.set_spacing <goId> <value>` | spacing 변경. |
| `ui.layout.set_padding` | `ui.layout.set_padding <goId> <left,bottom,right,top>` | padding 변경 (Vector4 형식). |
| `ui.layout.set_child_alignment` | `ui.layout.set_child_alignment <goId> <alignment>` | childAlignment 변경. |
| `ui.layout.set_force_expand` | `ui.layout.set_force_expand <goId> <width:true\|false> <height:true\|false>` | childForceExpandWidth, childForceExpandHeight 설정. |

##### 3.13 UIScrollView 명령 (`ui.scroll.*`)

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.scroll.info` | `ui.scroll.info <goId>` | UIScrollView 컴포넌트 정보 조회. |
| `ui.scroll.set_scroll_position` | `ui.scroll.set_scroll_position <goId> <x,y>` | scrollPosition 변경. |
| `ui.scroll.set_content_size` | `ui.scroll.set_content_size <goId> <w,h>` | contentSize 변경. |
| `ui.scroll.set_direction` | `ui.scroll.set_direction <goId> <horizontal:true\|false> <vertical:true\|false>` | horizontal, vertical 설정. |
| `ui.scroll.set_sensitivity` | `ui.scroll.set_sensitivity <goId> <value>` | scrollSensitivity 변경. |

##### 3.14 UI 디버깅 명령

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.debug.rects` | `ui.debug.rects <true\|false>` | CanvasRenderer.DebugDrawRects 토글. 모든 RectTransform의 아웃라인을 게임 뷰에 표시. |
| `ui.debug.overlap` | `ui.debug.overlap [canvasId]` | 겹치는 UI 요소 쌍을 검출하여 목록 반환. lastScreenRect를 비교하여 AABB 겹침 판정. |
| `ui.debug.hit_test` | `ui.debug.hit_test <screenX> <screenY>` | 지정 스크린 좌표에서 hit test 수행. 최상위 UI GO 반환. |

##### 3.15 UI 정렬/분배 명령

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.align` | `ui.align <edge> <goId1> <goId2> [goId3...]` | 여러 UI 요소를 지정 방향으로 정렬. edge = left, right, top, bottom, center_h, center_v. anchoredPosition 조정. |
| `ui.distribute` | `ui.distribute <axis> <goId1> <goId2> [goId3...]` | 여러 UI 요소를 균등 분배. axis = horizontal, vertical. 첫 번째와 마지막 요소 사이를 균등 배분. |

##### 3.16 UI 테마/일괄 적용 명령

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.theme.apply_color` | `ui.theme.apply_color <canvasId> <componentType> <field> <r,g,b,a>` | Canvas 하위의 모든 지정 컴포넌트 타입의 지정 색상 필드를 일괄 변경. 예: `ui.theme.apply_color 42 UIText color 1,1,1,1`. |
| `ui.theme.apply_font_size` | `ui.theme.apply_font_size <canvasId> <size>` | Canvas 하위의 모든 UIText의 fontSize를 일괄 변경. |

##### 3.17 UI 프리팹화 명령

| 명령 | 시그니처 | 설명 |
|------|----------|------|
| `ui.prefab.save` | `ui.prefab.save <goId> <path>` | UI GO 서브트리를 프리팹으로 저장. 기존 `prefab.save`와 동일하지만 UI 컨텍스트에서 편의 제공. |
| `ui.prefab.instantiate` | `ui.prefab.instantiate <guid> <parentId>` | UI 프리팹을 특정 Canvas/부모 하위에 인스턴스화. 일반 `prefab.instantiate`와 달리 부모를 즉시 설정. |

---

#### 4. 각 명령의 상세 응답 포맷

##### `ui.create_canvas`

```
ui.create_canvas [name]
```
- `name` 생략 시 "Canvas"

응답:
```json
{ "id": 42, "name": "Canvas" }
```

구현:
```csharp
_handlers["ui.create_canvas"] = args =>
{
    var name = args.Length > 0 ? args[0] : "Canvas";
    return ExecuteOnMainThread(() =>
    {
        var go = new GameObject(name);
        go.AddComponent<Canvas>();
        // Canvas.OnAddedToGameObject()에서 RectTransform 자동 추가됨
        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { id = go.GetInstanceID(), name = go.name });
    });
};
```

##### `ui.create_text`

```
ui.create_text <parentId> [text] [fontSize]
```

응답:
```json
{ "id": 43, "name": "Text" }
```

구현:
```csharp
_handlers["ui.create_text"] = args =>
{
    if (args.Length < 1)
        return JsonError("Usage: ui.create_text <parentId> [text] [fontSize]");

    if (!int.TryParse(args[0], out var parentId))
        return JsonError($"Invalid parent ID: {args[0]}");

    return ExecuteOnMainThread(() =>
    {
        var parent = FindGameObjectById(parentId);
        if (parent == null)
            return JsonError($"Parent not found: {parentId}");

        var go = new GameObject("Text");
        go.transform.SetParent(parent.transform);
        go.AddComponent<RectTransform>();
        var text = go.AddComponent<UIText>();
        if (args.Length > 1) text.text = args[1];
        if (args.Length > 2 && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fs))
            text.fontSize = fs;

        SceneManager.GetActiveScene().isDirty = true;
        return JsonOk(new { id = go.GetInstanceID(), name = go.name });
    });
};
```

##### `ui.create_button` (복합 생성)

```
ui.create_button <parentId> [label]
```

구현 -- Button GO 구조:
```
Button (UIButton + UIImage + RectTransform)
  └─ Label (UIText + RectTransform, StretchAll)
```

응답:
```json
{ "id": 44, "name": "Button", "labelId": 45 }
```

##### `ui.create_scroll` (복합 생성)

```
ui.create_scroll <parentId>
```

구현 -- ScrollView GO 구조:
```
ScrollView (UIScrollView + RectTransform)
  └─ Content (RectTransform)
```

응답:
```json
{ "id": 46, "name": "ScrollView", "contentId": 47 }
```

##### `ui.tree`

```
ui.tree [canvasId]
```

canvasId 미지정 시 모든 Canvas의 트리를 반환.

응답:
```json
{
  "canvases": [
    {
      "id": 42,
      "name": "Canvas",
      "renderMode": "ScreenSpaceOverlay",
      "sortingOrder": 0,
      "tree": {
        "id": 42,
        "name": "Canvas",
        "active": true,
        "rect": {
          "anchoredPosition": "0, 0",
          "sizeDelta": "0, 0",
          "anchorMin": "0, 0",
          "anchorMax": "1, 1",
          "pivot": "0.5, 0.5"
        },
        "uiComponents": ["Canvas"],
        "children": [
          {
            "id": 43,
            "name": "Text",
            "active": true,
            "rect": { "..." },
            "uiComponents": ["UIText"],
            "children": []
          }
        ]
      }
    }
  ]
}
```

##### `ui.rect.get`

```
ui.rect.get <goId>
```

응답:
```json
{
  "anchorMin": "0, 0",
  "anchorMax": "1, 1",
  "anchoredPosition": "0, 0",
  "sizeDelta": "0, 0",
  "pivot": "0.5, 0.5",
  "offsetMin": "-960, -540",
  "offsetMax": "960, 540",
  "rect": { "x": -960, "y": -540, "width": 1920, "height": 1080 },
  "lastScreenRect": { "x": 100, "y": 50, "width": 800, "height": 600 }
}
```

##### `ui.rect.set_preset`

```
ui.rect.set_preset <goId> <preset> [keepVisual]
```
- `preset`: AnchorPreset enum 이름 (예: `MiddleCenter`, `StretchAll`)
- `keepVisual`: `true`면 SetAnchorPresetKeepVisual() 호출 (기본값 `false`)

응답:
```json
{ "ok": true }
```

##### `ui.canvas.info`

```
ui.canvas.info <goId>
```

응답:
```json
{
  "renderMode": "ScreenSpaceOverlay",
  "sortingOrder": 0,
  "referenceResolution": "1920, 1080",
  "scaleMode": "ScaleWithScreenSize",
  "matchWidthOrHeight": 0.5
}
```

##### `ui.text.info`

```
ui.text.info <goId>
```

응답:
```json
{
  "text": "Hello World",
  "fontSize": 16,
  "color": "1, 1, 1, 1",
  "alignment": "UpperLeft",
  "overflow": "Overflow",
  "hasFont": true
}
```

##### `ui.button.set_colors`

```
ui.button.set_colors <goId> <normal> <hover> <pressed> <disabled>
```
예: `ui.button.set_colors 44 1,1,1,1 0.9,0.9,0.9,1 0.7,0.7,0.7,1 0.5,0.5,0.5,0.5`

응답:
```json
{ "ok": true }
```

##### `ui.debug.overlap`

```
ui.debug.overlap [canvasId]
```

응답:
```json
{
  "overlaps": [
    {
      "a": { "id": 43, "name": "Text" },
      "b": { "id": 44, "name": "Button" },
      "intersection": { "x": 100, "y": 200, "width": 50, "height": 30 }
    }
  ],
  "count": 1
}
```

구현: Canvas 하위 모든 RectTransform을 순회하며 `lastScreenRect`의 AABB 겹침을 검사. O(n^2)이지만 UI 요소는 보통 수백 개 이내이므로 문제 없음.

##### `ui.align`

```
ui.align <edge> <goId1> <goId2> [goId3...]
```
- `edge`: `left`, `right`, `top`, `bottom`, `center_h`, `center_v`
- 최소 2개의 GO ID 필요

응답:
```json
{ "ok": true, "aligned": 3 }
```

구현: 첫 번째 GO의 해당 edge 좌표를 기준으로 나머지 GO의 anchoredPosition을 조정.
- `left`: 모든 GO의 anchoredPosition.x를 첫 번째 GO의 x에 맞춤 (sizeDelta 고려)
- `center_h`: 모든 GO의 anchoredPosition.x를 첫 번째 GO의 중심 x에 맞춤
- 등

##### `ui.distribute`

```
ui.distribute <axis> <goId1> <goId2> [goId3...]
```
- `axis`: `horizontal`, `vertical`
- 최소 3개의 GO ID 필요 (2개면 이미 양 끝이라 분배 의미 없음)

응답:
```json
{ "ok": true, "distributed": 4 }
```

구현: 지정 축을 기준으로 첫 번째와 마지막 GO의 anchoredPosition 사이를 균등 분배.

##### `ui.theme.apply_color`

```
ui.theme.apply_color <canvasId> <componentType> <field> <r,g,b,a>
```
- `componentType`: `UIText`, `UIImage`, `UIPanel`, `UIButton` 등
- `field`: `color`, `textColor`, `backgroundColor`, `normalColor` 등

응답:
```json
{ "ok": true, "affected": 5 }
```

구현: Canvas GO 하위를 재귀 순회하며 지정 컴포넌트 타입을 가진 GO를 찾아 리플렉션으로 Color 필드 설정.

##### `ui.prefab.instantiate`

```
ui.prefab.instantiate <guid> <parentId>
```

응답:
```json
{ "id": 50, "name": "ButtonPrefab" }
```

구현: 기존 `prefab.instantiate`를 호출한 뒤 부모를 즉시 설정.

---

#### 5. C# 서버 측 구현 구조

##### 새 파일: `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs`

```csharp
// CliCommandDispatcher.UI.cs -- UI CLI 명령 핸들러
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public partial class CliCommandDispatcher
    {
        private void RegisterUIHandlers()
        {
            // === 편의 생성 ===
            // ui.create_canvas, ui.create_text, ui.create_image, ui.create_panel,
            // ui.create_button, ui.create_toggle, ui.create_slider,
            // ui.create_input, ui.create_layout, ui.create_scroll

            // === 트리/조회 ===
            // ui.tree, ui.list, ui.find, ui.canvas.list

            // === RectTransform ===
            // ui.rect.get, ui.rect.set_anchors, ui.rect.set_position, ui.rect.set_size,
            // ui.rect.set_pivot, ui.rect.set_offsets, ui.rect.set_preset, ui.rect.get_world_rect

            // === Canvas ===
            // ui.canvas.info, ui.canvas.set_render_mode, ui.canvas.set_sorting_order,
            // ui.canvas.set_reference_resolution, ui.canvas.set_scale_mode, ui.canvas.set_match

            // === 컴포넌트별 ===
            // ui.text.*, ui.image.*, ui.panel.*, ui.button.*, ui.toggle.*,
            // ui.slider.*, ui.input.*, ui.layout.*, ui.scroll.*

            // === 디버깅 ===
            // ui.debug.rects, ui.debug.overlap, ui.debug.hit_test

            // === 정렬/분배 ===
            // ui.align, ui.distribute

            // === 테마 ===
            // ui.theme.apply_color, ui.theme.apply_font_size

            // === 프리팹 ===
            // ui.prefab.save, ui.prefab.instantiate
        }

        // === 헬퍼 ===
        // ParseVector2, FormatVector2, ParseVector4, FormatVector4,
        // FindParentCanvas, BuildUITreeNode, CollectUIElements
    }
}
```

##### 기존 파일 수정: `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

1. 클래스 선언을 `public partial class CliCommandDispatcher`로 변경
2. `RegisterHandlers()` 끝에 `RegisterUIHandlers()` 호출 추가
3. 기존 헬퍼 메서드(`JsonOk`, `JsonError`, `ExecuteOnMainThread`, `FindGameObject` 등)의 접근 제한자를 `private` 유지 (partial class이므로 같은 클래스에서 접근 가능)

---

#### 6. Python CLI 측 수정 사항

**수정 불필요.** Python 클라이언트는 명령을 해석하지 않고 그대로 릴레이하므로, 새 명령 추가 시 Python 코드 변경이 필요 없다.

다만, CLI 스킬 문서(레퍼런스)를 업데이트해야 한다:
- `.claude/skills/rose-cli/references/command-reference.md`에 UI 카테고리 추가
- `.claude/skills/rose-cli/SKILL.md`의 자주 쓰는 워크플로우에 UI 관련 예시 추가

---

### 영향 범위

| 파일 | 변경 유형 |
|------|-----------|
| `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` | `class` → `partial class` 변경, `RegisterUIHandlers()` 호출 추가 |
| `src/IronRose.Engine/Cli/CliCommandDispatcher.UI.cs` | **신규 파일** -- 모든 UI 핸들러 구현 |
| `.claude/skills/rose-cli/references/command-reference.md` | UI 카테고리 레퍼런스 추가 |
| `.claude/skills/rose-cli/SKILL.md` | UI 워크플로우 예시 추가 |
| `tools/ironrose-cli/ironrose_cli.py` | 변경 없음 |

### 기존 기능에 미치는 영향

- `partial class` 변환은 기존 동작에 영향 없음 (C# 컴파일러가 동일하게 처리)
- 기존 명령의 핸들러 코드는 일체 수정하지 않음
- 새 명령은 모두 `ui.` 접두사를 사용하므로 기존 명령과 충돌 없음

---

## 구현 단계

### Phase 1: 인프라 + 편의 생성 (핵심)

- [ ] `CliCommandDispatcher.cs`를 `partial class`로 변경
- [ ] `CliCommandDispatcher.UI.cs` 파일 생성, 헬퍼 메서드 구현 (ParseVector2, FormatVector2, ParseVector4, FormatVector4, FindParentCanvas, BuildUITreeNode)
- [ ] `ui.create_canvas`, `ui.create_text`, `ui.create_image`, `ui.create_panel` 구현
- [ ] `ui.create_button` (복합 생성), `ui.create_toggle`, `ui.create_slider` 구현
- [ ] `ui.create_input`, `ui.create_layout`, `ui.create_scroll` (복합 생성) 구현
- [ ] 빌드 확인 (`dotnet build`)

### Phase 2: RectTransform + Canvas 명령

- [ ] `ui.rect.get`, `ui.rect.set_anchors`, `ui.rect.set_position`, `ui.rect.set_size` 구현
- [ ] `ui.rect.set_pivot`, `ui.rect.set_offsets`, `ui.rect.set_preset`, `ui.rect.get_world_rect` 구현
- [ ] `ui.canvas.info`, `ui.canvas.set_render_mode`, `ui.canvas.set_sorting_order` 구현
- [ ] `ui.canvas.set_reference_resolution`, `ui.canvas.set_scale_mode`, `ui.canvas.set_match` 구현
- [ ] 빌드 확인

### Phase 3: 컴포넌트별 조회/수정 명령

- [ ] `ui.text.info`, `ui.text.set_text`, `ui.text.set_font_size`, `ui.text.set_color`, `ui.text.set_alignment`, `ui.text.set_overflow` 구현
- [ ] `ui.image.info`, `ui.image.set_color`, `ui.image.set_type`, `ui.image.set_sprite`, `ui.image.set_preserve_aspect` 구현
- [ ] `ui.panel.info`, `ui.panel.set_color`, `ui.panel.set_sprite`, `ui.panel.set_type` 구현
- [ ] `ui.button.info`, `ui.button.set_interactable`, `ui.button.set_colors`, `ui.button.set_transition` 구현
- [ ] `ui.toggle.info`, `ui.toggle.set_on`, `ui.toggle.set_interactable`, `ui.toggle.set_colors` 구현
- [ ] `ui.slider.info`, `ui.slider.set_value`, `ui.slider.set_range`, `ui.slider.set_direction`, `ui.slider.set_whole_numbers`, `ui.slider.set_interactable`, `ui.slider.set_colors` 구현
- [ ] `ui.input.info`, `ui.input.set_text`, `ui.input.set_placeholder`, `ui.input.set_font_size`, `ui.input.set_max_length`, `ui.input.set_content_type`, `ui.input.set_interactable`, `ui.input.set_read_only` 구현
- [ ] `ui.layout.info`, `ui.layout.set_direction`, `ui.layout.set_spacing`, `ui.layout.set_padding`, `ui.layout.set_child_alignment`, `ui.layout.set_force_expand` 구현
- [ ] `ui.scroll.info`, `ui.scroll.set_scroll_position`, `ui.scroll.set_content_size`, `ui.scroll.set_direction`, `ui.scroll.set_sensitivity` 구현
- [ ] 빌드 확인

### Phase 4: 트리/조회 + 디버깅 + 정렬/분배 + 테마 + 프리팹

- [ ] `ui.tree`, `ui.list`, `ui.find`, `ui.canvas.list` 구현
- [ ] `ui.debug.rects`, `ui.debug.overlap`, `ui.debug.hit_test` 구현
- [ ] `ui.align`, `ui.distribute` 구현
- [ ] `ui.theme.apply_color`, `ui.theme.apply_font_size` 구현
- [ ] `ui.prefab.save`, `ui.prefab.instantiate` 구현
- [ ] 빌드 확인

### Phase 5: 문서 업데이트

- [ ] `command-reference.md`에 UI 카테고리 전체 레퍼런스 추가
- [ ] `SKILL.md`에 UI 워크플로우 섹션 추가
- [ ] 최종 빌드 확인 및 테스트

---

## 전체 명령 수 요약

| 카테고리 | 명령 수 |
|----------|---------|
| ui (편의 생성) | 10 |
| ui (트리/조회) | 4 |
| ui.rect | 8 |
| ui.canvas | 6 |
| ui.text | 6 |
| ui.image | 5 |
| ui.panel | 4 |
| ui.button | 4 |
| ui.toggle | 4 |
| ui.slider | 7 |
| ui.input | 8 |
| ui.layout | 6 |
| ui.scroll | 5 |
| ui.debug | 3 |
| ui (정렬/분배) | 2 |
| ui.theme | 2 |
| ui.prefab | 2 |
| **합계** | **84** |

---

## 대안 검토

### 1. CliCommandDispatcher에 인라인 추가 vs. partial class 분리

- **인라인 추가**: 기존 패턴과 일관적이지만, 파일이 4,000줄 이상으로 커져 유지보수 어려움
- **partial class 분리** (채택): 기존 코드 변경 최소화하면서 파일 분리 가능. C# partial class는 컴파일 시 동일 클래스로 합쳐지므로 런타임 영향 없음

### 2. 별도 핸들러 클래스 + 인터페이스 vs. partial class

- **별도 클래스**: `ICliHandlerGroup` 인터페이스를 정의하고 카테고리별 클래스를 구현하는 방식. 더 깨끗하지만 기존 핸들러도 모두 리팩토링해야 하므로 범위가 너무 커짐
- **partial class** (채택): 기존 코드를 건드리지 않으면서 새 파일만 추가

### 3. `ui.rect.*` 대신 `rect.*` 카테고리

- `rect.*`가 더 짧지만, 기존 `transform.*`과의 관계가 불명확
- `ui.rect.*`로 UI 하위임을 명시하는 것이 의미상 더 자연스러움 (요구사항에서도 `ui.rect.*` 지정)

---

## 미결 사항

없음. 모든 설계 결정 사항이 사용자 요구사항과 기존 코드 분석을 통해 확정됨.
