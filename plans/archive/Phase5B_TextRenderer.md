# Phase 5B: 3D Space TextRenderer + Font System (IronRose 전용)

## Context

IronRose 엔진에는 텍스트 렌더링 기능이 전혀 없다. Unity에서는 TextMesh(legacy) / TextMeshPro를 사용하지만, 이 둘 모두 복잡한 시스템이다. IronRose에서는 자체 `TextRenderer` 컴포넌트를 만들어 3D 공간에 텍스트를 렌더링한다. 이를 위해 먼저 **Font 시스템**(폰트 로딩 + 글리프 아틀라스 생성)을 구축한 뒤, TextRenderer가 아틀라스 기반 per-character 쿼드 메시를 생성하는 방식을 사용한다.

**핵심 설계**: SixLabors.Fonts(ImageSharp 생태계)로 글리프를 래스터라이즈하고, 하나의 아틀라스 텍스처에 패킹한 뒤, TextRenderer가 문자별 쿼드를 생성하여 기존 Sprite 알파 블렌드 파이프라인을 재사용한다.

---

## 구현 파일 목록

### 새 파일 (4개)
| 파일 | 설명 |
|------|------|
| `src/IronRose.Engine/RoseEngine/Font.cs` | 폰트 로딩 + 글리프 아틀라스 생성 |
| `src/IronRose.Engine/RoseEngine/TextRenderer.cs` | 3D 텍스트 렌더링 컴포넌트 |
| `src/IronRose.Engine/RoseEngine/TextAlignment.cs` | 정렬 열거형 |
| `src/IronRose.Demo/TextDemo.cs` | 데모 씬 |

### 수정 파일 (3개)
| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/RenderSystem.cs` | `DrawAllTexts()` 추가 (Sprite 파이프라인 재사용) |
| `src/IronRose.Engine/RoseEngine/SceneManager.cs` | TextRenderer 정리 (Destroy/Clear) |
| `src/IronRose.Demo/TestScript.cs` | Demo 4 등록 |

### 패키지 추가 (1개)
| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/IronRose.Engine.csproj` | `SixLabors.Fonts` 패키지 추가 |

---

## 상세 구현

### 1. `IronRose.Engine.csproj` 수정 — 패키지 추가

```xml
<PackageReference Include="SixLabors.Fonts" Version="2.1.0" />
```

SixLabors.Fonts는 이미 사용 중인 SixLabors.ImageSharp와 동일 생태계. ImageSharp의 `DrawText()` 확장 메서드를 통해 글리프를 래스터라이즈한다.

### 2. `TextAlignment.cs` — 새 파일

```csharp
namespace RoseEngine
{
    public enum TextAlignment
    {
        Left,
        Center,
        Right,
    }
}
```

### 3. `Font.cs` — 새 파일

폰트 로딩 + 글리프 아틀라스를 하나의 클래스로 관리.

