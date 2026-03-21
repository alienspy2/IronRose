# SDF 폰트, 다국어 지원, Font Fallback 설계

## 배경
- 현재 IronRose의 폰트 시스템은 SixLabors.Fonts + ImageSharp를 사용한 래스터 아틀라스 방식
- ASCII 인쇄 가능 문자(95자)만 `DefaultCharset`에 포함되어 있어 한글/일본어 등 비-ASCII 문자 렌더링 불가
- 폰트 크기 변경 시 새로운 아틀라스를 생성해야 하므로 비효율적
- 글리프가 없는 문자에 대해 반각 공백으로 대체하는 것이 유일한 폴백 처리

## 목표
1. **SDF 폰트 렌더링**: 크기 독립적인 고품질 텍스트 렌더링
2. **다국어 지원 (i18n)**: 한국어, 영어, 일본어 등 유니코드 텍스트 렌더링
3. **Font Fallback**: 주 폰트에 없는 글리프를 대체 폰트 체인에서 검색

## 현재 상태

### 폰트 시스템 (`src/IronRose.Engine/RoseEngine/Font.cs`)
- `SixLabors.Fonts`로 시스템 폰트 또는 .ttf/.otf 파일 로드
- `BuildAtlas()`: ASCII 95자를 512xN 크기 RGBA 아틀라스에 래스터라이즈
- RGB를 white로 강제 설정, alpha만 유지 (런타임 color tint 용)
- `GlyphInfo` 구조체: UV, 크기, bearing, advance 저장
- 캐시: `(name, size)` 키로 Font 인스턴스 캐싱

### 텍스트 렌더링 (`src/IronRose.Engine/RoseEngine/TextRenderer.cs`)
- `Component` 상속, `font`/`text`/`color`/`alignment`/`fontSize` 속성
- `BuildTextMesh()`: 글리프별 쿼드를 생성하여 Mesh로 조합
- 글리프가 없으면 반각 공백으로 대체 (`fontSize * 0.5f / ppu`)
- 스프라이트 파이프라인 재사용 (`_spritePipeline`)하여 Forward Pass에서 렌더링

### 폰트 임포터 (`src/IronRose.Engine/AssetPipeline/FontImporter.cs`)
- .ttf/.otf 파일을 `Font.CreateFromFile()`로 로드
- `.rose` 메타데이터에서 `font_size` 읽기

### 렌더링 파이프라인
- 텍스트는 Step 6 (Forward Pass)에서 `DrawAllTexts()`를 통해 스프라이트 파이프라인으로 렌더링
- Veldrid 기반 그래픽스 API

## 설계

### 개요

3단계로 나누어 구현한다:
1. **Phase 1 - SDF 아틀라스 생성 + SDF 셰이더**: 기존 래스터 아틀라스를 SDF 아틀라스로 교체
2. **Phase 2 - 동적 글리프 로딩 + 다국어**: 고정 charset 제거, 런타임에 필요한 글리프만 동적 추가
3. **Phase 3 - Font Fallback 체인**: 다중 폰트 검색 및 멀티 아틀라스 렌더링

### 상세 설계

---

#### Phase 1: SDF 아틀라스 + 셰이더

##### 1-1. SDF 아틀라스 생성기 (`Font.cs` 수정)

기존 `BuildAtlas()`를 SDF 방식으로 교체한다.

**접근 방식**: 고해상도(예: 256px)로 글리프를 래스터라이즈한 뒤, 거리 변환(Distance Transform)을 수행하여 SDF 텍스처를 생성한다. 최종 아틀라스 글리프 셀 크기는 64x64 또는 48x48 정도로 다운샘플한다.

```
새로운 메서드: BuildSdfAtlas(SixLabors.Fonts.Font slFont)

1. 각 글리프를 고해상도(renderSize = sdfSize * upscale)로 래스터라이즈
2. alpha 채널에서 inside/outside 판별
3. 8SSEDT 또는 brute-force 거리 변환 수행
4. 거리 값을 [0, 1]로 정규화하여 단일 채널(R8) 아틀라스에 기록
5. GlyphInfo에 sdfScale 필드 추가
```

**SDF 파라미터**:
- `SDF_SIZE = 48` (아틀라스 내 글리프 셀 크기)
- `SDF_UPSCALE = 4` (래스터라이즈 배율)
- `SDF_SPREAD = 6.0f` (거리 필드 확산 범위, 픽셀 단위)

