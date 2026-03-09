# Phase 36: Animation Editor (타임라인 + 커브 에디터)

## Context

Phase 35에서 런타임 애니메이션 시스템(AnimationCurve, AnimationClip, Animator, SpriteAnimation)과 `.anim` 에셋 파이프라인을 구축했다. 이제 에디터에서 시각적으로 키프레임을 편집하고, 커브를 조작하며, 실시간 프리뷰할 수 있는 Animation Editor 패널을 구현한다.

**Phase 35 → 36 연결**: Phase 35 문서의 "Phase 3: 에디터 타임라인 (별도 단계)" 항목을 이번에 구현.

---

## 전체 구조 다이어그램

```
┌─────────────────────────────────────────────────────────────────────┐
│  ImGuiAnimationEditorPanel                                          │
├─────────────────────────────────────────────────────────────────────┤
│  ┌─ Toolbar ───────────────────────────────────────────────────┐    │
│  │ [▶ Play] [⏸ Pause] [⏹ Stop] [⏮] [⏭]  FrameRate: 60       │    │
│  │ Clip: bounce.anim ▼   WrapMode: Loop ▼   Length: 2.00s     │    │
│  └─────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  ┌─ Track List ──────┬─ Timeline ──────────────────────────────┐   │
│  │                   │  0.0   0.5   1.0   1.5   2.0            │   │
│  │ localPosition.x   │  ◆─────────◆──────────◆                │   │
│  │ localPosition.y   │  ◆──────◆──────────────◆               │   │
│  │ localEulerAngles.z│  ◆─────────────────────◆               │   │
│  │ SpriteRenderer.   │  ◆────◆────◆───────────◆               │   │
│  │   color.a         │                                         │   │
│  │                   │  ▼ Playhead                              │   │
│  │ [+ Add Property]  │                                         │   │
│  └───────────────────┴─────────────────────────────────────────┘   │
│                                                                     │
│  ┌─ Curve Editor (토글) ───────────────────────────────────────┐   │
│  │  value                                                      │   │
│  │  │         ╱╲                                               │   │
│  │  │        ╱  ╲     ← Hermite curve 시각화                   │   │
│  │  │  ◆───╱    ╲───◆   (탄젠트 핸들 드래그 가능)              │   │
│  │  │     ╱        ╲                                           │   │
│  │  └──────────────────── time                                 │   │
│  └─────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Phase 1: 패널 등록 + 기본 레이아웃 (신규 1파일 + 기존 수정 2파일)

### 1-1. `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiAnimationEditorPanel.cs` (신규)

```csharp
public class ImGuiAnimationEditorPanel : IEditorPanel
```

**기본 구조**:
- `IEditorPanel` 구현 (`IsOpen`, `Draw()`)
- `Open(string animPath, AnimationClip clip)` — 외부에서 클립 열기 (Project 패널 더블클릭 등)
- `SetContext(Animator? animator)` — Inspector에서 선택된 Animator 연동
- 생성자: `ImGuiAnimationEditorPanel(GraphicsDevice device, VeldridImGuiRenderer renderer)`

**상태 필드**:
```csharp
private AnimationClip? _clip;
private string? _animPath;           // .anim 파일 경로
private Animator? _contextAnimator;  // 씬 내 프리뷰 대상
private float _playheadTime;         // 현재 재생 위치
private bool _isPlaying;             // 에디터 프리뷰 재생 중
private float _zoom = 100f;          // 타임라인 pixels per second
private float _scrollX;              // 타임라인 가로 스크롤
private int _selectedTrackIndex = -1;
private int _selectedKeyIndex = -1;
private bool _showCurveEditor;       // 하단 커브 에디터 토글
```

**Draw() 구성**:
```
1. Begin("Animation Editor", ref _isOpen)
2. DrawToolbar()
3. ImGui.Separator()
4. DrawTimeline()         ← 트랙 리스트 + 도프 시트
5. if (_showCurveEditor)
     DrawCurveEditor()    ← 선택된 커브 시각화 + 편집
