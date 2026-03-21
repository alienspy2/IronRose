# 에디터 에셋 레포 분리 및 프로젝트 구조 재설계

**작성일**: 2026-03-20

## 배경

현재 IronRose 레포 하나에 엔진 코드, 에디터 에셋(폰트/Matcap/Skybox), 게임 에셋(Assets/), 스크립트(FrozenCode/LiveCode/)가 모두 포함되어 있다. 이로 인해:

- 바이너리 에셋이 엔진 Git 히스토리를 비대하게 만듦
- 유저가 새 프로젝트를 시작하려면 엔진 레포 전체를 복제해야 함
- 엔진 업데이트와 프로젝트 에셋이 섞여 관리가 어려움

## 목표

1. **엔진 레포(IronRose)** 와 **에셋 프로젝트 레포**를 분리하여 독립적으로 관리
2. 유저가 에셋 프로젝트 폴더만 열어 작업할 수 있는 구조 설계
3. 설정 파일 포맷을 TOML로 통일
4. 확장자 기반 재귀 탐색으로 셰이더 파일 자동 수집 (엔진 + 프로젝트)

## 현재 상태

### 디렉토리 구조 (IronRose 레포)
```
IronRose/
  src/
    IronRose.Engine/         # 엔진 코어
    IronRose.Rendering/      # 렌더링
    IronRose.Physics/        # 물리
    IronRose.Scripting/      # Roslyn 핫리로드
    IronRose.Editor/         # 에디터 UI
    IronRose.Contracts/      # 플러그인 계약
    IronRose.AssetPipeline/  # 에셋 파이프라인
    IronRose.RoseEditor/     # 에디터 실행 프로젝트
    IronRose.Standalone/     # 스탠드얼론 빌드
  EditorAssets/              # 에디터 전용 에셋 (Fonts, Matcaps, Skybox)
  Assets/                    # 게임 에셋 (테스트용)
  Shaders/                   # GLSL 셰이더
  FrozenCode/                # 안정 스크립트
  LiveCode/                  # 실험 스크립트
  RoseCache/                 # 에셋 캐시 (.gitignore)
```

### 주요 경로 참조 코드

| 파일 | 참조 방식 |
|------|-----------|
| `EditorAssets.cs:23` | `Path.Combine(Directory.GetCurrentDirectory(), "EditorAssets", "Matcaps")` |
| `ImGuiOverlay.cs:260` | `Path.Combine(Directory.GetCurrentDirectory(), "EditorAssets", "Fonts")` |
| `ThumbnailGenerator.cs:238` | `Path.Combine(AppContext.BaseDirectory, "EditorAssets", "Fonts", "Roboto.ttf")` |
| `EngineCore.cs:569` | `Path.GetFullPath("Assets")` |
| `EngineCore.cs:507` | `Path.Combine(Directory.GetCurrentDirectory(), EngineDirectories.CachePath, "shaders")` |
| `AssetDatabase.cs:32` | `new RoseCache(Path.Combine(Directory.GetCurrentDirectory(), EngineDirectories.CachePath))` |
| `RenderSystem.cs:1775` | `"Shaders"` 후보 경로 탐색 (`"Shaders", "../Shaders", "../../Shaders"`) |
| `EngineConstants.cs:20-22` | `CachePath = "RoseCache"`, `LiveCodePath = "LiveCode"`, `FrozenCodePath = "FrozenCode"` |
| `PrefabVariantTree.cs:54` | `Path.Combine(Directory.GetCurrentDirectory(), "Assets")` |

---

## 설계

### 설계 전제

Unity와 달리, IronRose 엔진은 항상 소스코드와 함께 사용되며 소스코드와 함께 실행된다. 빌드된 바이너리 배포나 설치 개념이 없고, 개발자는 항상 엔진 소스 레포(IronRose)를 로컬에 두고 ProjectReference로 직접 참조한다.

### 개요

**2-레포 구조**로 분리한다:

```
~/git/
  IronRose/               # 엔진 레포 (소스 코드 + 기본 셰이더)
  MyGame/                  # 에셋 프로젝트 레포 (유저가 만드는 프로젝트)
```

에셋 프로젝트는 `ProjectReference Path="../IronRose/..."` 로 엔진을 로컬 소스 직접 참조한다. Hub UI 없이 에디터에서 프로젝트 폴더를 직접 열어 사용한다.

### 상세 설계

#### 1. 에셋 프로젝트 구조

