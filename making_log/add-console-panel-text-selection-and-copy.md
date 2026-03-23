# Console 패널 텍스트 선택 및 복사 기능 추가

## 유저 보고 내용
- Console 창의 출력 텍스트를 마우스로 선택(드래그)하고 Ctrl+C로 복사할 수 없음
- 기존 구현은 `ImGui.TextUnformatted`로 텍스트를 표시하여 선택/복사 기능이 없었음

## 원인
- `TextUnformatted`는 단순 텍스트 렌더링만 수행하며, ImGui에서 기본적으로 텍스트 선택 기능을 제공하지 않음
- 선택/복사를 위한 별도 로직이 구현되어 있지 않았음

## 수정 내용
- `TextUnformatted` 대신 `ImGui.Selectable`로 각 로그 항목을 렌더링하여 클릭/드래그로 행 단위 선택 가능하게 변경
- 선택 상태 관리:
  - `_selectionAnchor`: 선택 시작점 (최초 클릭한 항목의 visible index)
  - `_selectionEnd`: 선택 끝점 (마지막으로 클릭/드래그한 항목의 visible index)
  - `_isDragging`: 마우스 드래그 중 여부
- 선택 동작:
  - 클릭: 해당 항목 단일 선택
  - Shift+클릭: 앵커에서 클릭 위치까지 범위 선택
  - 마우스 드래그: 클릭 시작점에서 드래그 위치까지 범위 선택
  - Ctrl+A: 전체 visible 항목 선택
- Ctrl+C: 선택된 로그 항목을 `SystemClipboard.SetText`로 시스템 클립보드에 복사
- 색상 구분(Info=teal, Warning=yellow, Error=red)은 그대로 유지됨
- `_visibleIndices` 리스트로 필터링된 항목과 원본 인덱스 간 매핑 관리
- Clear 버튼 클릭 및 로그 트리밍 시 선택 상태 리셋

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiConsolePanel.cs` -- TextUnformatted를 Selectable로 변경, 행 단위 선택/드래그/Ctrl+C 복사 로직 추가

## 검증
- dotnet build 성공 확인 (새 경고/에러 없음)
- 유저 확인 필요: 실행하여 Console 창에서 로그 클릭/드래그 선택 후 Ctrl+C로 복사되는지 테스트
