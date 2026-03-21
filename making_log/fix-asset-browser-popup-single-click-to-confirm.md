# Asset Browser 팝업 싱글클릭으로 즉시 선택 확정 + 닫기

## 유저 보고 내용
- Asset Browser(Link Browser) 팝업에서 항목을 더블클릭해야 선택이 확정됨
- 싱글클릭으로 바로 선택 확정 + 창 닫기가 되도록 변경 요청

## 원인
- `DrawAssetBrowserPopup()`에서 `ImGui.Selectable` 클릭 시에는 `_assetBrowserSelectedGuid`만 설정하고, `confirmed = true`는 `ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)` 조건에서만 설정됨
- 즉, 싱글클릭은 "하이라이트만", 더블클릭이 "선택 확정"이었음

## 수정 내용
- `(None)` 항목과 에셋 항목 모두, `ImGui.Selectable` 클릭(싱글클릭) 시 바로 `confirmed = true`를 설정하도록 변경
- 더블클릭 전용 분기(`ImGui.IsMouseDoubleClicked`) 제거
- OK/Cancel 버튼, Enter/ESC 키 처리는 기존과 동일하게 유지

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs` -- Selectable 싱글클릭 시 confirmed 플래그 즉시 설정, 더블클릭 분기 제거

## 검증
- dotnet build 성공 확인 (오류 0개)