```
MyGame/                              # 에셋 프로젝트 루트
  project.toml                       # 프로젝트 설정 (엔진 경로, 프로젝트 메타)
  Assets/                            # 게임 에셋
    Scenes/
    Textures/
    Models/
    Prefabs/
    Settings/
      Default.renderer
  EditorAssets/                      # 에디터 에셋 (IronRose에서 이동)
    Fonts/
    Matcaps/
    Skybox/
  Shaders/                           # 프로젝트 커스텀 셰이더 (선택적, 어디에 두든 확장자로 자동 수집)
  FrozenCode/
    FrozenCode.csproj
  LiveCode/
    LiveCode.csproj
  RoseCache/                         # 빌드 캐시 (.gitignore)
  Directory.Build.props              # MSBuild 엔진 경로 변수 ($(IronRoseRoot))
  .gitignore
  MyGame.RoseEditor.csproj           # 에디터 실행 프로젝트
```

> **Standalone 빌드**: `Standalone.csproj`는 에셋 프로젝트 구조에 포함하지 않는다. Unity의 Build Settings처럼, 에디터의 Build 메뉴에서 최종 바이너리를 출력할 때만 사용하며 별도 Phase에서 처리한다.

#### 2. project.toml (프로젝트 설정 파일)

```toml
[project]
name = "MyGame"
version = "0.1.0"

[engine]
# 엔진 소스 경로 (상대 경로)
path = "../IronRose"

[editor]
# 에디터 실행 시 마지막으로 열린 씬 등 (자동 저장)
last_scene = "Assets/Scenes/Main.scene"

[build]
start_scene = "Assets/Scenes/Main.scene"
```

#### 3. csproj 파일 구조

**Directory.Build.props** (에셋 프로젝트 루트, 템플릿에 포함):
```xml
<Project>
  <PropertyGroup>
    <!-- 엔진 소스 경로. 기본값 ../IronRose.
         다른 경로에 엔진이 있다면 환경변수 IRONROSE_ROOT를 설정하거나 이 파일을 직접 수정. -->
    <IronRoseRoot Condition="'$(IronRoseRoot)' == ''">$(MSBuildThisFileDirectory)../IronRose</IronRoseRoot>
    <IronRoseRoot>$([MSBuild]::NormalizeDirectory($(IronRoseRoot)))</IronRoseRoot>
  </PropertyGroup>
</Project>
```

> **주의**: `engine.path`(project.toml)와 `IronRoseRoot`(Directory.Build.props)는 동일한 경로를 가리켜야 한다. 기본값은 둘 다 `../IronRose`.

**MyGame.RoseEditor.csproj** (에셋 프로젝트 루트):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <!-- Silk.NET.Windowing만 직접 참조. 나머지 패키지(Veldrid, ImGui.NET, SixLabors 등 12개)는
         IronRose.Engine의 전이 참조(transitive PackageReference)로 자동 포함됨. -->
    <PackageReference Include="Silk.NET.Windowing" Version="2.23.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- 엔진 소스 직접 참조. 경로는 Directory.Build.props의 $(IronRoseRoot) 사용. -->
    <!-- IronRose.Rendering, IronRose.Physics, IronRose.Scripting, IronRose.AssetPipeline은
         IronRose.Engine / IronRose.Editor의 전이 참조(transitive ProjectReference)로 자동 포함됨. -->
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Contracts/IronRose.Contracts.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Editor/IronRose.Editor.csproj" />
    <ProjectReference Include="FrozenCode/FrozenCode.csproj" />
  </ItemGroup>
</Project>
```

**FrozenCode/FrozenCode.csproj**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <!-- Directory.Build.props가 상위 디렉토리에서 자동 적용되므로 $(IronRoseRoot) 사용 가능. -->
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
  </ItemGroup>
</Project>
```

**LiveCode/LiveCode.csproj**:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Contracts/IronRose.Contracts.csproj" />
  </ItemGroup>