```csharp
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

namespace RoseEngine
{
    public class Font
    {
        public string name { get; private set; }
        public int fontSize { get; private set; }
        public float lineHeight { get; private set; }    // 월드 단위 (pixelsPerUnit 적용)

        // Atlas
        internal Texture2D? atlasTexture;
        internal Dictionary<char, GlyphInfo> glyphs = new();
        internal float pixelsPerUnit = 100f;

        // 기본 문자 셋 (ASCII printable)
        private const string DefaultCharset =
            " !\"#$%&'()*+,-./0123456789:;<=>?@" +
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
            "abcdefghijklmnopqrstuvwxyz{|}~";

        internal struct GlyphInfo
        {
            public Vector2 uvMin;       // 아틀라스 내 UV 좌상단
            public Vector2 uvMax;       // 아틀라스 내 UV 우하단
            public float width;         // 글리프 비트맵 폭 (px)
            public float height;        // 글리프 비트맵 높이 (px)
            public float bearingX;      // 베이스라인에서 좌측 오프셋 (px)
            public float bearingY;      // 베이스라인에서 상단 오프셋 (px)
            public float advance;       // 다음 문자까지 수평 이동 (px)
        }

        /// <summary>시스템 폰트에서 로드 (이름으로 검색)</summary>
        public static Font Create(string fontFamily, int size)
        {
            var font = new Font { name = fontFamily, fontSize = size };
            var slFamily = SystemFonts.Get(fontFamily);
            var slFont = slFamily.CreateFont(size, SixLabors.Fonts.FontStyle.Regular);
            font.BuildAtlas(slFont);
            return font;
        }

        /// <summary>.ttf/.otf 파일에서 로드</summary>
        public static Font CreateFromFile(string path, int size)
        {
            var font = new Font { name = Path.GetFileNameWithoutExtension(path), fontSize = size };
            var collection = new FontCollection();
            var slFamily = collection.Add(path);
            var slFont = slFamily.CreateFont(size, SixLabors.Fonts.FontStyle.Regular);
            font.BuildAtlas(slFont);
            return font;
        }

        /// <summary>시스템에서 사용 가능한 아무 폰트를 찾아 폴백</summary>
        public static Font CreateDefault(int size)
        {
            string[] fallbacks = { "DejaVu Sans", "Liberation Sans", "Arial", "Noto Sans" };
            foreach (var name in fallbacks)
            {
                if (SystemFonts.TryGet(name, out var family))
                {
                    var font = new Font { name = name, fontSize = size };
                    var slFont = family.CreateFont(size, SixLabors.Fonts.FontStyle.Regular);
                    font.BuildAtlas(slFont);
                    return font;
                }
            }
            throw new Exception("No system font found for fallback");
        }

        private void BuildAtlas(SixLabors.Fonts.Font slFont)
        {
            var options = new TextOptions(slFont);
            int padding = 2;  // 글리프 간 패딩 (bleeding 방지)

            // 1단계: 각 글리프의 크기 측정
            var measurements = new List<(char ch, FontRectangle bounds, float advance)>();
            foreach (char ch in DefaultCharset)
            {
                var bounds = TextMeasurer.MeasureBounds(ch.ToString(), options);
                var size = TextMeasurer.MeasureSize(ch.ToString(), options);
                measurements.Add((ch, bounds, size.Width));
            }

            // 2단계: 아틀라스 크기 결정 (행 패킹)
            int atlasWidth = 512;
            int rowHeight = fontSize + padding * 2;
            int cursorX = padding, cursorY = padding;
            int maxHeight = rowHeight + padding;

            // 배치 시뮬레이션으로 필요한 높이 계산
            foreach (var (ch, bounds, advance) in measurements)
            {
                int glyphW = (int)MathF.Ceiling(advance) + padding * 2;
                if (cursorX + glyphW > atlasWidth)
                {
                    cursorX = padding;
                    cursorY += rowHeight;
                    maxHeight = cursorY + rowHeight + padding;
                }
                cursorX += glyphW;
            }

            // 2의 거듭제곱으로 올림
            int atlasHeight = 1;
            while (atlasHeight < maxHeight) atlasHeight *= 2;

            // 3단계: 아틀라스 이미지에 글리프 렌더링
            using var atlas = new Image<Rgba32>(atlasWidth, atlasHeight, new Rgba32(0, 0, 0, 0));
            cursorX = padding;
            cursorY = padding;

            float baseline = slFont.FontMetrics.HorizontalMetrics.Ascender
                * slFont.Size / slFont.FontMetrics.UnitsPerEm;

            foreach (var (ch, bounds, advance) in measurements)
            {
                int glyphW = (int)MathF.Ceiling(advance) + padding * 2;
                if (cursorX + glyphW > atlasWidth)
                {
                    cursorX = padding;
                    cursorY += rowHeight;
                }

                // 흰색으로 글리프 렌더링 (런타임에 color tint 적용)
                atlas.Mutate(ctx => ctx.DrawText(
                    ch.ToString(),
                    slFont,
                    SixLabors.ImageSharp.Color.White,
                    new PointF(cursorX, cursorY)));

                // GlyphInfo 저장
                var info = new GlyphInfo
                {
                    uvMin = new Vector2(
                        (float)cursorX / atlasWidth,
                        (float)cursorY / atlasHeight),
                    uvMax = new Vector2(
                        (float)(cursorX + advance) / atlasWidth,
                        (float)(cursorY + rowHeight - padding * 2) / atlasHeight),
                    width = advance,
                    height = rowHeight - padding * 2,
                    bearingX = bounds.X,
                    bearingY = baseline,
                    advance = advance,
                };

                glyphs[ch] = info;
                cursorX += glyphW;
            }

            // 4단계: Image → byte[] → Texture2D
            byte[] pixelData = new byte[atlasWidth * atlasHeight * 4];
            atlas.CopyPixelDataTo(pixelData);

            atlasTexture = new Texture2D(atlasWidth, atlasHeight);
            atlasTexture.SetPixels(pixelData);

            // lineHeight 계산 (월드 단위)
            lineHeight = (float)rowHeight / pixelsPerUnit;
        }
    }
}
```

