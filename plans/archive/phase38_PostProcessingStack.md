# Phase 38-C: Post Processing Volume System

## 목표
현재 전역 PostProcessStack을 **Volume 기반 시스템**으로 전환.
카메라가 Volume 안에 진입했을 때만 해당 이펙트가 활성화되고, 여러 Volume이 겹치면 블렌딩한다.
**Volume 밖에서는 PP 없음.**

---

## 핵심 설계: Volume 시스템

### 개념

```
씬 공간
┌──────────────────────────────────────────┐
│                                          │
│  ┌──────────────┐  ┌─────────────────┐   │
│  │ Volume A     │  │ Volume B        │   │
│  │ Tonemap      │  │ Bloom + Tonemap │   │
│  │  ┌────────┐  │  └─────────────────┘   │
│  │  │ Camera │  │                        │  ← Volume A만 적용
│  │  └────────┘  │                        │
│  └──────────────┘                        │
│                                          │
│          ┌────────┐                      │
│          │ Camera │                      │  ← Volume 밖 = PP 없음
│          └────────┘                      │
└──────────────────────────────────────────┘
```

### 블렌딩 규칙

1. 카메라 위치 기준으로 진입한 Volume 목록 수집 (blendDistance 포함)
2. **진입 Volume이 없으면 PP 비활성** (렌더링 스킵)
3. 각 Volume의 **유효 가중치** 계산: `weight × distanceFactor`
4. 이펙트별 가중 평균 (weighted average) 으로 최종값 산출
5. **Volume에 없는 이펙트 = neutral value로 참여** (부드럽게 페이드 아웃)

### Neutral Value (이펙트 꺼짐 = 무효과 값)

Volume에 특정 이펙트가 **없으면**, 해당 이펙트의 neutral value로 블렌딩에 참여한다.
Neutral value는 "이 이펙트가 없는 것과 동일한 결과"를 내는 값이다.

| 이펙트 | 파라미터 | Neutral Value | 의미 |
|--------|----------|:------------:|------|
| Bloom | Intensity | 0.0 | 블룸 없음 |
| Bloom | Threshold | 10.0 (max) | 아무것도 통과 못함 |
| Bloom | Soft Knee | 0.0 | 무관 |
| Tonemap | Exposure | 1.0 | 밝기 변화 없음 |
| Tonemap | Saturation | 1.0 | 채도 변화 없음 |
| Tonemap | Contrast | 1.0 | 대비 변화 없음 |
| Tonemap | White Point | 4.0 (default) | 기본값 유지 |
| Tonemap | Gamma | 2.2 (default) | 기본값 유지 |

### 블렌딩 예시

### 케이스 1: A→B 전환 (경계 겹침 + neutral value)

```
Volume A: Bloom(Intensity=2.0), Tonemap(Exposure=1.5)
Volume B: Tonemap(Exposure=0.5)  ← Bloom 없음

카메라가 A→B로 이동 중 (겹침 영역):
  A 가중치: 0.3 (경계에서 빠져나가는 중)
  B 가중치: 0.8

  Bloom.Intensity:
    A: 2.0 (가중치 0.3)
    B: 0.0 (Bloom 없음 → neutral, 가중치 0.8)
    최종 = (2.0×0.3 + 0.0×0.8) / (0.3+0.8) = 0.55  ← 부드럽게 페이드아웃

  Tonemap.Exposure:
    A: 1.5 (가중치 0.3)
    B: 0.5 (가중치 0.8)
    최종 = (1.5×0.3 + 0.5×0.8) / (0.3+0.8) = 0.77

  B에만 도달 → A 가중치=0 → Bloom=0 (꺼짐), Exposure=0.5
```

### 케이스 2: 깊은 겹침 (blendDistance < 겹침 깊이)

두 Volume이 blendDistance보다 깊게 겹쳐 있으면, 중앙에 **정적 블렌딩 구간**이 생긴다.