</Project>
```

#### 4. 엔진 레포 정리 (IronRose)

분리 후 엔진 레포에서 **제거**되는 것:
- `EditorAssets/` -- 에셋 프로젝트 템플릿으로 이동
- `Assets/` -- 에셋 프로젝트 템플릿으로 이동
- `FrozenCode/`, `LiveCode/` -- 에셋 프로젝트 템플릿으로 이동
- `src/IronRose.RoseEditor/` -- 에셋 프로젝트 템플릿의 csproj로 대체

엔진 레포에 **유지**되는 것:
- `src/IronRose.Engine/`
- `src/IronRose.Rendering/`
- `src/IronRose.Physics/`
- `src/IronRose.Scripting/`
- `src/IronRose.Editor/`
- `src/IronRose.Contracts/`
- `src/IronRose.AssetPipeline/`
- `src/IronRose.Standalone/` -- 에디터 Build 메뉴용 (별도 Phase에서 처리)
- `Shaders/` -- 엔진 기본 셰이더
- `doc/`

엔진 레포에 **추가**되는 것:
- `templates/default/` -- 새 프로젝트 템플릿 (EditorAssets, 기본 Assets, 기본 csproj, project.toml, .gitignore 포함)

#### 5. 프로젝트 경로 해석 시스템

현재 모든 경로가 `Directory.GetCurrentDirectory()` 기준 하드코딩이므로, **프로젝트 컨텍스트** 개념을 도입한다.

**새 클래스: `ProjectContext`** (`src/IronRose.Engine/ProjectContext.cs`)

```csharp
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
        /// project.toml을 찾아 초기화. CWD에서 상위로 탐색한다.
        /// </summary>
        public static void Initialize(string? projectRoot = null)
        {
            ProjectRoot = projectRoot
                ?? FindProjectRoot(Directory.GetCurrentDirectory())
                ?? FindProjectRoot(AppContext.BaseDirectory)  // dotnet run --project 대응: bin/Debug/ 에서 상위 탐색
                ?? Directory.GetCurrentDirectory();

            // project.toml이 없으면 EngineRoot를 ProjectRoot 자신으로 폴백 (엔진 레포 직접 실행 케이스)
            var tomlPath = Path.Combine(ProjectRoot, "project.toml");
            if (File.Exists(tomlPath))
            {
                var config = TomlConfig.Parse(tomlPath);
                var engineRelPath = config.GetString("engine.path", "../IronRose");
                EngineRoot = Path.GetFullPath(Path.Combine(ProjectRoot, engineRelPath));
            }
            else
            {
                EngineRoot = ProjectRoot;
            }
        }

        private static string? FindProjectRoot(string startDir)
        {
            var dir = startDir;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "project.toml")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
```

#### 6. 셰이더 탐색 (확장자 기반 재귀 검색)

기존의 경로 후보 배열 방식(`"Shaders", "../Shaders", "../../Shaders"`)을 폐기하고, **확장자 기반으로 엔진/프로젝트 루트를 재귀 탐색**하여 셰이더 파일을 수집한다. 디렉토리 구조에 무관하게 확장자만으로 식별한다.

**대상 확장자** (현재 코드베이스에서 사용 중인 것):
- `.glsl` -- 범용 GLSL 셰이더
- `.vert` -- 버텍스 셰이더
- `.frag` -- 프래그먼트 셰이더
- `.comp` -- 컴퓨트 셰이더

**파일명 충돌 정책**: 파일명 기준 **선착순 등록**. 동일 파일명이 이미 등록되어 있으면 후속 파일은 무시하고 경고 로그를 출력한다. 엔진/프로젝트 간 셰이더 오버라이드는 지원하지 않으며, 프로젝트에서 커스텀 셰이더를 추가할 때는 고유한 파일명을 사용해야 한다.

**탐색 순서**: 엔진 루트 → 프로젝트 루트 (엔진 기본 셰이더가 먼저 등록됨)

```csharp
/// <summary>
/// 확장자 기반으로 엔진/프로젝트 루트를 재귀 탐색하여 셰이더 파일 맵을 구축한다.
/// Key: 파일명(예: "vertex.glsl"), Value: 절대 경로
/// 동일 파일명 충돌 시 먼저 등록된 것을 유지하고 경고를 출력한다.
/// </summary>
public static class ShaderRegistry
{
    private static readonly string[] ShaderExtensions = { ".glsl", ".vert", ".frag", ".comp" };

    private static Dictionary<string, string> _shaderMap = new();

    /// <summary>
    /// 엔진 루트와 프로젝트 루트를 재귀 탐색하여 셰이더 맵을 구축한다.
    /// Initialize()는 ProjectContext.Initialize() 이후에 호출되어야 한다.
    /// </summary>
    public static void Initialize()
    {
        _shaderMap.Clear();

        // 1. 엔진 루트에서 재귀 수집 (베이스)
        CollectShaders(ProjectContext.EngineRoot);

        // 2. 프로젝트 루트에서 재귀 수집 (프로젝트 커스텀 셰이더 추가, 중복 시 경고)
        if (ProjectContext.ProjectRoot != ProjectContext.EngineRoot)
            CollectShaders(ProjectContext.ProjectRoot);
    }

