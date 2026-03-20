# Phase A3: ProjectContext 클래스 구현

## 목표

프로젝트 경로 컨텍스트 시스템(`ProjectContext`)을 신규 구현한다. 이 클래스는 에셋 프로젝트의 루트 디렉토리와 엔진 루트 디렉토리를 자동 탐지하고, 프로젝트 내 주요 경로(`Assets`, `EditorAssets`, `RoseCache`, `LiveCode`, `FrozenCode`)를 절대 경로로 제공한다.

**이 Phase 완료 시 달성되는 것:**
- `ProjectContext` 클래스가 존재하고, `ProjectContext.Initialize()` 호출 시 `project.toml` 기반으로 프로젝트 루트/엔진 루트를 자동 설정
- `project.toml`이 없으면 CWD를 프로젝트 루트로 폴백 (기존 동작과 호환)
- `Directory.Build.props`와 `engine.path` 불일치 검증 로직 포함
- `dotnet build`가 성공하고, 기존 코드에 영향 없음 (이 Phase에서는 호출부를 아직 연결하지 않음)

**전후 상태:**
- Before: 모든 경로가 `Directory.GetCurrentDirectory()` 기준 하드코딩 또는 `EngineDirectories` 상수 사용
- After: `ProjectContext` 클래스가 존재하지만, 아직 `EngineCore.Initialize()`에서 호출되지 않음 (호출 연결은 Phase A5에서 수행)

## 선행 조건

- **Phase A1** (완료됨): `Tomlyn` NuGet 패키지 v0.20.0이 `IronRose.Engine.csproj`에 이미 포함됨
- **Phase A2** (완료 가정): `TomlConfig` 래퍼 클래스가 `src/IronRose.Engine/Config/TomlConfig.cs`에 존재해야 함
  - `TomlConfig.Parse(string filePath)` 정적 메서드: TOML 파일을 파싱하여 `TomlConfig` 인스턴스 반환
  - `GetString(string dottedKey, string defaultValue)` 인스턴스 메서드: 점 표기법 키로 문자열 값 조회

> **A2가 아직 완료되지 않은 경우**: 이 명세서 하단의 "A2 미완료 시 대안" 섹션을 참조하여 `TomlConfig` 대신 Tomlyn 직접 사용 버전으로 구현할 수 있다.

## 생성할 파일

### `src/IronRose.Engine/ProjectContext.cs`

- **역할**: 프로젝트 루트 및 엔진 루트 경로를 관리하는 정적 클래스. `project.toml`을 파싱하여 경로를 설정하고, 프로젝트 내 주요 디렉토리 절대 경로를 프로퍼티로 제공한다.
- **네임스페이스**: `IronRose.Engine`
- **클래스**: `public static class ProjectContext`

#### 전체 구현 코드

