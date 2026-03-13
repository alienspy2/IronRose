# Cursor.lockState / Cursor.visible API 구현

## 수행한 작업
- Unity 호환 `Cursor.lockState` / `Cursor.visible` API를 구현
- `CursorLockMode` enum 신규 생성 (None, Locked, Confined)
- `Cursor` 정적 클래스 신규 생성 (Silk.NET ICursor.CursorMode 래핑)
- `Input.cs`에 `_skipNextDelta` / `SkipNextDelta()` 추가하여 커서 모드 전환 시 델타 점프 방지
- `EditorPlayMode.cs`에 Play/Stop/Pause/Resume 시 커서 상태 자동 관리 추가
- `EngineCore.cs`에 Cursor 초기화, ESC 임시 해제, Game View 클릭 재진입 로직 추가

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CursorLockMode.cs` (신규) -- enum 정의
- `src/IronRose.Engine/RoseEngine/Cursor.cs` (신규) -- 정적 클래스, lockState/visible/ESC 오버라이드
- `src/IronRose.Engine/RoseEngine/Input.cs` (수정) -- `_skipNextDelta`, `SkipNextDelta()`, Update() 델타 보정
- `src/IronRose.Engine/Editor/EditorPlayMode.cs` (수정) -- Enter/Stop에 ResetToDefault(), Pause에 ApplyState(), Resume에 ApplyState()
- `src/IronRose.Engine/EngineCore.cs` (수정) -- InitInput()에 Cursor.Initialize(), ProcessEngineKeys()에 ESC 처리, UpdateImGuiInputState()에 Game View 재진입

## 주요 결정 사항
- **IMouse.IsConfined 미지원**: 설계 문서에서는 `IMouse.IsConfined`를 사용하도록 설계했으나, Silk.NET 2.23.0에는 해당 프로퍼티가 존재하지 않음. `Confined` 모드에서 `CursorMode`만 적용하고 윈도우 영역 제한은 TODO로 남김.
- **_isEditorMode 대신 !HeadlessEditor 사용**: 설계 문서에서 `_isEditorMode`를 참조했으나, EngineCore에는 `HeadlessEditor` 프로퍼티만 존재. `!HeadlessEditor`로 대체.
- **기존 ResetAllStates() 유지**: Input.cs에 이미 `ResetAllStates()` 메서드가 추가되어 있었음. SkipNextDelta()는 별도 메서드로 추가.

## 다음 작업자 참고
- `Confined` 모드의 윈도우 영역 제한은 Silk.NET 업그레이드 또는 SDL2 P/Invoke로 별도 구현 필요
- LiveCode 스크립트에서 `Cursor.lockState = CursorLockMode.Locked` 테스트 필요
- Standalone 빌드에서도 동작 확인 필요
