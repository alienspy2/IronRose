# IronRose 프로젝트 개발 가이드라인

## 디자인 가이드라인

### 메인 테마 색상

**IronRose Theme Color**: 금속의 백장미 (Metallic White Rose)

```csharp
// RGB: (230, 220, 210) - 은은한 베이지 톤
// 정규화: (0.902, 0.863, 0.824)
// Hex: #E6DCD2

// Veldrid 사용 예시
var ironRoseColor = new RgbaFloat(0.902f, 0.863f, 0.824f, 1.0f);

// Unity 스타일 Color32
var ironRoseColor32 = new Color32(230, 220, 210, 255);
```

**색상 설명**:
- 백장미의 우아하고 은은한 흰색
- 금속의 차가운 광택감
- RGB로 표현 시 따뜻한 베이지 톤
- 배경, UI 기본 색상, 엔진 로고 등에 사용

**보조 색상** (추후 정의):
- 어둡게: 회색 톤 (금속 그림자)
- 밝게: 순백색 (하이라이트)
- 강조: 장미의 붉은색 (액센트)

---

## 에이전트 사용 규칙

사용자가 다음 키워드를 사용하면 **반드시** 해당 에이전트(Task 도구)를 사용해야 합니다:

| 키워드 | 에이전트 | 용도 |
|--------|----------|------|
| `aca-fix` | `aca-user-feedback-and-fix` | 버그 수정, 디버깅, 작은 기능 수정 |
| `aca-plan` | `aca-plan` | 세부 기능 설계, Phase 계획 문서 작성 |
| `aca-archi` | `aca-architect-csharp` | 큰 아키텍처 설계, Phase별 상세 구현 명세서 작성 |

**사용 기준**:
- **큰 계획/아키텍처 설계** → `aca-archi` (aca-architect-csharp)
- **세부 기능 계획/설계 문서** → `aca-plan` (aca-plan)
- **디버깅, 버그 수정, 작은 기능 수정** → `aca-fix` (aca-user-feedback-and-fix)

**적극적 사용 원칙**:
- 버그 수정, 디버깅, 작은 기능 수정 작업이 필요한 경우 가능한 한 `aca-fix` 에이전트를 적극적으로 사용할 것
- 사용자가 명시적으로 키워드를 언급하지 않더라도, 버그 수정/디버깅 성격의 작업이라면 `aca-fix` 사용을 우선 고려

---

## 코딩 스타일

### 크로스 플랫폼
- **파일 경로**: 항상 `Path.Combine()`을 사용. `"foo/bar"` 또는 `"foo\\bar"` 금지.
- **줄 끝**: LF 기본 (`.editorconfig`, `.gitattributes` 참조)

### 인코딩
- **C# 소스 파일(.cs)**: UTF-8 with BOM 사용
- **.editorconfig**에 명시되어 자동 적용됨

### 네이밍 컨벤션
- Unity와 유사한 C# 표준 컨벤션 사용
- 클래스/메서드: PascalCase
- 필드/변수: camelCase
- 상수: UPPER_CASE

### Inspector 편집 필드 규칙
- 모든 DragFloat/DragInt 필드는 **싱글클릭으로 텍스트 편집 진입**해야 함
- `ImGui.DragFloat` 직접 사용 금지 → `DragFloatClickable`, `DragFloat2Clickable`, `DragFloat3Clickable`, `DragIntClickable` 헬퍼 사용
- 새로운 Inspector 필드 추가 시 반드시 위 헬퍼를 사용할 것

---

## Unity와의 차이점

### 스크립트 파일 위치 제한
- Unity에서는 `Assets/` 폴더 하위에 `.cs` 스크립트 파일을 자유롭게 배치하지만, **IronRose에서는 `Assets/` 폴더에 `.cs` 파일을 추가할 수 없음**
- 모든 C# 스크립트는 반드시 **LiveCode** 또는 **FrozenCode** 프로젝트에만 추가해야 함
- `Assets/` 폴더는 텍스처, 모델, 씬, 프리팹 등 **비코드 에셋 전용**

### Prefab Override 미지원
- Unity에서는 Prefab을 씬에 배치하거나 다른 Prefab 안에 Sub Prefab으로 넣을 때 개별 속성값을 override할 수 있지만, **IronRose에서는 Prefab 인스턴스의 값 override를 지원하지 않음**
- Prefab을 씬에 배치하면 원본 Prefab의 값이 그대로 사용됨
- Sub Prefab (Nested Prefab)도 마찬가지로 원본 값 그대로 사용됨
- **값을 변경하려면 반드시 Prefab Variant를 생성**하여 Variant에서 원하는 값을 수정해야 함

---

## 개발 워크플로우

### 0. 계획 문서 작성

