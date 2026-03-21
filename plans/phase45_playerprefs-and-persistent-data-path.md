# Phase 45: PlayerPrefs 및 Application.persistentDataPath 구현

## 배경
- IronRose 엔진은 Unity 호환 API를 `RoseEngine` 네임스페이스에서 제공하고 있으나, `PlayerPrefs`와 `Application.persistentDataPath`가 아직 구현되어 있지 않다.
- 게임 개발 시 사용자 설정, 진행 상태 등을 간단하게 저장/로드하는 기능이 필요하다.
- Unity의 PlayerPrefs API와 동일한 인터페이스를 제공하되, 저장 포맷은 TOML을 사용한다.

## 목표
1. Unity 호환 `PlayerPrefs` 정적 클래스를 `RoseEngine` 네임스페이스에 구현한다.
2. `Application.persistentDataPath` 프로퍼티를 크로스 플랫폼(Windows/Linux)으로 구현한다.
3. 에디터와 런타임이 같은 PlayerPrefs 파일을 공유한다 (Unity 방식).
4. TOML 포맷으로 사용자 홈 디렉토리에 저장하여 버전 관리에서 제외한다.

## 현재 상태

### Application 클래스 (`src/IronRose.Engine/RoseEngine/Application.cs`)
- `RoseEngine` 네임스페이스의 정적 클래스.
- `isPlaying`, `isPaused`, `platform`, `targetFrameRate`, `Quit()`, `Pause()`, `Resume()` 제공.
- `persistentDataPath`, `dataPath`, `companyName`, `productName` 등은 미구현.

### 기존 TOML 사용 패턴
- **Tomlyn 0.20.0** 패키지 사용 (`IronRose.Engine.csproj`에 이미 포함).
- `Toml.ToModel()` / `Toml.FromModel()`로 읽기/쓰기.
- `TomlTable`을 직접 조작하는 패턴이 표준 (EditorState, ProjectContext, ProjectSettings 등).
- 파일 저장 시 문자열 직접 조합 방식 사용 (가독성 좋은 출력을 위해).

### 글로벌 설정 디렉토리
- `~/.ironrose/` 디렉토리가 이미 사용되고 있음 (`settings.toml` 저장).
- `ProjectContext.GlobalSettingsDir`로 경로 접근 (단, private).

### ProjectContext
- `ProjectContext.ProjectName` : 프로젝트 이름 (`project.toml [project] name`).
- `ProjectContext.IsProjectLoaded` : 프로젝트 로드 여부.
- 프로젝트별 설정 구분에 `ProjectName`을 사용할 수 있음.

### EngineCore 초기화 흐름
```
Initialize() ->
  ProjectContext.Initialize()     // ProjectName 확정
  RoseConfig.Load()
  ProjectSettings.Load()
  EditorState.Load()
  InitApplication()               // Application 속성 설정
  ...
```

## 설계

### 개요

1. **PlayerPrefs** : `RoseEngine` 네임스페이스에 새 정적 클래스 추가. 내부적으로 `Dictionary<string, object>`에 값을 캐시하고, `Save()` 호출 시 TOML 파일에 기록한다.
2. **Application 확장** : `persistentDataPath`, `dataPath`, `companyName`, `productName` 프로퍼티를 추가한다.
3. **저장 경로** : 사용자 홈 디렉토리 기반 크로스 플랫폼 경로.

### 상세 설계

#### 1. 저장 경로 규칙

| 플랫폼 | PlayerPrefs 경로 | persistentDataPath |
|--------|------------------|--------------------|
| Linux | `~/.ironrose/playerprefs/{ProjectName}.toml` | `~/.local/share/IronRose/{CompanyName}/{ProductName}` |
| Windows | `%APPDATA%/IronRose/playerprefs/{ProjectName}.toml` | `%APPDATA%/IronRose/{CompanyName}/{ProductName}` |

- Linux의 `persistentDataPath`는 XDG 규격을 따른다: `$XDG_DATA_HOME` 환경변수가 설정되어 있으면 그것을 사용하고, 없으면 `~/.local/share`를 기본값으로 사용한다.
- Windows에서는 `Environment.SpecialFolder.ApplicationData` (`%APPDATA%`)를 사용한다.
- PlayerPrefs 경로는 기존 `~/.ironrose/` 디렉토리 하위를 활용하여 일관성을 유지한다. Windows에서도 `%APPDATA%/IronRose/` 하위에 통일한다.

#### 2. PlayerPrefs 클래스

