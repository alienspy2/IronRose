# rose_config.toml [cache] 섹션을 rose_projectSettings.toml로 통합

## 수행한 작업
- `rose_config.toml`의 `[cache]` 섹션(DontUseCache, DontUseCompressTexture, ForceClearCache)을 `rose_projectSettings.toml`로 통합
- `ProjectSettings.cs`에 캐시 관련 프로퍼티와 TOML 읽기/쓰기 추가
- `RoseConfig.cs`의 캐시 프로퍼티를 `ProjectSettings`에 위임하는 래퍼로 변경
- `ProjectCreator.cs`의 최소 프로젝트 생성 시 통합된 `rose_projectSettings.toml` 생성
- `templates/default/rose_projectSettings.toml` 템플릿 파일 생성

## 변경된 파일
- `src/IronRose.Engine/ProjectSettings.cs` — DontUseCache, DontUseCompressTexture, ForceClearCache 프로퍼티 추가. Load()에 [cache] 섹션 파싱 추가. Save()에 [cache] 섹션 직렬화 추가. Load() 이전에 프로그래밍 방식으로 설정된 ForceClearCache 값 보존 로직 추가.
- `src/IronRose.Engine/RoseConfig.cs` — 캐시 프로퍼티를 ProjectSettings에 위임하는 래퍼로 변경. Load()에서 [cache] 섹션 읽기 제거 (EnableEditor만 읽음). frontmatter 추가.
- `src/IronRose.Engine/Editor/ProjectCreator.cs` — CreateMinimalProject()에서 rose_config.toml 생성 제거, 통합된 rose_projectSettings.toml 생성으로 변경.
- `templates/default/rose_projectSettings.toml` — 신규 생성. [renderer], [build], [cache] 섹션 포함.

## 주요 결정 사항
- **RoseConfig를 삭제하지 않고 래퍼로 유지**: `RoseConfig.DontUseCache`, `RoseConfig.ForceClearCache` 등을 참조하는 코드가 AssetDatabase.cs, RoseCache.cs, EngineCore.cs, Program.cs 등 다수 존재. 호환성을 위해 래퍼로 유지하여 기존 호출 코드를 변경하지 않음.
- **ForceClearCache 프로그래밍 오버라이드 보존**: Program.cs에서 Reimport All 시 `RoseConfig.EnableForceClearCache()`를 `ProjectSettings.Load()` 이전에 호출함. Load()가 파일 값으로 덮어쓰는 것을 방지하기 위해, Load() 시작 시점의 ForceClearCache 값을 저장하고 로드 후 복원하는 로직 추가.
- **RoseConfig.EnableEditor는 RoseConfig에 잔류**: 이 값은 프로젝트 설정이 아닌 엔진 설정이므로 rose_config.toml의 [editor] 섹션에서 계속 읽음.

## 다음 작업자 참고
- 엔진 루트의 `rose_config.toml` 파일 자체는 아직 삭제하지 않았음. EnableEditor 설정을 위해 여전히 필요. 향후 EnableEditor도 다른 곳으로 이전하면 파일 삭제 가능.
- 기존 프로젝트에서 `rose_config.toml`에만 [cache] 설정이 있는 경우, `rose_projectSettings.toml`에 [cache] 섹션을 수동 추가해야 함. 자동 마이그레이션은 구현하지 않았음.
- `RoseConfig` 래퍼 클래스는 향후 모든 호출처를 `ProjectSettings`로 직접 변경한 뒤 삭제 가능.
