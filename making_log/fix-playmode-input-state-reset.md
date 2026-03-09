# Play mode 전환 시 Input 상태 리셋 버그 수정

## 유저 보고 내용
- Play 모드를 Enter/Stop할 때 이전에 눌려있던 키/마우스 상태가 리셋되지 않음
- Input.GetKey/GetKeyDown이 true로 남아 Play 모드 전환 직후 스크립트가 잘못된 입력을 받음

## 원인
- `Input` 클래스에 모든 상태를 한꺼번에 초기화하는 메서드가 없었음
- `EditorPlayMode.EnterPlayMode()`와 `StopPlayMode()`에서 입력 상태를 리셋하지 않았음
- Play 모드 전환 시 `_keysHeld`, `_mouseHeld`, `_pendingKeyEvents` 등에 이전 상태가 그대로 남음

## 수정 내용
- `Input.ResetAllStates()` internal 메서드 추가: 키보드(held/down/up), 마우스(held/down/up), pending 이벤트 버퍼, 스크롤, 문자 입력 전부 초기화
- `EditorPlayMode.EnterPlayMode()` 시작 부분에서 `Input.ResetAllStates()` 호출
- `EditorPlayMode.StopPlayMode()` 시작 부분에서 `Input.ResetAllStates()` 호출

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Input.cs` -- `ResetAllStates()` 메서드 추가
- `src/IronRose.Engine/Editor/EditorPlayMode.cs` -- Enter/Stop 시 `Input.ResetAllStates()` 호출 추가

## 검증
- dotnet build 성공 (오류 0개)
- 유저 실행 테스트 필요: 에디터에서 키를 누른 상태로 Play/Stop 전환 후 입력 상태가 깨끗한지 확인
