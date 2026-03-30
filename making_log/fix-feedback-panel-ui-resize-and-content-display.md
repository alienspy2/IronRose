# Feedback 패널 UI 리사이즈 대응 및 콘텐츠 표시 개선

## 유저 보고 내용
- Feedback 창 크기를 변경해도 UI 요소들이 고정되어 움직이지 않음
- 기존 feedback 파일의 내용이 표시되지 않음 (파일명만 보임)

## 원인
- 피드백 목록 영역이 `BeginChild` 없이 직접 렌더링되어 창 크기 변경 시 스크롤 및 공간 활용이 안 됨
- 파일 내용 표시는 이미 `CollapsingHeader` + `TextWrapped`로 구현되어 있었으나, 레이아웃 문제로 가독성이 떨어짐

## 수정 내용
1. **리사이즈 대응**: `BeginChild("##feedback_list")` 로 목록 영역을 감싸서 남은 공간을 자동으로 채우도록 변경. 스크롤바도 자동 생성됨.
2. **입력 영역**: `GetContentRegionAvail().X` 로 가용 너비를 채우는 방식 유지
3. **Save 버튼**: 우측 정렬로 배치
4. **Delete 버튼**: 빨간색 스타일 적용으로 시각적 구분
5. **콘텐츠 가독성**: 각 항목 내 들여쓰기(Indent) 추가

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs` -- BeginChild 추가, 버튼 스타일링, 레이아웃 개선

## 검증
- dotnet build 성공 확인
- UI 동작은 유저 확인 필요