    private static void CollectShaders(string rootDir)
    {
        if (!Directory.Exists(rootDir)) return;

        foreach (var ext in ShaderExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, $"*{ext}", SearchOption.AllDirectories))
            {
                // RoseCache, bin, obj 등 빌드 산출물 디렉토리는 제외
                if (ShouldExclude(file)) continue;

                var fileName = Path.GetFileName(file);
                var fullPath = Path.GetFullPath(file);

                if (_shaderMap.TryGetValue(fileName, out var existing))
                {
                    // 동일 파일명 충돌: 먼저 등록된 것을 유지하고 경고 출력
                    Logger.Warn($"[ShaderRegistry] Duplicate shader '{fileName}' ignored. " +
                        $"Registered: {existing}, Ignored: {fullPath}");
                    continue;
                }
                _shaderMap[fileName] = fullPath;
            }
        }
    }

    private static bool ShouldExclude(string path)
    {
        // 빌드 산출물, 캐시 디렉토리 제외
        return path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            || path.Contains($"{Path.DirectorySeparatorChar}RoseCache{Path.DirectorySeparatorChar}")
            || path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}");
    }

    /// <summary>
    /// 파일명으로 셰이더 절대 경로를 반환한다.
    /// </summary>
    public static string Resolve(string shaderFileName)
    {
        if (_shaderMap.TryGetValue(shaderFileName, out var path))
            return path;

        throw new FileNotFoundException($"Shader not found: {shaderFileName}");
    }

    /// <summary>
    /// 등록된 모든 셰이더 파일 목록을 반환한다. (디버그/에디터 UI용)
    /// </summary>
    public static IReadOnlyDictionary<string, string> All => _shaderMap;
}
```

**순환 의존성 회피**:

`ShaderRegistry`는 `IronRose.Engine`에 배치된다. 그런데 `IronRose.Rendering`의 PostProcess 클래스들(`PostProcessStack`, `BloomEffect`, `TonemapEffect`)도 셰이더 경로가 필요하다. `IronRose.Engine → IronRose.Rendering` 참조가 이미 존재하므로, `IronRose.Rendering → IronRose.Engine` 참조를 추가하면 **순환 의존성**이 발생한다.

이를 해결하기 위해, `IronRose.Rendering`의 PostProcess 클래스들은 `ShaderRegistry`를 직접 참조하지 않고, **셰이더 리졸버 델리게이트**(`Func<string, string>`)를 외부에서 주입받는다:

```csharp
// IronRose.Rendering — PostProcessStack 시그니처 변경
public void Initialize(GraphicsDevice device, uint width, uint height, Func<string, string> shaderResolver)
{
    _shaderResolver = shaderResolver;  // 기존 _shaderDir 대체
    // ...
}

// IronRose.Rendering — PostProcessEffect 베이스 클래스
protected Func<string, string> ShaderResolver { get; private set; }  // 기존 ShaderDir 대체

// IronRose.Rendering — BloomEffect 사용 예시
string fullscreenVert = ShaderResolver("fullscreen.vert");
_thresholdShaders = ShaderCompiler.CompileGLSL(Device, fullscreenVert,
    ShaderResolver("bloom_threshold.frag"));

// IronRose.Engine — 호출부 (Engine에서 Rendering 초기화 시)
postProcessStack.Initialize(device, width, height, ShaderRegistry.Resolve);
```

이 방식으로 `IronRose.Rendering`은 `IronRose.Engine`을 참조하지 않고도 셰이더 경로를 해석할 수 있다.

**기존 코드 변경 요약**:
- `RenderSystem.FindShaderDirectory()` 제거 -- `ShaderRegistry.Resolve()` 로 대체
- `_shaderDir` 필드 제거 -- `Path.Combine(_shaderDir, "vertex.glsl")` 같은 호출을 `ShaderRegistry.Resolve("vertex.glsl")` 로 변경
- `PostProcessStack` -- `string shaderDir` 매개변수를 `Func<string, string> shaderResolver`로 변경
- `PostProcessEffect` 베이스 -- `ShaderDir` 프로퍼티를 `ShaderResolver` (`Func<string, string>`)로 변경
- `BloomEffect`, `TonemapEffect` -- `Path.Combine(ShaderDir, ...)` 호출을 `ShaderResolver(...)` 호출로 변경
- `EngineCore.Initialize()` 내 `ShaderRegistry.Initialize()` 호출 추가 (A5의 `ProjectContext.Initialize()` 직후)

#### 7. TOML 파서

`Tomlyn` NuGet 패키지가 이미 적용되어 있으며, 기존 `.rose_editor_state.toml` 등 TOML 파일 파싱에 사용 중이다. 기존 파서를 확장하여 `project.toml`도 처리한다.

#### 8. 에디터에서 프로젝트 열기

Hub UI 없이 다음 방식으로 프로젝트를 연다:

- **방법 A**: CWD 기반 -- 프로젝트 폴더에서 `dotnet run` 실행
  ```bash
  cd ~/git/MyGame
  dotnet run
  ```

- **방법 B**: 명령줄 인자
  ```bash
  dotnet run --project ~/git/MyGame/MyGame.RoseEditor.csproj
  ```
  > `dotnet run --project`는 CWD를 변경하지 않으므로, `ProjectContext`는 `AppContext.BaseDirectory`(빌드 출력 디렉토리, 프로젝트 내부)에서 상위 탐색하여 `project.toml`을 찾는다.

- **방법 C**: 에디터 메뉴 -- File > Open Project (NativeFileDialog로 폴더 선택)
  - 선택된 폴더에 `project.toml`이 있으면 해당 프로젝트로 재시작
  - 없으면 에러 메시지 표시

#### 9. 프로젝트 템플릿 (`templates/default/`)

엔진 레포에 포함될 기본 프로젝트 템플릿:

```
templates/default/
  project.toml
  Assets/
    Scenes/
      SampleScene.scene
    Settings/
      Default.renderer
  EditorAssets/
    Fonts/          # 폰트 파일 복사
    Matcaps/        # Matcap 텍스처 복사
    Skybox/         # HDR 환경맵 복사
  FrozenCode/
    FrozenCode.csproj
  LiveCode/
    LiveCode.csproj
  Directory.Build.props
  .gitignore
  {{ProjectName}}.RoseEditor.csproj