**핵심**:
- 모든 글리프를 **흰색**으로 렌더링 → 런타임에 MaterialUniforms.Color로 tint 적용
- 아틀라스 폭 512px 고정, 높이는 글리프 수에 따라 자동 조절 (2의 거듭제곱)
- 글리프 간 padding 2px로 텍스처 샘플링 bleeding 방지
- `pixelsPerUnit = 100f` (Sprite와 동일 비율)

### 4. `TextRenderer.cs` — 새 파일

MeshRenderer/SpriteRenderer와 동일한 정적 리스트 패턴 사용.

```csharp
namespace RoseEngine
{
    public class TextRenderer : Component
    {
        public Font? font;
        public string text = "";
        public Color color = Color.white;
        public TextAlignment alignment = TextAlignment.Left;
        public float pixelsPerUnit = 100f;
        public int sortingOrder = 0;
        public bool enabled = true;

        internal Mesh? _cachedMesh;
        private string? _cachedText;
        private Font? _cachedFont;
        private TextAlignment _cachedAlignment;
        private float _cachedPixelsPerUnit;

        internal static readonly List<TextRenderer> _allTextRenderers = new();

        internal override void OnAddedToGameObject() => _allTextRenderers.Add(this);
        internal static void ClearAll() => _allTextRenderers.Clear();

        /// <summary>text/font/alignment 변경 감지 → 메시 재생성</summary>
        internal void EnsureMesh()
        {
            if (font == null || string.IsNullOrEmpty(text))
            {
                _cachedMesh = null;
                _cachedText = null;
                return;
            }

            if (_cachedMesh != null && _cachedText == text &&
                _cachedFont == font && _cachedAlignment == alignment &&
                _cachedPixelsPerUnit == pixelsPerUnit)
                return;

            _cachedMesh = BuildTextMesh(font, text, alignment, pixelsPerUnit);
            _cachedText = text;
            _cachedFont = font;
            _cachedAlignment = alignment;
            _cachedPixelsPerUnit = pixelsPerUnit;
        }

        private static Mesh BuildTextMesh(Font font, string text, TextAlignment alignment, float ppu)
        {
            var mesh = new Mesh();
            var verts = new List<Vertex>();
            var indices = new List<uint>();

            // 행 분리
            string[] lines = text.Split('\n');

            float cursorY = 0f;

            foreach (string line in lines)
            {
                // 행 폭 계산 (정렬용)
                float lineWidth = 0f;
                foreach (char ch in line)
                {
                    if (font.glyphs.TryGetValue(ch, out var g))
                        lineWidth += g.advance / ppu;
                }

                // 정렬 오프셋
                float offsetX = alignment switch
                {
                    TextAlignment.Center => -lineWidth / 2f,
                    TextAlignment.Right => -lineWidth,
                    _ => 0f,
                };

                float cursorX = offsetX;

                foreach (char ch in line)
                {
                    if (!font.glyphs.TryGetValue(ch, out var glyph))
                    {
                        cursorX += font.fontSize * 0.5f / ppu; // 미지 문자: 반각 공백
                        continue;
                    }

                    float w = glyph.width / ppu;
                    float h = glyph.height / ppu;

                    // 베이스라인 기준 배치
                    float x0 = cursorX;
                    float x1 = cursorX + w;
                    float y0 = cursorY;                  // bottom
                    float y1 = cursorY + h;              // top

                    uint baseIndex = (uint)verts.Count;

                    // Z+ facing 쿼드 (SpriteRenderer와 동일 방향)
                    verts.Add(new Vertex(new Vector3(x0, y0, 0f), Vector3.forward, new Vector2(glyph.uvMin.x, glyph.uvMax.y)));
                    verts.Add(new Vertex(new Vector3(x1, y0, 0f), Vector3.forward, new Vector2(glyph.uvMax.x, glyph.uvMax.y)));
                    verts.Add(new Vertex(new Vector3(x1, y1, 0f), Vector3.forward, new Vector2(glyph.uvMax.x, glyph.uvMin.y)));
                    verts.Add(new Vertex(new Vector3(x0, y1, 0f), Vector3.forward, new Vector2(glyph.uvMin.x, glyph.uvMin.y)));

                    indices.Add(baseIndex);
                    indices.Add(baseIndex + 1);
                    indices.Add(baseIndex + 2);
                    indices.Add(baseIndex);
                    indices.Add(baseIndex + 2);
                    indices.Add(baseIndex + 3);

                    cursorX += glyph.advance / ppu;
                }

                cursorY -= font.lineHeight;  // 다음 행 (아래로)
            }

            mesh.vertices = verts.ToArray();
            mesh.indices = indices.ToArray();
            return mesh;
        }
    }
}
```