**파일**: `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs`
**네임스페이스**: `RoseEngine`

```csharp
// ------------------------------------------------------------
// @file    PlayerPrefs.cs
// @brief   Unity 호환 PlayerPrefs API. TOML 포맷으로 사용자 홈 디렉토리에 저장.
//          에디터와 런타임이 같은 파일을 공유한다.
// @deps    IronRose.Engine/ProjectContext, RoseEngine/Debug, Tomlyn
// @exports
//   static class PlayerPrefs
//     SetInt(string, int): void
//     GetInt(string, int): int
//     SetFloat(string, float): void
//     GetFloat(string, float): float
//     SetString(string, string): void
//     GetString(string, string): string
//     HasKey(string): bool
//     DeleteKey(string): void
//     DeleteAll(): void
//     Save(): void
// @note    값은 메모리에 캐시되며 Save() 호출 시 디스크에 기록된다.
//          앱 종료 시 자동으로 Save()가 호출된다.
//          스레드 안전성: lock으로 보호한다.
// ------------------------------------------------------------
namespace RoseEngine
{
    public static class PlayerPrefs
    {
        // 내부 저장소
        private static readonly Dictionary<string, object> _data = new();
        private static readonly object _lock = new();
        private static bool _dirty = false;
        private static bool _loaded = false;

        // TOML 섹션 이름
        private const string SECTION_INT = "int";
        private const string SECTION_FLOAT = "float";
        private const string SECTION_STRING = "string";

        // --- Set ---
        public static void SetInt(string key, int value);
        public static void SetFloat(string key, float value);
        public static void SetString(string key, string value);

        // --- Get (기본값 파라미터 포함) ---
        public static int GetInt(string key);                    // 기본값 0
        public static int GetInt(string key, int defaultValue);
        public static float GetFloat(string key);               // 기본값 0.0f
        public static float GetFloat(string key, float defaultValue);
        public static string GetString(string key);             // 기본값 ""
        public static string GetString(string key, string defaultValue);

        // --- 관리 ---
        public static bool HasKey(string key);
        public static void DeleteKey(string key);
        public static void DeleteAll();
        public static void Save();

        // --- 내부 ---
        internal static void Initialize();   // 파일에서 로드
        internal static void Shutdown();     // 더티 상태면 자동 Save
    }
}
```

**내부 저장 구조**:
- `_data` 딕셔너리에 `key -> (type, value)` 형태로 저장.
- 타입 구분을 위해 `PlayerPrefEntry` 내부 구조체 사용:
  ```csharp
  private enum PrefType { Int, Float, String }
  private readonly record struct PrefEntry(PrefType Type, object Value);
  ```
- `_data`의 타입: `Dictionary<string, PrefEntry>`

**TOML 파일 포맷 예시**:
```toml
[int]
score = 100
level = 5

[float]
volume = 0.8
sensitivity = 1.5

[string]
player_name = "Alice"
language = "ko"
```

**스레드 안전성**:
- 모든 public 메서드에서 `lock (_lock)` 사용.
- Unity와 동일하게 메인 스레드 제한은 두지 않지만, 동시 접근에 안전하게 설계.

**초기화/종료 흐름**:
- `Initialize()` : TOML 파일이 존재하면 로드하여 `_data`에 채움. 파일이 없으면 빈 상태로 시작.
- `Shutdown()` : `_dirty`가 true이면 `Save()` 호출.
- `Save()` : `_data`를 타입별 섹션으로 분류하여 TOML 파일에 기록. 디렉토리 없으면 생성.

**키 유효성**:
- 키에 `.`, `[`, `]`, `"`, `\` 문자가 포함되면 TOML 키로 사용 시 문제가 될 수 있으므로, 따옴표로 감싸는 TOML quoted key 문법을 사용한다.
- 빈 문자열 키는 `ArgumentException` 발생.

**타입 변환 규칙** (Unity와 동일):
- `SetInt`로 저장한 키를 `GetFloat`로 읽으면 int를 float로 변환하여 반환.
- `SetFloat`로 저장한 키를 `GetInt`로 읽으면 float를 int로 truncate하여 반환.
- `SetString`으로 저장한 키를 `GetInt`/`GetFloat`로 읽으면 기본값 반환.
- `SetInt`/`SetFloat`로 저장한 키를 `GetString`로 읽으면 기본값 반환.

#### 3. Application 클래스 확장

**파일**: `src/IronRose.Engine/RoseEngine/Application.cs`

추가할 프로퍼티:

```csharp
/// <summary>회사/조직 이름. project.toml [project] company에서 읽음. 기본값 "DefaultCompany".</summary>
public static string companyName { get; internal set; } = "DefaultCompany";