**Font.GlyphInfo 확장**:
```csharp
internal struct GlyphInfo
{
    // 기존 필드 유지
    public Vector2 uvMin;
    public Vector2 uvMax;
    public float width;
    public float height;
    public float bearingX;
    public float bearingY;
    public float advance;
    // 추가
    public float sdfScale;  // SDF 텍스처와 실제 em 크기 간 비율
}
```

##### 1-2. SDF 텍스트 셰이더

새 셰이더 파일 2개 추가:
- `Shaders/text_sdf.vert` - 기존 스프라이트 vertex 셰이더와 유사
- `Shaders/text_sdf.frag` - SDF 기반 알파 처리

```glsl
// text_sdf.frag 핵심 로직
float distance = texture(u_Texture, v_TexCoord).r;
float smoothWidth = fwidth(distance);  // 화면 공간 미분으로 자동 AA
float alpha = smoothstep(0.5 - smoothWidth, 0.5 + smoothWidth, distance);
outColor = vec4(u_Color.rgb, u_Color.a * alpha);
```

**SDF 셰이더의 장점**:
- `smoothstep`의 edge 값을 조절하면 outline, shadow, glow 효과를 셰이더만으로 구현 가능
- `fwidth()`를 사용하면 줌 레벨에 관계없이 일정한 안티앨리어싱 품질

##### 1-3. RenderSystem 파이프라인 추가

`RenderSystem.cs`에 `_textSdfPipeline`을 새로 생성한다. 기존 `_spritePipeline`과 분리하여 SDF 셰이더를 바인딩한다.

```
기존: DrawAllTexts() -> _spritePipeline (래스터 알파)
변경: DrawAllTexts() -> _textSdfPipeline (SDF 알파)
```

파이프라인 설정:
- Blend: SrcAlpha / OneMinusSrcAlpha (기존과 동일)
- DepthTest: LessEqual, DepthWrite: false (투명 객체)
- Uniform: `u_Color` (vec4), `u_SdfParams` (vec2: edge, smoothing)

---

#### Phase 2: 동적 글리프 로딩 + 다국어

##### 2-1. 동적 글리프 아틀라스 (`DynamicGlyphAtlas` 클래스 신규)

파일: `src/IronRose.Engine/RoseEngine/DynamicGlyphAtlas.cs`

```csharp
namespace RoseEngine
{
    internal class DynamicGlyphAtlas
    {
        private int atlasWidth;
        private int atlasHeight;
        private int cursorX, cursorY;
        private int rowHeight;
        private byte[] pixelData;
        private Texture2D texture;
        private bool isDirty;

        // 글리프 캐시
        private Dictionary<(int fontId, int codepoint), GlyphInfo> glyphCache;

        // 아틀라스가 가득 차면 새 페이지 추가
        private List<AtlasPage> pages;

        /// <summary>
        /// 요청된 코드포인트의 글리프가 아틀라스에 있는지 확인하고,
        /// 없으면 SDF를 생성하여 아틀라스에 삽입한다.
        /// </summary>
        public GlyphInfo EnsureGlyph(int fontId, SixLabors.Fonts.Font slFont, int codepoint);

        /// <summary>GPU 텍스처 업데이트 (프레임당 1회)</summary>
        public void FlushToGpu();
    }
}
```

**아틀라스 페이지 전략**:
- 초기 크기: 1024x1024 (R8 포맷, 약 ~400개 48px 글리프)
- 가득 차면 새 `AtlasPage` 생성 (최대 4페이지)
- `GlyphInfo`에 `pageIndex` 필드 추가

##### 2-2. Font 클래스 변경

```csharp
public class Font
{
    // 기존 정적 charset 제거
    // private const string DefaultCharset = ...;  // 삭제

    // SDF 모드 플래그
    public bool useSdf { get; private set; } = true;

    // 동적 아틀라스 참조 (SDF 모드 시)
    internal DynamicGlyphAtlas? dynamicAtlas;

    // 폰트 파일로부터 SixLabors.Fonts.Font 유지 (동적 글리프 생성용)
    internal SixLabors.Fonts.Font? sourceFont;
    internal int fontId;  // 아틀라스 내 폰트 식별자

    /// <summary>글리프 요청 (동적 로딩)</summary>
    internal GlyphInfo? GetGlyph(int codepoint)
    {
        if (dynamicAtlas == null || sourceFont == null) return null;
        return dynamicAtlas.EnsureGlyph(fontId, sourceFont, codepoint);
    }
}
```

##### 2-3. TextRenderer 변경

`BuildTextMesh()`에서 char 대신 Unicode codepoint 단위로 순회:

