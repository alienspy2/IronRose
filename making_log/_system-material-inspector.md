# Material Inspector 시스템

## 구조
- `ImGuiInspectorPanel.cs` -- Material 편집 UI를 포함하는 Inspector 패널
  - `DrawMaterialEditor()` -- `.mat` 파일 선택 시 편집 가능한 Material Inspector 표시
  - `DrawReadOnlyMaterialInspector()` -- GLB sub-asset 등 읽기 전용 Material 표시

## 핵심 동작
- `DrawMaterialEditor()`는 `_editedMatTable` (TomlTable)을 직접 편집하고 `SaveMatFile()`로 디스크에 저장
- Undo는 `Toml.FromModel(_editedMatTable)`로 전체 TOML 스냅샷을 캡처하여 `MaterialPropertyUndoAction`으로 기록
- DragFloat 등 연속 변경 가능한 위젯은 `BeginEdit/EndEdit` 패턴 사용
- Combo 등 즉시 확정되는 위젯은 변경 시점에서 바로 old/new 스냅샷 기록

## 주의사항
- `_editedMatTable`은 TomlTable 타입이며 TOML 키-값 쌍으로 접근 (예: `_editedMatTable["blendMode"] = "AlphaBlend"`)
- BlendMode는 문자열로 TOML에 저장됨 (enum 정수값이 아님)
- `EditorWidgets.BeginPropertyRow()`를 사용하여 라벨-값 2열 레이아웃을 설정해야 함
- DragFloat 직접 사용 금지 -- `DragFloatClickable` 등 헬퍼 사용 (CodingGuide.md 참조)

## 사용하는 외부 라이브러리
- ImGui.NET (ImGuiNET) -- UI 위젯 렌더링
- Tomlyn -- TOML 직렬화/역직렬화