/// <summary>제품 이름. ProjectContext.ProjectName에서 설정.</summary>
public static string productName { get; internal set; } = "DefaultProduct";

/// <summary>
/// 영속 데이터 저장 경로. 크로스 플랫폼.
/// Linux: $XDG_DATA_HOME/IronRose/{companyName}/{productName}
///        (XDG_DATA_HOME 미설정 시 ~/.local/share)
/// Windows: %APPDATA%/IronRose/{companyName}/{productName}
/// </summary>
public static string persistentDataPath { get; internal set; } = "";

/// <summary>
/// 에셋 데이터 경로. ProjectContext.AssetsPath와 동일.
/// </summary>
public static string dataPath => IronRose.Engine.ProjectContext.AssetsPath;
```

**`persistentDataPath` 경로 결정 로직**:

```csharp
internal static void InitializePaths(string companyName, string productName)
{
    Application.companyName = companyName;
    Application.productName = productName;

    string basePath;
    if (OperatingSystem.IsWindows())
    {
        // %APPDATA%/IronRose/{companyName}/{productName}
        basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IronRose", companyName, productName);
    }
    else
    {
        // Linux/macOS: XDG_DATA_HOME 또는 ~/.local/share
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdgDataHome))
            xdgDataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        basePath = Path.Combine(xdgDataHome, "IronRose", companyName, productName);
    }

    persistentDataPath = basePath;
}
```

#### 4. PlayerPrefs 저장 경로 결정

`PlayerPrefs` 내부에서 사용하는 저장 경로:

```csharp
private static string GetPrefsFilePath()
{
    var projectName = IronRose.Engine.ProjectContext.ProjectName;
    if (string.IsNullOrEmpty(projectName))
        projectName = "Default";

    // 파일명에 사용할 수 없는 문자 제거
    var safeName = SanitizeFileName(projectName);

    string baseDir;
    if (OperatingSystem.IsWindows())
    {
        baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IronRose", "playerprefs");
    }
    else
    {
        baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ironrose", "playerprefs");
    }

    return Path.Combine(baseDir, safeName + ".toml");
}
```

- Linux: `~/.ironrose/playerprefs/{ProjectName}.toml` (기존 `~/.ironrose/` 디렉토리 활용)
- Windows: `%APPDATA%/IronRose/playerprefs/{ProjectName}.toml`

#### 5. project.toml 확장

`project.toml`의 `[project]` 섹션에 `company` 필드를 추가할 수 있도록 한다:

```toml
[project]
name = "MyGame"
version = "0.1.0"
company = "MyStudio"    # 선택사항, 기본값 "DefaultCompany"
```

`ProjectContext.Initialize()`에서 이 값을 읽어 `Application.companyName`에 설정한다.

#### 6. EngineCore 통합

**`EngineCore.InitApplication()`** 수정:

```csharp
private void InitApplication()
{
    Application.isPlaying = false;
    Application.isPaused = false;
    Application.QuitAction = () => _window!.Close();
    Application.PauseCallback = IronRose.Engine.Editor.EditorPlayMode.PausePlayMode;
    Application.ResumeCallback = IronRose.Engine.Editor.EditorPlayMode.ResumePlayMode;

    // 경로 초기화 (ProjectContext가 이미 초기화된 상태)
    var companyName = Application.companyName;  // ProjectContext.Initialize()에서 설정됨
    var productName = ProjectContext.ProjectName;
    if (string.IsNullOrEmpty(productName)) productName = "DefaultProduct";
    Application.InitializePaths(companyName, productName);

    // PlayerPrefs 초기화
    PlayerPrefs.Initialize();
}
```

**`EngineCore.Shutdown()`** 수정:

```csharp
public void Shutdown()
{
    RoseEngine.EditorDebug.Log("[Engine] EngineCore shutting down...");
    PlayerPrefs.Shutdown();  // 더티 상태면 자동 Save
    Application.isPlaying = false;
    // ... 기존 코드 ...
}
```

#### 7. ProjectContext 수정

**`ProjectContext.Initialize()`**에서 `company` 필드를 읽는 로직 추가:

```csharp
// [project] 섹션에서 company 읽기
if (projTable.TryGetValue("company", out var companyVal) && companyVal is string companyStr)
    Application.companyName = companyStr;
