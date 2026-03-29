# Phase 47c: EngineCore 통합 -- Play mode 콜백 연결 및 최종 정리

## 목표
- EditorPlayMode에 `OnBeforeEnterPlayMode` 콜백 추가
- EngineCore의 `InitScripts()`���서 Play mode 콜백을 ScriptReloadManager에 연결
- CliCommandDispatcher의 assembly.info 응답 최종 정리
- 모든 LiveCode/FrozenCode 참조가 완전히 제거되었는지 확인

## 선행 조건
- Phase 47b 완료 (ScriptReloadManager.cs 생성, EngineCore 기본 변경 완료)
- `ScriptReloadManager`에 `OnEnterPlayMode()`, `OnExitPlayMode()` 메서드가 존재

## 수정할 파일

### `src/IronRose.Engine/Editor/EditorPlayMode.cs`

#### 변경 1: OnBeforeEnterPlayMode 콜백 추가
- **추가 위치**: 기존 `OnAfterStopPlayMode` 선언 근처 (50행 부근)
- **추가 내용**:
```csharp
/// <summary>
/// Play 모드 진입 직전 호출되는 콜백.
/// ScriptReloadManager가 FileSystemWatcher를 중단하는 데 사용��니다.
/// </summary>
public static Action? OnBeforeEnterPlayMode;
```

#### 변경 2: EnterPlayMode()에서 콜백 호출
- `EnterPlayMode()` 메서드에서 State 체크 직후에 콜백 호출 추가:
- **변경 전** (54~58행):
```csharp
public static void EnterPlayMode()
{
    if (State != PlayModeState.Edit) return;

    var scene = SceneManager.GetActiveScene();
```
- **변경 후**:
```csharp
public static void EnterPlayMode()
{
    if (State != PlayModeState.Edit) return;

    // Play mode 진입 전 콜백 (예: FileSystemWatcher 중단)
    OnBeforeEnterPlayMode?.Invoke();

    var scene = SceneManager.GetActiveScene();
```

### `src/IronRose.Engine/EngineCore.cs`

#### 변경: InitScripts()에 Play mode 콜백 연결 추가
- Phase 47b에서 작성한 `InitScripts()`에 콜백 등록을 추가:
- **변경 전** (Phase 47b에�� 작성된 상태):
```csharp
private void InitScripts()
{
    _scriptReloadManager = new ScriptReloadManager();
    _scriptReloadManager.Initialize();
    _staticScriptReloadManager = _scriptReloadManager;
    if (_pendingOnAfterReload != null)
    {
        _scriptReloadManager.OnAfterReload = _pendingOnAfterReload;
        _pendingOnAfterReload = null;
    }
}
```
- **변경 후**:
```csharp
private void InitScripts()
{
    _scriptReloadManager = new ScriptReloadManager();
    _scriptReloadManager.Initialize();
    _staticScriptReloadManager = _scriptReloadManager;
    if (_pendingOnAfterReload != null)
    {
        _scriptReloadManager.OnAfterReload = _pendingOnAfterReload;
        _pendingOnAfterReload = null;
    }

    // Play mode 진입/종료 시 ScriptReloadManager 콜백 연결
    var mgr = _scriptReloadManager;
    Editor.EditorPlayMode.OnBeforeEnterPlayMode += () => mgr.OnEnterPlayMode();
    Editor.EditorPlayMode.OnAfterStopPlayMode += () => mgr.OnExitPlayMode();
}
```

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`

#### 변경: assembly.info 핸들러 최종 정리
- Phase 47b에서 `EngineCore.ScriptDemoTypes`로 변경되었으나, 주변 주석과 변수명도 최종 정리:
- **확인 사항** (Phase 47b에서 이미 변경되었어야 함):
  - `// Scripts 데모 타입 (ScriptReloadManager에서 등록된 것)` 주석
  - `var scriptTypes = EngineCore.ScriptDemoTypes`
  - JSON 응답의 `scriptDemoTypes`, `scriptDemoCount`
- Phase 47b에서 이미 수행되었으면 변경 불필요. 빌드 확인만 수행.

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `EditorPlayMode.cs`에 `OnBeforeEnterPlayMode` 콜백이 존재
- [ ] `EngineCore.cs`의 `InitScripts()`���서 `OnBeforeEnterPlayMode`과 `OnAfterStopPlayMode` 콜백을 등록
- [ ] grep으로 `src/` 하위에서 `LiveCodeManager` 타입 참조가 없음 확인
- [ ] grep으로 `src/` 하위에서 `LiveCodeDemoTypes` 참조가 없음 확인
- [ ] grep으로 `src/` 하위에서 `InitFrozenCode` 참조가 없음 확인
- [ ] grep으로 `src/` 하위에서 `InitLiveCode` 참조가 없음 확인

## 참고
- `OnBeforeEnterPlayMode`은 `EnterPlayMode()` 내에서 State가 `Edit`인지 확인한 직후, 씬 스냅샷 저장 전에 호출된다. 이 시점에서 FileSystemWatcher를 중단해야 Play mode 중 불필요한 파일 변경 이벤트가 발생하지 않는다.
- `OnAfterStopPlayMode`에 `OnExitPlayMode()`를 연결하면, Play mode 종��� 시 씬 복원이 완료된 후 FileSystemWatcher가 재활성화되고 변경 감지가 수행된다.
- 이 Phase는 비교적 작은 변경이다. Phase 47b에서 EngineCore ���경의 대부분이 수행되었으므로, 여기서는 콜백 연결만 추가한다.
