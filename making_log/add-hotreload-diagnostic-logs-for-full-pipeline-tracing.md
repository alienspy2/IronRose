# 핫 리로드 파이프라인 전체에 진단 로그 추가

## 목적
핫 리로드가 실패할 때 원인을 빠르게 파악할 수 있도록, 파이프라인의 모든 단계에 상세한 진단 로그를 추가한다.
로그는 `EditorDebug.Log(..., force: true)`를 사용하여 `Verbose` 설정과 무관하게 항상 출력된다.

## 로그가 추가된 지점

### 1. `OnLiveCodeChanged()` (LiveCodeManager.cs)
- 파일명, 변경 타입, 디바운스 타이머 리셋 시각 -- `force: true`로 변경

### 2. `ProcessReload()` (LiveCodeManager.cs)
- 디바운스 대기 중 경과 시간 (Verbose 전용 -- 매 프레임 출력되므로)
- 디바운스 통과 시 경과 시간
- 플레이모드 체크 결과 및 보류 여부

### 3. `FlushPendingReload()` (LiveCodeManager.cs)
- 호출 시 pending 여부 로그
- 보류 리로드 실행 시작 로그

### 4. `ExecuteReload()` (LiveCodeManager.cs)
- 실행 시작/종료 마커 (`=== ExecuteReload START/END ===`)
- 전체 리로드 소요 시간(ms)

### 5. `Initialize()` (LiveCodeManager.cs)
- 참조 어셈블리 목록 (각 타입별 경로 포함)
- EntryAssembly 존재 여부

### 6. `CompileAllLiveCode()` (LiveCodeManager.cs)
- LiveCode 디렉토리 목록 및 존재 여부
- 발견된 .cs 파일 전체 목록 (파일 크기, 마지막 수정 시각)
- 파일이 0개일 때 경고 로그
- 컴파일 소요 시간, 성공 시 어셈블리/PDB 크기
- ScriptDomain.IsLoaded 상태 및 Reload/LoadScripts 분기
- ClearMethodCache() 호출 로그
- 실패 시 에러 목록

### 7. `CompileFromFiles()` (ScriptCompiler.cs)
- 각 파일 읽기 성공/실패 (파일 미존재, IOException)
- SyntaxTree 파싱 결과 (각 파일의 문자 수)
- 파싱 에러가 있으면 상세 출력
- 최종 syntax tree 수 및 스킵 통계

### 8. `CompileFromSyntaxTrees()` (ScriptCompiler.cs)
- 참조 어셈블리 전체 목록 (`r.Display`)
- 컴파일 Warning도 로그 출력 (기존에는 Error만 출력)
- 에러 메시지에 위치 정보(`d.Location`) 추가

### 9. `RegisterLiveCodeBehaviours()` (LiveCodeManager.cs)
- 로드된 전체 타입 목록 (FullName, isAbstract, isMonoBehaviour, baseType)
- MonoBehaviour로 등록된 타입 목록
- 에디터 캐시 무효화 완료 로그

### 10. `MigrateEditorComponents()` (LiveCodeManager.cs)
- 스킵 조건 로그 (scriptDomain null, demoTypes 0)
- newTypeMap 전체 목록 (타입명 -> AssemblyQualifiedName)
- 각 GO/컴포넌트의 마이그레이션 결정 (skip/migrate/failed)
- 최종 통계: 스캔된 GO 수, 컴포넌트 수, 마이그레이션/스킵/실패 수

### 11. ScriptDomain.cs 전체
- `LoadScripts()`: ALC 생성, 어셈블리 로드 성공/실패, ReflectionTypeLoadException 감지
- `Reload()`: 바이트 크기, 시작/완료 로그
- `UnloadPreviousContext()`: 언로드 대상 ALC 정보, 스크립트 인스턴스 수
- `VerifyPreviousContextUnloaded()`: GC 수거 결과 (성공/실패)
- ALC Resolving 핸들러: 참조 해결 시도 로그 (성공 시 found, 실패 시 NOT FOUND)

## 설계 원칙
- **`force: true`**: Verbose 설정과 무관하게 항상 에디터 로그 파일에 기록 (핫 리로드는 빈번하지 않으므로 성능 영향 없음)
- **예외: 디바운스 대기 중 로그** (`ProcessReload` 내 `elapsed < DEBOUNCE_SECONDS`): 매 프레임 출력되므로 `force: false` (Verbose 전용)
- **Warning/Error 로그**: `EditorDebug.LogWarning()`/`LogError()` 사용 -- 에디터 콘솔 패널에도 표시됨
- **기존 동작 변경 없음**: 순수 로그 추가만

## 변경된 파일
- `src/IronRose.Engine/LiveCodeManager.cs` -- 핫 리로드 파이프라인 전체에 진단 로그 추가
- `src/IronRose.Scripting/ScriptCompiler.cs` -- 컴파일 과정 진단 로그 추가 (파일 파싱, Warning 출력, 참조 목록)
- `src/IronRose.Scripting/ScriptDomain.cs` -- 어셈블리 로드/언로드/리로드 진단 로그 추가

## 검증
- `dotnet build` 성공 (0 Error, 기존 경고 8개만 존재)
- 기존 동작 변경 없음 -- 로그만 추가
