# Phase 5.1: 3D Space SpriteRenderer (Unity 호환)

## Context

IronRose 엔진은 현재 MeshRenderer + MeshFilter 기반의 3D 렌더링만 지원하며, 2D 스프라이트 관련 클래스가 전혀 없다. Unity에서는 SpriteRenderer가 3D 공간에서 텍스처 쿼드를 렌더링하는 핵심 컴포넌트이다. 이번 Phase 5.1에서 `Rect`, `Sprite`, `SpriteRenderer`를 추가하고, 알파 블렌딩 + Unlit 렌더링 파이프라인을 구현한다.

---

## 구현 파일 목록

### 새 파일 (4개)
| 파일 | 설명 |
|------|------|
| `src/IronRose.Engine/RoseEngine/Rect.cs` | 사각형 구조체 |
| `src/IronRose.Engine/RoseEngine/Sprite.cs` | 텍스처 + 영역 + 피벗 래핑 |
| `src/IronRose.Engine/RoseEngine/SpriteRenderer.cs` | 스프라이트 렌더링 컴포넌트 |
| `src/IronRose.Demo/SpriteDemo.cs` | 데모 씬 |

### 수정 파일 (4개)
| 파일 | 변경 내용 |
|------|-----------|
| `Shaders/fragment.glsl` | Unlit 모드 early-out 추가 (`LightCount < 0`) |
| `src/IronRose.Engine/RenderSystem.cs` | 알파 블렌드 파이프라인 + `DrawAllSprites()` |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | SpriteRenderer 정리 (Destroy/Clear) |
| `src/IronRose.Demo/TestScript.cs` | Demo 3 등록 |

### 소규모 수정 (1개)
| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/RoseEngine/Texture2D.cs` | `SetPixels(byte[])` public 메서드 추가 |

---

## 상세 구현

### 1. `Rect.cs` — 새 파일

```csharp
namespace RoseEngine
{
    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height) { ... }
        public float xMin, yMin, xMax, yMax (computed)
        public Vector2 center, size, position (computed)
        public bool Contains(Vector2 point)
    }
}
```

### 2. `Sprite.cs` — 새 파일

```csharp
namespace RoseEngine
{
    public class Sprite
    {
        public Texture2D texture { get; }
        public Rect rect { get; }
        public Vector2 pivot { get; }           // [0..1] 정규화 비율
        public float pixelsPerUnit { get; }     // 기본값 100 (Unity 동일)

        // 계산된 UV 좌표 (텍스처 크기로 정규화)
        internal Vector2 uvMin, uvMax;

        // 월드 크기
        public Vector2 bounds => new(rect.width / pixelsPerUnit, rect.height / pixelsPerUnit);

        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit = 100f);
    }
}
```

### 3. `SpriteRenderer.cs` — 새 파일

MeshRenderer와 동일한 정적 리스트 패턴 사용.

```csharp
namespace RoseEngine
{
    public class SpriteRenderer : Component
    {
        public Sprite? sprite;
        public Color color = Color.white;
        public bool flipX, flipY;
        public int sortingOrder = 0;
        public bool enabled = true;

        internal Mesh? _cachedMesh;             // 스프라이트 변경 시 재생성
        internal static readonly List<SpriteRenderer> _allSpriteRenderers = new();

        internal override void OnAddedToGameObject() => _allSpriteRenderers.Add(this);
        internal static void ClearAll() => _allSpriteRenderers.Clear();

        // sprite/flipX/flipY 변경 감지 → 쿼드 메시 재생성
        internal void EnsureMesh();