```
Volume A (blendDistance=2)        Volume B (blendDistance=2)
┌────────────────────┐           ┌────────────────────┐
│                    │←  2m  →│←  2m  →│                    │
│   A only           │  A fade  │  B fade │   B only           │
│   A=1.0            │  A: 1→0  │  B: 0→1 │   B=1.0            │
└────────────────────┘           └────────────────────┘

겹침 영역이 4m보다 넓으면 (예: 8m):
┌────────────────────────────────────────────────────────┐
│ A only │ A fade-in │ 정적 구간 (A=1, B=1) │ B fade-in │ B only │
│ A=1.0  │ B: 0→1   │  둘 다 최대 가중치     │ A: 1→0   │ B=1.0  │
└────────────────────────────────────────────────────────┘

정적 구간에서:
  A: Bloom(2.0), Tonemap(Exposure=1.5)   가중치 1.0
  B: Tonemap(Exposure=0.5)               가중치 1.0

  Bloom.Intensity = (2.0×1.0 + 0.0×1.0) / (1.0+1.0) = 1.0
  Tonemap.Exposure = (1.5×1.0 + 0.5×1.0) / (1.0+1.0) = 1.0

→ 정적 구간에서는 값이 고정, 경계에서만 전환 발생
```

### 케이스 3: 중첩 Volume (B가 A 내부에 포함)

작은 Volume B가 큰 Volume A 안에 완전히 포함된 경우.

```
Volume A (큰 방 전체)
┌──────────────────────────────────────┐
│                                      │
│   Volume B (작은 영역)                │
│   ┌──────────────┐                   │
│   │              │                   │
│   │   Camera ●   │                   │
│   │              │                   │
│   └──────────────┘                   │
│                                      │
└──────────────────────────────────────┘

카메라가 B 내부에 있을 때 (A도 내부):
  A distanceFactor = 1.0 (A 내부 깊숙이)
  B distanceFactor = 1.0 (B 내부)

  A: weight=1.0, effectiveWeight = 1.0 × 1.0 = 1.0
  B: weight=1.0, effectiveWeight = 1.0 × 1.0 = 1.0

  예) A: Bloom(1.0), B: Bloom(3.0)
  최종 Bloom = (1.0×1.0 + 3.0×1.0) / (1.0+1.0) = 2.0  ← 50:50 블렌딩

카메라가 B 경계에서 나갈 때 (blendDistance=2):
  A: effectiveWeight = 1.0 (여전히 A 내부)
  B: effectiveWeight = 0.5 (경계 근처)

  최종 Bloom = (1.0×1.0 + 3.0×0.5) / (1.0+0.5) = 1.67

카메라가 B 완전히 벗어남:
  A: effectiveWeight = 1.0
  B: effectiveWeight = 0.0 → Volume 목록에서 제거

  최종 Bloom = 1.0  ← A만 적용
```

### 케이스 4: 단일 Volume + 낮은 weight

Volume이 하나뿐이면, weight 값에 관계없이 **항상 100% 반영**된다.

```
Volume A: Bloom(Intensity=2.0), weight=0.1

카메라가 A 내부:
  effectiveWeight = 0.1 × 1.0 = 0.1

  최종 Bloom.Intensity = (2.0 × 0.1) / 0.1 = 2.0  ← 100% 반영

→ 가중 평균에서 Volume이 하나면 분자/분모가 상쇄되어 항상 원래 값
→ weight는 다른 Volume과의 상대적 비율에만 영향을 줌

예) Volume A(weight=0.1) + Volume B(weight=1.0, Bloom=4.0) 겹침:
  최종 Bloom = (2.0×0.1 + 4.0×1.0) / (0.1+1.0) = 3.82
  → A의 영향력이 B 대비 10분의 1
```

---

## 컴포넌트 설계

### PostProcessVolume (MonoBehaviour)

Box Collider 영역 기반. 카메라가 영역 안에 있을 때만 활성화.

