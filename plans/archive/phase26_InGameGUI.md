# Phase 26 — ImGui Canvas UI System 설계 문서

## Context
HTML/CSS 기반 UI 시스템 제거 후, Unity와 동일한 Canvas + RectTransform + Sprite 기반 UI 시스템을 ImGui DrawList 위에 구축. 유니티 uGUI 패턴을 따르되 ImGui 렌더링 파이프라인 활용.

## 새 파일 목록

### Core UI Components (`src/IronRose.Engine/RoseEngine/`)
- `RectTransform.cs` — UI 전용 Transform (anchor, pivot, sizeDelta)
- `Canvas.cs` — UI 루트 컴포넌트 (Screen Space Overlay)
- `CanvasRenderer.cs` — Canvas 렌더링 시스템 (ImGui DrawList)

### UI Elements (`src/IronRose.Engine/RoseEngine/UI/`)
- `UIImage.cs` — 이미지/스프라이트 표시 (9-slice 지원)
- `UIText.cs` — 텍스트 표시
- `UIButton.cs` — 클릭 가능 버튼 (onClick 콜백)
- `UIPanel.cs` — 배경 패널 (색상/이미지)
- `UISlider.cs` — 슬라이더
- `UIToggle.cs` — 체크박스/토글
- `UIScrollView.cs` — 스크롤 컨테이너
- `UILayoutGroup.cs` — 자동 레이아웃 (Horizontal/Vertical)

### Sprite System
- `SpriteAsset.cs` (`AssetPipeline/`) — Sprite 임포트 데이터 (slice 정보, 9-slice border)
- `SpriteImporter.cs` (`AssetPipeline/`) — Texture → Sprite 변환

### Editor
- `ImGuiSpriteEditorPanel.cs` (`Editor/ImGui/Panels/`) — Sprite Slice 에디터
- Inspector 확장: RectTransform, Canvas, UIImage 등 커스텀 에디터

---

## 1. RectTransform

Unity의 RectTransform과 동일. Transform을 상속하고 UI-specific 필드 추가.

```
파일: src/IronRose.Engine/RoseEngine/RectTransform.cs
```

### 필드
| 필드 | 타입 | 설명 |
|------|------|------|
| anchorMin | Vector2 | 좌하단 앵커 (0,0)~(1,1) |
| anchorMax | Vector2 | 우상단 앵커 (0,0)~(1,1) |
| anchoredPosition | Vector2 | 앵커 기준 오프셋 (px) |
| sizeDelta | Vector2 | 앵커 stretch 보정 크기 (px) |
| pivot | Vector2 | 피벗 포인트 (0,0)~(1,1), 기본 (0.5, 0.5) |

### 계산된 Rect
```
Rect GetWorldRect(Rect parentRect):
  anchorRect = parentRect에서 anchorMin~anchorMax 영역
  finalSize = anchorRect.size + sizeDelta
  position = anchorRect.center + anchoredPosition - pivot * finalSize
  return Rect(position, finalSize)
```

### 설계 핵심
- `Transform`을 상속하되 `localPosition`은 `anchoredPosition`에서 자동 동기화
- 부모 RectTransform의 `GetWorldRect()`를 재귀적으로 호출하여 최종 스크린 좌표 산출
- Canvas가 루트 Rect 제공 (Screen Space: viewport 크기)

---

## 2. Canvas

Unity의 Canvas와 동일. UI 트리의 루트.

```
파일: src/IronRose.Engine/RoseEngine/Canvas.cs
```

### 필드
| 필드 | 타입 | 설명 |
|------|------|------|
| renderMode | CanvasRenderMode | ScreenSpaceOverlay (Phase 1만) |
| sortingOrder | int | 캔버스 간 렌더 순서 |
| referenceResolution | Vector2 | 기준 해상도 (1920x1080) |
| scaleMode | CanvasScaleMode | ConstantPixelSize / ScaleWithScreenSize |
| matchWidthOrHeight | float | 0=width 기준, 1=height 기준 |

### CanvasRenderMode enum
```
ScreenSpaceOverlay  — 화면 위 직접 렌더 (Phase 1)
ScreenSpaceCamera   — 카메라 기준 렌더 (Phase 2)
WorldSpace          — 3D 공간에 배치 (Phase 3)
```

