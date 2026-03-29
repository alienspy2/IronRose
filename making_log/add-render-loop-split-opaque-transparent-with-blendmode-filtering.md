# 렌더 루프 분리: Opaque/Transparent 메시 분리 렌더링

## 수행한 작업
- `DrawOpaqueRenderers()`에 `BlendMode.Opaque` 필터링 추가하여 반투명 메시를 제외
- `DrawTransparentRenderers()` 메서드 신규 추가: AlphaBlend/Additive 메시를 카메라 거리 역순(Back-to-Front) 정렬 후 Forward 패스로 렌더링
- 렌더 루프 Forward Pass 영역에 `DrawTransparentRenderers()` 호출 추가 (스프라이트/텍스트보다 먼저)
- Shadow Pass의 두 렌더링 루프에서 반투명 메시 제외
- 선행 조건인 `BlendMode` enum, `blendMode` 프로퍼티, `_meshAlphaBlendPipeline`/`_meshAdditivePipeline` 파이프라인 필드 및 생성/소멸 코드 추가 (Phase 48c 내용이 worktree에 미반영되어 직접 추가)

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Material.cs` — `BlendMode` enum 추가, `blendMode` 프로퍼티 추가, frontmatter 추가
- `src/IronRose.Engine/RenderSystem.Draw.cs` — `DrawOpaqueRenderers` BlendMode 필터링, `DrawTransparentRenderers` 메서드 추가, frontmatter 추가
- `src/IronRose.Engine/RenderSystem.Shadow.cs` — 두 shadow 렌더링 루프에 BlendMode 필터링 추가
- `src/IronRose.Engine/RenderSystem.cs` — `_meshAlphaBlendPipeline`/`_meshAdditivePipeline` 필드, 파이프라인 생성, 소멸 코드 추가, Forward Pass에 `DrawTransparentRenderers` 호출 추가

## 주요 결정 사항
- Phase 48c의 BlendMode/파이프라인 코드가 worktree에 없어서 직접 추가함 (main repo 코드 참조)
- `DrawTransparentRenderers`에서 매 프레임 `List<>` 할당 발생 — 간단함 우선, 향후 최적화 가능
- 반투명 메시 정렬은 오브젝트 중심점 기준 거리제곱 사용 (sqrt 불필요)
- `UploadForwardLightData`를 `DrawTransparentRenderers` 내부에서 호출하여 독립적 사용 가능

## 다음 작업자 참고
- `DrawTransparentRenderers`의 매 프레임 List 할당은 클래스 필드로 재사용하여 최적화 가능
- 교차하는 반투명 메시에서 정렬 아티팩트 발생 가능 (Forward 렌더링의 일반적 한계, OIT 필요)
- main repo에 Phase 48c 머지 후 이 worktree와 충돌 가능성 있음 (Material.cs, RenderSystem.cs의 동일 위치 수정)
