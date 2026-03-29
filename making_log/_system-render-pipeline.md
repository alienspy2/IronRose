# 렌더 파이프라인 시스템

## 구조
- `src/IronRose.Engine/RenderSystem.cs` — 모든 Veldrid Pipeline 생성/관리, 렌더 루프 실행
- `src/IronRose.Engine/RenderSystem.Draw.cs` — 메시/스프라이트/텍스트 드로우 메서드 (partial class)
- `src/IronRose.Engine/RenderSystem.Shadow.cs` — 섀도우 아틀라스 렌더링 (partial class)
- `src/IronRose.Engine/RoseEngine/Material.cs` — Material 데이터 클래스, BlendMode enum 정의

## 핵심 동작
- RenderSystem은 여러 Pipeline 객체를 생성하여 용도별로 사용:
  - `_forwardPipeline` — 불투명 메시 (Forward 셰이더, 깊이 쓰기 O)
  - `_wireframePipeline` — 와이어프레임
  - `_spritePipeline` — 스프라이트 (알파 블렌드, 깊이 쓰기 X, 컬링 없음)
  - `_meshAlphaBlendPipeline` — 반투명 메시 (알파 블렌드, 깊이 쓰기 X, 백페이스 컬링)
  - `_meshAdditivePipeline` — 가산 블렌딩 메시 (Additive, 깊이 쓰기 X, 백페이스 컬링)
- Veldrid Pipeline은 immutable이므로 블렌드 모드별로 별도 Pipeline 필요
- 모든 메시 파이프라인은 `_forwardShaders`를 공유, 블렌드 상태와 깊이 설정만 다름
- 렌더 루프 순서: Deferred GBuffer → Shadow → Deferred Lighting → Skybox → Forward Pass (wireframe → transparent meshes → sprites → text) → Post-Processing
- `DrawOpaqueRenderers`는 BlendMode.Opaque만 렌더링, `DrawTransparentRenderers`는 AlphaBlend/Additive를 Back-to-Front 정렬 후 렌더링
- Shadow Pass에서는 반투명 메시 제외 (그림자 미생성)

## 주의사항
- 반투명 파이프라인은 `depthWriteEnabled: false`여야 뒤의 물체가 가려지지 않음
- 반투명 파이프라인은 `depthTestEnabled: true`여야 불투명 물체 뒤에서 그려지지 않음
- 스프라이트 파이프라인과 메시 파이프라인의 차이: cullMode (None vs Back)
- Pipeline 생성 순서: Forward → Wireframe → Sprite → AlphaBlend → Additive → DebugOverlay
- `DrawMesh()`는 파이프라인 바인딩 없이 transform/material만 업로드하므로, 파이프라인은 호출자가 설정해야 함
- `DrawTransparentRenderers`에서 매 프레임 List 할당 발생 (향후 최적화 대상)

## 사용하는 외부 라이브러리
- Veldrid — GPU 파이프라인, 셰이더, 버퍼 관리