### Canvas Scaler 로직
```
scaleFactor 계산:
  ConstantPixelSize → 1.0
  ScaleWithScreenSize →
    logWidth = log2(screenW / refW)
    logHeight = log2(screenH / refH)
    scaleFactor = 2^(lerp(logWidth, logHeight, matchWidthOrHeight))
```

### 등록 패턴
```csharp
internal static readonly List<Canvas> _allCanvases = new();
// sortingOrder 순으로 렌더
```

---

## 3. CanvasRenderer (렌더링 시스템)

ImGui DrawList를 사용하여 Canvas 트리를 렌더링.

```
파일: src/IronRose.Engine/RoseEngine/CanvasRenderer.cs
```

### 렌더 파이프라인
1. `EngineCore.Update()` 끝에서 호출
2. 모든 Canvas를 sortingOrder 순으로 순회
3. 각 Canvas의 RectTransform 트리를 DFS 순회
4. 각 UI 컴포넌트의 `OnRenderUI(ImDrawListPtr drawList, Rect worldRect)` 호출

### Game View 통합
- 에디터 모드: `ImGuiGameViewPanel`에서 DrawList 획득 → 이미지 영역에 오프셋/스케일 적용
- 스탠드얼론: 스왑체인 크기 기준 직접 렌더

### 핵심 메서드
```
static void RenderAll(ImDrawListPtr drawList, float offsetX, float offsetY, float scaleX, float scaleY)
  foreach canvas in _allCanvases (sorted by order):
    Rect rootRect = ComputeCanvasRect(canvas, scaleX, scaleY)
    RenderNode(drawList, canvas.rectTransform, rootRect, offsetX, offsetY, scaleX, scaleY)

static void RenderNode(drawList, rectTransform, parentRect, ox, oy, sx, sy):
  Rect worldRect = rectTransform.GetWorldRect(parentRect)
  Rect screenRect = Rect(ox + worldRect.x * sx, oy + worldRect.y * sy, worldRect.w * sx, worldRect.h * sy)

  // 각 UI 컴포넌트 렌더
  foreach comp in gameObject.GetComponents<IUIRenderable>():
    comp.OnRenderUI(drawList, screenRect)

  // 자식 재귀
  foreach child in rectTransform.children:
    RenderNode(drawList, child, worldRect, ox, oy, sx, sy)
```

---

## 4. UI Components

모든 UI 컴포넌트는 `IUIRenderable` 인터페이스 구현.

```csharp
interface IUIRenderable {
    void OnRenderUI(ImDrawListPtr drawList, Rect screenRect);
    int renderOrder { get; }  // 같은 GO 내 렌더 순서
}
```

### UIImage
```
파일: src/IronRose.Engine/RoseEngine/UI/UIImage.cs

필드:
  sprite: Sprite?         — 표시할 스프라이트
  color: Color            — 틴트 색상
  imageType: ImageType    — Simple / Sliced / Tiled / Filled
  preserveAspect: bool

렌더링:
  Simple → drawList.AddImage(textureId, min, max, uvMin, uvMax, colorU32)
  Sliced → 9-slice 렌더 (아래 섹션 참조)
```

### UIText
```
파일: src/IronRose.Engine/RoseEngine/UI/UIText.cs

필드:
  text: string
  fontSize: float
  color: Color
  alignment: TextAnchor   — UpperLeft/Center/LowerRight 등
  overflow: TextOverflow   — Wrap / Overflow / Ellipsis

렌더링:
  drawList.AddText(font, fontSize * scale, position, colorU32, text)
```

### UIButton
```
파일: src/IronRose.Engine/RoseEngine/UI/UIButton.cs

필드:
  onClick: Action?
  normalColor, hoverColor, pressedColor, disabledColor: Color
  transition: ButtonTransition  — ColorTint / SpriteSwap

동작:
  마우스 위치가 screenRect 안이면 hover 상태
  ImGui.IsMouseClicked() + hover → onClick 발동
```

### UISlider / UIToggle / UIScrollView / UILayoutGroup
동일 패턴. 각각 상태 + 렌더 + 이벤트 로직.

---

## 5. Sprite System 확장

### 5-1. Texture Import 옵션에 Sprite 모드 추가

기존 `RoseMetadata`에 texture import settings 추가:

```
파일: src/IronRose.Engine/AssetPipeline/RoseMetadata.cs

새 필드:
  textureType: TextureType      — Default / Sprite / NormalMap
  spriteMode: SpriteMode        — Single / Multiple
  pixelsPerUnit: float          — 기본 100
  spritePivot: Vector2           — Single 모드 피벗
  spriteSlices: List<SpriteSlice> — Multiple 모드 슬라이스 목록
```

