# Console View 빌드 에러 더블클릭 시 잘못된 파일로 이동하는 문제 수정

## 유저 보고 내용
- Console View에서 빌드(컴파일) 에러를 더블클릭하면, 실제 에러가 발생한 소스 파일(LiveCode 등)이 아니라 `EditorDebug.LogBuildError`를 호출한 엔진 코드(`ScriptCompiler.cs`, `LiveCodeManager.cs`)로 이동함

## 원인
- `EditorDebug.Write` 메서드에서 `StackTrace`를 캡처하여 `StackTraceHelper.ResolveCallerFrame`으로 호출자 위치를 추출
- 빌드 에러의 경우, 호출자는 `ScriptCompiler.cs`나 `LiveCodeManager.cs`이므로 `CallerFilePath`/`CallerLine`이 엔진 코드 위치를 가리킴
- Roslyn `Diagnostic.Location`에는 실제 에러 발생 소스 파일 경로와 줄 번호가 포함되어 있지만, 이 정보가 `LogEntry`에 전달되지 않았음

## 수정 내용
1. **`EditorDebug.LogBuildError`에 `sourceFile`/`sourceLine` 파라미터 추가**: 빌드 에러 로그 시 실제 에러 발생 소스 파일 정보를 명시적으로 전달할 수 있도록 함
2. **`Write` 메서드에서 빌드 에러 + 소스 파일 정보가 있으면 해당 정보를 `CallerFilePath`/`CallerLine`에 사용**: 기존 StackTrace 기반 호출자 추출 대신 명시적으로 전달된 소스 파일 정보를 우선 사용
3. **`CompilationError` 구조체 도입**: `CompilationResult.Errors`를 `List<string>`에서 `List<CompilationError>`로 변경. `CompilationError`는 에러 메시지(`Message`), 소스 파일 경로(`FilePath`), 줄 번호(`Line`)를 포함
4. **`ScriptCompiler.CompileFromSyntaxTrees`에서 Diagnostic 파일/줄 추출**: `d.Location.GetMappedLineSpan()`으로 실제 에러 소스 파일 경로와 줄 번호를 추출하여 `CompilationError`에 저장하고 `LogBuildError`에 전달
5. **`LiveCodeManager`와 `EngineCore` FrozenCode 에러에도 소스 파일 정보 전달**: `CompilationError`의 `FilePath`/`Line`을 활용

## 변경된 파일
- `src/IronRose.Contracts/EditorDebug.cs` -- `LogBuildError` 메서드에 `sourceFile`/`sourceLine` 옵션 파라미터 추가, `Write` 메서드에서 빌드 에러 시 소스 파일 정보 우선 사용
- `src/IronRose.Scripting/ScriptCompiler.cs` -- `CompilationError` 구조체 추가, `CompilationResult.Errors` 타입을 `List<CompilationError>`로 변경, Roslyn Diagnostic에서 파일/줄 추출
- `src/IronRose.Engine/LiveCodeManager.cs` -- `CompilationError`의 파일/줄 정보를 `LogBuildError`에 전달
- `src/IronRose.Engine/EngineCore.cs` -- FrozenCode 컴파일 에러도 `LogBuildError`로 변경하고 소스 파일 정보 전달

## 검증
- `dotnet build` 성공 확인 (0 Error, 기존 경고만 존재)
- 런타임 검증은 유저 확인 필요: LiveCode에 의도적 컴파일 에러를 넣고, Console View에서 해당 에러를 더블클릭하여 올바른 소스 파일로 이동하는지 확인