사용자가 "계획 문서 작성", "설계 문서 만들어" 등 계획/설계를 요청하면:

1. `docs/plans/` 디렉토리에 마크다운 문서를 생성
2. 파일명은 기능/Phase를 반영 (예: `phase41_animation_blend.md`)
3. **사용자가 문서를 검수한 후 명시적으로 "구현 시작"이라고 할 때까지 코드 구현 금지**
4. 사용자 피드백에 따라 계획 문서를 수정/보완

### 1. 단계별 구현 및 검증
매 단계 구현 후 반드시 다음 순서로 테스트:

```bash
# 1. 빌드
dotnet build

# 2. 실행 파일 테스트 — 작업 내용에 따라 적절한 프로젝트 선택

# 에디터 기능 구현 중 → 에디터 프로젝트 실행
dotnet run --project src/IronRose.RoseEditor

# Standalone 빌드/런타임 테스트 필요 시 → Standalone 프로젝트 실행
dotnet run --project src/IronRose.Standalone
```

**중요**: 코드 레벨 유닛 테스트만으로는 부족합니다. 반드시 빌드 후 실제 실행 파일을 실행하여 통합 테스트를 수행해야 합니다.
- **에디터 UI, Inspector, SceneView 등 에디터 관련 작업** → `IronRose.RoseEditor` 실행
- **게임 런타임, Standalone 빌드 관련 작업** → `IronRose.Standalone` 실행

### 2. 로깅 전략
모든 주요 동작에 대해 상세한 로그를 남겨야 합니다:

```csharp
// RoseEngine.Debug 사용 (권장 — 파일 로그 + 콘솔 + LogSink 동시 출력)
Debug.Log($"[Engine] Initializing scene: {sceneName}");
Debug.LogWarning($"[Renderer] Fallback to software rasterizer");
Debug.LogError($"[Physics] Timestep overflow: {deltaTime:F4}s");

// Debug 를 사용할 수 없는 경우에만 stdout 으로 대체
// (예: 엔진 초기화 이전, 정적 생성자, 외부 라이브러리 콜백 등)
Console.WriteLine($"[Bootstrap] Pre-engine init: {message}");
```

**로그 카테고리**:
- `[IronRose]`: 엔진 시작/종료
- `[Engine]`: 게임 오브젝트, 씬, 컴포넌트 생명주기
- `[Renderer]`: 렌더링 파이프라인, 그래픽스 API 호출
- `[Physics]`: 물리 시뮬레이션, 충돌 감지
- `[Scripting]`: 스크립트 컴파일, 핫 리로드
- `[Asset]`: 에셋 로딩, 임포팅

### 3. 진행 상황 추적

매 작업 단계 완료 후 [Progress.md](Progress.md) 파일을 업데이트해야 합니다:

**업데이트 시점**:
- Phase 완료 시
- 주요 기능 구현 완료 시
- 중요한 마일스톤 달성 시

**업데이트 내용**:
```markdown
## Phase X: [제목] ✅

**완료 날짜**: YYYY-MM-DD
**소요 시간**: X시간/일

### 완료된 작업
- [x] 작업 항목 1
- [x] 작업 항목 2

### 주요 결정 사항
- 결정 내용 및 이유

### 알려진 이슈
- 발견된 문제점 및 해결 계획
```

**다음 단계 업데이트**:
```markdown
## 다음 단계: Phase Y

**목표**: [목표 설명]

### 예정된 작업
- [ ] 작업 항목 1
- [ ] 작업 항목 2
```

**전체 진행도 체크박스 업데이트**:
```markdown
- [x] Phase X: 완료된 단계 ✅
- [ ] Phase Y: 현재 작업 중 🚧
```

### 4. 디버깅 전략

문제 발생 시 **로그 기반 디버깅**을 최우선으로 사용합니다:

```
1. 의심 지점에 Debug.Log 로그 추가
2. 빌드 후 실행 테스트 (필요시 Human-in-the-Loop로 사용자에게 확인 요청)
3. 로그 출력 확인 → 원인 분석 → 수정
4. 문제 해결 확인 후 디버깅용 로그 정리
```

**원칙**:
- 코드만 읽고 추측하여 수정하지 말 것 — 반드시 로그를 추가하고 실행하여 실제 동작을 확인
- 한 번에 너무 많은 곳을 수정하지 말 것 — 로그로 문제 범위를 좁힌 후 최소한의 수정 적용
- 실행이 필요한 경우 사용자에게 실행 결과를 요청하거나, 자동화 명령 파일을 활용

**사용자 테스트 요청 시 making_log 작성**:
- 사용자에게 실행/테스트를 요청하기 전에 반드시 `making_log/` 디렉토리에 작업 로그를 작성
- 현재 작업 내용, 진단 로그 위치, 테스트 절차, 다음 단계를 기록
- 사용자 피드백 후 해당 로그를 이어서 업데이트하며 작업을 계속할 수 있도록 함
- 파일명: `making_log/fix-{간략한-설명}.md` (예: `fix-variant-tree-disappear.md`)

