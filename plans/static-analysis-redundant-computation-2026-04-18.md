---
title: 정적 분석 - 불필요한 반복 연산 핫패스
date: 2026-04-18
type: static-analysis
scope: redundant-computation / per-frame-waste
status: report-only (수정 금지, 참고용)
---

# IronRose 정적 분석: 불필요하게 연산을 많이 하는 코드

매 프레임/매 스텝 등 자주 호출되는 경로에서 **캐시/재사용으로 쉽게 줄일 수 있는데 그렇지 않은** 연산들을 수집했다. 각 항목은 실제 파일을 열어 교차 확인했다.

범례:
- **High**: 매 프레임 호출 + GC 할당 또는 O(n) 이상 순회·정렬
- **Medium**: 매 프레임 호출이지만 단일 오브젝트 또는 작은 상수 오버헤드
- **Low**: 간헐 호출이거나 이미 내부 캐시가 있지만 진입 비용 자체가 있는 경우

---

## High (우선순위 상)

### H1. 매 프레임 `new List<>` — Transparent 메시 수집
- 위치: [RenderSystem.Draw.cs:59](src/IronRose.Engine/RenderSystem.Draw.cs#L59)
- 현재: `DrawTransparentRenderers`가 진입할 때마다 `new List<(MeshRenderer, Material, float)>()`을 만들고, 거기에 Add 후 정렬한다.
- 낭비 이유: 매 프레임 GC 할당. 반투명 오브젝트가 없을 수도 있는데 리스트는 무조건 할당된다.
- 개선 방향: 필드로 재사용 리스트를 두고 `Clear()` 후 채우기.

### H2. `LINQ Where + ToList` — 스프라이트 드로우 경로
- 위치: [RenderSystem.Draw.cs:162-165](src/IronRose.Engine/RenderSystem.Draw.cs#L162-L165)
- 현재:
  ```csharp
  var active = SpriteRenderer._allSpriteRenderers
      .Where(sr => sr.enabled && sr.sprite != null && ...)
      .ToList();
  ```
- 낭비 이유: 매 프레임 enumerator 할당 + 리스트 할당 + 이후 `Sort`까지 O(n log n).
- 개선 방향: 정적 재사용 버퍼(`List<SpriteRenderer>`)에 조건 필터링하며 직접 Add. 정렬은 H4 참고.

### H3. `LINQ Where + ToList` — 텍스트 드로우 경로
- 위치: [RenderSystem.Draw.cs:205-209](src/IronRose.Engine/RenderSystem.Draw.cs#L205-L209)
- 현재: H2와 동일 패턴 (`TextRenderer._allTextRenderers.Where(...).ToList()`).
- 낭비 이유: H2와 동일. 텍스트 렌더러 개수만큼 프레임당 반복.
- 개선 방향: 정적 버퍼 재사용.

### H4. Sprite/Text 드로우 — 변경이 없어도 매 프레임 정렬
- 위치: [RenderSystem.Draw.cs:169-175](src/IronRose.Engine/RenderSystem.Draw.cs#L169-L175), [205-219](src/IronRose.Engine/RenderSystem.Draw.cs#L213-L219)
- 현재: `sortingOrder` 및 `sqrMagnitude` 기반 `List.Sort`를 매 프레임 수행.
- 낭비 이유: 대부분의 프레임에서 순서 변경은 없다. 그런데도 O(n log n) 풀 정렬.
- 개선 방향:
  - SpriteRenderer/TextRenderer에 dirty flag(enable·sortingOrder·transform move 변경 시 true)
  - dirty 아니면 이전 정렬 결과 재사용.
  - 또는 sortingOrder만 Insertion-sort로 유지(거의 정렬된 상태 O(n)).

### H5. `PhysicsWorld3D.ContactEventCollector.Flush` — 스텝당 3개 리스트 신규 할당
- 위치: [PhysicsWorld3D.cs:66-73](src/IronRose.Physics/PhysicsWorld3D.cs#L66-L73)
- 현재:
  ```csharp
  entered = new List<(int, int)>();
  staying = new List<(int, int)>();
  exited  = new List<(int, int)>();
  ```
- 낭비 이유: 물리 스텝마다 3 × `List` 할당. 게임 오브젝트 다수 충돌 시 hotspot.
- 개선 방향: `out` 대신 컬렉터 내부에 static/instance 3개 리스트를 두고, 호출자가 `Clear()` 후 재사용하도록 API 변경.

### H6. `PhysicsWorld3D.GetContactingIds` — 호출마다 `new List<int>`
- 위치: [PhysicsWorld3D.cs:102-111](src/IronRose.Physics/PhysicsWorld3D.cs#L102-L111)
- 현재: 접촉 쌍 집합을 순회하며 결과를 새 리스트로 반환.
- 낭비 이유: Rigidbody/Collider 수가 많고 쿼리가 잦으면 GC 부담.
- 개선 방향: `GetContactingIds(int id, List<int> outBuffer)` 오버로드 제공. 내부적으로는 id → 인접 id 딕셔너리를 스텝당 1회 구축하면 더 저렴해짐.

### H7. `GetComponent<MeshFilter>` — 드로우 경로에서 매 프레임 조회
- 위치: [RenderSystem.Draw.cs:35](src/IronRose.Engine/RenderSystem.Draw.cs#L35), [66](src/IronRose.Engine/RenderSystem.Draw.cs#L66), [102](src/IronRose.Engine/RenderSystem.Draw.cs#L102), [119](src/IronRose.Engine/RenderSystem.Draw.cs#L119)
- 현재: DrawOpaque/DrawTransparent/DrawAll 각각에서 `renderer.GetComponent<MeshFilter>()` 호출. Transparent는 1차 필터 + 2차 드로우에서 **같은 렌더러에 대해 2번** 호출.
- 낭비 이유: 컴포넌트 리스트 선형 탐색이 렌더러 × 패스 횟수만큼 반복.
- 개선 방향: MeshRenderer 쪽에 `_cachedFilter` 또는 `MeshFilter`와 쌍으로 등록된 리스트를 유지. Transparent는 수집 단계에서 얻은 참조를 튜플에 포함해 재사용.

---

## Medium

### M1. `Transform.position` / `lossyScale`이 부모 체인 재귀, 캐시 없음
- 위치: [Transform.cs:149-168](src/IronRose.Engine/RoseEngine/Transform.cs#L149-L168), [182-190](src/IronRose.Engine/RoseEngine/Transform.cs#L182-L190)
- 현재: `position` getter가 `_parent.lossyScale` + `_parent.rotation` + `_parent.position`을 재귀 호출. 같은 프레임에서 여러 번 부르면 매번 체인 전체를 다시 계산.
- 낭비 이유:
  - 드로우 경로에서 `renderer.transform.position`, `sr.transform.position` 등을 sort 비교(§H4)와 uniform 업로드 시점에서 반복 읽음.
  - `rotation`/`position`을 섞어 부르면 부모 rotation도 두 번 순회.
- 개선 방향: localMatrix 또는 world cache + dirty propagation. 깊이 ≥ 3인 hierarchy에서 체감이 큼.

### M2. Canvas `GetScaleFactor` — `Log2` + `Pow` 를 프레임마다
- 위치: [Canvas.cs:42-53](src/IronRose.Engine/RoseEngine/Canvas.cs#L42-L53)
- 현재: 캔버스 렌더마다 `MathF.Log2(w)`, `Log2(h)`, `Pow(2, ...)`.
- 낭비 이유: `screenW/H`, `referenceResolution`, `matchWidthOrHeight`가 모두 같으면 결과도 같다. 매 프레임 계산할 이유가 없음.
- 개선 방향: 입력 해시를 캐시에 두고 변경 시에만 재계산. 수식은 `pow(w/ref, 1-m) * pow(h/ref, m)`로 풀어쓰면 대수 로그 회피도 가능.

### M3. Canvas 리스트 정렬이 RenderAll / HitTest / HitTestAll / CollectHitsInRect에서 반복
- 위치: [CanvasRenderer.cs:120](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs#L120), [269](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs#L269), [313](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs#L313), [391](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs#L391)
- 현재: 네 곳 모두 `_sorted.Clear()` → `foreach Canvas._allCanvases` → `_sorted.Sort(...)`.
- 낭비 이유: 같은 프레임 내에서도 RenderAll + HitTest 경로가 둘 다 돌면 정렬이 중복된다. Canvas는 자주 변하지 않음.
- 개선 방향: Canvas 추가/제거/sortingOrder 변경 시에만 dirty로 만들고, 각 진입점은 정렬된 리스트를 그냥 읽기만. (HitTest는 역순이 필요하면 뒤에서부터 순회).

### M4. `DrawTransparentRenderers`가 2차 foreach에서 `mat.blendMode` 재확인
- 위치: [RenderSystem.Draw.cs:94](src/IronRose.Engine/RenderSystem.Draw.cs#L94)
- 현재: 수집 때 이미 `blendMode`를 읽어 필터링했지만, 드로우 루프에서 다시 `mat.blendMode != currentMode` 를 비교. `BlendMode currentMode = Opaque;` 를 센티넬로 쓰는데 정렬 기준도 distance만임 — 블렌드 모드별로 먼저 정렬되어 있지 않으면 파이프라인 바인딩 전환이 여러 번 발생.
- 낭비 이유: 파이프라인 스위칭은 GPU state change 비용. distance-only sort는 블렌드 모드 간 교차를 허용한다.
- 개선 방향: primary key = `blendMode`, secondary key = `distSq` 로 정렬하면 파이프라인 바인딩이 최대 2회로 수렴.

### M5. `SkyZenithColor`/`SkyHorizonColor` 게터가 매번 `new Vector4`
- 위치: [RenderSystem.Lighting.cs:223-230](src/IronRose.Engine/RenderSystem.Lighting.cs#L223-L230) 근처
- 현재: property get마다 `Color` → `Vector4` 복사.
- 낭비 이유: `ComputeSkyAmbientColor`, `RenderSkybox`, `UploadEnvMapData` 세 경로에서 호출. 스택 할당이라 GC는 아니지만 계산 중복.
- 개선 방향: 프레임 시작에 한 번 캐시, 또는 RenderSettings 변경 시 invalidate.

### M6. `TransformVertices` — 회전이 0일 때도 sin/cos 호출
- 위치: [CanvasRenderer.cs:234-250](src/IronRose.Engine/RoseEngine/CanvasRenderer.cs#L234-L250)
- 현재: 회전 각도와 무관하게 `MathF.Cos(rad)`, `MathF.Sin(rad)` 계산 후 행렬 적용.
- 낭비 이유: UI 대부분은 회전 0. 회전이 0이면 곱하기 4번이 필요 없음.
- 개선 방향: `if (rad == 0f) { scale+translate만 } else { 회전 경로 }` 분기.

### M7. 라이팅 업로드 경로에서 `camera.transform.position` 다중 재계산
- 위치: [RenderSystem.Draw.cs:152](src/IronRose.Engine/RenderSystem.Draw.cs#L152), 그리고 Lighting 계열 함수들
- 현재:
  ```csharp
  CameraPos = new Vector4(camera.transform.position.x,
                          camera.transform.position.y,
                          camera.transform.position.z, 0),
  ```
  — `camera.transform.position` getter(M1)가 3번 연속 호출됨.
- 낭비 이유: Transform.position은 부모 체인 순회. 같은 표현식을 3번.
- 개선 방향: `var p = camera.transform.position;` 캐시 후 `new Vector4(p.x, p.y, p.z, 0)`.

---

## Low (간헐·미세)

### L1. 머티리얼 오버라이드 조건 — 드로우 루프마다 확인
- 위치: [RenderSystem.Draw.cs:39-42](src/IronRose.Engine/RenderSystem.Draw.cs#L39-L42), [69-72](src/IronRose.Engine/RenderSystem.Draw.cs#L69-L72)
- 현재: 에디터용 drag-hover 프리뷰 전용 로직이 런타임 드로우 루프 안에 존재.
- 낭비 이유: 99.9% 프레임에서 `_materialOverride == null`이라 즉시 빠지지만, 모든 렌더러에 대해 조건·InstanceID 비교 수행.
- 개선 방향: 오버라이드가 있을 때만 다른 경로로 분기(`if (_materialOverride != null) DrawWithOverride()`).

### L2. `Mesh.UploadToGPU` — 드로우 패스마다 호출
- 위치: [RenderSystem.Draw.cs:48](src/IronRose.Engine/RenderSystem.Draw.cs#L48), [104](src/IronRose.Engine/RenderSystem.Draw.cs#L104), [122](src/IronRose.Engine/RenderSystem.Draw.cs#L122), [182](src/IronRose.Engine/RenderSystem.Draw.cs#L182), [226](src/IronRose.Engine/RenderSystem.Draw.cs#L226)
- 현재: 내부 dirty 체크로 대부분 noop이겠지만, 메시·렌더 패스·스프라이트·텍스트 등 호출 지점 자체가 분산.
- 개선 방향: 프레임 시작에 한 번 모든 활성 메시를 업로드하는 single pass. 드로우 시점에는 dirty 체크 없이 바로 바인딩.

### L3. 텍스처 `UploadToGPU` — 스프라이트/텍스트마다 호출
- 위치: [RenderSystem.Draw.cs:187](src/IronRose.Engine/RenderSystem.Draw.cs#L187), [231](src/IronRose.Engine/RenderSystem.Draw.cs#L231)
- 개선 방향: L2와 동일 취지로 텍스처도 dirty-only set을 두고 일괄 업로드.

### L4. `PrepareMaterial` — 동일 머티리얼을 매번 준비
- 위치: [RenderSystem.Draw.cs:51](src/IronRose.Engine/RenderSystem.Draw.cs#L51), [107](src/IronRose.Engine/RenderSystem.Draw.cs#L107), [141](src/IronRose.Engine/RenderSystem.Draw.cs#L141)
- 현재: 드로우마다 `(matUniforms, texView, normalTexView, mroTexView)` 튜플 생성.
- 개선 방향: Material에 dirty flag + 캐시된 튜플을 두고 반환 — 파라미터 변경이 없으면 reuse.

### L5. SSIL denoise 파라미터 — H/V 거의 동일 페이로드 두 번 업로드
- 위치: [RenderSystem.SSIL.cs:86-105](src/IronRose.Engine/RenderSystem.SSIL.cs#L86-L105)
- 개선 방향: `Direction`만 바꾸는 패스라면 constant buffer의 해당 필드만 갱신 또는 push constant로 축소.

### L6. SSIL prefilter 파라미터 루프 — MIP마다 구조체 재구성
- 위치: [RenderSystem.SSIL.cs:37-55](src/IronRose.Engine/RenderSystem.SSIL.cs#L37-L55)
- 개선 방향: 스택 할당이라 비용은 적음. 한 번 빌드한 배열을 인덱싱만 하는 쪽이 clean.

### L7. `Animator.BuildTargets` — Reflection 경로
- 위치: [Animator.cs](src/IronRose.Engine/RoseEngine/Animator.cs) (Play 호출부에서 사용)
- 개선 방향: 클립 로드 시점에 타겟 바인딩을 미리 구성. Play는 인덱싱만.

---

## Summary / 권장 착수 순서

| 순위 | 항목 | 예상 ROI |
|------|------|---------|
| 1 | H2·H3·H1 (드로우 경로 LINQ/List 제거 + 정적 버퍼) | 매 프레임 GC 제거, 단순 치환 |
| 2 | H4 (SpriteRenderer/TextRenderer dirty-flag 정렬) | O(n log n) → 대부분 O(1) |
| 3 | H5·H6 (Physics Contact 재사용) | 다수 충돌 시 GC 스파이크 제거 |
| 4 | M1 (Transform world cache) | 전역 파급 효과, 구현 난도 있음 — 별도 설계 필요 |
| 5 | M3 (Canvas _sorted 캐시) | UI 프레임 안정성 |
| 6 | H7·L4 (MeshFilter/Material 캐시) | 드로우콜 많을 때 체감 |
| 7 | M2·M4·M6·M7 | 작지만 낮은 난도, 묶어서 처리 가능 |
| 8 | L1·L2·L3·L5·L6·L7 | 여유 있을 때 정리 |

## 주의 및 후속

- 본 문서는 **정적 분석만** 수행했다. 실제 프레임 비용은 프로파일러(프레임 타임, Alloc/s)로 검증한 뒤 우선순위 재조정을 권장.
- M1 (Transform world cache)는 파급 범위가 커서 별도 plan 문서로 분리 필요.
- H4, M3의 dirty flag 추가는 "데이터 변경 경로"를 모두 파악해야 하므로 설계 단계(`aca-archi`) 거쳐 구현 권장.
- 본 분석에는 포함하지 않은 관점(할당 프로파일, 락 경합, GPU state redundancy 등)은 별도 분석이 필요하다.
