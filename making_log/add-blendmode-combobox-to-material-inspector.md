# Material Inspector에 BlendMode 콤보박스 추가

## 수행한 작업
- `DrawMaterialEditor()`에 BlendMode 콤보박스 (Opaque/AlphaBlend/Additive) 추가
- `DrawReadOnlyMaterialInspector()`에 BlendMode 읽기 전용 텍스트 표시 추가
- `Material.cs`에 `BlendMode` enum 및 `blendMode` 프로퍼티 추가 (Phase 48a 선행 작업이 worktree에 미반영이어서 함께 적용)

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Material.cs` -- BlendMode enum 추가, Material 클래스에 blendMode 프로퍼티 추가, frontmatter 추가
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` -- DrawMaterialEditor()에 Blend Mode 콤보박스 및 Undo 지원 추가, DrawReadOnlyMaterialInspector()에 읽기 전용 표시 추가

## 주요 결정 사항
- Undo 패턴: Combo는 DragFloat와 달리 드래그 중 연속 변경이 없으므로, BeginEdit/EndEdit 대신 선택 즉시 old/new 스냅샷을 기록하는 간소화 패턴 사용 (명세서 권장 방식)
- blendMode 값은 TOML 테이블에 문자열로 저장 ("Opaque", "AlphaBlend", "Additive")
- Phase 48a의 Material.cs 변경이 worktree에 없어서 함께 적용

## 다음 작업자 참고
- Phase 48c/48d의 렌더 파이프라인에서 Material.blendMode를 실제로 사용하여 블렌딩 상태를 적용해야 함
- Phase 48f에서 rose-cli에 blendMode 관련 명령 추가 예정