        // 피벗 기준 쿼드 생성, UV는 sprite.uvMin/uvMax에서 계산
        private static Mesh BuildSpriteMesh(Sprite sprite, bool flipX, bool flipY);
    }
}
```

**쿼드 메시 생성**: 기존 `PrimitiveGenerator.CreateQuad()`와 동일 방향(Z+ facing), 피벗 오프셋 적용, UV는 sprite rect에서 계산.

### 4. `fragment.glsl` 수정 — Unlit 모드

기존 셰이더에 5줄 추가. `LightCount = -1`이면 라이팅 스킵:

```glsl
void main()
{
    vec4 texColor = HasTexture > 0.5
        ? texture(sampler2D(MainTexture, MainSampler), frag_UV)
        : vec4(1.0);
    vec4 baseColor = Color * texColor;

    // ★ 추가: Unlit 모드 (스프라이트용)
    if (LightCount < 0)
    {
        out_Color = vec4(baseColor.rgb + Emission.rgb, baseColor.a);
        return;
    }

    // ... 기존 라이팅 코드 그대로 ...
}
```

**핵심**: 새 셰이더 파일이나 파이프라인 레이아웃 불필요. 동일 셰이더 재사용.

### 5. `RenderSystem.cs` 수정

**A. 필드 추가:**
```csharp
private Pipeline? _spritePipeline;
```

**B. `Initialize()` — 알파 블렌드 파이프라인 생성 (기존 파이프라인 생성 이후):**
- BlendState: `SrcAlpha / InvSrcAlpha` (표준 알파 블렌딩)
- DepthStencil: `test=true, write=false` (3D 오브젝트 뒤에 가려지지만 depth 기록 안함)
- Rasterizer: `CullMode=None` (양면 렌더링)
- 동일 셰이더, 동일 리소스 레이아웃 재사용

**C. `Render()` — 스프라이트 패스 추가 (기존 패스 이후):**
```
Pass 1: Opaque (기존 MeshRenderer)     ← 변경 없음
Pass 2: Wireframe (옵션)               ← 변경 없음
Pass 3: Sprites (새로 추가)            ← 알파 블렌드, unlit
```

**D. `DrawAllSprites()` 새 메서드 (~60줄):**
1. `_lightBuffer`에 `LightCount = -1` 업로드 (unlit 모드 활성화)
2. `_spritePipeline` 바인딩
3. 활성 SpriteRenderer 수집
4. 정렬: `sortingOrder ASC` → `카메라 거리 DESC` (뒤에서 앞으로)
5. 각 스프라이트: Transform → World 매트릭스, color → MaterialUniforms, sprite.texture 바인딩, DrawIndexed

**E. `Dispose()` — `_spritePipeline?.Dispose()` 추가**

### 6. `SceneManager.cs` 수정

- `ExecuteDestroy()`: `SpriteRenderer` 체크 추가 (GameObject 경로, Component 경로 모두)
  ```csharp
  if (comp is SpriteRenderer spr)
      SpriteRenderer._allSpriteRenderers.Remove(spr);
  ```
- `Clear()`: `SpriteRenderer.ClearAll()` 추가

### 7. `Texture2D.cs` 수정

`SetPixels` public 메서드 1개 추가 (데모에서 프로시저럴 텍스처 생성용):
```csharp
public void SetPixels(byte[] rgbaData)
{
    _pixelData = rgbaData;
    _isDirty = true;
}
```

### 8. `SpriteDemo.cs` — 데모 씬

프로시저럴 텍스처로 SpriteRenderer 기능 시연:
- 체커보드 스프라이트 (기본 렌더링)
- 반투명 틴트 스프라이트 (알파 블렌딩 + color tint)
- FlipX/FlipY 스프라이트 (뒤집기)
- 회전하는 별 스프라이트 (3D 공간 회전 — Y축 회전으로 3D 특성 시연)
- sortingOrder 겹침 데모 (빨강 뒤, 파랑 앞)

### 9. `TestScript.cs` 수정

```csharp
case 3:
    var go3 = new GameObject("SpriteDemo");
    go3.AddComponent<SpriteDemo>();
    Debug.Log("[Demo] >> Sprite Renderer");
    break;
```

Demo 메뉴의 `[3] (reserved)` → `[3] Sprite Renderer` 변경.

---

## 렌더링 파이프라인 아키텍처

```
BeginFrame (clear color + depth)
  │
  ├─ Pass 1: OPAQUE (MeshRenderer)
  │   BlendState: SingleOverrideBlend (불투명)
  │   DepthStencil: test=true, write=true
  │   CullMode: Back
  │   LightData: LightCount >= 0 (정상 라이팅)
  │
  ├─ Pass 2: WIREFRAME (옵션)
  │
  └─ Pass 3: SPRITES (SpriteRenderer) ★ NEW
      BlendState: SrcAlpha / InvSrcAlpha
      DepthStencil: test=true, write=false
      CullMode: None (양면)
      LightData: LightCount = -1 (Unlit)
      정렬: sortingOrder ASC → distance DESC
  │
EndFrame (submit + swap)
```

---

## 구현 순서

1. `Rect.cs` (의존성 없음)
2. `Sprite.cs` (Rect, Texture2D, Vector2)
3. `SpriteRenderer.cs` (Sprite, Mesh, Component)
4. `Texture2D.cs` 수정 (SetPixels 추가)
5. `fragment.glsl` 수정 (Unlit early-out)
6. `RenderSystem.cs` 수정 (알파 파이프라인 + DrawAllSprites)
7. `SceneManager.cs` 수정 (정리 코드)
8. `SpriteDemo.cs` (데모)
9. `TestScript.cs` 수정 (Demo 3 등록)

---

## 검증

```bash
cd src/IronRose.Demo && dotnet build
dotnet run
# 키보드 [3] 입력 → Sprite Renderer 데모 로드
```

확인 항목:
- 스프라이트가 3D 공간에서 렌더링됨
- 알파 블렌딩 (반투명 영역 정상 표시)
- 라이팅 없음 (Unlit — 원색 그대로 표시)
- flipX/flipY 정상 작동
- sortingOrder에 따른 렌더 순서
- Y축 회전 시 3D 쿼드 특성 확인
- 기존 Demo 1, 2 정상 작동 (회귀 없음)
