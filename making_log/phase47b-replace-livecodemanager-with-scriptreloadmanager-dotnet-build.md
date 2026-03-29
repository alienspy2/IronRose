# Phase 47b: LiveCodeManager를 ScriptReloadManager로 교체 (dotnet build 방식 전환)

## 수행한 작업
- `LiveCodeManager.cs` 삭제, `ScriptReloadManager.cs` 새로 생성
- Roslyn 인메모리 컴파일(`ScriptCompiler`)을 `dotnet build` + DLL 파일 읽기 방식으로 전환
- Play mode 중 FileSystemWatcher 완전 중단 / 종료 시 변경 감지 후 일괄 빌드/리로드 메서드 추가 (`OnEnterPlayMode`, `OnExitPlayMode`)
- `FindLiveCodeDirectories()` 삭제, `ProjectContext.ScriptsPath` 단일 경로만 사용
- `FlushPendingReload()`, `HasPendingReload`, `_pendingReloadAfterPlayStop` 제거
- `ProcessReload()`에서 play mode 체크 로직 제거
- `EngineCore.cs`에서 `InitFrozenCode()` + `InitLiveCode()` -> `InitScripts()` 통합
- `EngineCore.cs`에서 `LiveCodeManager` -> `ScriptReloadManager`, `LiveCodeDemoTypes` -> `ScriptDemoTypes` 변경
- `CliCommandDispatcher.cs`에서 `EngineCore.LiveCodeDemoTypes` -> `EngineCore.ScriptDemoTypes` 변경

## 변경된 파일
- `src/IronRose.Engine/LiveCodeManager.cs` -- 삭제
- `src/IronRose.Engine/ScriptReloadManager.cs` -- 신규 생성 (LiveCodeManager 대체)
- `src/IronRose.Engine/EngineCore.cs` -- LiveCodeManager -> ScriptReloadManager 참조 변경, InitFrozenCode/InitLiveCode -> InitScripts 통합
- `src/IronRose.Engine/Cli/CliCommandDispatcher.cs` -- LiveCodeDemoTypes -> ScriptDemoTypes

## 주요 결정 사항
- `BuildScripts()` 메서드는 `dotnet build`를 동기적으로 실행한다. `ProcessStartInfo`에서 stdout/stderr를 리다이렉트하여 빌드 오류를 에디터 콘솔에 표시.
- DLL은 `Scripts/bin/Debug/net10.0/Scripts.dll` 경로에서 `File.ReadAllBytes`로 읽어 `ScriptDomain.LoadScripts`/`Reload`에 전달.
- Play mode 콜백 연결(`OnEnterPlayMode`/`OnExitPlayMode`)은 Phase 47c에서 수행 예정. 이 Phase에서는 메서드만 준비.
- `EditorPlayMode.OnAfterStopPlayMode`에 대한 `FlushPendingReload()` 등록 제거 완료.
- `using IronRose.Rendering` 제거 (PostProcessStack 참조가 ScriptCompiler 제거와 함께 불필요해짐).

## 다음 작업자 참고
- Phase 47c에서 `EditorPlayMode`에 `ScriptReloadManager.OnEnterPlayMode()`/`OnExitPlayMode()` 콜백을 연결해야 한다.
- 현재 `OnEnterPlayMode()`/`OnExitPlayMode()` 메서드는 존재하지만 아무 곳에서도 호출되지 않는 상태이다.
- `HasSourceChangedSinceBuild()`는 DLL의 LastWriteTime과 .cs 파일들의 LastWriteTime을 비교하는 간단한 구현이다. obj/bin 디렉토리는 제외한다.
- `SaveHotReloadableState()`와 `RestoreHotReloadableState()`는 기존 코드 그대로 유지되었으나, 현재 `ExecuteReload()`에서 호출되지 않는다. 필요시 후속 Phase에서 활성화.
