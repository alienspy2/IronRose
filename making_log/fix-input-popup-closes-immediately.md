# Fix: 입력 팝업(Rename/CreateFolder)이 즉시 닫히는 문제

## 증상
- F2(Rename) 또는 Create Folder 시 이름 입력창이 뜨자마자 닫힘
- 처음에는 정상 동작하다가 어느 순간부터 발생

## 진단 로그 위치

### 태그: `[DiagPopup]` — Project 패널 모달 팝업
| 파일 | 내용 |
|------|------|
| `EditorModal.cs` | `OpenPopup` 요청, `BeginPopupModal` 실패, 닫힘 사유(enter/confirm/cancel/esc) |
| `ImGuiProjectPanel.cs` | F2/CreateFolder/Rename 트리거 지점 (어디서 팝업이 열렸는지) |

### 태그: `[DiagRename]` — Hierarchy 패널 인라인 리네임
| 파일 | 내용 |
|------|------|
| `ImGuiHierarchyPanel.cs` | `BeginRename`, `CommitRename(caller)`, `CancelRename(caller)`, `InputText` 프레임별 상태 (isActive, justRequested, windowFocused) |

## 핵심 의심 포인트
1. **포커스 빼앗김**: 다른 패널/윈도우가 포커스를 가져가면 InputText가 비활성 → CommitRename("focus lost") 호출
2. **Escape 키 잔존**: 이전 조작의 Escape가 다음 프레임에 남아서 팝업을 즉시 닫음
3. **중복 트리거**: F2가 여러 곳에서 동시에 처리되어 팝업이 열렸다 닫힘

## 테스트 절차
1. 빌드 후 실행
2. 정상 동작할 때 F2/Create Folder를 몇 번 사용
3. 문제가 재발하면 콘솔 로그에서 `[DiagPopup]` / `[DiagRename]` 확인
4. 특히 `BeginPopupModal FAILED` 또는 `lost focus unexpectedly` 로그 주목

## 다음 단계
- 사용자 테스트 결과를 기반으로 로그 분석
- 원인 확정 후 수정
- 수정 완료 후 모든 `[DiagPopup]` / `[DiagRename]` 로그 제거