6. End()
```

이 단계에서는 빈 레이아웃만 잡고, 클립 로딩/표시만 확인.

### 1-2. `ImGuiOverlay.cs` 수정

- 필드 추가: `private ImGuiAnimationEditorPanel? _animEditor;`
- `Initialize()`에서 인스턴스 생성
- `Update()`에서 `_animEditor?.Draw()` 호출
- Windows 메뉴에 `"Animation Editor"` MenuItem 추가
- `SyncPanelStatesToEditorState()` / `RestorePanelStates()` 연동

### 1-3. `EditorState.cs` 수정

- `public static bool PanelAnimationEditor { get; set; } = false;`
- `Save()` / `Load()`의 `[panels]` 섹션에 `animation_editor` 키 추가

---

## Phase 2: 도프 시트 타임라인 (기존 파일 수정)

### 2-1. DrawToolbar() 구현

- 재생 컨트롤: Play / Pause / Stop / PrevKey / NextKey 버튼
- Play 시 `_playheadTime`을 `Time.unscaledDeltaTime * clip.frameRate` 기반으로 진행
- 프리뷰: `_contextAnimator`가 있으면 `Animator.time` 직접 설정하여 씬 실시간 반영
- 클립 메타: frameRate (DragIntClickable), wrapMode (Combo), length (읽기 전용)
- `[Save]` 버튼: `AnimationClipImporter.Export(clip, animPath)` 호출

### 2-2. DrawTimeline() — 도프 시트

**좌측 트랙 목록** (고정 폭 ~200px):
- `_clip.curves` 키 목록을 트리로 표시
  - `localPosition` 그룹 → `.x`, `.y`, `.z` 자식
  - `SpriteRenderer.color` 그룹 → `.r`, `.g`, `.b`, `.a` 자식
- 클릭으로 트랙 선택 (`_selectedTrackIndex`)
- 우클릭 컨텍스트: "Remove Property"
- 하단 `[+ Add Property]` 버튼 → 프로퍼티 선택 팝업 (Transform, SpriteRenderer 등 필드 목록)

**우측 타임라인** (스크롤 가능):
- 시간 눈금자 (상단): 0.0, 0.5, 1.0 ... 초 단위, zoom에 따라 스케일
- 각 트랙 행에 키프레임 다이아몬드(◆) 렌더링 (`ImGui.GetWindowDrawList()`)
  - 위치: `keyframe.time * _zoom - _scrollX`
  - 선택 상태: 채워진 다이아몬드 vs 비어있는 다이아몬드
- Playhead: 빨간 세로선, 드래그로 시간 스크러빙
- 마우스 휠: zoom 변경
- 가로 스크롤바: `_scrollX` 제어

**키프레임 조작**:
- 클릭: 키프레임 선택 (`_selectedKeyIndex`)
- 더블클릭 (빈 공간): 현재 시간에 키프레임 추가
- 드래그: 키프레임 시간 이동 (`AnimationCurve.MoveKey`)
- Delete 키: 선택된 키프레임 삭제 (`AnimationCurve.RemoveKey`)
- Ctrl+클릭: 다중 선택 (선택적)

### 2-3. 프리뷰 재생 로직

```csharp
private void UpdatePreview()
{
    if (!_isPlaying || _clip == null) return;

    _playheadTime += Time.unscaledDeltaTime;
    _playheadTime = WrapTime(_playheadTime, _clip.length, _clip.wrapMode);

    // 에디터 프리뷰: Animator에 시간 직접 주입
    if (_contextAnimator != null)
    {
        _contextAnimator.time = _playheadTime;
        // Animator가 자체적으로 Evaluate → 프로퍼티 적용
    }
}
```

---

## Phase 3: 커브 에디터

### 3-1. DrawCurveEditor() 구현

**좌표계**:
- X축: time (0 ~ clip.length)
- Y축: value (자동 범위 계산 — 모든 키프레임 value의 min/max + padding)
- `ImGui.GetWindowDrawList()`로 직접 렌더링

**곡선 렌더링**:
```csharp
// Hermite 곡선을 세그먼트로 샘플링
for (float t = 0; t < clip.length; t += sampleStep)
{
    float v = curve.Evaluate(t);
    Vector2 p = TimeValueToScreen(t, v);
    drawList.PathLineTo(p);
}
drawList.PathStroke(curveColor, ImDrawFlags.None, 2f);
```

**키프레임 노드**:
- 사각형 또는 다이아몬드로 렌더링
- 선택 시 탄젠트 핸들 2개 표시 (inTangent, outTangent)
- 핸들은 키프레임 위치에서 좌/우로 연장되는 선 + 원형 그립

**탄젠트 핸들 드래그**:
```csharp
// 핸들 위치 → 탄젠트 값 변환
// tangent = dy / dx (화면 좌표 → time-value 좌표)
keyframe.outTangent = (handleValueDelta) / (handleTimeDelta);
curve[keyIndex] = keyframe;
```

**그리드**:
- 배경에 수평/수직 그리드 라인
- 줌 레벨에 따라 눈금 간격 자동 조절

**줌/패닝**:
- 마우스 휠: Y축 줌
- Shift+마우스 휠: X축 줌
- 중간 버튼 드래그: 패닝
- `[F]` 키: 선택된 커브에 맞게 뷰 리셋 (Fit)

---

## Phase 4: Undo + 파일 저장

### 4-1. `src/IronRose.Engine/Editor/Undo/Actions/AnimationClipUndoAction.cs` (신규)

MaterialPropertyUndoAction 패턴 — TOML 스냅샷 기반:

```csharp
public sealed class AnimationClipUndoAction : IUndoAction
{
    public string Description { get; }
    private readonly string _animPath;
    private readonly string _oldToml;
    private readonly string _newToml;