**메시 생성 방식**:
- 문자 하나당 쿼드 1개 (4 vertices, 6 indices)
- 모든 쿼드가 하나의 Mesh에 합쳐져 **1 draw call**로 렌더링
- Z+ facing (SpriteRenderer 쿼드와 동일 방향)
- 멀티라인 지원 (`\n` 분리)
- alignment에 따른 행별 수평 오프셋

### 5. `RenderSystem.cs` 수정

**A. `Render()` — 텍스트 패스 추가 (스프라이트 이후):**

```
Pass 1: OPAQUE (MeshRenderer)          ← 변경 없음
Pass 2: WIREFRAME (옵션)               ← 변경 없음
Pass 3: SPRITES (SpriteRenderer)       ← 변경 없음
Pass 4: TEXT (TextRenderer) ★ NEW      ← Sprite 파이프라인 재사용
```

기존 `Render()` 메서드 하단에 추가:

```csharp
// --- Text pass (alpha blend, unlit — reuses sprite pipeline) ---
if (_spritePipeline != null && TextRenderer._allTextRenderers.Count > 0)
{
    DrawAllTexts(cl, viewProj, camera);
}
```

**B. `DrawAllTexts()` 새 메서드 (~50줄):**

DrawAllSprites()와 거의 동일한 구조. 차이점:
- `TextRenderer._allTextRenderers`에서 순회
- `font.atlasTexture`를 텍스처로 바인딩
- 나머지 (unlit 모드, 알파 블렌드, 정렬) 동일

