# ProjectContext Initialize 로직 단순화 - settings.toml 전용으로 변경

## 유저 보고 내용
- `ProjectContext.Initialize()`에서 CWD 탐색, 상위 디렉토리 탐색 로직을 제거하고, 항상 `~/.ironrose/settings.toml`의 `last_project` 경로에서 project.toml을 읽도록 통일 요청

## 원인
- 기존 Initialize()는 3단계 폴백 체인을 가지고 있었다:
  1. CWD에서 project.toml 탐색 (FindProjectRoot)
  2. AppBaseDir에서 project.toml 탐색 (FindProjectRoot)
  3. 못 찾으면 settings.toml의 last_project에서 재귀 호출
- 이 복잡한 탐색 로직이 불필요해져서 단순화가 필요했다.

## 수정 내용
- `Initialize()` 메서드의 ProjectRoot 결정 로직을 단순화:
  - `projectRoot` 인자 -> `ReadLastProjectPath()` -> CWD 폴백
  - CWD/AppBaseDir에서 상위 탐색하는 기존 로직 제거
  - else 분기에서 재귀 호출하던 로직 제거 (ReadLastProjectPath가 이미 최초 단계에서 호출됨)
- `FindProjectRoot(string startDir)` private 메서드 완전 제거
- 파일 헤더 주석 및 시스템 문서 업데이트

## 변경된 파일
- `src/IronRose.Engine/ProjectContext.cs` -- Initialize() 로직 단순화, FindProjectRoot() 제거, 헤더 주석 업데이트
- `making_log/_system-project-context.md` -- 초기화 흐름 설명 업데이트, 재귀 호출 관련 주의사항 갱신

## 검증
- `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 빌드 성공 (에러 0, 기존 워닝만 존재)
- 정적 분석으로 검증: FindProjectRoot는 Initialize 내부에서만 호출되어 제거 안전, ReadLastProjectPath의 반환값 흐름이 기존 else 분기 동작과 동일
