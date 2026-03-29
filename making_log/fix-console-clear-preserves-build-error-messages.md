# Console Clear 시 빌드 에러 메시지 유지

## 유저 보고 내용
- Console View에서 Clear 버튼을 클릭하면 빌드(스크립트 컴파일) 에러 메시지까지 모두 지워져서, 에러 내용을 다시 확인하려면 리컴파일해야 하는 불편함이 있음
- Clear 시 빌드 에러 메시지는 남겨두길 원함

## 원인
- `ImGuiConsolePanel.Draw()`에서 Clear 버튼 클릭 시 `_entries.Clear()`로 모든 로그를 무조건 삭제하고 있었음
- 빌드 에러와 일반 로그를 구분하는 메커니즘이 없었음

## 수정 내용
1. **`LogEntry` record에 `IsBuildError` 플래그 추가** (기본값 `false`)
   - 기존 호출 코드에 영향 없이 빌드 에러 여부를 표시할 수 있도록 함
2. **`EditorDebug.LogBuildError()` 메서드 추가**
   - 내부적으로 `Write("ERROR", message, isBuildError: true)` 호출
   - `Write` 메서드에 `isBuildError` 파라미터 추가하여 `LogEntry` 생성 시 플래그 전달
3. **`ScriptCompiler`와 `LiveCodeManager`의 컴파일 에러 로그를 `LogBuildError`로 변경**
   - 컴파일 실패 시 출력되는 에러 메시지들이 `IsBuildError = true`로 기록됨
4. **Console 패널 Clear 동작 수정**
   - `_entries.Clear()` 대신 `_entries.RemoveAll(e => !e.IsBuildError)`로 변경
   - 빌드 에러 항목만 남기고 나머지를 제거

## 변경된 파일
- `src/IronRose.Contracts/LogTypes.cs` -- `LogEntry` record에 `bool IsBuildError = false` 파라미터 추가
- `src/IronRose.Contracts/EditorDebug.cs` -- `LogBuildError()` 메서드 추가, `Write()` 메서드에 `isBuildError` 파라미터 추가
- `src/IronRose.Scripting/ScriptCompiler.cs` -- 컴파일 에러 로그를 `EditorDebug.LogBuildError`로 변경
- `src/IronRose.Engine/LiveCodeManager.cs` -- 컴파일 에러 로그를 `EditorDebug.LogBuildError`로 변경
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiConsolePanel.cs` -- Clear 시 `IsBuildError` 항목 유지하도록 변경

## 검증
- dotnet build 성공 확인 (새 에러/경고 없음)
- 유저 확인 필요: 에디터 실행 후 스크립트 컴파일 에러 발생시킨 뒤 Clear 버튼 클릭 시 빌드 에러만 남는지 테스트