```csharp
private void DrawAllTexts(CommandList cl, Matrix4x4 viewProj, Camera camera)
{
    // Unlit mode (LightCount = -1) — 이미 DrawAllSprites에서 설정된 경우 중복이지만 안전 보장
    var unlitLightData = new LightUniforms
    {
        CameraPos = new Vector4(camera.transform.position.x, camera.transform.position.y, camera.transform.position.z, 0),
        LightCount = -1,
    };
    cl.UpdateBuffer(_lightBuffer, 0, unlitLightData);

    cl.SetPipeline(_spritePipeline);

    // 활성 TextRenderer 수집
    var active = TextRenderer._allTextRenderers
        .Where(tr => tr.enabled && tr.font?.atlasTexture != null &&
                     !string.IsNullOrEmpty(tr.text) &&
                     tr.gameObject.activeInHierarchy && !tr._isDestroyed)
        .ToList();

    if (active.Count == 0) return;

    // 정렬: sortingOrder ASC → 카메라 거리 DESC
    var camPos = camera.transform.position;
    active.Sort((a, b) =>
    {
        int orderCmp = a.sortingOrder.CompareTo(b.sortingOrder);
        if (orderCmp != 0) return orderCmp;
        float distA = (a.transform.position - camPos).sqrMagnitude;
        float distB = (b.transform.position - camPos).sqrMagnitude;
        return distB.CompareTo(distA);
    });

    foreach (var tr in active)
    {
        tr.EnsureMesh();
        if (tr._cachedMesh == null) continue;

        var mesh = tr._cachedMesh;
        mesh.UploadToGPU(_device!);
        if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

        var t = tr.transform;
        var worldMatrix = RoseEngine.Matrix4x4.TRS(t.position, t.rotation, t.localScale).ToNumerics();

        var transforms = new TransformUniforms { World = worldMatrix, ViewProjection = viewProj };
        cl.UpdateBuffer(_transformBuffer, 0, transforms);

        // 텍스처: font atlas
        TextureView? texView = null;
        float hasTexture = 0f;
        tr.font!.atlasTexture!.UploadToGPU(_device!);
        if (tr.font.atlasTexture.TextureView != null)
        {
            texView = tr.font.atlasTexture.TextureView;
            hasTexture = 1f;
        }

        var materialData = new MaterialUniforms
        {
            Color = new Vector4(tr.color.r, tr.color.g, tr.color.b, tr.color.a),
            Emission = new Vector4(0, 0, 0, 0),
            HasTexture = hasTexture,
        };
        cl.UpdateBuffer(_materialBuffer, 0, materialData);

        var perObjectSet = GetOrCreateResourceSet(texView);
        cl.SetGraphicsResourceSet(0, perObjectSet);
        cl.SetGraphicsResourceSet(1, _perFrameResourceSet);

        cl.SetVertexBuffer(0, mesh.VertexBuffer);
        cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
        cl.DrawIndexed((uint)mesh.indices.Length);
    }
}
```

**새 파이프라인 불필요** — `_spritePipeline`이 이미 알파 블렌드 + 양면 + 깊이 테스트(쓰기 없음) + Unlit을 지원한다.

### 6. `SceneManager.cs` 수정

- `ExecuteDestroy()`: TextRenderer 체크 추가 (GameObject, Component 경로 모두)
  ```csharp
  if (comp is TextRenderer txr)
      TextRenderer._allTextRenderers.Remove(txr);
  ```
- `Clear()`: `TextRenderer.ClearAll()` 추가

### 7. `TextDemo.cs` — 데모 씬

`Assets/Fonts/NotoSans_eng.ttf` 폰트 파일을 사용하여 TextRenderer 기능 시연.
화면 좌상단에 데모 정보 HUD 표시.

