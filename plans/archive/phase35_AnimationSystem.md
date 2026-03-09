# Phase 34: Animation System

## Context

IronRose 엔진에 애니메이션 시스템이 전혀 없음. Transform/SpriteRenderer 등의 프로퍼티를 시간 기반으로 변화시키는 애니메이션 인프라 구축.

---

## 라이프사이클 다이어그램

```
EngineCore.Update()
│
├─ Input.Update()
├─ InputSystem.Update()
│
├─ [Playing 상태일 때만]
│   │
│   ├─ FixedUpdate 루프 (물리 고정 타임스텝)
│   │   ├─ PhysicsManager.FixedUpdate()
│   │   └─ SceneManager.FixedUpdate()
│   │       └─ MonoBehaviour.FixedUpdate()
│   │
│   ├─ LiveCodeManager.UpdateScripts()
│   │
│   └─ SceneManager.Update()
│       ├─ 1. MonoBehaviour.Start()        ← 신규 등록된 컴포넌트
│       ├─ 2. InvokeScheduler.Process()
│       ├─ 3. MonoBehaviour.Update()       ← Animator.Update() 여기서 실행
│       │       │
│       │       ├─ [멀티스레드] Curve Evaluate + 프로퍼티 적용
│       │       └─ [메인스레드] AnimationEvent 큐 → invoke
│       │
│       ├─ 4. CoroutineScheduler.Process()
│       ├─ 5. MonoBehaviour.LateUpdate()   ← 애니메이션 결과 후처리 (카메라 추적 등)
│       └─ 6. DestroyQueue 처리
│
├─ ImGuiOverlay.Update()
└─ Render()
```

**핵심 포인트**:
- `Animator.Update()`는 일반 `MonoBehaviour.Update()` 단계(3번)에서 실행
- Curve 평가 + 프로퍼티 적용은 멀티스레드, AnimationEvent는 메인스레드
- `LateUpdate()`(5번)에서 애니메이션 결과 기반 후처리 가능 (예: 카메라 follow)
- `SpriteAnimation.Update()`도 동일한 3번 단계에서 프레임 전환

---

## Phase 1: 런타임 코어 (신규 5파일)

### 1-1. `src/IronRose.Engine/RoseEngine/WrapMode.cs`
- enum: Once, Loop, PingPong, ClampForever

### 1-2. `src/IronRose.Engine/RoseEngine/AnimationCurve.cs`
- **Keyframe** struct: time, value, inTangent, outTangent
- **AnimationCurve** class: sorted keyframe list
  - `Evaluate(float time)` → Hermite cubic 보간 (binary search + tangent interpolation)
  - `AddKey`, `RemoveKey`, `MoveKey`
  - Factory: `Linear()`, `EaseInOut()`, `Constant()`

### 1-3. `src/IronRose.Engine/RoseEngine/AnimationClip.cs`
- **AnimationEvent** struct: time, functionName, float/int/string params
- **AnimationClip** : Object
  - `Dictionary<string, AnimationCurve>` — propertyPath → curve
  - propertyPath 규칙: `"localPosition.x"`, `"SpriteRenderer.color.r"`
  - length, frameRate, wrapMode
  - events 배열

### 1-4. `src/IronRose.Engine/RoseEngine/Animator.cs`
- MonoBehaviour 컴포넌트
- `clip: AnimationClip`, `speed: float`
- `Play()`, `Stop()`, `Pause()`
- Update()에서: elapsed 누적 → WrapTime → 모든 curve Evaluate → 리플렉션으로 프로퍼티 적용
- AnimationEvent 발화 (MonoBehaviour에서 method 찾아 invoke)
- **PropertyTarget 캐시**: Play() 시 리플렉션 결과 캐싱, 프레임당 GetValue/SetValue만
- **스레딩 정책**:
  - Curve Evaluate + 프로퍼티 적용 → **멀티스레드** (Parallel.ForEach 등으로 Animator 단위 병렬 처리)
  - AnimationEvent 호출 → **메인 스레드 전용** (이벤트 큐에 적재 후 메인 스레드에서 순차 invoke)

### 1-5. `src/IronRose.Engine/RoseEngine/SpriteAnimation.cs`
- MonoBehaviour, `[RequireComponent(typeof(SpriteRenderer))]`
- `frames: Sprite[]`, `framesPerSecond: float`, `loop: bool`
- Update()에서 타이머 기반 프레임 전환 → SpriteRenderer.sprite 교체

---

## Phase 2: 에셋 파이프라인 (신규 1파일 + 기존 수정 4파일)

### 2-1. `src/IronRose.Engine/AssetPipeline/AnimationClipImporter.cs`
- `.anim` (TOML) 파싱 → AnimationClip 반환
- Export: AnimationClip → TOML 파일 쓰기
- TOML 구조: `[[curves]]` + `[[curves.keys]]` + `[[events]]`

### 2-2. AssetDatabase.cs 수정
- AnimationClipImporter 인스턴스 추가
- `.anim` 확장자 import 라우팅
- `LoadByGuid<AnimationClip>` 지원

### 2-3. RoseMetadata.cs 수정
- `.anim` → `AnimationClipImporter` 타입 추론

### 2-4. SceneSerializer.cs 수정
- `AssetReferenceTypes`에 `typeof(AnimationClip)` 추가
- SerializeAssetRef / DeserializeAssetRef에 AnimationClip 케이스
- **Sprite[] 배열 직렬화**: IsArray && element ∈ AssetReferenceTypes → TomlTableArray로 직렬화/역직렬화

### 2-5. ImGuiInspectorPanel.cs 수정
- `AssetNameExtractors`에 `[typeof(AnimationClip)]` 추가 (Ping 가능 에셋 라벨)

---

## Phase 3: 에디터 타임라인 (별도 단계, 이번 구현 범위 밖)

- ImGuiAnimationTimelinePanel — playhead, 트랙, 키프레임 편집, 커브 에디터
- ImGuiOverlay에 패널 등록 + EditorState 영속화
- Undo: SetAnimationKeyframeAction

---

## 구현 순서

```
Phase 1 (순서 의존):
  WrapMode → AnimationCurve → AnimationClip → Animator → SpriteAnimation

Phase 2 (Phase 1 완료 후):
  AnimationClipImporter → AssetDatabase → RoseMetadata → SceneSerializer → Inspector
```

## 검증

1. Phase 1 완료 후 `dotnet build` 확인
2. LiveCode 스크립트로 프로그래밍 방식 테스트:
   - AnimationCurve.Evaluate 정확성 (Debug.Log)
   - Animator + AnimationClip으로 오브젝트 이동/회전
   - SpriteAnimation으로 프레임 전환
3. Phase 2 완료 후 `.anim` 에셋 생성 → Inspector에서 확인 → 씬 저장/로드 검증