```

이 코드는 기존 `ProjectName` 읽기 바로 아래에 추가한다.

### 영향 범위

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/RoseEngine/PlayerPrefs.cs` | **신규** - PlayerPrefs 클래스 전체 구현 |
| `src/IronRose.Engine/RoseEngine/Application.cs` | **수정** - `persistentDataPath`, `dataPath`, `companyName`, `productName`, `InitializePaths()` 추가 |
| `src/IronRose.Engine/ProjectContext.cs` | **수정** - `[project] company` 필드 읽기 추가 |
| `src/IronRose.Engine/EngineCore.cs` | **수정** - `InitApplication()`에서 경로/PlayerPrefs 초기화, `Shutdown()`에서 PlayerPrefs 저장 |
| `templates/default/project.toml` | **수정** - `company` 필드 추가 (선택, 주석 처리) |

### 기존 기능에 미치는 영향
- `Application` 클래스에 프로퍼티만 추가하므로 기존 코드에 영향 없음.
- `ProjectContext.Initialize()`에서 `company` 읽기가 실패해도 기본값 유지, 기존 동작 변경 없음.
- `EngineCore.Shutdown()`에 `PlayerPrefs.Shutdown()` 호출이 추가되지만, 기존 종료 흐름에 영향 없음.

## 구현 단계

- [ ] 1단계: `Application.cs` 확장
  - `companyName`, `productName` 프로퍼티 추가
  - `persistentDataPath`, `dataPath` 프로퍼티 추가
  - `InitializePaths()` internal 메서드 추가 (크로스 플랫폼 경로 결정)

- [ ] 2단계: `ProjectContext.cs` 수정
  - `[project] company` 필드 읽기 추가
  - 읽은 값을 `Application.companyName`에 설정

- [ ] 3단계: `PlayerPrefs.cs` 신규 작성
  - `PrefEntry` 내부 타입 정의
  - `SetInt/GetInt`, `SetFloat/GetFloat`, `SetString/GetString` 구현
  - `HasKey`, `DeleteKey`, `DeleteAll` 구현
  - `Save()` 구현 (TOML 파일 쓰기)
  - `Initialize()` 구현 (TOML 파일 읽기)
  - `Shutdown()` 구현 (더티 시 자동 Save)
  - `lock` 기반 스레드 안전성 적용

- [ ] 4단계: `EngineCore.cs` 통합
  - `InitApplication()`에서 `Application.InitializePaths()` 및 `PlayerPrefs.Initialize()` 호출
  - `Shutdown()`에서 `PlayerPrefs.Shutdown()` 호출 (기존 코드 앞에)

- [ ] 5단계: 템플릿 업데이트
  - `templates/default/project.toml`에 `company` 필드 추가 (주석 예시)

- [ ] 6단계: 빌드 확인
  - `dotnet build`로 빌드 성공 확인
  - 워닝 정리

## 대안 검토

### 저장 포맷 대안
| 포맷 | 장점 | 단점 | 결정 |
|------|------|------|------|
| TOML | 엔진 전체에서 이미 사용, 가독성 좋음, Tomlyn 이미 의존 | 대용량 데이터에는 부적합 | **채택** |
| JSON | 범용, 빠른 파싱 | 추가 의존성, 주석 미지원, 엔진 컨벤션과 불일치 | 미채택 |
| Registry (Windows) | Unity 기본 방식 | 크로스 플랫폼 불가, 접근 어려움 | 미채택 |

### 저장 위치 대안
| 위치 | 장점 | 단점 | 결정 |
|------|------|------|------|
| 사용자 홈 디렉토리 | 버전 관리 제외, 사용자별 분리, 기존 ~/.ironrose/ 활용 | 프로젝트 디렉토리에서 직접 보이지 않음 | **채택** |
| 프로젝트 루트 | 프로젝트와 함께 이동 가능 | .gitignore 필요, 사용자별 설정 충돌 | 미채택 |

### persistentDataPath XDG 준수 여부
- Linux에서 XDG Base Directory Specification을 따르는 것이 표준적이다.
- `$XDG_DATA_HOME` (기본 `~/.local/share`) 하위에 저장하는 것이 올바른 접근이다.
- PlayerPrefs 경로는 기존 `~/.ironrose/` 컨벤션을 유지하여 엔진 설정 파일과 함께 관리한다 (PlayerPrefs는 엔진 내부 기능이므로).
- `persistentDataPath`는 사용자 게임 데이터용이므로 XDG 규격을 따른다.

## 미결 사항

없음. 모든 주요 결정사항이 사용자 답변을 통해 확정되었다.
