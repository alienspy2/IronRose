# Phase 45a: Application 클래스 확장 및 ProjectContext company 필드 읽기

## 목표
- `Application` 클래스에 `companyName`, `productName`, `persistentDataPath`, `dataPath` 프로퍼티를 추가한다.
- `Application.InitializePaths()` internal 메서드로 크로스 플랫폼 경로를 결정한다.
- `ProjectContext.Initialize()`에서 `project.toml`의 `[project] company` 필드를 읽어 `Application.companyName`에 설정한다.
- `EngineCore.InitApplication()`에서 경로 초기화를 호출한다.
- `templates/default/project.toml`에 `company` 필드를 추가한다.

## 선행 조건
- 없음 (기존 코드 위에 작업)

## 수정할 파일

### `src/IronRose.Engine/RoseEngine/Application.cs`
- **변경 내용**: 기존 정적 클래스에 프로퍼티 4개와 internal 메서드 1개를 추가한다.
- **이유**: Unity 호환 `Application.persistentDataPath`, `Application.dataPath`, `Application.companyName`, `Application.productName`을 제공하기 위함.

현재 파일 전체 내용:
```csharp
using System;

namespace RoseEngine
{
    public static class Application
    {
        internal static Action? QuitAction { get; set; }
        internal static Action? PauseCallback { get; set; }
        internal static Action? ResumeCallback { get; set; }

        public static bool isPlaying { get; internal set; } = false;
        public static bool isPaused { get; internal set; } = false;
        public static string platform => Environment.OSVersion.Platform.ToString();
        public static int targetFrameRate { get; set; } = -1;

        // ... Pause(), Resume(), Quit() 메서드 ...
    }
}
```

추가할 멤버 (기존 `targetFrameRate` 프로퍼티 아래, `Pause()` 메서드 위에 삽입):

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

추가할 internal 메서드 (`Quit()` 메서드 아래에 삽입):

```csharp
/// <summary>
/// persistentDataPath를 크로스 플랫폼으로 결정한다.
/// EngineCore.InitApplication()에서 호출된다.
/// </summary>
internal static void InitializePaths(string company, string product)
{
    companyName = company;
    productName = product;

    string basePath;
    if (OperatingSystem.IsWindows())
    {
        // %APPDATA%/IronRose/{companyName}/{productName}
        basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IronRose", company, product);
    }
    else
    {
        // Linux/macOS: XDG_DATA_HOME 또는 ~/.local/share
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdgDataHome))
            xdgDataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        basePath = Path.Combine(xdgDataHome, "IronRose", company, product);
    }

    persistentDataPath = basePath;
}
```

- **필요한 using 추가**: `using System.IO;` (기존에 `using System;`만 있음)
- **네이밍 규칙**: Unity 호환을 위해 프로퍼티는 camelCase (`companyName`, `productName`, `persistentDataPath`, `dataPath`)로 기존 `isPlaying`, `isPaused` 등과 동일한 규칙을 따른다.

### `src/IronRose.Engine/ProjectContext.cs`
- **변경 내용**: `Initialize()` 메서드 내부, `ProjectName`을 읽는 코드 바로 아래에 `company` 필드 읽기 로직 추가.
- **이유**: `project.toml`의 `[project] company` 필드를 읽어 `Application.companyName`에 설정하기 위함.

현재 코드에서 `ProjectName`을 읽는 부분 (93~95행):
```csharp
var project = config.GetSection("project");
if (project != null)
    ProjectName = project.GetString("name", "");
```

이 코드를 다음과 같이 확장한다:
```csharp
var project = config.GetSection("project");
if (project != null)
{
    ProjectName = project.GetString("name", "");

    // [project] company 필드 읽기 -> Application.companyName 설정
    var company = project.GetString("company", "");
    if (!string.IsNullOrEmpty(company))
        RoseEngine.Application.companyName = company;
}
```

- **주의**: 기존 코드는 `if (project != null)` 뒤에 중괄호 없이 한 줄이었으므로, 중괄호 블록으로 감싸야 한다.
- **using 추가 불필요**: `RoseEngine`은 이미 `using RoseEngine;`으로 임포트되어 있음.

### `src/IronRose.Engine/EngineCore.cs`
- **변경 내용**: `InitApplication()` 메서드에 경로 초기화 호출 추가.
- **이유**: `Application.persistentDataPath`를 엔진 초기화 시 설정하기 위함.

현재 `InitApplication()` (517~524행):
```csharp
private void InitApplication()
{
    Application.isPlaying = false;
    Application.isPaused = false;
    Application.QuitAction = () => _window!.Close();
    Application.PauseCallback = IronRose.Engine.Editor.EditorPlayMode.PausePlayMode;
    Application.ResumeCallback = IronRose.Engine.Editor.EditorPlayMode.ResumePlayMode;
}
```

변경 후:
```csharp
private void InitApplication()
{
    Application.isPlaying = false;
    Application.isPaused = false;
    Application.QuitAction = () => _window!.Close();
    Application.PauseCallback = IronRose.Engine.Editor.EditorPlayMode.PausePlayMode;
    Application.ResumeCallback = IronRose.Engine.Editor.EditorPlayMode.ResumePlayMode;

    // 경로 초기화 (ProjectContext가 이미 초기화된 상태)
    var company = Application.companyName;  // ProjectContext.Initialize()에서 설정됨
    var product = ProjectContext.ProjectName;
    if (string.IsNullOrEmpty(product)) product = "DefaultProduct";
    Application.InitializePaths(company, product);
}
```

### `templates/default/project.toml`
- **변경 내용**: `[project]` 섹션에 `company` 필드를 주석 예시로 추가.
- **이유**: 새 프로젝트 생성 시 `company` 필드를 쉽게 설정할 수 있도록 템플릿에 포함.

현재 내용:
```toml
[project]
name = "{{ProjectName}}"
version = "0.1.0"
```

변경 후:
```toml
[project]
name = "{{ProjectName}}"
version = "0.1.0"
# company = "DefaultCompany"
```

## NuGet 패키지
- 없음 (추가 패키지 불필요)

## 검증 기준
- [ ] `dotnet build` 성공
- [ ] `Application.companyName`이 기본값 `"DefaultCompany"`를 가진다
- [ ] `Application.productName`이 기본값 `"DefaultProduct"`를 가진다
- [ ] `Application.persistentDataPath`가 빈 문자열이 아닌 유효한 경로를 반환한다 (엔진 초기화 후)
- [ ] `Application.dataPath`가 `ProjectContext.AssetsPath`와 동일한 값을 반환한다
- [ ] `project.toml`에 `company = "MyStudio"` 필드가 있으면 `Application.companyName`이 `"MyStudio"`가 된다

## 참고
- `Application.dataPath`는 `IronRose.Engine.ProjectContext.AssetsPath`를 참조하므로, 네임스페이스를 풀네임으로 사용한다 (`RoseEngine` 네임스페이스 안에서 `IronRose.Engine`을 참조).
- `OperatingSystem.IsWindows()`는 .NET 5+ API이므로 문제없이 사용 가능하다 (IronRose는 .NET 8 이상).
- `InitializePaths()`는 `ProjectContext.Initialize()` 이후 (즉 `InitApplication()` 시점에) 호출되므로, `ProjectContext.ProjectName`과 `Application.companyName`이 이미 설정된 상태이다.
- 파일 인코딩: UTF-8 with BOM.
