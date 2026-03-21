# 설정 파일 위치를 ~/.ironrose/settings.toml로 변경 (Phase 1)

## 수행한 작업
- `ProjectContext.cs`의 마지막 프로젝트 경로 저장/읽기 로직을 CWD 기반 `.rose_last_project`에서 `~/.ironrose/settings.toml` TOML 기반으로 전환
- 하위 호환 마이그레이션: CWD의 `.rose_last_project`가 존재하면 `settings.toml`로 자동 마이그레이션 후 레거시 파일 삭제
- `SaveLastProjectPath()`를 `public static`으로 유지 (Phase 2에서 외부 호출 필요)

## 변경된 파일
- `src/IronRose.Engine/ProjectContext.cs`
  - `GlobalSettingsDir` 프로퍼티 추가 (`~/.ironrose/`)
  - `GlobalSettingsPath` 프로퍼티 추가 (`~/.ironrose/settings.toml`)
  - `LEGACY_LAST_PROJECT_FILE` 상수 추가 (기존 `LastProjectFileName` 대체, 마이그레이션용)
  - `ReadLastProjectPath()` 재작성: settings.toml에서 `[editor].last_project` 읽기 + 레거시 마이그레이션
  - `SaveLastProjectPath()` 재작성: settings.toml에 TOML 형식으로 저장
  - frontmatter 갱신: `SaveLastProjectPath` export 추가, 하위 호환 노트 추가

## 주요 결정 사항
- `ReadLastProjectPath()`에서 settings.toml 파싱 실패 시 경고 로그 출력 후 레거시 마이그레이션으로 폴스루 (기존에는 전체 try-catch로 묵음)
- 레거시 마이그레이션의 외부 try-catch 내 파일 읽기 실패는 빈 catch로 무시 (기존 패턴과 동일)
- `SaveLastProjectPath()`에서 경로를 forward slash로 정규화 (`Replace("\\", "/")`) -- TOML 파일 내 크로스 플랫폼 호환

## 다음 작업자 참고
- Phase 2 (ImGuiStartupPanel): `SaveLastProjectPath()`가 `public static`으로 되어 있으므로 외부에서 호출 가능
- Phase 4 (설정 파일 통합): `settings.toml`에 향후 `[editor]` 섹션 외에 다른 글로벌 설정을 추가할 수 있음. 현재는 `SaveLastProjectPath()`가 파일을 통째로 덮어쓰므로, 다른 키가 추가되면 기존 TOML 모델을 읽고 수정하는 방식으로 변경 필요
