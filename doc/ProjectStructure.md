# IronRose 프로젝트 구조

## 2-레포 아키텍처

IronRose는 **엔진 레포**와 **에셋 프로젝트 레포**로 분리된 구조를 사용한다.

```
~/git/
  IronRose/              ← 엔진 레포 (이 레포)
  MyGame/                ← 에셋 프로젝트 (별도 레포)
    project.toml         ← engine.path = "../IronRose"
    Directory.Build.props
    MyGame.RoseEditor.csproj
    Assets/
    EditorAssets/
    FrozenCode/
    LiveCode/
```

에셋 프로젝트는 `$(IronRoseRoot)` MSBuild 변수로 엔진을 로컬 소스 참조한다.
새 프로젝트는 `templates/default/`를 복사하여 생성하거나, 에디터의 File > New Project에서 생성 가능.

## 엔진 레포 구조

### 실행

- `src/IronRose.RoseEditor/` — 에디터 실행 프로젝트 (엔진 개발용 진입점)
- `src/IronRose.Standalone/` — Standalone 빌드. 에디터 Build 메뉴용 (별도 Phase에서 처리 예정).

에셋 프로젝트는 자체 `*.RoseEditor.csproj`로 실행한다.
엔진 개발 시에는 엔진 루트의 `project.toml` (`engine.path = "."`)과 `src/IronRose.RoseEditor/`로 직접 실행.

### 엔진 핵심 라이브러리

- `src/IronRose.Engine/` — 엔진 코어
  - `EngineCore.cs` — 메인 루프 (업데이트/렌더 오케스트레이션)
  - `ProjectContext.cs` — 프로젝트/엔진 루트 경로 중앙 관리 (`project.toml` 파싱)
  - `ShaderRegistry.cs` — 셰이더 경로 중앙 관리, `Resolve()` API
  - `RenderSystem.cs` — Forward/Deferred 하이브리드 렌더링
  - `RoseEngine/` — Unity 호환 API (~59파일)
    - 수학: Vector3, Vector2, Vector4, Quaternion, Color, Matrix4x4, Mathf
    - 코어: GameObject, Component, Transform, MonoBehaviour, SceneManager, Object
    - 렌더링: Camera, Light, Mesh, MeshFilter, MeshRenderer, Material, Texture2D, Shader
    - 입력: Input(레거시), InputSystem/(액션 기반, 7파일)
    - 물리: Rigidbody, Rigidbody2D, Collider(3D+2D), Collision, ForceMode
    - 2D: Sprite, SpriteRenderer, Font, TextRenderer, Rect
    - IBL: Cubemap, RenderSettings
    - 유틸: Random, Debug, Time, Screen, Application, Resources, Attributes, Coroutine
  - `AssetPipeline/` — 에셋 임포트 (AssetDatabase, MeshImporter, TextureImporter, PrefabImporter 등)
  - `Editor/` — 에디터 UI (ImGui 패널, StartupPanel, ProjectCreator)
  - `Physics/PhysicsManager.cs` — PhysicsWorld3D/2D 통합

- `src/IronRose.Rendering/` — 렌더링 파이프라인 (GBuffer, GraphicsManager, ShaderCompiler)
  - `PostProcessing/` — BloomEffect, TonemapEffect, PostProcessStack (`Func<string, string>` 셰이더 리졸버)

- `src/IronRose.Physics/` — 물리 래퍼
  - `PhysicsWorld3D.cs` — BepuPhysics v2.4.0
  - `PhysicsWorld2D.cs` — Aether.Physics2D v2.2.0

- `src/IronRose.Scripting/` — Roslyn 런타임 컴파일 (핫 리로드)

- `src/IronRose.Contracts/` — 플러그인 API 계약 인터페이스

- `src/IronRose.Editor/` — 에디터 유틸리티

### 셰이더

- `Shaders/` — GLSL 셰이더 (엔진 기본 셰이더)
  - Forward: vertex.glsl, fragment.glsl
  - Deferred: deferred_geometry.*, deferred_lighting.*
  - Skybox/IBL: skybox.*
  - PostProcess: bloom_*.frag, gaussian_blur.frag, tonemap*.frag
  - 공통: fullscreen.vert

### 프로젝트 템플릿

- `templates/default/` — 새 프로젝트 생성 시 복사되는 기본 템플릿
  - `{{ProjectName}}.RoseEditor.csproj` — 에디터 실행 프로젝트
  - `Program.cs` — 에디터 진입점
  - `Directory.Build.props` — `$(IronRoseRoot)` 변수 정의
  - `project.toml` — 프로젝트 설정
  - `FrozenCode/FrozenCode.csproj` — 안정 스크립트
  - `LiveCode/LiveCode.csproj` — 실험 스크립트 (Roslyn 핫 리로드)
  - `EditorAssets/` — 에디터 에셋 (Fonts, Matcaps, Skybox)
  - `Assets/` — 빈 에셋 디렉토리 구조
  - `.gitignore` — 프로젝트용

### 경로 시스템

`ProjectContext` 클래스가 모든 경로를 중앙 관리한다:

| 프로퍼티 | 설명 |
|----------|------|
| `ProjectContext.ProjectRoot` | 에셋 프로젝트 루트 (`project.toml` 위치) |
| `ProjectContext.EngineRoot` | 엔진 소스 루트 |
| `ProjectContext.AssetsPath` | `ProjectRoot/Assets` |
| `ProjectContext.EditorAssetsPath` | `EngineRoot/EditorAssets` (엔진 전용, internal) |
| `ProjectContext.CachePath` | `ProjectRoot/RoseCache` |
| `ProjectContext.LiveCodePath` | `ProjectRoot/LiveCode` |
| `ProjectContext.FrozenCodePath` | `ProjectRoot/FrozenCode` |
| `ProjectContext.IsProjectLoaded` | `project.toml` 발견 여부 |

`ShaderRegistry.Resolve(filename)` — 셰이더 파일명 → 절대 경로 변환.

### 글로벌 설정

| 경로 | 설명 |
|------|------|
| `~/.ironrose/settings.toml` | 마지막 프로젝트 경로 등 유저 로컬 설정 (`[editor] last_project`) |

프로젝트 미지정 시 StartupPanel이 표시되며, New/Open Project 후 설정 저장 → 프로세스 재시작 흐름.

### 기타

- `.claude/commands/` — Claude 커스텀 커맨드
- `doc/` — 프로젝트 문서
- `plans/` — 설계 문서
- `reference/` — 참고 구현
