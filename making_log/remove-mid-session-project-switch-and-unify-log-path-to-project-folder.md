# Phase 3 + Phase 5: mid-session 전환 코드 제거 및 로그 경로 프로젝트 폴더 통일

## 수행한 작업

### Phase 3: mid-session 프로젝트 전환 코드 제거
- EngineCore에서 `_loadedProjectRoot` 필드 완전 제거
- `Initialize()`에서 `_loadedProjectRoot` 할당 코드 제거, 워밍업 시작 조건을 `ProjectContext.IsProjectLoaded`로 단순화
- `Update()`에서 프로젝트 전환 감지 블록(ProjectRoot != _loadedProjectRoot) 전체 삭제

### Phase 5: 로그 경로를 프로젝트 폴더로 통일
- Debug.cs에 `_logFileName` 필드 추가, `_logPath`에서 `readonly` 제거
- `SetLogDirectory(string logDir)` 정적 메서드 추가 (기존 로그 내용 복사 + 원본 삭제)
- EngineCore.Initialize()에서 ProjectContext.Initialize() 직후 로그 경로 전환 호출 추가
- templates/default/.gitignore에 `Logs/` 항목 추가

## 변경된 파일
- `src/IronRose.Engine/EngineCore.cs` — _loadedProjectRoot 필드/사용 제거, Update() mid-session 감지 블록 삭제, Initialize()에 로그 경로 전환 코드 추가, frontmatter 갱신
- `src/IronRose.Contracts/Debug.cs` — _logFileName 필드 추가, _logPath readonly 제거, SetLogDirectory() 메서드 추가, frontmatter 추가
- `templates/default/.gitignore` — Logs/ 항목 추가

## 주요 결정 사항
- 기존 Log/LogWarning/LogError/Write 메서드는 전혀 변경하지 않음 (지시 사항 준수)
- SetLogDirectory()에서 기존 로그 복사 실패 시 예외를 삼키고 새 파일부터 시작하는 방식 채택 (로그 복사 실패로 엔진 크래시 방지)
- _logPath 비교는 문자열 비교이므로 동일 경로의 다른 표현(예: 심볼릭 링크)은 구분하지 않음. 현재 사용 패턴에서는 문제 없음.

## 다음 작업자 참고
- Phase 4(설정 파일 통합 및 재배치)가 아직 미구현 상태
- `ImGuiStartupPanel`의 `LoadProject()` -> `SetProjectAndNotifyRestart()` 전환은 Phase 2에서 처리 (별도 작업)
- 각 패널의 `IsProjectLoaded` 가드는 의도적으로 유지 (설계 문서 Phase 3 명시)
