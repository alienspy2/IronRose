# Phase 38-A: Renderer Settings Panel

## 목표
현재 Render Settings 패널의 FSR / SSIL 섹션을 독립된 **Renderer Settings** 패널로 분리.
**에셋 파일(`.renderer`)로 저장**하여 프로젝트 전역에 적용하고, 여러 프로파일을 만들어 전환할 수 있게 한다.

---

## 핵심 설계: 에셋 기반 프로파일

### 개요
- `.renderer` 파일을 `Assets/` 하위에 저장 (TOML 포맷)
- 여러 프로파일 생성 가능 (예: Default, LowQuality, Ultra)
- Project 패널에서 더블클릭으로 활성 프로파일 전환
- Inspector 또는 전용 패널에서 파라미터 편집
- 씬에 무관하게 프로젝트 전역 적용

### 디렉토리 구조

```
Assets/
└── Settings/
    ├── Default.renderer       ← 활성 프로파일
    ├── LowQuality.renderer
    └── Ultra.renderer
```

### 파일 포맷 (`.renderer`, TOML)

```toml
# Default.renderer
[fsr]
enabled = false
scaleMode = "Quality"
customScale = 1.2
sharpness = 0.5
jitterScale = 1.0

[ssil]
enabled = true
radius = 1.5
falloffScale = 2.0
sliceCount = 3
stepsPerSlice = 3
aoIntensity = 0.5
indirectEnabled = true
indirectBoost = 0.37
saturationBoost = 2.0
```

### 활성 프로파일 참조

프로젝트 설정에 현재 활성 프로파일의 GUID를 기록:

```toml
# ProjectSettings/RendererSettings.toml (또는 씬 파일 루트)
activeRendererProfile = "guid-of-Default.renderer"
```

### 로드/저장 흐름

```
시작:  ProjectSettings → activeRendererProfile GUID → .renderer 파일 로드 → RenderSettings 반영
편집:  패널에서 값 변경 → RenderSettings(런타임) 갱신 + .renderer 파일 자동 저장
전환:  Project 패널에서 다른 .renderer 더블클릭 → 로드 → RenderSettings 교체 → activeProfile 갱신
신규:  Project 패널 우클릭 → "Create > Renderer Profile" → 기본값으로 .renderer 생성
```

---

## 패널 구조

```
Renderer Settings
├── [Dropdown] Active Profile: [Default.renderer ▼]    ← 프로파일 선택
│
├── FSR Upscaler
│   ├── [Checkbox] FSR Enabled
│   ├── [Combo]    Scale Mode  (Quality / Balanced / Performance / Ultra / Custom)
│   ├── [Slider]   Custom Scale   (1.0 – 3.0)   ← Custom 모드일 때만
│   ├── [Slider]   Sharpness      (0.0 – 1.0)
│   └── [Slider]   Jitter Scale   (0.0 – 2.0)
│
└── SSIL / AO
    ├── [Checkbox] SSIL Enabled
    ├── [Slider]   Radius          (0.1 – 5.0)
    ├── [Slider]   Falloff Scale   (0.1 – 10.0)
    ├── [Slider]   Slice Count     (1 – 8)
    ├── [Slider]   Steps/Slice     (1 – 8)
    ├── [Slider]   AO Intensity    (0.0 – 2.0)
    ├── [Checkbox] Indirect Enabled
    ├── [Slider]   Indirect Boost     (0.0 – 2.0)   ← Indirect 활성 시
    └── [Slider]   Saturation Boost   (0.0 – 5.0)   ← Indirect 활성 시
```

## 대응 RenderSettings 프로퍼티

| 프로퍼티 | 타입 | 기본값 |
|----------|------|--------|
| `fsrEnabled` | bool | false |
| `fsrScaleMode` | FsrScaleMode | Quality |
| `fsrCustomScale` | float | 1.2 |
| `fsrSharpness` | float | 0.5 |
| `fsrJitterScale` | float | 1.0 |
| `ssilEnabled` | bool | true |
| `ssilRadius` | float | 1.5 |
| `ssilFalloffScale` | float | 2.0 |
| `ssilSliceCount` | int | 3 |
| `ssilStepsPerSlice` | int | 3 |
| `ssilAoIntensity` | float | 0.5 |
| `ssilIndirectEnabled` | bool | true |
| `ssilIndirectBoost` | float | 0.37 |
| `ssilSaturationBoost` | float | 2.0 |

## 구현 작업

### 에셋 시스템
- [ ] `RendererProfile` 클래스 — 14개 프로퍼티를 담는 데이터 클래스
- [ ] `.renderer` 파일 TOML 직렬화/역직렬화
- [ ] `AssetDatabase`에 `.renderer` 확장자 임포터 등록
- [ ] Project 패널 우클릭 → "Create > Renderer Profile" 메뉴
- [ ] Project 패널 더블클릭 → 활성 프로파일 전환
- [ ] `ProjectSettings/RendererSettings.toml`에 activeRendererProfile GUID 저장/로드

### 패널
- [ ] `ImGuiRendererSettingsPanel.cs` 생성 — 프로파일 드롭다운 + FSR/SSIL 섹션
- [ ] 패널 상단에 활성 프로파일 선택 UI
- [ ] 값 변경 시 `.renderer` 파일 자동 저장 + `RenderSettings` 런타임 반영
- [ ] `ImGuiOverlay` / `ImGuiLayoutManager`에 새 패널 등록
- [ ] 기존 `ImGuiRenderSettingsPanel`에서 FSR / SSIL 섹션 제거
- [ ] 메뉴 Window → Renderer Settings 추가

### 초기화
- [ ] 엔진 시작 시 `Assets/Settings/Default.renderer` 없으면 기본값으로 자동 생성
- [ ] 활성 프로파일 로드 실패 시 기본값 폴백 + 경고 로그

---

## 스크립트 API

프로파일 시스템은 `RenderSettings` static 프로퍼티의 **영속화 백엔드**이다.
스크립트는 기존과 동일하게 `RenderSettings`에 직접 접근한다.

```
RendererProfile (.renderer 파일)     ← 디스크 저장 단위
        ↕ Load/Save
RenderSettings (static)              ← 스크립트 접근 (기존 API 유지)
        ↓ 매 프레임 참조
RenderSystem                         ← 실제 렌더링
```

### 읽기/쓰기 (기존 API 그대로)

```csharp
// 읽기
if (RenderSettings.fsrEnabled)
    Debug.Log($"FSR sharpness: {RenderSettings.fsrSharpness}");

// 쓰기 (런타임 오버라이드 — 디스크에는 저장되지 않음)
RenderSettings.ssilAoIntensity = 1.5f;
```

### 프로파일 전환 (새 API)

```csharp
// 경로로 로드
RenderSettings.LoadRendererProfile("Assets/Settings/Ultra.renderer");

// 현재 활성 프로파일 이름
string name = RenderSettings.activeRendererProfileName;  // "Ultra"
```