```csharp
public class TextDemo : MonoBehaviour
{
    public override void Start()
    {
        // 카메라 설정
        var camGo = new GameObject("Camera");
        var cam = camGo.AddComponent<Camera>();
        camGo.transform.position = new Vector3(0, 0, -5);

        // Assets/Fonts/NotoSans_eng.ttf 로드 (2가지 크기)
        var fontPath = System.IO.Path.Combine(
            System.IO.Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
        var fontLarge = Font.CreateFromFile(fontPath, 48);
        var fontSmall = Font.CreateFromFile(fontPath, 24);

        // --- HUD: 좌상단 정보 오버레이 ---
        // FOV=60, distance=5 -> halfH=2.887, halfW=5.132
        var infoGo = new GameObject("InfoText");
        var infoTr = infoGo.AddComponent<TextRenderer>();
        infoTr.font = fontSmall;
        infoTr.text = "[4] Text Renderer Demo\n"
                     + "Font: NotoSans_eng.ttf\n"
                     + "Phase 5B - TextRenderer + Font System";
        infoTr.color = new Color(0.9f, 0.9f, 0.9f, 0.8f);
        infoTr.alignment = TextAlignment.Left;
        infoTr.sortingOrder = 100;  // 항상 앞에 표시
        infoGo.transform.position = new Vector3(-4.9f, 2.7f, 0f);

        // 1. 기본 텍스트 (흰색, 좌측 정렬)
        var go1 = new GameObject("BasicText");
        var tr1 = go1.AddComponent<TextRenderer>();
        tr1.font = fontLarge;
        tr1.text = "Hello, IronRose!";
        tr1.color = Color.white;
        go1.transform.position = new Vector3(-2f, 1.5f, 0f);

        // 2. 색상 텍스트 (빨강)
        var go2 = new GameObject("ColorText");
        var tr2 = go2.AddComponent<TextRenderer>();
        tr2.font = fontLarge;
        tr2.text = "Red Text";
        tr2.color = new Color(1f, 0.2f, 0.2f, 1f);
        go2.transform.position = new Vector3(-2f, 0.5f, 0f);

        // 3. 중앙 정렬 텍스트
        var go3 = new GameObject("CenterText");
        var tr3 = go3.AddComponent<TextRenderer>();
        tr3.font = fontLarge;
        tr3.text = "Centered";
        tr3.color = Color.yellow;
        tr3.alignment = TextAlignment.Center;
        go3.transform.position = new Vector3(0f, -0.5f, 0f);

        // 4. 멀티라인 텍스트
        var go4 = new GameObject("MultilineText");
        var tr4 = go4.AddComponent<TextRenderer>();
        tr4.font = fontLarge;
        tr4.text = "Line 1\nLine 2\nLine 3";
        tr4.color = new Color(0.5f, 1f, 0.5f, 1f);
        go4.transform.position = new Vector3(-2f, -1.5f, 0f);

        // 5. 3D 회전 텍스트 (Y축 회전으로 3D 특성 시연)
        var go5 = new GameObject("RotatingText");
        var tr5 = go5.AddComponent<TextRenderer>();
        tr5.font = fontLarge;
        tr5.text = "3D Text!";
        tr5.color = new Color(0.5f, 0.8f, 1f, 1f);
        go5.transform.position = new Vector3(2f, -2.5f, 0f);
        go5.AddComponent<TextRotator>();

        Debug.Log("[TextDemo] 6 text objects created (1 info + 5 demo)");
    }
}

// 회전 헬퍼
public class TextRotator : MonoBehaviour
{
    public override void Update()
    {
        transform.rotation *= Quaternion.Euler(0, 60f * Time.deltaTime, 0);
    }
}
```

**HUD 배치 계산**: 카메라 (0,0,-5), FOV=60 → Z=0에서 가시 영역 ≈ X[-5.13, 5.13], Y[-2.89, 2.89].
좌상단 (-4.9, 2.7, 0)에 작은 폰트(24px)로 3줄 정보를 표시하고 `sortingOrder=100`으로 항상 앞에 렌더.

### 8. `TestScript.cs` 수정

```csharp
case 4:
    var go4 = new GameObject("TextDemo");
    go4.AddComponent<TextDemo>();
    Debug.Log("[Demo] >> Text Renderer");
    break;
```

Demo 메뉴의 `[4] (reserved)` → `[4] Text Renderer` 변경.