```

나중에 `dotnet new ironrose` 같은 dotnet template으로 발전시킬 수 있다.

### 영향 범위

#### 수정이 필요한 파일 (엔진 측)

| 파일 | 변경 내용 |
|------|-----------|
| `src/IronRose.Engine/RoseEngine/EngineConstants.cs` | `EngineDirectories` 경로를 `ProjectContext`로 대체하거나, 폴더명 상수만 유지 |
| `src/IronRose.Engine/EngineCore.cs` | `ProjectContext.Initialize()` 호출 추가, 모든 하드코딩 경로를 `ProjectContext` 사용으로 변경 |
| `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` | `RoseCache` 경로를 `ProjectContext.CachePath`로 변경 |
| `src/IronRose.Engine/Editor/EditorAssets.cs` | `EditorAssets` 경로를 `ProjectContext.EditorAssetsPath`로 변경 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` | 폰트 경로를 `ProjectContext.EditorAssetsPath`로 변경 |
| `src/IronRose.Engine/Editor/ThumbnailGenerator.cs` | 폰트 경로를 `ProjectContext.EditorAssetsPath`로 변경 |
| `src/IronRose.Engine/RenderSystem.cs` | `FindShaderDirectory()` 제거, `_shaderDir` 제거, `ShaderRegistry.Resolve()` 사용 |
| `src/IronRose.Engine/Rendering/SceneViewRenderer.cs` | 셰이더 경로를 `ShaderRegistry.Resolve()` 로 변경 |
| `src/IronRose.Engine/Editor/MeshPreviewRenderer.cs` | 셰이더 경로를 `ShaderRegistry.Resolve()` 로 변경 |
| `src/IronRose.Rendering/PostProcessing/PostProcessStack.cs` | `string shaderDir` 매개변수를 `Func<string, string> shaderResolver`로 변경 |
| `src/IronRose.Rendering/PostProcessing/PostProcessEffect.cs` | `ShaderDir` 프로퍼티를 `ShaderResolver` (`Func<string, string>`)로 변경 |
| `src/IronRose.Rendering/PostProcessing/BloomEffect.cs` | `Path.Combine(ShaderDir, ...)` → `ShaderResolver(...)` 호출로 변경 |
| `src/IronRose.Rendering/PostProcessing/TonemapEffect.cs` | `Path.Combine(ShaderDir, ...)` → `ShaderResolver(...)` 호출로 변경 |
| `src/IronRose.Engine/Editor/PrefabVariantTree.cs` | Assets 경로를 `ProjectContext.AssetsPath`로 변경 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiSceneViewPanel.cs` | 직접 하드코딩 경로 없음 — `EditorAssets` 메서드 호출만 사용. B1(EditorAssets.cs 변경)으로 자동 해결 |
| `src/IronRose.Engine/ProjectSettings.cs` | 파일 탐색 경로를 `ProjectContext` 기반으로 변경 (이미 TOML 포맷 사용 중) |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs` | `EngineDirectories.LiveCodePath`, `FrozenCodePath` 직접 조합을 `ProjectContext` 사용으로 변경 |
| `src/IronRose.Engine/LiveCodeManager.cs` | `EngineDirectories` 경로를 `ProjectContext` 기반으로 변경 |
| `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs` | `Path.Combine("Assets", "Scenes")` 하드코딩을 `ProjectContext.AssetsPath` 기반으로 변경 |
| `src/IronRose.RoseEditor/Program.cs` | `Path.GetFullPath(Path.Combine("Assets", ...))` 하드코딩을 `ProjectContext` 기반으로 변경 |
| `src/IronRose.Engine/Editor/EditorState.cs` | `.rose_editor_state.toml` 생성 경로를 `ProjectContext.ProjectRoot` 기반으로 변경 |
| `src/IronRose.Engine/Editor/ImGui/ImGuiLayoutManager.cs` | `.rose_editor_state.toml` 참조 경로를 `ProjectContext.ProjectRoot` 기반으로 변경 |

