# ProjectContext 시스템

## 구조
- `src/IronRose.Engine/ProjectContext.cs` — 정적 클래스. 프로젝트 경로 관리의 단일 진입점.
  - 프로젝트 루트, 엔진 루트, 각종 서브 디렉토리 경로를 프로퍼티로 제공
  - `Initialize()`에서 project.toml 탐색 및 파싱
  - 글로벌 설정(last_project)을 `~/.ironrose/settings.toml`에 저장/읽기

## 핵심 동작

### 초기화 흐름 (Initialize)
1. 명시적 projectRoot가 주어지면 그것을 사용
2. 아니면 `~/.ironrose/settings.toml`의 `[editor] last_project` 경로를 사용
3. 그것도 없으면 CWD를 폴백으로 사용
4. 결정된 ProjectRoot에 project.toml이 있으면: engine.path 읽기, IsProjectLoaded = true, BuildProps 검증
5. project.toml이 없으면: IsProjectLoaded = false, EngineRoot = ProjectRoot (엔진 레포 직접 실행 케이스)

> **CWD/상위 디렉토리 탐색 로직은 제거됨**. FindProjectRoot 메서드가 삭제되어 더 이상 파일시스템을 올라가며 project.toml을 찾지 않는다.

### 글로벌 설정 파일
- 위치: `~/.ironrose/settings.toml`
- 형식: `[editor]\nlast_project = "/absolute/path/to/project"`
- `ReadLastProjectPath()`: settings.toml 파싱 -> 레거시 `.rose_last_project` 마이그레이션 폴스루
- `SaveLastProjectPath()`: settings.toml에 TOML 형식으로 저장 (파일 전체 덮어쓰기)

### 의존 관계
- `IronRose.Engine.TomlConfig` — TOML 파일 읽기/쓰기 래퍼
- `RoseEngine.Debug` — 로그 출력
- `System.Xml.Linq` — Directory.Build.props 파싱 (ValidateBuildPropsAlignment)

## 주의사항
- `SaveLastProjectPath()`는 `TomlConfig` read-modify-write 패턴으로 기존 settings.toml의 다른 섹션을 보존한다.
- `Initialize()`는 더 이상 재귀 호출하지 않는다. ProjectRoot 결정이 단일 경로로 단순화되었다.
- 레거시 `.rose_last_project` 마이그레이션: CWD에 파일이 있으면 settings.toml로 이전 후 삭제. 마이그레이션 실패 시 무시.
- **로그 경로 전환**: EngineCore.Initialize()에서 ProjectContext 초기화 직후 `Debug.SetLogDirectory(ProjectRoot/Logs/)`를 호출하여 로그를 프로젝트 폴더로 전환. 프로젝트 .gitignore에 `Logs/` 포함 필요.
- **프로세스 = 프로젝트 모델**: mid-session 프로젝트 전환은 지원하지 않음. 프로젝트 전환은 프로세스 재시작을 통해서만 가능.

## 사용하는 외부 라이브러리
- TomlConfig (내부): TOML 파일 로드/저장/섹션 접근 (`LoadFile`, `CreateEmpty`, `GetSection`, `SetSection`, `SetValue`, `SaveToFile`)
