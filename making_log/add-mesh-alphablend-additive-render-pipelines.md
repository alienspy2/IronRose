# RenderSystem에 AlphaBlend/Additive 메시 렌더 파이프라인 추가

## 수행한 작업
- `RenderSystem.cs`에 `_meshAlphaBlendPipeline`과 `_meshAdditivePipeline` 2개의 Veldrid Pipeline 필드 추가
- 기존 Forward 셰이더(`_forwardShaders`)를 재사용하여 파이프라인 생성 코드 추가
- AlphaBlend: `destinationColorFactor = InverseSourceAlpha` (표준 알파 블렌딩)
- Additive: `destinationColorFactor = One` (가산 블렌딩)
- 둘 다 `depthWriteEnabled: false`, `depthTestEnabled: true`, `cullMode: FaceCullMode.Back`
- `Dispose()`에 새 파이프라인 해제 추가
- `Material.cs`에 `BlendMode` enum (Opaque=0, AlphaBlend=1, Additive=2) 및 `blendMode` 프로퍼티 추가 (Phase 48a 내용 포함)

## 변경된 파일
- `src/IronRose.Engine/RenderSystem.cs` — 파이프라인 필드 2개, 생성 코드, Dispose 해제 추가
- `src/IronRose.Engine/RoseEngine/Material.cs` — BlendMode enum 및 blendMode 프로퍼티 추가, frontmatter 추가

## 주요 결정 사항
- 이 worktree에 Phase 48a(BlendMode enum) 변경이 없었으므로 Material.cs도 함께 업데이트함
- 파이프라인 생성 위치는 `_spritePipeline` 생성 직후, Debug Overlay Pipeline 직전

## 다음 작업자 참고
- 새 파이프라인은 아직 렌더 루프에서 사용되지 않음. Phase 48d에서 `DrawTransparentRenderers()` 메서드 추가 시 사용 예정
- 직렬화(Phase 48b)도 별도로 진행 필요 (MaterialSerializer에 blendMode 읽기/쓰기)