```csharp
// 서로게이트 페어 처리
var enumerator = StringInfo.GetTextElementEnumerator(text);
while (enumerator.MoveNext())
{
    string element = enumerator.GetTextElement();
    int codepoint = char.ConvertToUtf32(element, 0);
    var glyph = font.GetGlyph(codepoint);
    // ...
}
```

**멀티 페이지 렌더링**: 글리프가 서로 다른 아틀라스 페이지에 있을 수 있으므로, 페이지별로 draw call을 분리한다. `BuildTextMesh()`가 `List<(int pageIndex, Mesh mesh)>`를 반환하도록 변경.

##### 2-4. 초기 글리프 프리로드

자주 사용하는 문자를 사전 로딩하여 첫 프레임 히칭 방지:

```csharp
// FontImporter 또는 Font.Create 시점에 호출
void PreloadCommonGlyphs()
{
    // ASCII
    for (int cp = 0x20; cp <= 0x7E; cp++)
        dynamicAtlas.EnsureGlyph(fontId, sourceFont, cp);

    // 한국어 자주 쓰는 글자 (KS X 1001 기반 약 2,350자) - 옵션
    // 일본어 히라가나/카타카나 (약 190자) - 옵션
}
```

---

#### Phase 3: Font Fallback 체인

##### 3-1. FontFallbackChain 클래스 (신규)

파일: `src/IronRose.Engine/RoseEngine/FontFallbackChain.cs`

```csharp
namespace RoseEngine
{
    public class FontFallbackChain
    {
        private List<Font> chain = new();

        public void AddFont(Font font) => chain.Add(font);
        public void InsertFont(int index, Font font) => chain.Insert(index, font);

        /// <summary>
        /// 체인에서 해당 codepoint를 가진 첫 번째 폰트의 글리프를 반환한다.
        /// </summary>
        public (Font font, GlyphInfo glyph)? ResolveGlyph(int codepoint)
        {
            foreach (var font in chain)
            {
                var g = font.GetGlyph(codepoint);
                if (g.HasValue)
                    return (font, g.Value);
            }
            return null;  // tofu 또는 대체 문자 표시
        }
    }
}
```

##### 3-2. TextRenderer에 폴백 통합

```csharp
public class TextRenderer : Component
{
    public Font? font;
    public FontFallbackChain? fallbackChain;  // 추가

    // BuildTextMesh 내부:
    // 1. font.GetGlyph(codepoint) 시도
    // 2. 실패 시 fallbackChain?.ResolveGlyph(codepoint)
    // 3. 모두 실패 시 tofu 사각형(U+25A1) 또는 빈 공간
}
```

##### 3-3. 기본 폴백 체인 자동 구성

```csharp
// Font.CreateDefault() 확장
public static FontFallbackChain CreateDefaultChain(int size)
{
    var chain = new FontFallbackChain();

    // 1순위: 요청된 폰트
    // 2순위: Noto Sans CJK (한중일)
    // 3순위: 시스템 기본 폰트
    string[] cjkFonts = { "Noto Sans CJK KR", "Noto Sans KR", "Malgun Gothic", "NanumGothic" };
    string[] defaultFonts = { "DejaVu Sans", "Liberation Sans", "Arial" };

    foreach (var name in cjkFonts)
        if (SystemFonts.TryGet(name, out _))
            chain.AddFont(Font.Create(name, size));

    foreach (var name in defaultFonts)
        if (SystemFonts.TryGet(name, out _))
            chain.AddFont(Font.Create(name, size));

    return chain;
}
```

##### 3-4. 멀티 폰트 렌더링 처리

폴백으로 인해 한 텍스트 내에서 여러 폰트(아틀라스)가 사용될 수 있다. 렌더링 전략:

1. `BuildTextMesh()` 단계에서 글리프를 `(atlasTexture, pageIndex)` 기준으로 그룹화
2. 동일 텍스처/페이지의 글리프는 하나의 Mesh로 묶음
3. `DrawAllTexts()`에서 텍스처 바인드 단위로 draw call 발행