#### 신규 파일

| 파일 | 내용 |
|------|------|
| `src/IronRose.Engine/ProjectContext.cs` | 프로젝트 경로 컨텍스트 |
| `src/IronRose.Engine/ShaderRegistry.cs` | 확장자 기반 재귀 셰이더 탐색 및 파일명-경로 맵 |
| `src/IronRose.Engine/Config/TomlConfig.cs` | TOML 설정 래퍼 (Tomlyn 기반). `ProjectContext`에서 `TomlConfig.Parse(path)` 형태로 사용 |
| `templates/default/project.toml` | 기본 프로젝트 설정 |
| `templates/default/Directory.Build.props` | MSBuild 엔진 경로 변수 (`$(IronRoseRoot)`) 정의 |
| `templates/default/*.csproj` | 기본 프로젝트 csproj 파일들 |
| `templates/default/.gitignore` | 프로젝트용 gitignore |

#### 삭제/이동 대상 (최종 단계)

| 현재 위치 | 이동 대상 |
|-----------|-----------|
| `IronRose/EditorAssets/` | `templates/default/EditorAssets/` |
| `IronRose/Assets/` | `templates/default/Assets/` (샘플만) |
| `IronRose/FrozenCode/` | `templates/default/FrozenCode/` |
| `IronRose/LiveCode/` | `templates/default/LiveCode/` |
| `IronRose/src/IronRose.RoseEditor/` | 템플릿 csproj로 대체 |
| `IronRose/src/IronRose.Standalone/` | 유지 — 에디터 Build 메뉴용 (별도 Phase에서 처리) |

---

## 구현 단계

### Phase A: 기반 인프라 (엔진 레포 내에서 진행)

- [x] A1: `Tomlyn` NuGet 패키지 추가 (`IronRose.Engine.csproj`) — 이미 v0.20.0 적용됨
- [ ] A2: `TomlConfig` 래퍼 클래스 구현 (`src/IronRose.Engine/Config/TomlConfig.cs`)
- [ ] A3: `ProjectContext` 클래스 구현 (`src/IronRose.Engine/ProjectContext.cs`)
- [ ] A4: `project.toml` 파일 포맷 정의 및 파서 구현
- [ ] A5: `EngineCore.Initialize()` 시작 부분에 `ProjectContext.Initialize()` 호출 추가

### Phase B: 경로 마이그레이션 (하위 호환 유지)

- [ ] B1: `EditorAssets.cs` -- `ProjectContext.EditorAssetsPath` 사용으로 변경
- [ ] B2: `ImGuiOverlay.cs` -- 폰트 경로 변경
- [ ] B3: `ThumbnailGenerator.cs` -- 폰트 경로 변경
- [ ] B4: `AssetDatabase.cs` -- RoseCache 경로 변경
- [ ] B5: `EngineCore.cs` -- Assets, Shaders, Cache 경로를 ProjectContext 기반으로 변경
- [ ] B6: `ShaderRegistry` 클래스 신규 구현 (`src/IronRose.Engine/ShaderRegistry.cs`)
- [ ] B6-init: `EngineCore.Initialize()`에 `ShaderRegistry.Initialize()` 호출 추가 (`ProjectContext.Initialize()` 직후)
- [ ] B6a: `RenderSystem.cs` -- `FindShaderDirectory()` 제거, `_shaderDir` 제거, 모든 셰이더 경로를 `ShaderRegistry.Resolve()` 호출로 변경
- [ ] B6b: `PostProcessStack.cs` -- `string shaderDir` → `Func<string, string> shaderResolver`로 시그니처 변경. `PostProcessEffect` 베이스의 `ShaderDir` → `ShaderResolver` 변경. `BloomEffect.cs`, `TonemapEffect.cs` -- `Path.Combine(ShaderDir, ...)` → `ShaderResolver(...)` 호출로 변경 (순환 의존성 회피를 위한 델리게이트 패턴)
- [ ] B6c: `SceneViewRenderer.cs`, `MeshPreviewRenderer.cs` -- `FindShaderDirectory()` 메서드 제거, `_shaderDir` 제거, 셰이더 경로를 `ShaderRegistry.Resolve()` 로 변경
- [ ] B7: `PrefabVariantTree.cs` -- Assets 경로 변경
- [ ] B8: `EngineConstants.cs` -- 폴더명 상수만 유지하도록 정리 (경로 조합은 ProjectContext 담당)
- [ ] B9: `ImGuiScriptsPanel.cs` -- LiveCode/FrozenCode 경로를 `ProjectContext` 기반으로 변경
- [ ] B10: `LiveCodeManager.cs` -- `EngineDirectories` 경로를 `ProjectContext` 기반으로 변경
- [ ] B11: `ImGuiProjectSettingsPanel.cs` -- Assets 하드코딩 경로를 `ProjectContext.AssetsPath` 기반으로 변경
- [ ] B12: `Program.cs` (RoseEditor) -- 하드코딩 에셋 경로를 `ProjectContext` 기반으로 변경
- [ ] B13: `EditorState.cs`, `ImGuiLayoutManager.cs` -- `.rose_editor_state.toml` 경로를 `ProjectContext.ProjectRoot` 기반으로 변경
- [ ] B14: `ProjectSettings.cs` -- 파일 탐색 경로를 `ProjectContext` 기반으로 변경