---

## 렌더링 파이프라인 아키텍처

```
BeginFrame (clear color + depth)
  |
  +- Pass 1: OPAQUE (MeshRenderer)
  |   BlendState: SingleOverrideBlend (불투명)
  |   DepthStencil: test=true, write=true
  |   CullMode: Back
  |   LightData: LightCount >= 0 (정상 라이팅)
  |
  +- Pass 2: WIREFRAME (옵션)
  |
  +- Pass 3: SPRITES (SpriteRenderer)
  |   BlendState: SrcAlpha / InvSrcAlpha
  |   DepthStencil: test=true, write=false
  |   CullMode: None (양면)
  |   LightData: LightCount = -1 (Unlit)
  |   정렬: sortingOrder ASC -> distance DESC
  |
  +- Pass 4: TEXT (TextRenderer) ★ NEW
  |   파이프라인: _spritePipeline 재사용 (동일 설정)
  |   정렬: sortingOrder ASC -> distance DESC
  |   텍스처: Font atlas (글리프 아틀라스)
  |
EndFrame (submit + swap)
```

---

## 글리프 아틀라스 구조

```
+--512px--+
|Aa Bb Cc | <- row 0 (fontSize + padding)
|Dd Ee Ff | <- row 1
|...      |
|{  }  ~  | <- 마지막 row
+---------+
  height: 2의 거듭제곱으로 올림

- 각 글리프: 흰색 래스터 (런타임에 color tint)
- padding: 글리프 간 2px (텍스처 bleeding 방지)
- 문자셋: ASCII printable (32~126, 95자)
```

---

## 의존성 관계

```
SixLabors.Fonts (NEW)
  |
Font.cs ---- SixLabors.ImageSharp (기존)
  |           Texture2D (기존)
  |
TextRenderer.cs ---- Mesh (기존)
  |                  Component (기존)
  |
RenderSystem.cs ---- _spritePipeline (기존, 재사용)
```

---

## 구현 순서

1. `IronRose.Engine.csproj` 수정 (SixLabors.Fonts 추가)
2. `TextAlignment.cs` (의존성 없음)
3. `Font.cs` (SixLabors.Fonts, ImageSharp, Texture2D)
4. `TextRenderer.cs` (Font, Mesh, Component)
5. `RenderSystem.cs` 수정 (DrawAllTexts 추가)
6. `SceneManager.cs` 수정 (정리 코드)
7. `TextDemo.cs` (데모)
8. `TestScript.cs` 수정 (Demo 4 등록)

---

## 검증

```bash
cd src/IronRose.Demo && dotnet build
dotnet run
# 키보드 [4] 입력 -> Text Renderer 데모 로드
```

확인 항목:
- `Assets/Fonts/NotoSans_eng.ttf` 로드 성공 (2가지 크기: 24px, 48px)
- 좌상단 HUD 정보 텍스트 표시 (3줄, 반투명 흰색)
- 텍스트가 3D 공간에서 렌더링됨
- 알파 블렌딩 (글리프 가장자리 안티앨리어싱)
- Unlit 렌더링 (라이팅 없음, 원색 그대로)
- TextAlignment (Left, Center, Right) 정상 작동
- 멀티라인 (`\n`) 지원
- color tint 적용 (빨강, 노랑, 초록, 파랑)
- Y축 회전 시 3D 쿼드 특성 확인
- 기존 Demo 1, 2, 3 정상 작동 (회귀 없음)

---

## 향후 확장 (이번 Phase 범위 밖)

- 한글/CJK 문자셋 지원 (동적 아틀라스 확장)
- 커닝 테이블 적용
- Bold/Italic 스타일 변형
- 텍스트 외곽선(outline) / 그림자(shadow)
- SDF(Signed Distance Field) 폰트 렌더링 (확대 시 품질 향상)
- Billboard 모드 (항상 카메라를 향하는 텍스트)