**비전 정보가 필요한 경우** (렌더링 결과, UI 레이아웃, 시각적 버그 등):
- `EngineCore.ScreenCaptureEnabled = true` 로 디버그 스크린캡처를 활성화
- 프레임 1, 60, 이후 300프레임마다 자동으로 `logs/screenshot_frame{N}_{timestamp}.png` 에 저장됨
- 저장된 스크린샷을 Read 도구로 읽어 시각적 상태를 확인
- 로그만으로 원인을 파악하기 어려운 렌더링/UI 문제에 적극 활용할 것

**디버깅 시 입력이 필요한 경우**:

엔진은 JSON 기반 명령 파일(`.claude/test_commands.json`)을 통해 키 입력, 씬 로드 등을 자동화할 수 있습니다.
디버깅 중 특정 입력 시퀀스를 재현해야 할 때 이 인터페이스를 활용합니다:

```json
{
  "commands": [
    {"type": "scene.load", "scene": "Assets/Scenes/MyScene.toml"},
    {"type": "play_mode", "action": "enter"},
    {"type": "wait", "duration": 1.0},
    {"type": "input.key_press", "key": "Space"},
    {"type": "wait", "duration": 0.5},
    {"type": "screenshot", "path": ".claude/test_outputs/result.png"},
    {"type": "play_mode", "action": "stop"},
    {"type": "quit"}
  ]
}
```

- 엔진은 시작 시 `.claude/test_commands.json` 존재 여부를 확인하고, 있으면 자동 실행
- 파일이 없으면 아무 동작 없이 정상 실행됨
- 각 명령 실행 후 `[Automation]` 태그로 성공/실패 상태를 로그에 기록
- 빌드 → 명령 파일 생성 → 실행 → 로그/스크린샷 확인의 전체 디버깅 루프를 자동화 가능

**지원 명령 타입**:
| type | 필드 | 설명 |
|------|------|------|
| `scene.load` | `scene`: 씬 파일 경로 (.toml) | SceneSerializer.Load()로 씬 로드 |
| `input.key_press` | `key`: KeyCode enum 이름 (예: `Space`, `Return`, `A`, `F1`) | 다음 프레임에 KeyDown+KeyUp 시뮬레이션 |
| `wait` | `duration`: 대기 시간(초) | 지정된 시간만큼 프레임 단위로 대기 |
| `screenshot` | `path`: 저장 경로 (생략 시 `.claude/test_outputs/` 자동 생성) | 다음 렌더 프레임에 스크린샷 캡처 |
| `play_mode` | `action`: `enter` / `stop` / `pause` / `resume` (기본: `enter`) | 에디터 플레이 모드 제어 |
| `quit` | — | 엔진 종료 |

### 5. 핫 리로드 워크플로우

#### FrozenCode / LiveCode 종속성 구조
```
IronRose.Engine  ←──── FrozenCode (컴파일 타임 참조)
IronRose.Contracts ←─┘
                       ↑
                  RoseEditor ── FrozenCode (ProjectReference)
                  Standalone ── FrozenCode (ProjectReference)

IronRose.Engine  ←──── LiveCode (csproj 참조는 동일하나 실행 시 직접 참조하지 않음)
IronRose.Contracts ←─┘
                       ↑
                  LiveCodeManager ── Roslyn 런타임 컴파일 (FileSystemWatcher 감시)
```

- **FrozenCode** — `dotnet build` 시 컴파일. RoseEditor/Standalone이 `ProjectReference`로 직접 참조
- **LiveCode** — 실행 시 `LiveCodeManager`가 Roslyn으로 런타임 컴파일하여 핫 리로드. 실행 프로젝트가 직접 참조하지 않음
- 두 프로젝트 모두 동일한 종속성(`IronRose.Engine` + `IronRose.Contracts`)

#### 스크립트 핫 리로드 (Phase 2)
```
1. LiveCode/*.cs 수정
2. Roslyn 런타임 컴파일
3. 즉시 로드 및 실행
```

### 6. 스크립트 편입 (`/digest`)

엔진 실행이 종료된 후, 핫 리로드로 검증이 완료된 LiveCode 스크립트를 `/digest` 커맨드로 `FrozenCode/` 프로젝트에 편입합니다.
- LiveCode에서 테스트 완료된 `.cs` 파일을 FrozenCode 프로젝트로 이동
- LiveCode 디렉토리는 항상 실험/개발 중인 스크립트만 유지
- **중요: LiveCode ↔ FrozenCode 간 스크립트 이동은 반드시 엔진이 종료된 상태에서만 수행할 것** (실행 중 이동 시 어셈블리 불일치로 컴포넌트 참조가 깨질 수 있음)