```csharp
public class PostProcessVolume : MonoBehaviour
{
    public float blendDistance = 0f;        // 경계에서 페이드인 거리
    public float weight = 1f;              // 최대 블렌딩 강도 (0~1)
    public PostProcessProfile profile;     // 이펙트 프로파일
}
// BoxCollider 컴포넌트 필수 (Volume 영역 정의)
```

### PostProcessProfile (에셋 파일 `.ppprofile`)

```toml
# CaveProfile.ppprofile
[[effects]]
type = "Bloom"
enabled = true
[effects.params]
threshold = 0.5
softKnee = 0.3
intensity = 2.0

[[effects]]
type = "Tonemap"
enabled = true
[effects.params]
exposure = 0.8
saturation = 1.2
contrast = 1.0
whitePoint = 4.0
gamma = 2.2
```

이펙트가 프로파일에 **있으면** 모든 파라미터가 블렌딩에 참여.
이펙트가 **없으면** neutral value로 참여 (페이드아웃).

---

## 블렌딩 파이프라인

### 매 프레임 흐름

```
1. PostProcessManager.Update(cameraPosition)
   │
   ├── 씬의 모든 PostProcessVolume 수집
   ├── 카메라가 진입한 Volume만 필터링 (BoxCollider + blendDistance)
   │
   ├── 진입 Volume 없음 → PP 비활성, 렌더링 스킵
   │
   ├── 각 Volume의 유효 가중치 계산
   │   └── effectiveWeight = weight × distanceFactor(camera, collider, blendDistance)
   │
   ├── 이펙트별 가중 평균 계산
   │   for each effect type (Bloom, Tonemap, ...):
   │     for each param:
   │       - Volume에 해당 이펙트 있음 → profile 값 사용
   │       - Volume에 해당 이펙트 없음 → neutral value 사용
   │       최종값 = Σ(value × effectiveWeight) / Σ(effectiveWeight)
   │
   └── 최종 파라미터 → PostProcessStack에 적용 → 렌더링
```

### 거리 기반 페이드

```
blendDistance = 0  →  안/밖 이진 전환 (0 또는 1)
blendDistance > 0  →  경계에서 부드럽게 페이드

    Volume 내부        경계      외부
  weight=1.0 ─────╲          ────── weight=0.0
                    ╲
              blendDistance
```

---

## Inspector UI

### PostProcessVolume 컴포넌트

```
PostProcessVolume
├── [Slider]   Blend Distance   (0.0 – 50.0)
├── [Slider]   Weight           (0.0 – 1.0)
│
└── Profile: [CaveProfile.ppprofile ▼]  [New] [Clone]
```
※ BoxCollider 컴포넌트가 Volume 영역을 정의

### PostProcessProfile Inspector (에셋 선택 시)

```
PostProcess Profile: CaveProfile
│
├── [Add Effect ▼]          ← 드롭다운으로 이펙트 추가
│
├── [Effect] Bloom ✓
│   ├── Threshold     [====|====] 0.50
│   ├── Soft Knee     [====|====] 0.30
│   └── Intensity     [====|====] 2.00
│
└── [Effect] Tonemap ✓
    ├── Exposure      [====|====] 0.80
    ├── Saturation    [====|====] 1.20
    ├── Contrast      [====|====] 1.00
    ├── White Point   [====|====] 4.00
    └── Gamma         [====|====] 2.20
```

---

## 이펙트 설계 규칙

새로운 Post Processing 이펙트를 추가할 때 반드시 다음 규칙을 따른다:

1. **Neutral Value 필수**: 모든 이펙트는 파라미터 조합만으로 **이펙트가 꺼진 것과 완전히 동일한 결과**를 낼 수 있어야 한다. 이 값을 해당 이펙트의 **Neutral Value**로 정의하고, 파라미터 테이블에 명시한다.
2. **볼륨 블렌딩 호환**: Neutral Value는 가중 평균 블렌딩에서 사용된다. Volume에 해당 이펙트가 없으면 Neutral Value로 블렌딩에 참여하여 부드럽게 페이드아웃된다. 따라서 Neutral Value에서의 렌더링 결과가 이펙트 미적용과 **픽셀 단위로 동일**해야 한다.
3. **파라미터 테이블 형식**: 이펙트 추가 시 아래와 같은 형식으로 모든 파라미터의 Neutral Value를 문서화한다.

