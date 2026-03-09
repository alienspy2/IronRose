# Phase 38-B: Scene Environment Panel

## 목표
현재 Render Settings 패널의 Skybox / Ambient / Sky 섹션을 독립된 **Scene Environment** 패널로 분리.
**씬 단위로 종속**되어 `.scene` 파일에 함께 저장/로드되는 것이 핵심이다.

---

## 핵심 설계: 씬 종속 (Per-Scene)

### 현재 문제
- `RenderSettings`의 모든 프로퍼티가 **전역 static** — 씬을 전환해도 환경 설정이 유지됨
- 씬 파일(`.scene`)에는 skybox 3개 필드만 저장 — ambient, sky 색상/강도는 저장 안 됨

### 목표 동작
- Scene Environment 값은 **씬마다 독립** — 씬 A와 씬 B가 서로 다른 환경을 가짐
- `.scene` 파일에 모든 환경 설정이 포함됨
- 씬 로드 시 환경 설정이 자동 복원됨
- 씬 전환 시 이전 씬의 환경이 새 씬의 값으로 교체됨

### 씬 파일 직렬화 (TOML)

현재 `.scene` 파일에는 `[renderSettings]`에 skybox만 저장:
```toml
[renderSettings]
skyboxTextureGuid = "abc-123"
skyboxExposure = 1.0
skyboxRotation = 0.0
```

변경 후 — `[sceneEnvironment]` 섹션으로 확장:
```toml
[sceneEnvironment]
# Skybox
skyboxTextureGuid = "abc-123"
skyboxExposure = 1.0
skyboxRotation = 0.0

# Ambient
ambientIntensity = 1.0
ambientLight = [0.2, 0.2, 0.2, 1.0]

# Procedural Sky
skyZenithIntensity = 0.8
skyHorizonIntensity = 1.0
sunIntensity = 20.0
skyZenithColor = [0.15, 0.3, 0.65, 1.0]
skyHorizonColor = [0.6, 0.7, 0.85, 1.0]
```

### 씬 로드/저장 흐름

```
Save:  패널 편집 → RenderSettings(런타임) → SceneSerializer.Save() → .scene 파일
Load:  .scene 파일 → SceneSerializer.Load() → RenderSettings(런타임) 복원 → 패널에 반영
New:   새 씬 생성 시 기본값으로 초기화
```

---

## 패널 구조

```
Scene Environment
├── Skybox
│   ├── [DragDrop] Texture          (파노라마 HDR / 이미지)
│   ├── [Button]   Clear
│   ├── [Slider]   Exposure    (0.0 – 10.0)   ← Skybox 설정 시
│   └── [Slider]   Rotation    (0.0 – 360.0)  ← Skybox 설정 시
│
├── Ambient
│   ├── [Slider]   Ambient Intensity   (0.0 – 5.0)
│   └── [Color]    Ambient Color
│
└── Procedural Sky
    ├── [Slider]   Zenith Intensity    (0.0 – 5.0)
    ├── [Slider]   Horizon Intensity   (0.0 – 5.0)
    ├── [Slider]   Sun Intensity       (0.0 – 50.0)
    ├── [Color]    Zenith Color
    └── [Color]    Horizon Color
```

## 대응 RenderSettings 프로퍼티

| 프로퍼티 | 타입 | 기본값 | 씬 저장 (현재) |
|----------|------|--------|:--------------:|
| `skybox` | Material? | null | (런타임만) |
| `skyboxTextureGuid` | string? | null | O |
| `skyboxExposure` | float | 1.0 | O |
| `skyboxRotation` | float | 0.0 | O |
| `ambientLight` | Color | (0.2, 0.2, 0.2, 1) | **X — 추가 필요** |
| `ambientIntensity` | float | 1.0 | **X — 추가 필요** |
| `skyZenithColor` | Color | (0.15, 0.3, 0.65) | **X — 추가 필요** |
| `skyHorizonColor` | Color | (0.6, 0.7, 0.85) | **X — 추가 필요** |
| `skyZenithIntensity` | float | 0.8 | **X — 추가 필요** |
| `skyHorizonIntensity` | float | 1.0 | **X — 추가 필요** |
| `sunIntensity` | float | 20.0 | **X — 추가 필요** |

## 구현 작업

### 패널 분리
- [ ] `ImGuiSceneEnvironmentPanel.cs` 생성 — `DrawSkyboxSection()`, `DrawAmbientSection()`, `DrawSkySection()` 이동
- [ ] Skybox 에러 팝업 로직 함께 이동
- [ ] `ApplySkyboxTexture()` 헬퍼 함께 이동
- [ ] `ImGuiOverlay` / `ImGuiLayoutManager`에 새 패널 등록
- [ ] 기존 `ImGuiRenderSettingsPanel`에서 해당 섹션 제거
- [ ] 메뉴 Window → Scene Environment 추가

### 씬 직렬화 (핵심)
- [ ] `SceneSerializer.Save()` — `[sceneEnvironment]` 섹션에 11개 프로퍼티 모두 직렬화
- [ ] `SceneSerializer.Load()` — `[sceneEnvironment]` 읽어 `RenderSettings`에 복원 (기존 `[renderSettings]` 하위호환 유지)
- [ ] 씬 로드 시 누락된 필드는 기본값으로 폴백
- [ ] 환경 값 변경 시 `Scene.isDirty = true` 마킹

### Undo 연동
- [ ] Scene Environment 값 변경을 Undo 히스토리에 기록 (SceneSnapshot에 이미 포함됨)
