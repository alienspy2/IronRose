# ColorEdit4 Undo 미동작 수정 계획

## 증상
SceneView(인스펙터)에서 UIText(Label) 등의 `color`를 변경한 후 Ctrl+Z로 undo가 되지 않음.
색상 변경 자체는 반영되지만 undo 스택에 기록되지 않음.

## 원인
`EditorWidgets.ColorEdit4Core` ([EditorWidgets.cs:309-337](../src/IronRose.Engine/Editor/ImGui/EditorWidgets.cs#L309-L337))는
내부적으로 세 개의 ImGui 위젯을 submit 한다:

1. `ImGui.ColorEdit4` (NoPicker, 숫자 입력용)
2. `ImGui.ColorButton` (팝업 오픈)
3. 팝업 내부 `ImGui.ColorPicker4`

실제 편집은 팝업 내부 `ColorPicker4`에서 발생해 `changed=true`가 반환되지만,
호출 직후 외부에서 호출하는 `ImGui.IsItemDeactivatedAfterEdit()`는
팝업이 `EndPopup()`된 후의 "마지막 아이템"(=ColorButton)을 대상으로 한다.
따라서 picker의 deactivation 신호가 외부로 전달되지 않아 undo 기록 조건이 영구히 false.

영향 위치:
- [ImGuiInspectorPanel.cs:1836-1841](../src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L1836-L1841) — 리플렉션 기반 다중선택 Color 처리
  - [1931-1941](../src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L1931-L1941)의 `isDeactivatedAfterEdit` 체크가 동작 안 함
- [ImGuiInspectorPanel.cs:5148-5162](../src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L5148-L5162) — 단일 컴포넌트 Color 처리 (동일 패턴, 동일 버그)
- [ImGuiSceneEnvironmentPanel.cs:301, 336, 343](../src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneEnvironmentPanel.cs#L301) — Ambient/Zenith/Horizon Color (undo 체인 자체 없음, 별도 건)
- 기타 `EditorWidgets.ColorEdit4` 사용처 전반

## 수정 방향

### 1. ColorEdit4Core가 deactivation 신호를 노출
`ColorEdit4Core` 내부에서 각 위젯(`ColorEdit4` 숫자 입력, 팝업 내 `ColorPicker4`) 호출 직후
`ImGui.IsItemDeactivatedAfterEdit()`를 OR로 누적해 out 파라미터로 반환.

```csharp
public static bool ColorEdit4(string label, ref Color color, out bool deactivatedAfterEdit)
public static bool ColorEdit4(string label, ref Color color) // 기존 시그니처 유지 (내부에서 out 버전 호출, 신호 버림)
```

`ColorEdit4Core`도 동일하게 out 파라미터 추가:
- 숫자 입력 `ColorEdit4` 호출 직후: `deactivatedAfterEdit |= ImGui.IsItemDeactivatedAfterEdit()`
- 팝업 내 `ColorPicker4` 호출 직후: `deactivatedAfterEdit |= ImGui.IsItemDeactivatedAfterEdit()`
- 팝업이 이번 프레임에 닫혔는지도 함께 고려 (picker에서 드래그 중 esc/바깥 클릭으로 닫히는 경우)

### 2. 인스펙터 측 수정
- 다중선택 경로(1836): `changed = EditorWidgets.ColorEdit4(label, ref c, out bool colorDeactivated)` 로 받고,
  1931의 `isDeactivatedAfterEdit || pendingSliderDeactivation` 조건에 `colorDeactivated`를 OR로 추가
  (또는 Color 타입일 때 별도 경로로 분기).
- 단일 경로(5148): 마찬가지로 out 값을 사용해 `UndoSystem.Record` 트리거.

### 3. 검증
- 단일 GameObject의 UIText.color 변경 후 Ctrl+Z로 원복 확인
- 다중선택 상태에서 color 변경 후 undo 확인 (CompoundUndoAction 생성)
- 숫자 입력 필드(RGB)로 직접 수정한 경우와 팝업 picker로 수정한 경우 모두 undo 동작
- Escape로 팝업 닫는 경우, 바깥 클릭으로 닫는 경우 각각 검증
- Material.color, Material.emission([3138, 3175](../src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs#L3138)) 등 다른 ColorEdit4 사용처에 회귀 없는지 확인

## 별도 후속 작업 (이번 Plan 범위 밖, 옵션)
- `ImGuiSceneEnvironmentPanel`의 Ambient/Zenith/Horizon Color는 애초에 undo 기록 로직이 없음.
  동일 API(`out deactivatedAfterEdit`)를 활용해 undo 연동을 추가하는 별도 plan 고려.
