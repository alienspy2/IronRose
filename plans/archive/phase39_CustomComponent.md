# Phase 39: Custom Component 시스템

## Context

현재 IronRose 엔진은 리플렉션 기반 제네릭 Inspector/Serializer를 갖추고 있어, 사용자 MonoBehaviour의 public 필드가 자동으로 Inspector에 표시되고 씬 파일에 저장됩니다. 하지만 지원 타입이 제한적(float, int, bool, string, Vector2, Vector3, Color, enum)이고, 배열/리스트, GameObject/Component 참조, 중첩 구조체 등이 빠져 있어 실질적인 커스텀 컴포넌트 활용에 한계가 있습니다. 이 Phase에서 이를 확장합니다.

---

## Phase 39-A: 코어 타입 확장

**목표**: Quaternion, Vector4, long, double, byte 지원 추가

### 수정 파일

**1. ImGuiInspectorPanel.cs** (`src/IronRose.Engine/Editor/ImGui/Panels/`)
- `IsSupportedType()` — Quaternion, Vector4, long, double, byte 추가
- `DrawValue()` — 각 타입별 새 분기 추가:
  - **Quaternion** → 오일러 각도(Vector3)로 변환하여 DragFloat3Clickable 표시
  - **Vector4** → 새 DragFloat4Clickable 위젯 사용
  - **long** → DragIntClickable (int 캐스트, clamp)
  - **double** → DragFloatClickable (float 캐스트) 또는 SliderFloatWithInput ([Range])
  - **byte** → DragIntClickable (0-255 clamp)

**2. EditorWidgets.cs** (`src/IronRose.Engine/Editor/ImGui/`)
- `DragFloat4Clickable()` 신규 메서드 — DragFloat3Clickable 패턴 동일, 4축 클릭 감지

**3. SceneSerializer.cs** (`src/IronRose.Engine/Editor/`)
- `IsSupportedValueType()` — Vector4, long, double, byte 추가 (Quaternion은 이미 있음)
- `ValueToToml()` — long, double, byte 직렬화 분기
- `TomlToValue()` / `DeserializeFieldValue()` — 역직렬화 분기

---

## Phase 39-B: 배열/리스트 지원

**목표**: `T[]` 및 `List<T>` (T = 지원 값 타입) Inspector 편집 + 씬 직렬화

### 수정 파일

**1. ImGuiInspectorPanel.cs**
- `IsSupportedElementType()` 신규 — 재귀 방지용 단순 타입 체크
- `IsSupportedType()` — 배열/List<T> 감지 추가
- `DrawArrayOrListValue()` 신규 메서드:
  - TreeNodeEx 폴더아웃 `label [count]`
  - Size 필드 (DragIntClickable)
  - 각 요소를 DrawValue()로 재귀 렌더
  - +/- 버튼 (add/remove)
  - Undo: 컬렉션 전체를 old/new로 기록
- `ResizeCollection()` 신규 헬퍼 — Array.CreateInstance / List 리사이징

**2. SceneSerializer.cs**
- `IsSupportedCollectionType()` 신규 헬퍼
- `IsSerializableField()` / `IsSerializableProperty()` — 컬렉션 타입 허용
- `SerializeMember()` — 배열/리스트 → TomlArray 직렬화 (중첩 배열: `[[1,2,3],[4,5,6]]`)
- `SetMemberValue()` — TomlArray → Array.CreateInstance / List 역직렬화

**TOML 형식**:
```toml
speeds = [1.5, 2.0, 3.5]
waypoints = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
```

---

## Phase 39-C: 씬 오브젝트 참조

**목표**: `public GameObject target;`, `public Transform followTarget;` 등 씬 내 오브젝트/컴포넌트 참조 필드

### 수정 파일

**1. ImGuiInspectorPanel.cs**
- `IsSceneObjectReferenceType()` 신규 — `typeof(GameObject)` 또는 `typeof(Component).IsAssignableFrom`
- `IsSupportedType()` — 씬 참조 타입 추가
- `DrawGameObjectReferenceField()` 신규 — Combo 드롭다운 (씬 내 모든 GO 목록), (None) 옵션
- `DrawComponentReferenceField()` 신규 — Combo 드롭다운 (해당 타입 컴포넌트를 가진 GO 목록)
- `DrawValue()` — GameObject/Component 분기 추가

**2. SceneSerializer.cs**
- 직렬화: `{ "_sceneRef": "Transform", "_guid": "go-guid-string" }` 형식 TOML 테이블
- 역직렬화: **지연 해석** — 모든 GO 생성 후 GUID로 참조 복원
  - `_pendingSceneRefs` 리스트 + `ResolveSceneReferences()` 메서드 (Load 끝에서 호출)
- `IsSerializableField()` / `IsSerializableProperty()` — 씬 참조 타입 허용

---

## Phase 39-D: 중첩 직렬화 구조체

**목표**: `[Serializable] struct Stats { public int hp; public float speed; }` 인라인 폴더아웃

### 수정 파일

**1. ImGuiInspectorPanel.cs**
- `IsNestedSerializableType()` 신규 — `[Serializable]` 어트리뷰트 + 값/클래스 타입 감지
- `DrawNestedSerializableType()` 신규 — TreeNodeEx 폴더아웃, 내부 필드 재귀 렌더, depth 제한 5

**2. SceneSerializer.cs**
- `SerializeMember()` — 중첩 타입 → TomlTable 재귀 직렬화
- `SetMemberValue()` — TomlTable → Activator.CreateInstance + 필드별 재귀 역직렬화

**TOML 형식**:
```toml
[components.fields.stats]
hp = 100
speed = 5.5
```

---

## Phase 39-E: LiveCode 타입 캐시 무효화

**목표**: 핫 리로드 후 새 타입이 Add Component 메뉴 + 씬 역직렬화에 즉시 반영

### 수정 파일

- **ImGuiInspectorPanel.cs** — `InvalidateComponentTypeCache()` 신규
- **SceneSerializer.cs** — `InvalidateComponentTypeCache()` 신규
- **LiveCodeManager.cs** (`src/IronRose.Engine/`) — `RegisterLiveCodeBehaviours()` 끝에서 양쪽 캐시 무효화 호출

---

## 구현 순서

```
39-A (코어 타입) → 39-E (캐시 무효화) → 39-B (배열/리스트) → 39-C (씬 참조) → 39-D (중첩 구조체)
```

39-A가 기반이 되며, 39-E는 독립적이므로 일찍 처리. 39-B/C/D는 39-A 위에서 병렬 가능하나, 순차 구현이 안전.

---

## 검증 방법

매 Sub-Phase 완료 후:
1. `dotnet build` 성공 확인
2. `dotnet run --project src/IronRose.Demo` 실행
3. LiveCode에 테스트 스크립트 작성:
```csharp
public class CustomComponentTest : MonoBehaviour
{
    // 39-A
    public Vector4 customVec4;
    public double customDouble;
    public byte customByte;

    // 39-B
    public float[] speeds;
    public List<Vector3> waypoints;

    // 39-C
    public GameObject target;
    public Transform followTarget;

    // 39-D
    [Serializable]
    public struct Stats { public int hp; public float speed; public Color tint; }
    public Stats playerStats;
}
```
4. Inspector에서 각 필드 편집 가능 확인
5. 씬 저장 → 재로드 후 값 보존 확인
6. Undo/Redo 동작 확인