### Phase C: 레거시 호환 (`project.toml` 없을 때 현재 동작 유지)

- [ ] C1: `ProjectContext.Initialize()`에서 `project.toml` 미발견 시 CWD를 ProjectRoot로 사용하는 폴백 로직
- [ ] C2: 엔진 레포 루트에 `project.toml` 추가 (개발용, engine.path = ".")
- [ ] C3: 기존 `dotnet run --project src/IronRose.RoseEditor` 가 계속 동작하는지 검증

### Phase D: 템플릿 및 분리

- [ ] D1: `templates/default/` 디렉토리 생성 및 기본 파일 배치
- [ ] D2: `EditorAssets/`를 `templates/default/EditorAssets/`로 복사
- [ ] D3: 템플릿 csproj 파일 작성 (`{{ProjectName}}` 플레이스홀더)
- [ ] D4: 기본 `project.toml`, `.gitignore` 작성
- [ ] D5: 에디터 시작 화면 구현 — 프로젝트 미지정 시 강제 표시 (새 프로젝트 생성 / 기존 프로젝트 열기)
- [ ] D5a: File > New Project 메뉴 구현 — `templates/default/` 복사 + `project.toml` 치환 후 재시작
- [ ] D5b: 에디터가 프로젝트 없이 실행 가능하도록 `ProjectContext` 미초기화 상태 처리
  - `ProjectContext`에 `bool IsProjectLoaded` 프로퍼티 추가 (`project.toml` 발견 여부)
  - active project가 없는 상태(최초 실행, 또는 File > Close Project 후)에서는 **New Project / Open Project 기능만 활성화**
  - 프로젝트 의존 시스템 초기화 분기:
    - `RenderSystem` — 프로젝트 없으면 빈 배경만 렌더링 (씬 로드 스킵)
    - `AssetDatabase` — 프로젝트 없으면 초기화 스킵, 에셋 브라우저 패널 비활성화
    - `ScriptCompilationService` (LiveCodeManager) — 프로젝트 없으면 초기화 스킵
    - `ShaderRegistry` — 프로젝트 없으면 엔진 셰이더만 등록 (ProjectRoot 탐색 스킵)
  - ImGui 패널 비활성화: `ImGuiSceneViewPanel`, `ImGuiHierarchyPanel`, `ImGuiInspectorPanel`, `ImGuiAssetBrowserPanel`, `ImGuiScriptsPanel` 등 프로젝트 의존 패널은 `ProjectContext.IsProjectLoaded` 체크 후 비활성화/숨김
  - File > Close Project 실행 시 동일한 "프로젝트 없는 상태"로 전환 (시스템 언로드 → 시작 화면 표시)
- [ ] D6: 실제 에셋 프로젝트를 만들어 E2E 테스트

### Phase E: 정리

- [ ] E1: 엔진 레포에서 `EditorAssets/`, `Assets/`, `FrozenCode/`, `LiveCode/` 제거
- [ ] E2: `src/IronRose.RoseEditor/` 제거 (템플릿 csproj로 대체됨)
- [x] E3: `doc/ProjectStructure.md` 업데이트
- [x] E4: `.gitignore` 정리

