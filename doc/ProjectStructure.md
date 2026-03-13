# IronRose 프로젝트 구조

## 실행 프로젝트 (dotnet run 대상)

- `src/IronRose.RoseEditor/` — 에디터 실행 파일. 에디터 UI/Inspector/SceneView 테스트용.
  - `FrozenCode/` — 안정된 스크립트 (dotnet build 시 컴파일)
  - `LiveCode/` — 실험용 스크립트 (Roslyn 런타임 핫 리로드)
- `src/IronRose.Standalone/` — Standalone 빌드. 게임 런타임 테스트용.

## 엔진 핵심 라이브러리

- `src/IronRose.Engine/` — 엔진 코어
  - `EngineCore.cs` — 메인 루프 (업데이트/렌더 오케스트레이션)
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
  - `AssetPipeline/` — 에셋 임포트 (AssetDatabase, MeshImporter, TextureImporter, PrefabImporter, GlbTextureExtractor, RoseMetadata, UnityYamlParser)
  - `Physics/PhysicsManager.cs` — PhysicsWorld3D/2D 통합

- `src/IronRose.Rendering/` — 렌더링 파이프라인 (GBuffer, GraphicsManager, ShaderCompiler)
  - `PostProcessing/` — BloomEffect, TonemapEffect, PostProcessStack

- `src/IronRose.Physics/` — 물리 래퍼 (Engine이 참조하지 않음)
  - `PhysicsWorld3D.cs` — BepuPhysics v2.4.0
  - `PhysicsWorld2D.cs` — Aether.Physics2D v2.2.0

- `src/IronRose.Scripting/` — Roslyn 런타임 컴파일 (핫 리로드)

- `src/IronRose.Contracts/` — 플러그인 API 계약 인터페이스

## 에셋 / 셰이더

- `Assets/` — 게임 에셋 (텍스처, 모델, 씬, 프리팹). C# 파일 금지.
  - `Textures/` — IBL 큐브맵, 텍스처 + .rose 메타데이터
- `Shaders/` — GLSL 셰이더
  - Forward: vertex.glsl, fragment.glsl
  - Deferred: deferred_geometry.*, deferred_lighting.*
  - Skybox/IBL: skybox.*
  - PostProcess: bloom_*.frag, gaussian_blur.frag, tonemap*.frag
  - 공통: fullscreen.vert

## 기타

- `.claude/commands/` — Claude 커스텀 커맨드 (digest.md 등)
- `.claude/test_outputs/` — 자동화 테스트 결과물 (스크린샷 등)
- `doc/` — 프로젝트 문서
- `reference/` — 참고 구현 (Unity IBL 등)
- `Screenshots/` — 테스트 스크린샷
