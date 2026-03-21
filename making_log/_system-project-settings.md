# ProjectSettings 시스템

## 구조
- `src/IronRose.Engine/ProjectSettings.cs` — 정적 클래스. 프로젝트별 설정(rose_projectSettings.toml)의 단일 진입점.
  - [renderer]: 활성 렌더러 프로파일 GUID
  - [build]: 시작 씬 경로
  - [editor]: 외부 스크립트 에디터 경로
  - [cache]: 캐시 사용 여부, 텍스처 압축 여부, 캐시 강제 삭제
- `src/IronRose.Engine/RoseConfig.cs` — 호환성 래퍼. 캐시 프로퍼티를 ProjectSettings에 위임.
  - EnableEditor만 독립적으로 rose_config.toml에서 읽음

## 핵심 동작

### 설정 파일 경로
- `{ProjectContext.ProjectRoot}/rose_projectSettings.toml`
- 프로젝트 루트에 위치하며 버전 관리 대상

### Load() 흐름
1. ForceClearCache의 현재 값을 저장 (프로그래밍 오버라이드 보존)
2. rose_projectSettings.toml 파싱: [renderer], [build], [editor], [cache] 섹션
3. 프로그래밍 방식으로 설정된 ForceClearCache 복원
4. EditorState 레거시 마이그레이션 (ActiveRendererProfileGuid)

### Save() 흐름
- 모든 섹션을 TOML 문자열로 직렬화하여 파일 덮어쓰기

### RoseConfig 호환성
- `RoseConfig.DontUseCache` -> `ProjectSettings.DontUseCache`
- `RoseConfig.DontUseCompressTexture` -> `ProjectSettings.DontUseCompressTexture`
- `RoseConfig.ForceClearCache` -> `ProjectSettings.ForceClearCache`
- `RoseConfig.EnableForceClearCache()` -> `ProjectSettings.ForceClearCache = true`
- AssetDatabase, RoseCache, EngineCore 등에서 `RoseConfig.*`로 접근하지만 실제 데이터는 ProjectSettings에 저장

## 주의사항
- **ForceClearCache 프로그래밍 오버라이드**: Reimport All 시 Program.cs에서 `RoseConfig.EnableForceClearCache()`를 ProjectSettings.Load() 이전에 호출함. Load()에서 이 값이 덮어쓰이지 않도록 보존 로직이 있음.
- **초기화 순서**: EngineCore.Initialize()에서 RoseConfig.Load() -> ProjectSettings.Load() 순서. RoseConfig.Load()는 [editor] 섹션만 읽고, ProjectSettings.Load()가 [cache] 포함 전체를 읽음.
- **rose_config.toml은 아직 사용 중**: EnableEditor 설정을 위해 RoseConfig.Load()에서 여전히 읽음. [cache] 섹션만 rose_projectSettings.toml로 이관된 상태.
- **ActiveRendererProfileGuid 자동 수정**: `EngineCore.EnsureDefaultRendererProfile()`에서 저장된 GUID로 프로파일 로드가 실패하면, Default.renderer를 fallback으로 로드하고 그 GUID를 ProjectSettings에 자동 저장한다. 템플릿의 `active_profile_guid`는 빈 문자열이어야 한다 (하드코딩된 GUID 금지).

## 사용하는 외부 라이브러리
- Tomlyn: TOML 파싱 (`Toml.ToModel()`, `TomlTable`)