> **Standalone 빌드**: `src/IronRose.Standalone/`은 이 Phase에서 제거하지 않는다. Unity의 Build Settings처럼 에디터의 Build 메뉴에서 최종 빌드를 출력하는 용도로, 별도 Phase(Phase F 이후)에서 에셋 프로젝트와 연동하는 빌드 파이프라인을 설계한다.

---

## 대안 검토

### NuGet 패키지 참조 vs 로컬 소스 참조

| 방식 | 장점 | 단점 |
|------|------|------|
| NuGet 패키지 | 버전 관리 명확, 배포 용이 | 빌드/배포 파이프라인 필요, 디버깅 불편 |
| **로컬 소스 참조 (선택)** | 즉시 변경 반영, 디버깅 용이, 인프라 불필요 | 엔진 경로가 로컬에 종속 |

현재 단계에서는 로컬 소스 참조가 개발 효율성 면에서 압도적으로 유리하다. 나중에 엔진이 안정되면 NuGet으로 전환 가능.

### Git Submodule vs 별도 레포

| 방식 | 장점 | 단점 |
|------|------|------|
| Submodule | 엔진 버전 고정 가능 | 관리 복잡, 초보자 혼란 |
| **별도 레포 + 로컬 경로 (선택)** | 단순, 유연 | 엔진 버전 추적 수동 |

### TOML vs JSON vs YAML

| 포맷 | 장점 | 단점 |
|------|------|------|
| **TOML (선택)** | 사람이 읽기 좋음, 주석 지원, 기존 `.rose_editor_state.toml` 호환 | 중첩 구조가 깊으면 불편 |
| JSON | 파서 풍부 | 주석 불가, 가독성 낮음 |
| YAML | 가독성 좋음 | 들여쓰기 민감, 파서 복잡 |

---

## 미결 사항

- **에디터 상태 파일 위치**: 현재 `.rose_editor_state.toml`이 CWD에 생성됨. 프로젝트 루트에 생성되도록 경로 변경 필요. (ProjectContext 도입 시 자연스럽게 해결)
- **프로젝트 생성**: CLI 방식(`dotnet new`) 대신 에디터 내에서 처리한다.
  - 에디터는 프로젝트 지정 없이도 실행 가능 (프로젝트 없는 상태 허용)
  - 프로젝트가 없는 상태(최초 실행, Close Project 후)에서는 **시작 화면**을 강제 표시 — "새 프로젝트 생성" / "기존 프로젝트 열기" 선택만 가능하며, 다른 에디터 기능은 비활성화
  - 이미 프로젝트가 열린 상태에서는 File > New Project / File > Close Project 메뉴 제공
  - 새 프로젝트 생성 시 `templates/default/`를 지정 경로에 복사하고 `project.toml`의 `name` 등을 치환 후 재시작
- **`project.toml` ↔ `Directory.Build.props` 경로 이중 관리**:

  **문제**: 런타임(`engine.path`)과 빌드타임(`IronRoseRoot`)이 서로 다른 경로를 가리키면, MSBuild 빌드는 성공하지만 런타임에 셰이더/에셋을 찾지 못하는 **디버깅하기 어려운 오류**가 발생한다. 에러 메시지가 "Shader not found" 같은 간접적 형태로 나타나 원인 파악이 어렵다.

  **현재 전략 (단기)**:
  - 기본값 `../IronRose`를 양쪽에서 공유
  - `ProjectContext.Initialize()` 시점에 양쪽 값을 비교하는 **검증 로직** 추가:
    ```csharp
    // ProjectContext.Initialize() 내부
    var propsPath = Path.Combine(ProjectRoot, "Directory.Build.props");
    if (File.Exists(propsPath))
    {
        var propsEngineRoot = ParseIronRoseRootFromProps(propsPath);
        if (propsEngineRoot != null && Path.GetFullPath(propsEngineRoot) != EngineRoot)
        {
            Logger.Error($"[ProjectContext] engine.path ({EngineRoot}) and " +
                $"Directory.Build.props IronRoseRoot ({propsEngineRoot}) mismatch! " +
                $"빌드/런타임 경로 불일치로 에셋 탐색 실패 가능.");
        }
    }
    ```
  - 경로 변경 시 양쪽 모두 수동으로 일치시켜야 함을 프로젝트 생성 시 안내

  **장기 전략**:
  - MSBuild 타겟으로 빌드 시 `project.toml`에서 `Directory.Build.props`의 `IronRoseRoot`를 자동 동기화하는 방안 검토
  - 또는 `Directory.Build.props`가 `project.toml`을 직접 읽는 MSBuild inline task 도입
- **EditorAssets 바이너리 관리**: 템플릿에 포함된 폰트/Matcap/Skybox 파일이 Git LFS 대상인지 여부 결정 필요
