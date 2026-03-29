# Script Reload 시스템

## 구조
- `src/IronRose.Engine/ScriptReloadManager.cs` -- Scripts 핫 리로드 관리자
  - FileSystemWatcher로 Scripts 디렉토리의 .cs 파일 변경 감시
  - `dotnet build`로 Scripts.csproj 빌드 후 DLL을 byte[]로 읽어 ScriptDomain에 로드
  - 리로드 시 기존 컴포넌트를 새 어셈블리 타입으로 마이그레이션 (MigrateEditorComponents)
- `src/IronRose.Scripting/ScriptDomain.cs` -- AssemblyLoadContext 기반 스크립트 격리 도메인
  - LoadScripts(), Reload(), GetLoadedTypes() 등 제공
  - 이전 ALC 언로드 및 GC 수거 검증
- `src/IronRose.Engine/EngineCore.cs` -- ScriptReloadManager 소유 및 생명주기 관리
  - `InitScripts()`에서 ScriptReloadManager 초기화
  - `Update()`에서 `ProcessReload()` (디바운스), `UpdateScripts()` 호출
  - `Shutdown()`에서 `Dispose()` 호출
  - `ScriptDemoTypes` static 프로퍼티로 외부에 타입 목록 노출

## 핵심 동작

### 초기화 흐름
1. `ScriptReloadManager.Initialize()` 호출
2. Default ALC에 빌드 타임 Scripts.dll 중복 로드 감지 경고
3. ScriptDomain 생성 및 TypeFilter 설정 (MonoBehaviour 제외)
4. Scripts 디렉토리 존재 확인/생성
5. Scripts.csproj, Scripts.dll 경로 설정
6. FileSystemWatcher 설정 (*.cs, 서브디렉토리 포함)
7. `BuildScripts()` 초기 빌드

### 빌드 흐름 (BuildScripts)
1. `dotnet build Scripts.csproj --no-restore -c Debug -v q` 실행
2. 빌드 실패 시 에러 로그 출력 후 리턴
3. 빌드 성공 시 Scripts.dll + Scripts.pdb를 File.ReadAllBytes로 읽기
4. ScriptDomain.LoadScripts() 또는 Reload() 호출
5. RegisterScriptBehaviours()로 MonoBehaviour 타입 등록 및 에디터 캐시 무효화

### 핫 리로드 흐름
1. FileSystemWatcher가 .cs 파일 변경 감지 -> `_reloadRequested = true`, 타이머 리셋
2. `ProcessReload()`에서 0.5초 디바운스 후 `ExecuteReload()` 호출
3. `ExecuteReload()`: BuildScripts -> MigrateEditorComponents -> VerifyPreviousContextUnloaded

### Play Mode 연동 (Phase 47c에서 콜백 연결 예정)
- `OnEnterPlayMode()`: FileSystemWatcher 중단
- `OnExitPlayMode()`: FileSystemWatcher 재개, 변경 감지 시 일괄 빌드/리로드
- `HasSourceChangedSinceBuild()`: DLL 타임스탬프 vs .cs 파일 타임스탬프 비교

## 주의사항
- `dotnet build`는 동기적으로 실행된다 (UI 프리징 가능). 향후 비동기화 고려 가능.
- DLL 경로는 `Scripts/bin/Debug/net10.0/Scripts.dll`로 하드코딩. TFM이 변경되면 수정 필요.
- `--no-restore` 플래그를 사용하므로, NuGet 패키지 추가 시 별도로 `dotnet restore` 필요.
- `SaveHotReloadableState()`/`RestoreHotReloadableState()`는 존재하지만 현재 ExecuteReload에서 호출되지 않음.
- MigrateEditorComponents는 타입 이름(Name) 기반 매칭. 동일 이름 다른 네임스페이스 타입 충돌 가능.
- **프리팹 캐시 무효화 필수**: 핫 리로드 시 `AssetDatabase.InvalidateScriptPrefabCache()`를 호출하여 스크립트 컴포넌트를 포함하는 프리팹 캐시를 제거해야 함. 그렇지 않으면 `PrefabUtility.InstantiatePrefab()`가 이전 ALC 타입의 컴포넌트를 가진 캐시된 템플릿을 사용하여 stale 타입 문제 발생.

## 사용하는 외부 라이브러리
- System.Diagnostics.Process: dotnet build 실행
- System.IO.FileSystemWatcher: 파일 변경 감시