---

## 프로젝트 디렉토리 구조

```
IronRose/
├── .claude/
│   ├── commands/                    # Claude 커스텀 커맨드 (digest.md 등)
│   └── test_outputs/                # 테스트 결과물 (스크린샷 등)
├── Assets/                          # 게임 에셋
│   └── Textures/                    # IBL 큐브맵, 텍스처 + .rose 메타데이터
├── Shaders/                         # GLSL 셰이더 (14파일)
│   ├── vertex.glsl, fragment.glsl   # Forward 렌더링
│   ├── deferred_geometry.*          # G-Buffer Geometry Pass
│   ├── deferred_lighting.*          # PBR Lighting Pass
│   ├── skybox.*                     # 스카이박스/IBL
│   ├── bloom_*.frag, gaussian_blur.frag  # Post-Processing
│   ├── tonemap*.frag               # ACES Tone Mapping
│   └── fullscreen.vert              # Fullscreen triangle
├── src/
│   ├── IronRose.Engine/             # 엔진 코어 (EXE 진입점)
│   │   ├── EngineCore.cs            # 엔진 업데이트/렌더 오케스트레이션
│   │   ├── RenderSystem.cs          # Forward/Deferred 하이브리드 렌더링
│   │   ├── RoseEngine/              # Unity 호환 API (59파일, ~5500줄)
│   │   │   ├── 수학: Vector3, Vector2, Vector4, Quaternion, Color, Matrix4x4, Mathf
│   │   │   ├── 코어: GameObject, Component, Transform, MonoBehaviour, SceneManager, Object
│   │   │   ├── 렌더링: Camera, Light, Mesh, MeshFilter, MeshRenderer, Material, Texture2D, Shader
│   │   │   ├── 입력: Input(레거시), InputSystem/(액션 기반, 7파일)
│   │   │   ├── 물리: Rigidbody, Rigidbody2D, Collider(3D+2D), Collision, ForceMode
│   │   │   ├── 2D: Sprite, SpriteRenderer, Font, TextRenderer, Rect
│   │   │   ├── IBL: Cubemap, RenderSettings
│   │   │   └── 유틸: Random, Debug, Time, Screen, Application, Resources, Attributes, Coroutine
│   │   ├── AssetPipeline/           # 에셋 임포트 (7파일)
│   │   │   ├── AssetDatabase.cs, MeshImporter.cs, TextureImporter.cs
│   │   │   ├── PrefabImporter.cs, GlbTextureExtractor.cs
│   │   │   └── RoseMetadata.cs, UnityYamlParser.cs
│   │   └── Physics/
│   │       └── PhysicsManager.cs    # PhysicsWorld3D/2D 통합
│   ├── IronRose.RoseEditor/        # 에디터 실행 파일 (에디터 기능 테스트용)
│   │   ├── FrozenCode/              # 안정된 데모 씬
│   │   └── LiveCode/                # 핫 리로드 실험 스크립트
│   ├── IronRose.Standalone/        # Standalone 빌드 실행 파일 (런타임 테스트용)
│   ├── IronRose.Rendering/         # 렌더링 파이프라인
│   │   ├── GBuffer.cs, GraphicsManager.cs, ShaderCompiler.cs
│   │   └── PostProcessing/          # BloomEffect, TonemapEffect, PostProcessStack
│   ├── IronRose.Physics/           # 물리 래퍼 (Engine 미참조)
│   │   ├── PhysicsWorld3D.cs        # BepuPhysics v2.4.0
│   │   └── PhysicsWorld2D.cs        # Aether.Physics2D v2.2.0
│   ├── IronRose.Scripting/         # Roslyn 런타임 컴파일
│   └── IronRose.Contracts/         # 플러그인 API 계약
├── Screenshots/                     # 테스트 스크린샷
├── reference/                       # 참고 구현 (Unity IBL 등)
└── docs/                            # 문서 (18개 마크다운 파일)
```

---

## 체크리스트

### 매 작업 단계 완료 시
- [ ] [Progress.md](Progress.md) 업데이트
  - [ ] 완료된 작업 체크
  - [ ] 완료 날짜 기록
  - [ ] 주요 결정 사항 문서화
  - [ ] 다음 단계 정의
- [ ] 빌드 성공 (`dotnet build`)
- [ ] 실행 파일 테스트 완료
- [ ] 주요 동작에 로그 추가됨

### 매 커밋/PR 전 확인
- [ ] UTF-8 BOM 인코딩 확인 (C# 파일)
- [ ] 명명 규칙 준수
- [ ] 불필요한 파일 제외 (.gitignore 확인)
- [ ] 코드 리뷰 준비 완료