### 영향 범위

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/RoseEngine/Font.cs` | SDF 아틀라스 생성, 동적 글리프 로딩, sourceFont 유지 |
| `src/IronRose.Engine/RoseEngine/TextRenderer.cs` | Unicode codepoint 순회, 멀티페이지/멀티폰트 메시, 폴백 통합 |
| `src/IronRose.Engine/RoseEngine/DynamicGlyphAtlas.cs` | **신규** - 동적 SDF 글리프 아틀라스 |
| `src/IronRose.Engine/RoseEngine/FontFallbackChain.cs` | **신규** - 폴백 체인 |
| `src/IronRose.Engine/AssetPipeline/FontImporter.cs` | SDF 파라미터, 폴백 체인 메타데이터 |
| `src/IronRose.Engine/RenderSystem.cs` | `_textSdfPipeline` 추가, `DrawAllTexts()` 수정 |
| `Shaders/text_sdf.vert` | **신규** - SDF 텍스트 버텍스 셰이더 |
| `Shaders/text_sdf.frag` | **신규** - SDF 텍스트 프래그먼트 셰이더 |

### 기존 기능 영향
- 기존 래스터 방식은 `useSdf = false`로 유지 가능 (하위 호환)
- 스프라이트 렌더링에는 영향 없음 (별도 파이프라인)
- Forward Pass 순서: Sprites -> Text (기존과 동일)

## 구현 단계

### Phase 1: SDF 기반 폰트 렌더링
- [ ] SDF 거리 변환 알고리즘 구현 (8SSEDT 권장)
- [ ] `Font.BuildSdfAtlas()` 메서드 구현
- [ ] `text_sdf.vert` / `text_sdf.frag` 셰이더 작성
- [ ] `RenderSystem`에 `_textSdfPipeline` 추가
- [ ] `DrawAllTexts()`를 SDF 파이프라인으로 전환
- [ ] 기존 래스터 모드와의 전환 플래그 (`useSdf`) 구현
- [ ] SDF outline/shadow 효과 지원 (셰이더 uniform으로)

### Phase 2: 동적 글리프 로딩 + 다국어
- [ ] `DynamicGlyphAtlas` 클래스 구현
- [ ] 아틀라스 페이지 관리 (overflow 시 새 페이지)
- [ ] `Font` 클래스에서 `sourceFont` 유지 및 동적 글리프 요청 API
- [ ] `TextRenderer`에서 Unicode codepoint 기반 순회 (서로게이트 페어 처리)
- [ ] 멀티 페이지 draw call 분리
- [ ] ASCII + 한글 기본 글리프 프리로드
- [ ] `FontImporter`에 charset 메타데이터 지원 (`preload_charset: "korean"` 등)

### Phase 3: Font Fallback 체인
- [ ] `FontFallbackChain` 클래스 구현
- [ ] `TextRenderer`에 `fallbackChain` 속성 추가 및 글리프 해석 로직
- [ ] 기본 폴백 체인 자동 구성 (시스템 CJK 폰트 탐색)
- [ ] 멀티 폰트 렌더링 (텍스처별 draw call 그룹화)
- [ ] tofu 대체 문자 (U+25A1) 렌더링
- [ ] Inspector에서 폴백 체인 편집 UI

## 대안 검토

### MSDF vs SDF
- **MSDF (Multi-channel SDF)**: 날카로운 코너 보존이 우수하지만, 3채널(RGB) 사용으로 아틀라스 메모리 3배, 셰이더 복잡도 증가
- **SDF (단일 채널)**: 구현이 간단하고 메모리 효율적. 게임 엔진 텍스트에는 충분한 품질
- **결정**: Phase 1에서는 SDF로 시작하고, 필요 시 MSDF로 업그레이드할 수 있도록 셰이더를 모듈화

### 외부 라이브러리 vs 자체 구현
- **msdfgen**: C++ 네이티브 라이브러리로 고품질 SDF 생성. P/Invoke 바인딩 필요
- **자체 구현**: SixLabors로 래스터라이즈 후 거리 변환. 의존성 없음
- **결정**: 자체 구현 (기존 SixLabors 의존성 활용, 크로스 플랫폼 유지)

### 글리프 로딩 전략
- **정적 전체 로딩**: 유니코드 블록 전체를 미리 로딩. 한글만 11,172자로 아틀라스 거대화
- **동적 온디맨드**: 사용되는 글리프만 로딩. 첫 등장 시 미세 히칭 가능
- **결정**: 동적 온디맨드 + 자주 쓰는 문자 프리로드 조합

## 미결 사항
- 에디터 UI(ImGui) 텍스트와 게임 내 텍스트(TextRenderer)의 폰트 시스템을 공유할지, 완전히 분리할지
- SDF 아틀라스의 최적 글리프 셀 크기 (48px vs 64px) - 실측 품질 비교 필요
- GPU 텍스처 부분 업데이트(`UpdateTexture` 서브리전) Veldrid API 지원 확인 필요