    public AnimationClipUndoAction(string desc, string animPath, string oldToml, string newToml)
    {
        Description = desc;
        _animPath = animPath;
        _oldToml = oldToml;
        _newToml = newToml;
    }

    public void Undo() => WriteAndReimport(_oldToml);
    public void Redo() => WriteAndReimport(_newToml);

    private void WriteAndReimport(string toml)
    {
        File.WriteAllText(_animPath, toml, new UTF8Encoding(true));
        // AssetDatabase에서 재임포트 트리거
        AssetDatabase.Reimport(_animPath);
    }
}
```

### 4-2. 에디터 패널 Undo 통합

모든 키프레임 조작 전 스냅샷 캡처:
```csharp
private string CaptureSnapshot()
    => AnimationClipImporter.ExportToString(_clip);

private void RecordUndo(string desc)
{
    var after = CaptureSnapshot();
    UndoSystem.Record(new AnimationClipUndoAction(desc, _animPath, _beforeSnapshot, after));
    _beforeSnapshot = after;
}
```

**Undo 기록 시점**:
- 키프레임 추가/삭제
- 키프레임 드래그 완료 (IsItemDeactivatedAfterEdit)
- 탄젠트 핸들 드래그 완료
- 프로퍼티 트랙 추가/삭제
- frameRate, wrapMode 변경

### 4-3. 자동 저장 / 수동 저장

- 변경 시 타이틀바에 `*` 표시 (dirty flag)
- Ctrl+S: 즉시 저장 (`AnimationClipImporter.Export`)
- 패널 닫기 시 미저장 경고 모달

---

## Phase 5: 통합 + 워크플로우 연결

### 5-1. Project 패널 연동

`ImGuiProjectPanel.cs` 수정:
- `.anim` 파일 더블클릭 → `_animEditor.Open(path, clip)` 호출
- 이미 `AssetDatabase.LoadByGuid<AnimationClip>` 지원됨

### 5-2. Inspector 연동

`ImGuiInspectorPanel.cs` 수정:
- Animator 컴포넌트의 `clip` 필드 옆에 `[Edit]` 버튼 추가
- 클릭 시 → Animation Editor 열기 + `SetContext(animator)`

### 5-3. Add Property 팝업

선택된 GameObject의 컴포넌트를 리플렉션으로 탐색:
```
Transform
├── localPosition.x / .y / .z
├── localEulerAngles.x / .y / .z
└── localScale.x / .y / .z
SpriteRenderer
├── color.r / .g / .b / .a
└── sprite (Sprite 타입 — 키프레임 애니메이션은 미지원, 안내 표시)
```
- float, int, bool 타입 프로퍼티만 곡선 대상
- 선택 시 빈 커브 생성 + 현재 값으로 키프레임 1개 삽입

---

## 구현 순서

```
Phase 1 (기본 뼈대):
  ImGuiAnimationEditorPanel 생성 → ImGuiOverlay 등록 → EditorState 연동
  → 빌드 확인 + 빈 패널 표시 검증

Phase 2 (도프 시트):
  Toolbar → Track List → Timeline 렌더링 → 키프레임 선택/추가/삭제/이동
  → 프리뷰 재생
  → 빌드 + .anim 파일로 키프레임 편집 검증

Phase 3 (커브 에디터):
  곡선 렌더링 → 키프레임 노드 → 탄젠트 핸들 → 줌/패닝
  → 빌드 + 커브 시각적 편집 검증

Phase 4 (Undo + 저장):
  AnimationClipUndoAction → 스냅샷 기반 Undo/Redo → 저장 로직
  → Ctrl+Z/Y 검증

Phase 5 (통합):
  Project 패널 더블클릭 → Inspector [Edit] 버튼 → Add Property 팝업
  → 전체 워크플로우 검증
```

---

## 검증

1. Phase 1 완료 후: `dotnet build` + 실행 → Windows 메뉴에서 Animation Editor 토글 확인
2. Phase 2 완료 후: `.anim` 파일 로드 → 트랙/키프레임 표시 → 추가/삭제/이동 → 프리뷰 재생
3. Phase 3 완료 후: 커브 에디터에서 Hermite 곡선 시각화 → 탄젠트 핸들 드래그 → 곡선 변형 확인
4. Phase 4 완료 후: Ctrl+Z/Y로 키프레임 조작 Undo/Redo → 저장 후 재로드 일치 확인
5. Phase 5 완료 후: Project → 더블클릭 .anim → 에디터 열림 → Inspector [Edit] → Animator 연동 프리뷰
