# Animation Editor — 작업 로그

> Phase 36 Animation Editor 구현 완료 요약

---

## 구현 완료 요약

- 기본 기능 5건 (Dopesheet, 프리뷰 재생, 키프레임 CRUD, Target 선택, Add Property)
- 확장 기능 10건 (Value editing, Undo/Redo, Snap, Curves 모드, Tangent, Multi-select, Copy/Paste, Events, Shortcuts, Current value)
- 버그 수정 7건 (Tangent DZ, Event index, Selection rebuild, Event iteration, Undo 이중, Edge tangent, Preview orphan)
- 기능 개선 8건 (더블클릭 편집, Y축 피팅, Foldout, Tooltip, 가시성 토글, Thread safety, Path 검증, LOD 렌더링)
- 후속 작업 8건 (스플리터 리사이즈, Y축 고정, 패닝 수정, 컨텍스트 메뉴, 숫자칸 제거, 아이콘 변경, Inspector 제거, PlayMode 차단)
- Record Mode 구현 (Inspector/Gizmo 변경 감지 → 자동 키프레임 생성)

---

## TODO — 미구현 (향후 작업)

### Animator State Machine
현재 Animator는 단일 AnimationClip만 재생하는 구조. 향후 다음 기능 구현 필요:
- 여러 AnimationClip 등록 및 관리
- 조건(Trigger, Bool, Float 파라미터)에 따른 상태 전환 (State Transition)
- 상태 간 블렌딩 (Crossfade / Transition Duration)
- AnimatorController 에셋 (상태 그래프 직렬화)
- Animation Layer + Weight 기반 레이어 블렌딩

현재는 **임시로 PlayMode 진입 시 등록된 clip을 자동 재생**하는 방식으로 동작.

---

## 파일 목록

| 파일 | 역할 |
|------|------|
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiAnimationEditorPanel.cs` | Animation Editor UI 전체 |
| `src/IronRose.Engine/RoseEngine/Animator.cs` | 애니메이션 재생 + 프로퍼티 적용 |
| `src/IronRose.Engine/RoseEngine/AnimationCurve.cs` | Hermite cubic spline 커브 |
| `src/IronRose.Engine/RoseEngine/AnimationClip.cs` | 클립 데이터 (curves + events) |
| `src/IronRose.Engine/Editor/Undo/Actions/AnimationClipUndoAction.cs` | 클립 스냅샷 Undo 액션 |