```csharp
public class SpriteSlice {
    public string name;       // 슬라이스 이름
    public string guid;       // 고유 GUID (sub-asset 식별자)
    public Rect rect;         // 텍스처 내 영역 (px)
    public Vector2 pivot;     // (0,0)~(1,1)
    public Vector4 border;    // 9-slice 경계 (left, bottom, right, top) px
}
```

### 5-2. Sub-Asset GUID 체계

각 Sprite slice는 독립적인 GUID를 가진 sub-asset.

```
RoseMetadata (.rose 파일) 예시:

guid = "main-texture-guid"
textureType = "Sprite"
spriteMode = "Multiple"
pixelsPerUnit = 100

[[spriteSlices]]
name = "btn_normal"
guid = "a1b2c3d4-..."
rect = [0, 0, 64, 64]
pivot = [0.5, 0.5]
border = [8, 8, 8, 8]

[[spriteSlices]]
name = "btn_hover"
guid = "e5f6g7h8-..."
rect = [64, 0, 64, 64]
pivot = [0.5, 0.5]
border = [8, 8, 8, 8]
```

### 5-3. Sprite 로딩 파이프라인

```
AssetDatabase.Load<Sprite>(path) 또는 LoadByGuid<Sprite>(guid):

1. spriteSlice의 guid로 직접 로드:
   - 모든 .rose 파일의 spriteSlices에서 guid 매칭
   - 부모 텍스처 로드 + 해당 slice의 rect/pivot/border로 Sprite 생성

2. 경로 기반 로드 (SubAsset 패턴):
   - "Assets/atlas.png#Sprite:btn_normal" → name으로 매칭
   - "Assets/atlas.png#Sprite:0" → index로 매칭

3. Single 모드:
   - 텍스처 전체를 하나의 Sprite로 생성 (guid = 텍스처 자체 guid)
```

### 5-4. 기존 Sprite 클래스 확장

```
파일: src/IronRose.Engine/RoseEngine/Sprite.cs

추가 필드:
  border: Vector4    — 9-slice 경계 (left, bottom, right, top) px
  name: string       — 슬라이스 이름
  guid: string       — sub-asset GUID

추가 메서드:
  GetInnerUVs() → (uvBorderMin, uvBorderMax) for 9-slice 내부 영역
```

---

## 6. 9-Slice Rendering

UIImage의 `imageType == Sliced` 일 때 사용.

### 9개 영역 분할
```
border = (L, B, R, T) in pixels

┌──────┬────────────────┬──────┐
│ TL   │      Top       │  TR  │
│ L×T  │   stretch-X    │  R×T │
├──────┼────────────────┼──────┤
│ Left │    Center      │ Right│
│ L×?  │  stretch-XY    │  R×? │
├──────┼────────────────┼──────┤
│ BL   │    Bottom      │  BR  │
│ L×B  │   stretch-X    │  R×B │
└──────┴────────────────┴──────┘
```

### DrawList 렌더링
각 영역을 `drawList.AddImage(texId, pMin, pMax, uvMin, uvMax, color)`로 렌더.
코너 4개(고정 크기) + 엣지 4개(한 방향 스트레치) + 센터 1개(양방향 스트레치) = 9회 AddImage.

### UV 계산
```
texW, texH = sprite.texture.width/height
uvL = border.x / texW
uvR = 1 - border.z / texW
uvB = border.y / texH
uvT = 1 - border.w / texH
```

---

## 7. Sprite Slice Editor

```
파일: src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSpriteEditorPanel.cs
```

### 기능
1. **텍스처 미리보기**: 확대/축소/팬 가능한 텍스처 뷰
2. **Slice 모드 선택**: Single / Multiple (Grid / Auto)
3. **Slice 조작**: 드래그로 Rect 생성/리사이즈, 이름 지정
4. **9-Slice Border 에디터**: 4변 경계선 드래그 (녹색 선으로 표시)
5. **Pivot 설정**: 각 슬라이스별 피벗 포인트 선택
6. **GUID 자동 생성**: 새 slice 추가 시 고유 GUID 자동 할당
7. **적용/되돌리기**: Apply → RoseMetadata 저장 + Sprite 리임포트

### 열기 방법
- Inspector에서 Sprite 텍스처 선택 → "Open Sprite Editor" 버튼
- Project 패널에서 Sprite 텍스처 더블클릭