```csharp
// ------------------------------------------------------------
// @file    ProjectContext.cs
// @brief   프로젝트 경로 컨텍스트. 에셋 프로젝트의 루트와 엔진 루트를 관리한다.
//          project.toml에서 설정을 읽어 초기화된다.
// @deps    IronRose.Engine/Config/TomlConfig, RoseEngine/Debug
// @exports
//   class ProjectContext (static)
//     Initialize(string?): void        -- 프로젝트 루트 탐색 및 초기화
//     ProjectRoot: string              -- 에셋 프로젝트 루트 절대 경로
//     EngineRoot: string               -- 엔진 소스 루트 절대 경로
//     IsProjectLoaded: bool            -- project.toml 발견 여부
//     AssetsPath: string               -- Assets/ 절대 경로
//     EditorAssetsPath: string         -- EditorAssets/ 절대 경로
//     CachePath: string                -- RoseCache/ 절대 경로
//     LiveCodePath: string             -- LiveCode/ 절대 경로
//     FrozenCodePath: string           -- FrozenCode/ 절대 경로
// ------------------------------------------------------------
using System;
using System.IO;
using System.Xml.Linq;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// 프로젝트 경로 컨텍스트. 에셋 프로젝트의 루트와 엔진 루트를 관리한다.
    /// project.toml에서 설정을 읽어 초기화된다.
    /// </summary>
    public static class ProjectContext
    {
        /// <summary>에셋 프로젝트 루트 (project.toml이 있는 디렉토리).</summary>
        public static string ProjectRoot { get; private set; } = "";

        /// <summary>엔진 소스 루트 (IronRose/ 디렉토리).</summary>
        public static string EngineRoot { get; private set; } = "";

        /// <summary>project.toml이 발견되어 프로젝트가 로드된 상태인지 여부.</summary>
        public static bool IsProjectLoaded { get; private set; } = false;

        /// <summary>Assets/ 절대 경로.</summary>
        public static string AssetsPath => Path.Combine(ProjectRoot, "Assets");

        /// <summary>EditorAssets/ 절대 경로.</summary>
        public static string EditorAssetsPath => Path.Combine(ProjectRoot, "EditorAssets");

        /// <summary>RoseCache/ 절대 경로.</summary>
        public static string CachePath => Path.Combine(ProjectRoot, "RoseCache");

        /// <summary>LiveCode/ 절대 경로.</summary>
        public static string LiveCodePath => Path.Combine(ProjectRoot, "LiveCode");

        /// <summary>FrozenCode/ 절대 경로.</summary>
        public static string FrozenCodePath => Path.Combine(ProjectRoot, "FrozenCode");

        /// <summary>
        /// 프로젝트 루트를 탐색하고 project.toml을 읽어 초기화한다.
        /// </summary>
        /// <param name="projectRoot">
        /// 명시적으로 프로젝트 루트를 지정할 때 사용. null이면 자동 탐색.
        /// </param>
        public static void Initialize(string? projectRoot = null)
        {
            ProjectRoot = projectRoot
                ?? FindProjectRoot(Directory.GetCurrentDirectory())
                ?? FindProjectRoot(AppContext.BaseDirectory)
                ?? Directory.GetCurrentDirectory();

            // 정규화: 후행 슬래시 제거, 심볼릭 링크 해석
            ProjectRoot = Path.GetFullPath(ProjectRoot);

            var tomlPath = Path.Combine(ProjectRoot, "project.toml");
            if (File.Exists(tomlPath))
            {
                try
                {
                    var config = TomlConfig.Parse(tomlPath);
                    var engineRelPath = config.GetString("engine.path", "../IronRose");
                    EngineRoot = Path.GetFullPath(Path.Combine(ProjectRoot, engineRelPath));
                    IsProjectLoaded = true;

                    Debug.Log($"[ProjectContext] Project loaded: {ProjectRoot}");
                    Debug.Log($"[ProjectContext] Engine root: {EngineRoot}");

                    // Directory.Build.props와 engine.path 불일치 검증
                    ValidateBuildPropsAlignment();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ProjectContext] Failed to parse {tomlPath}: {ex.Message}");
                    EngineRoot = ProjectRoot;
                    IsProjectLoaded = false;
                }
            }
            else
            {
                // project.toml이 없으면 엔진 레포 직접 실행 케이스.
                // EngineRoot를 ProjectRoot 자신으로 폴백.
                EngineRoot = ProjectRoot;
                IsProjectLoaded = false;
                Debug.Log($"[ProjectContext] No project.toml found. Fallback: ProjectRoot = EngineRoot = {ProjectRoot}");
            }
        }

        /// <summary>
        /// startDir에서 상위 디렉토리로 올라가며 project.toml을 탐색한다.
        /// </summary>
        /// <param name="startDir">탐색 시작 디렉토리.</param>
        /// <returns>project.toml이 있는 디렉토리 경로. 없으면 null.</returns>
        private static string? FindProjectRoot(string startDir)
        {
            var dir = Path.GetFullPath(startDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "project.toml")))
                    return dir;

                var parent = Path.GetDirectoryName(dir);
                // 루트 디렉토리에 도달하면 중단 (parent == dir인 경우)
                if (parent == null || parent == dir)
                    break;
                dir = parent;
            }
            return null;
        }

        /// <summary>
        /// Directory.Build.props의 IronRoseRoot 값과 project.toml의 engine.path가
        /// 동일한 경로를 가리키는지 검증한다. 불일치 시 경고 로그를 출력한다.
        /// </summary>
        private static void ValidateBuildPropsAlignment()
        {
            var propsPath = Path.Combine(ProjectRoot, "Directory.Build.props");
            if (!File.Exists(propsPath))
                return;

            try
            {
                var propsEngineRoot = ParseIronRoseRootFromProps(propsPath);
                if (propsEngineRoot == null)
                    return;

                // Directory.Build.props의 경로를 ProjectRoot 기준으로 절대 경로로 변환
                var propsAbsolute = Path.GetFullPath(Path.Combine(ProjectRoot, propsEngineRoot));

                // 후행 디렉토리 구분자 제거 후 비교
                var normalizedProps = propsAbsolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedEngine = EngineRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.Equals(normalizedProps, normalizedEngine, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError(
                        $"[ProjectContext] engine.path ({EngineRoot}) and " +
                        $"Directory.Build.props IronRoseRoot ({propsAbsolute}) mismatch! " +
                        $"빌드/런타임 경로 불일치로 에셋 탐색 실패 가능.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ProjectContext] Failed to validate Directory.Build.props: {ex.Message}");
            }
        }

        /// <summary>
        /// Directory.Build.props XML에서 IronRoseRoot의 기본값(MSBuild 변수 미확장)을 추출한다.
        /// </summary>
        /// <param name="propsPath">Directory.Build.props 파일 절대 경로.</param>
        /// <returns>
        /// IronRoseRoot의 기본값 문자열 (예: "../IronRose").
        /// MSBuild 변수가 포함된 경우 null 반환 (비교 불가).
        /// 파싱 실패 시에도 null 반환.
        /// </returns>
        private static string? ParseIronRoseRootFromProps(string propsPath)
        {
            var doc = XDocument.Load(propsPath);
            // <PropertyGroup> 내의 <IronRoseRoot> 요소를 찾는다.
            // Condition 없는(또는 빈 값 체크 Condition이 있는) 첫 번째 IronRoseRoot를 사용.
            foreach (var pg in doc.Descendants("PropertyGroup"))
            {
                foreach (var elem in pg.Elements("IronRoseRoot"))
                {
                    var condition = elem.Attribute("Condition")?.Value;

                    // Condition이 있는 요소가 기본값을 가진다 (빈 값일 때 설정하는 패턴)
                    // 예: <IronRoseRoot Condition="'$(IronRoseRoot)' == ''">../IronRose</IronRoseRoot>
                    if (condition != null && condition.Contains("$(IronRoseRoot)") && condition.Contains("''"))
                    {
                        var value = elem.Value.Trim();
                        // $(MSBuildThisFileDirectory) 같은 MSBuild 변수가 포함되면 런타임에서 해석 불가
                        if (value.Contains("$("))
                        {
                            // MSBuild 변수 제거 시도: $(MSBuildThisFileDirectory)는 props 파일 디렉토리
                            // 이 패턴에서는 $(MSBuildThisFileDirectory)../IronRose 형태
                            var cleaned = value.Replace("$(MSBuildThisFileDirectory)", "");
                            if (!cleaned.Contains("$("))
                                return cleaned;
                            return null;
                        }
                        return value;
                    }
                }
            }

            // Condition 없는 IronRoseRoot도 시도
            foreach (var pg in doc.Descendants("PropertyGroup"))
            {
                foreach (var elem in pg.Elements("IronRoseRoot"))
                {
                    if (elem.Attribute("Condition") == null)
                    {
                        var value = elem.Value.Trim();
                        if (!value.Contains("$("))
                            return value;
                    }
                }
            }

            return null;
        }
    }
}
```