```
| 파라미터 | 타입 | 범위 | 기본값 | Neutral |
```

---

## 이펙트 파라미터 정의

### BloomEffect

| 파라미터 | 타입 | 범위 | 기본값 | Neutral |
|----------|------|------|--------|---------|
| Threshold | float | 0 – 10 | 1.0 | 10.0 |
| Soft Knee | float | 0 – 1 | 0.5 | 0.0 |
| Intensity | float | 0 – 5 | 1.0 | 0.0 |

### TonemapEffect

| 파라미터 | 타입 | 범위 | 기본값 | Neutral |
|----------|------|------|--------|---------|
| Exposure | float | 0.01 – 10 | 1.0 | 1.0 |
| Saturation | float | 0 – 3 | 1.0 | 1.0 |
| Contrast | float | 0.5 – 2 | 1.0 | 1.0 |
| White Point | float | 0.5 – 20 | 4.0 | 4.0 |
| Gamma | float | 1.0 – 3.0 | 2.2 | 2.2 |

---

## 스크립트 API

```csharp
// Volume 생성 (BoxCollider 필수)
var go = new GameObject("PP Volume");
go.AddComponent<BoxCollider>().size = new Vector3(20, 10, 20);

var volume = go.AddComponent<PostProcessVolume>();
volume.blendDistance = 3f;
volume.weight = 1f;

// 프로파일 로드
volume.profile = Resources.Load<PostProcessProfile>("CaveProfile");

// 런타임에 파라미터 변경
if (volume.profile.TryGetEffect<BloomEffect>(out var bloom))
{
    bloom.intensity = 3.0f;
    bloom.threshold = 0.2f;
}
```

---

## 구현 작업

### Volume 시스템 (코어)
- [ ] `PostProcessVolume` 컴포넌트 — blendDistance, weight, profile (BoxCollider 필수)
- [ ] `PostProcessProfile` 에셋 클래스 — 이펙트 목록 + 파라미터
- [ ] `PostProcessManager` — 매 프레임 Volume 수집 → 진입 판정 → 블렌딩 → 최종값 적용
- [ ] BoxCollider 기반 거리/진입 판정
- [ ] 이펙트별 가중 평균 블렌딩 (neutral value 포함)
- [ ] Volume 밖 = PP 비활성 처리

### 에셋 파일
- [ ] `.ppprofile` TOML 직렬화/역직렬화
- [ ] `AssetDatabase`에 `.ppprofile` 임포터 등록
- [ ] Project 패널 우클릭 → "Create > Post Process Profile" 메뉴

### 기존 이펙트 리팩터
- [ ] `BloomEffect` / `TonemapEffect`에 neutral value 정의 추가
- [ ] 기존 `PostProcessStack`이 `PostProcessManager`의 최종 블렌딩 결과를 소비하도록 연결

### Inspector / 패널
- [ ] `PostProcessVolume` Inspector UI (blend distance, weight, profile 슬롯)
- [ ] `PostProcessProfile` Inspector UI (이펙트 목록 + 슬라이더)
- [ ] "Add Effect" 드롭다운
- [ ] `ImGuiOverlay` / `ImGuiLayoutManager`에 등록
- [ ] 기존 `ImGuiRenderSettingsPanel`에서 Post Processing 섹션 제거
- [ ] 기존 `ImGuiRenderSettingsPanel.cs` 삭제 (모든 섹션 이동 완료 후)

### 마이그레이션
- [ ] `RenderSettings.postProcessing` 제거 — Volume이 없으면 PP 없음
- [ ] 기존 씬 로드 시 씬 전체를 덮는 BoxCollider Volume으로 자동 마이그레이션 (1회성)