### UI 레이아웃
```
┌─── Sprite Editor ──────────────────────────────────┐
│ [Slice Mode: v Single ▾] [Apply] [Revert]         │
├────────────────────────────────────────────────────┤
│                                                    │
│    ┌──────────────────────────┐                    │
│    │   텍스처 미리보기         │    Slice Info:     │
│    │   (줌/팬 가능)           │    Name: ____      │
│    │                          │    GUID: (readonly) │
│    │   [녹색 9-slice 선]      │    Rect: x,y,w,h   │
│    │   [파란 slice rect들]    │    Pivot: Center ▾  │
│    │                          │    Border: L B R T  │
│    └──────────────────────────┘                    │
│                                                    │
└────────────────────────────────────────────────────┘
```

---

## 8. Inspector 확장

### Texture Inspector
기존 텍스처 Inspector에 추가:
- **Texture Type** 드롭다운: Default / Sprite / NormalMap
- Sprite 선택 시: Sprite Mode (Single/Multiple), Pixels Per Unit
- "Open Sprite Editor" 버튼

### RectTransform Inspector
- Anchor Preset 시각적 선택기 (Unity의 앵커 프리셋 그리드)
- anchoredPosition, sizeDelta, pivot 필드
- 부모 기준 최종 Rect 미리보기

### UIImage Inspector
- Sprite 필드 (GUID 기반 드래그&드롭 — 개별 slice도 드롭 가능)
- Image Type 드롭다운 (Simple/Sliced/Tiled/Filled)
- Color 피커
- Preserve Aspect 체크박스

---

## 9. EngineCore 통합

```csharp
// Initialize()
InitCanvasUI();  // CanvasRenderer 초기화

// Update()
CanvasRenderer.Update(dt, mouseX, mouseY, ...);  // 이벤트 처리

// ImGuiGameViewPanel에서
CanvasRenderer.RenderAll(drawList, offsetX, offsetY, scaleX, scaleY);
```

---

## 10. Scene Serialization

### RectTransform 직렬화
```toml
[gameObjects.rectTransform]
anchorMin = [0.0, 0.0]
anchorMax = [1.0, 0.0]
anchoredPosition = [0.0, 50.0]
sizeDelta = [0.0, 100.0]
pivot = [0.5, 0.5]
parentIndex = 0
```

### UI 컴포넌트 직렬화
```toml
[[gameObjects.components]]
type = "Canvas"
[gameObjects.components.fields]
sortingOrder = 0
referenceResolution = [1920.0, 1080.0]
scaleMode = "ScaleWithScreenSize"
matchWidthOrHeight = 0.5

[[gameObjects.components]]
type = "UIImage"
[gameObjects.components.fields]
spriteGuid = "a1b2c3d4-..."
color = [1.0, 1.0, 1.0, 1.0]
imageType = "Sliced"
```

---

## 구현 순서

### Step 1: Core (RectTransform + Canvas + CanvasRenderer)
- RectTransform 클래스
- Canvas 컴포넌트
- CanvasRenderer (ImGui DrawList 렌더링)
- EngineCore 통합
- 기본 Scene 직렬화

### Step 2: UIImage + Sprite 확장
- Sprite.border, name, guid 필드 추가
- UIImage (Simple + Sliced 렌더)
- 9-slice DrawList 렌더링
- RoseMetadata에 textureType/spriteMode/spriteSlices 추가
- Sub-asset GUID 체계 구현

### Step 3: UIText + UIButton
- UIText (ImGui font 렌더)
- UIButton (이벤트 + 색상 전환)

### Step 4: Sprite Editor
- ImGuiSpriteEditorPanel
- Slice rect 생성/편집 (각 slice에 GUID 자동 할당)
- 9-slice border 드래그 편집
- RoseMetadata 저장

### Step 5: Inspector + 나머지 UI 위젯
- RectTransform 커스텀 Inspector
- Texture Inspector Sprite 모드
- UISlider, UIToggle, UIScrollView, UILayoutGroup

## 검증
- `dotnet build` 성공
- 에디터에서 Canvas + UIImage 생성 → Game View에 렌더 확인
- Sprite Editor로 9-slice 설정 → UIImage Sliced 모드 확인
- 개별 sprite slice가 고유 GUID로 참조 가능 확인
- Scene 저장/로드 후 UI 유지 확인