#### 주요 메서드 동작 설명

1. **`Initialize(string? projectRoot = null)`**
   - `projectRoot`가 명시적으로 전달되면 해당 경로 사용
   - `null`이면 `FindProjectRoot(CWD)` -> `FindProjectRoot(AppContext.BaseDirectory)` -> CWD 순서로 폴백
   - `project.toml` 발견 시: `TomlConfig.Parse()`로 TOML 파싱, `engine.path` 키를 읽어 `EngineRoot` 설정, `IsProjectLoaded = true`
   - `project.toml` 미발견 시: `EngineRoot = ProjectRoot` (엔진 레포 직접 실행 케이스), `IsProjectLoaded = false`
   - `project.toml` 파싱 실패 시: 경고 로그 출력 후 미발견과 동일하게 폴백

2. **`FindProjectRoot(string startDir)`**
   - `startDir`에서 시작하여 `Path.GetDirectoryName()`으로 부모 디렉토리를 순회
   - 각 디렉토리에 `project.toml` 파일이 존재하는지 확인
   - 루트 디렉토리(`/` 또는 `C:\`)에 도달하면 `null` 반환
   - 무한 루프 방지: `parent == dir` 체크

3. **`ValidateBuildPropsAlignment()`**
   - `Directory.Build.props`에서 `IronRoseRoot`의 기본값을 XML 파싱으로 추출
   - `project.toml`의 `engine.path`로 계산된 `EngineRoot`와 비교
   - 경로 비교 시 후행 슬래시 제거 및 대소문자 무시(Windows 호환)
   - 불일치 시 `Debug.LogError()` 출력

4. **`ParseIronRoseRootFromProps(string propsPath)`**
   - `System.Xml.Linq.XDocument`로 XML 파싱
   - `<PropertyGroup>` 내 `<IronRoseRoot>` 요소 탐색
   - `Condition="'$(IronRoseRoot)' == ''"` 패턴의 요소에서 기본값 추출
   - `$(MSBuildThisFileDirectory)` 변수는 제거하여 상대 경로만 추출
   - 해석 불가한 MSBuild 변수가 남아 있으면 `null` 반환 (비교 스킵)

#### 의존 파일

- `src/IronRose.Engine/Config/TomlConfig.cs` (Phase A2에서 생성)
- `src/IronRose.Contracts/Debug.cs` (기존 파일 -- `RoseEngine.Debug` 클래스)

## 수정할 파일

이 Phase에서는 **기존 파일을 수정하지 않는다**. `ProjectContext`는 신규 파일로 추가되며, `EngineCore.Initialize()`에서의 호출 연결은 Phase A5에서 수행한다.

## NuGet 패키지

추가 패키지 불필요. 이미 사용 중인 패키지로 충분하다:
- `Tomlyn` v0.20.0 -- TOML 파싱 (이미 `IronRose.Engine.csproj`에 포함)
- `System.Xml.Linq` -- `XDocument`로 `Directory.Build.props` XML 파싱 (.NET 기본 포함, 별도 패키지 불필요)

## 검증 기준

- [ ] `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 성공
- [ ] `dotnet build` (솔루션 전체) 성공
- [ ] `ProjectContext.cs` 파일이 `src/IronRose.Engine/ProjectContext.cs` 경로에 존재
- [ ] `ProjectContext` 클래스가 `IronRose.Engine` 네임스페이스에 속함
- [ ] 코드 내에서 `TomlConfig.Parse()`가 올바르게 호출됨 (A2 의존)
- [ ] `project.toml`이 없는 상태에서도 `Initialize()` 호출 시 예외 없이 폴백 동작
- [ ] `IsProjectLoaded` 프로퍼티가 `project.toml` 발견 여부에 따라 올바르게 설정됨

### 수동 확인 (Phase A5 이후)

아래 항목은 Phase A5에서 `EngineCore.Initialize()`에 호출이 연결된 후 확인 가능:

- `project.toml`이 없는 상태에서 엔진 실행 시 `ProjectRoot == CWD`, `EngineRoot == CWD`
- `project.toml`이 있는 상태에서 `engine.path` 값이 `EngineRoot`에 반영됨

## A2 미완료 시 대안

Phase A2(`TomlConfig` 래퍼)가 아직 구현되지 않은 경우, `Initialize()` 내부에서 Tomlyn을 직접 사용하는 대안을 적용할 수 있다. 이 경우 `TomlConfig` 의존 코드를 다음으로 교체한다:

```csharp
// TomlConfig 대신 Tomlyn 직접 사용 (A2 미완료 시)
using Tomlyn;
using Tomlyn.Model;

// Initialize() 내부의 project.toml 파싱 부분:
var table = Toml.ToModel(File.ReadAllText(tomlPath));
var engineRelPath = "../IronRose"; // 기본값
if (table.TryGetValue("engine", out var engineVal) && engineVal is TomlTable engineTable)
{
    if (engineTable.TryGetValue("path", out var pathVal) && pathVal is string pathStr)
        engineRelPath = pathStr;
}
EngineRoot = Path.GetFullPath(Path.Combine(ProjectRoot, engineRelPath));
IsProjectLoaded = true;
```

이 패턴은 기존 코드베이스의 `EditorState.cs`, `RoseConfig.cs`, `ProjectSettings.cs`에서 사용하는 것과 동일한 Tomlyn 직접 사용 패턴이다. A2가 완료되면 `TomlConfig.Parse()` 사용으로 리팩터링한다.

> **중요**: A2 미완료 시 대안을 사용하는 경우, using 문에 `using Tomlyn;`과 `using Tomlyn.Model;`을 추가하고 `using IronRose.Engine.Config;`는 제거해야 한다. 또한 위의 "전체 구현 코드" 섹션의 `TomlConfig.Parse()` 호출 부분을 위 코드로 교체한다.

## 참고

### 설계 문서 관련 섹션
- `plans/editor-assets-repo-separation.md` -- "5. 프로젝트 경로 해석 시스템" 섹션
- `plans/editor-assets-repo-separation.md` -- "미결 사항" 중 `project.toml ↔ Directory.Build.props 경로 이중 관리`

### 코드베이스 컨텍스트
- **로깅 API**: `RoseEngine.Debug.Log()`, `RoseEngine.Debug.LogWarning()`, `RoseEngine.Debug.LogError()` 사용 (정의: `src/IronRose.Contracts/Debug.cs`)
- **기존 경로 패턴**: 현재 코드베이스는 `Directory.GetCurrentDirectory()` 또는 `AppContext.BaseDirectory` 기준 상대 경로를 사용. `ProjectContext`는 이를 대체할 단일 진입점이 됨
- **기존 경로 상수**: `EngineDirectories.CachePath = "RoseCache"`, `EngineDirectories.LiveCodePath = "LiveCode"`, `EngineDirectories.FrozenCodePath = "FrozenCode"` (정의: `src/IronRose.Engine/RoseEngine/EngineConstants.cs`). 이 상수들은 Phase B8에서 정리될 예정이며, `ProjectContext`의 프로퍼티가 이를 대체함
- **TOML 파싱 패턴**: 기존 코드 (`EditorState.cs`, `RoseConfig.cs`, `ProjectSettings.cs`)는 `Tomlyn.Toml.ToModel()` + `TomlTable.TryGetValue()` 패턴을 사용
- **파일 헤더 주석**: 기존 코드(`EngineCore.cs`)의 파일 헤더 주석 스타일을 따름

### 주의사항
- `FindProjectRoot()`에서 무한 루프 방지: `Path.GetDirectoryName()`이 루트 디렉토리에서 `null` 또는 동일 경로를 반환할 수 있으므로 `parent == dir` 체크 필수
- `Path.GetFullPath()`로 경로 정규화를 반드시 수행하여, 상대 경로/심볼릭 링크로 인한 비교 오류 방지
- Windows에서 경로 비교 시 대소문자 무시 필요 (`StringComparison.OrdinalIgnoreCase`)
- `XDocument.Load()`는 `System.Xml.Linq` 네임스페이스이며, .NET 런타임에 기본 포함되어 별도 NuGet 불필요
- 이 Phase에서는 `EngineCore.Initialize()`를 수정하지 않으므로, `ProjectContext.Initialize()`는 아직 어디서도 호출되지 않음. Phase A5에서 연결됨

### 의존 관계 (이 Phase 이후)
- **Phase A4** (project.toml 파일 포맷): `ProjectContext`가 파싱하는 `project.toml`의 전체 스키마 정의
- **Phase A5**: `EngineCore.Initialize()` 시작부에 `ProjectContext.Initialize()` 호출 추가
- **Phase B1~B14**: 개별 파일의 하드코딩 경로를 `ProjectContext` 프로퍼티로 교체
- **Phase D5b**: `IsProjectLoaded` 프로퍼티를 사용하여 프로젝트 없는 상태 처리
