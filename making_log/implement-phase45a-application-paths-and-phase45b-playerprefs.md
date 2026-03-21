# Phase 45a/45b: Application 경로 확장 및 PlayerPrefs 구현

## 수행한 작업

### Phase 45a: Application 클래스 확장 및 ProjectContext company 필드 읽기
- `Application.cs`에 `companyName`, `productName`, `persistentDataPath`, `dataPath` 프로퍼티 추가
- `Application.InitializePaths()` internal 메서드 추가 (크로스 플랫폼 경로 결정)
- `ProjectContext.Initialize()`에서 `project.toml`의 `[project] company` 필드를 읽어 `Application.companyName` 설정
- `EngineCore.InitApplication()`에서 `Application.InitializePaths()` 호출 추가
- `templates/default/project.toml`에 `company` 필드를 주석 예시로 추가

### Phase 45b: PlayerPrefs 클래스 구현 및 EngineCore 통합
- `PlayerPrefs.cs` 신규 생성: Unity 호환 PlayerPrefs API (SetInt/GetInt/SetFloat/GetFloat/SetString/GetString/HasKey/DeleteKey/DeleteAll/Save)
- TOML 포맷으로 `~/.ironrose/playerprefs/{ProjectName}.toml`에 저장/로드
- TomlConfig 래퍼 API만 사용 (Tomlyn 직접 사용 없음)
- lock 기반 스레드 안전성 보장
- SaveInternal() private 메서드로 Save/Shutdown 간 lock 재진입 문제 해결
- `EngineCore.InitApplication()`에 `PlayerPrefs.Initialize()` 호출 추가
- `EngineCore.Shutdown()`에 `PlayerPrefs.Shutdown()` 호출 추가 (더티 상태 자동 Save)

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/Application.cs` -- companyName, productName, persistentDataPath, dataPath 프로퍼티 및 InitializePaths() 메서드 추가. frontmatter 추가.
- `src/IronRose.Engine/ProjectContext.cs` -- Initialize()에서 [project] company 필드 읽기 추가. frontmatter 갱신.
- `src/IronRose.Engine/EngineCore.cs` -- InitApplication()에 경로 초기화 및 PlayerPrefs.Initialize() 추가, Shutdown()에 PlayerPrefs.Shutdown() 추가. frontmatter 갱신.
- `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` -- 신규 생성. Unity 호환 PlayerPrefs 정적 클래스.
- `templates/default/project.toml` -- [project] 섹션에 company 필드 주석 추가.

## 주요 결정 사항
- PlayerPrefs.cs는 사전에 구현 완료. Application.cs, ProjectContext.cs, EngineCore.cs, project.toml 4개 파일만 수정하여 통합.
- `Save()` / `Shutdown()`의 lock 재진입 문제를 `SaveInternal()` private 메서드 분리로 해결 (명세서 권장 방식).
- TOML에 int는 `(long)`, float는 `(double)` 캐스트하여 저장 (Tomlyn 내부 타입 호환).
- 타입 변환 규칙은 Unity 호환: Int<->Float 간 변환 허용, String은 다른 타입으로 변환 불가.
- Application.cs에 frontmatter를 새로 추가하고, ProjectContext.cs와 EngineCore.cs의 frontmatter를 갱신함.

## 다음 작업자 참고
- PlayerPrefs 파일 경로는 `ProjectContext.ProjectName` 기반이므로, 프로젝트 이름이 없으면 "Default"로 폴백됨.
- 키에 TOML bare key 불가 문자가 포함되어도 Tomlyn이 quoted key로 자동 처리하므로 별도 이스케이프 불필요.
- 현재 PlayerPrefs는 에디터와 런타임이 같은 파일을 공유. Standalone 빌드에서도 동일 경로 사용.
