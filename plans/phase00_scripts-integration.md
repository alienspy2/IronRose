# Phase 00: Scripts 통합 -- 디렉토리/csproj/상수/경로 변경

## 목표
- LiveCode + FrozenCode 이중 구조를 Scripts 단일 구조로 통합
- 상수, 경로 속성, 템플릿, csproj 참조, UI 패널, CLI 검색, 로그 메시지를 모두 Scripts로 변경
- MyGame 프로젝트의 디렉토리/파일도 Scripts로 마이그레이션

## 선행 조건
- 없음 (첫 번째 phase)

## 수정할 파일

### `src/IronRose.Engine/RoseEngine/EngineConstants.cs`
- **변경 내용**: `LiveCodePath`, `FrozenCodePath` 상수를 `ScriptsPath`로 통합
- **변경 전** (44~49행):
```csharp
        /// <summary>라이브 코드 폴더명.</summary>
        public const string LiveCodePath = "LiveCode";

        /// <summary>프로즌 코드 폴더명.</summary>
        public const string FrozenCodePath = "FrozenCode";
```
- **변경 후**:
```csharp
        /// <summary>스크립트 폴더명.</summary>
        public const string ScriptsPath = "Scripts";
```
- 파일 상단 `@exports` 주석의 `LiveCodePath`, `FrozenCodePath` 설명도 `ScriptsPath`로 변경

### `src/IronRose.Engine/ProjectContext.cs`
- **변경 내용**: `LiveCodePath`, `FrozenCodePath` 속성을 `ScriptsPath`로 통합
- **변경 전** (62~66행):
```csharp
        /// <summary>LiveCode/ 절대 경��.</summary>
        public static string LiveCodePath => Path.Combine(ProjectRoot, "LiveCode");

        /// <summary>FrozenCode/ 절대 경로.</summary>
        public static string FrozenCodePath => Path.Combine(ProjectRoot, "FrozenCode");
```
- **변경 후**:
```csharp
        /// <summary>Scripts/ 절대 경로.</summary>
        public static string ScriptsPath => Path.Combine(ProjectRoot, "Scripts");
```
- 파일 상단 `@exports` 주석의 `LiveCodePath`, `FrozenCodePath` 설명도 `ScriptsPath`로 변경
- `@note` 주석 부분도 적절히 수정

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs`
- **변경 내용**: LiveCode/FrozenCode 이중 트리를 Scripts 단일 트리로 변경
- **구체적 수정 사항**:
  1. 파일 상단 `@file`, `@brief`, `@note` 주석 -- "LiveCode/FrozenCode"를 "Scripts"로 변경
  2. 클래스 doc comment -- "LiveCode / FrozenCode 폴더"를 "Scripts 폴더"로 변경
  3. 필드 변경:
     - `_liveCodeRoot`, `_frozenCodeRoot` --> `_scriptsRoot` (단일 `string?`)
     - `_liveCodeTree`, `_frozenCodeTree` --> `_scriptsTree` (단일 `ScriptFolderNode?`)
     - `_liveCodeWatcher`, `_frozenCodeWatcher` --> `_scriptsWatcher` (단일 `FileSystemWatcher?`)
  4. `Draw()` 메서드 -- 트리 렌더링을 `_scriptsTree` 단일 노드로 변경:
     ```csharp
     if (_scriptsTree != null)
         DrawFolderNode(_scriptsTree, isRoot: true);
     ```
  5. `RebuildTree()` 메서드 -- `_scriptsRoot` 하나만 빌드:
     ```csharp
     private void RebuildTree()
     {
         if (_scriptsRoot != null && Directory.Exists(_scriptsRoot))
             _scriptsTree = BuildFolderNode(_scriptsRoot, "Scripts");
         else
             _scriptsTree = null;
     }
     ```
  6. `FindRootDirectories()` 메서드 -- `ProjectContext.ScriptsPath` 사용:
     ```csharp
     private void FindRootDirectories()
     {
         var scriptsDir = ProjectContext.ScriptsPath;

         if (Directory.Exists(scriptsDir))
             _scriptsRoot = scriptsDir;

         // Fallback: Scripts 디렉토리가 없으면 생성
         if (_scriptsRoot == null)
         {
             _scriptsRoot = scriptsDir;
             Directory.CreateDirectory(_scriptsRoot);
         }

         Debug.Log($"[Scripts] Scripts root: {_scriptsRoot}");
     }
     ```
  7. `SetupWatchers()` 메서드 -- 단일 watcher:
     ```csharp
     private void SetupWatchers()
     {
         if (_scriptsRoot != null)
             _scriptsWatcher = CreateWatcher(_scriptsRoot);
     }
     ```

### `src/IronRose.Engine/Cli/CliCommandDispatcher.cs`
- **변경 내용**: `ResolveComponentType()` 메서드의 FrozenCode/LiveCode 어셈블리 검색을 Scripts 단일 검색으로 변경
- **변경 전** (2446~2495행): FrozenCode/LiveCode 어셈블리를 순서대로 검색
- **변경 후**:
```csharp
/// <summary>
/// typeName 문자열로부터 Component Type을 찾는다.
/// 검색 순서: 1) RoseEngine 네임스페이스 (엔진 내장), 2) Scripts 어셈블리.
/// </summary>
private static Type? ResolveComponentType(string typeName)
{
    // 1. 엔진 내장 타입 (RoseEngine 네임스페이스)
    var engineAssembly = typeof(Component).Assembly;
    foreach (var type in engineAssembly.GetTypes())
    {
        if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
            return type;
    }

    // 2. Scripts 어셈블리 (collectible ALC)
    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
    {
        var asmName = asm.GetName().Name;
        if (asmName != null && asmName.Contains("Scripts", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var type in asm.GetTypes())
                {
                    if (type.Name == typeName && typeof(Component).IsAssignableFrom(type))
                        return type;
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // collectible ALC 해제 후 접근 시 발생 가능 -- 무시
            }
        }
    }

    return null;
}
```
- **추가 변경**: `assembly.info` 핸들러 (2084행 부근)의 "LiveCode" 용어를 "Scripts"로 변경:
  - `liveCodeTypes` 변수명 --> `scriptTypes`
  - `EngineCore.LiveCodeDemoTypes` --> `EngineCore.ScriptDemoTypes` (주의: 이 프로퍼티는 Phase 02에서 이름이 변경됨. 이 Phase에서는 아직 `LiveCodeDemoTypes`가 사용되므로 **변수명만 변경하고 프로퍼티 참조는 그대로 유지**)
  - JSON 응답의 `liveCodeDemoTypes` --> `scriptDemoTypes`, `liveCodeDemoCount` --> `scriptDemoCount`

### `src/IronRose.Engine/RoseEngine/SceneManager.cs`
- **변경 내용**: 117행의 주석 "LiveCode 마이그레이션"을 "Scripts 마이그레이션"으로 변경
- **변경 전** (117행):
```csharp
        /// LiveCode 마이��레이션 등에서 라이프사이클 콜백 없이 행동 등록 해제.
```
- **변경 후**:
```csharp
        /// Scripts 마이그레이션 등에서 라��프사이클 콜백 없이 행동 등록 해제.
```

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiInspectorPanel.cs`
- **변경 내용**: 주석의 "LiveCode" 참조를 "Scripts"로 변경 (1081행, 1100행)
- **변경 전**:
```csharp
            // 같은 이름의 타입 중복 방지 (LiveCode 리로드 시 이전 어셈블리가 남아있을 수 있음)
            ...
                        // 나중에 로드된 어셈블리(최신 LiveCode)가 ��전 것을 덮어씀
```
- **변경 후**:
```csharp
            // 같은 이름의 타입 중복 방지 (Scripts 리로드 시 이전 어셈블리가 남아있을 수 있음)
            ...
                        // 나중에 로드된 어셈블리(최신 Scripts)가 이전 것을 덮어씀
```

### `src/IronRose.Engine/Editor/EditorPlayMode.cs`
- **변경 내용**: 주석의 "LiveCodeManager"를 "ScriptReloadManager"로 변경 (48~49행)
- **변경 전**:
```csharp
        /// Play 모드 종료 후 호출되는 콜백.
        /// LiveCodeManager가 보류 중인 핫 리로드를 수행하는 데 사용됩니다.
```
- **변경 후**:
```csharp
        /// Play 모드 종료 후 호출되는 콜백.
        /// ScriptReloadManager가 보류 중��� 핫 리로드를 수행하는 데 사용됩니다.
```

### `src/IronRose.Contracts/Screen.cs`
- **변경 내용**: 주석의 "LiveCode"를 "Scripts"로 변경 (7행)
- **변경 전**:
```csharp
    /// LiveCode 스��립트에서 호출 가능.
```
- **변��� 후**:
```csharp
    /// Scripts 스크립트에서 호출 가능.
```

### `src/IronRose.Standalone/IronRose.Standalone.csproj`
- **변경 내용**: FrozenCode ProjectReference를 Scripts로 변경
- **변경 전** (18행):
```xml
    <ProjectReference Include="..\..\FrozenCode\FrozenCode.csproj" />
```
- **변경 후**:
```xml
    <ProjectReference Include="..\..\Scripts\Scripts.csproj" />
```

## 템플릿 디렉토리 변경

### `templates/default/LiveCode/` --> `templates/default/Scripts/`
- **작업**: 디렉토리명을 `LiveCode`에서 `Scripts`로 변경 (git mv 사용)
- `templates/default/LiveCode/LiveCode.csproj` --> `templates/default/Scripts/Scripts.csproj`

### `templates/default/Scripts/Scripts.csproj` (이름 변경된 파일)
- **변경 내용**: csproj 내용 수정
- **변경 전** (현재 LiveCode.csproj):
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- 빌드 출력을 런타임 디렉토리에 복사하지 않음 -->
    <!-- LiveCode는 Roslyn 런타임 컴파일 전용. 빌드는 IntelliSense 지원 목적으로만 수행 -->
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Contracts/IronRose.Contracts.csproj" />
  </ItemGroup>

</Project>
```
- **변�� 후**:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- dotnet build 핫리로드: DLL은 bin/Debug/net10.0/Scripts.dll로 출력됨 -->
    <!-- 의존성 DLL 복사 불필요 (엔진이 이미 로드한 어셈블리를 ALC Resolving으로 참조) -->
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Contracts/IronRose.Contracts.csproj" />
  </ItemGroup>

</Project>
```

### `templates/default/FrozenCode/` --> 삭제
- **작업**: `templates/default/FrozenCode/` 디렉토리 전체 삭제 (git rm -r 사용)

## MyGame 프로젝트 마이그레이션

### 디렉토리/파일 이동
1. `MyGame/Scripts/` 디렉토리 생성
2. `MyGame/LiveCode/` 하위의 모든 .cs 파일 및 서브디렉토리를 `MyGame/Scripts/`로 이동 (bin/, obj/ 제외)
   - `MyGame/LiveCode/HueRotator.cs` --> `MyGame/Scripts/HueRotator.cs`
   - `MyGame/LiveCode/SimpleGameBase.cs` --> `MyGame/Scripts/SimpleGameBase.cs`
   - `MyGame/LiveCode/AngryClawd/` --> `MyGame/Scripts/AngryClawd/` (하위 7개 .cs 파일 포함)
3. `MyGame/FrozenCode/` 하위의 .cs 파일을 `MyGame/Scripts/`로 이동 (bin/, obj/ 제외)
   - `MyGame/FrozenCode/PlayerPrefsTest.cs` --> `MyGame/Scripts/PlayerPrefsTest.cs`
4. `MyGame/LiveCode/` 디렉토리 삭제 (bin/, obj/ 포함 전부)
5. `MyGame/FrozenCode/` 디렉���리 삭제 (bin/, obj/ 포함 전부)

### `MyGame/Scripts/Scripts.csproj` (새로 생성)
- LiveCode.csproj 기반, 주석만 변경:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

    <!-- dotnet build 핫리로드: DLL은 bin/Debug/net10.0/Scripts.dll로 출력됨 -->
    <!-- 의존�� DLL 복사 불필요 (엔진이 이미 로드�� 어���블리를 ALC Resolving으��� 참조) -->
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Engine/IronRose.Engine.csproj" />
    <ProjectReference Include="$(IronRoseRoot)src/IronRose.Contracts/IronRose.Contracts.csproj" />
  </ItemGroup>

</Project>
```

### `MyGame/MyGame.sln`
- **변경 내용**: LiveCode/FrozenCode 프로젝트 참조를 Scripts로 변경
- LiveCode 프로젝트 엔트리(GUID `{3A479949-50B1-4681-9531-175BFFF2C176}`)를 Scripts로 변경:
  - `"LiveCode", "LiveCode\LiveCode.csproj"` --> `"Scripts", "Scripts\Scripts.csproj"` (GUID는 동일하게 유지 가능)
- FrozenCode 프로젝트 엔트리(GUID `{0343B9F4-9C20-48D0-A6A3-E740F91F884A}`)를 제거:
  - `Project` 블록과 `GlobalSection(ProjectConfigurationPlatforms)` 내의 해당 GUID 행 전부 삭제

## 검증 기준
- [ ] `dotnet build` 성공 (IronRose 엔진 솔루션)
- [ ] `dotnet build` 성공 (MyGame 솔루션: `dotnet build /home/alienspy/git/MyGame/MyGame.sln`)
- [ ] `templates/default/Scripts/Scripts.csproj` 파일이 존재
- [ ] `templates/default/FrozenCode/` 디렉토리가 삭제됨
- [ ] `templates/default/LiveCode/` 디렉토리가 삭제됨
- [ ] `MyGame/Scripts/Scripts.csproj` 파일이 존재
- [ ] `MyGame/LiveCode/` 디렉토리가 삭제됨
- [ ] `MyGame/FrozenCode/` 디렉토리가 삭제됨

## 참고
- 이 Phase에서는 `LiveCodeManager.cs`의 내부 코드는 수정하지 않는다. LiveCodeManager가 참조하는 `ProjectContext.LiveCodePath`가 `ProjectContext.ScriptsPath`로 변경되므로, LiveCodeManager 내부의 `ProjectContext.LiveCodePath` 호출도 `ProjectContext.ScriptsPath`로 변경해야 빌드가 성공한다. 이 수정은 최소한으로 수행한다 (Phase 01에서 파일 전체를 재작성하므로, 빌드만 통과하는 수준으로).
- `LiveCodeManager.cs`에서 `ProjectContext.LiveCodePath` 참조 (211행, 241행)를 `ProjectContext.ScriptsPath`로 변경해야 빌드 통과됨.
- `EngineCore.cs`에서 `ProjectContext.FrozenCodePath` 참조 (738행)를 `ProjectContext.ScriptsPath`로 변경해야 빌드 통과됨. 단, `InitFrozenCode()` 메서드는 Phase 02에서 삭제될 예정이므로 임시 수정.
- `EngineDirectories.LiveCodePath`를 참조하는 `LiveCodeManager.cs` 226행도 `EngineDirectories.ScriptsPath`로 변경 필요.
- `CliCommandDispatcher.cs`의 `assembly.info` 핸들러에서 `EngineCore.LiveCodeDemoTypes`를 참조하는데, 이 프로퍼티는 Phase 02에서 이름이 변경된다. 이 Phase에서는 **프로퍼티명은 그대로 유지**하고 출력 JSON key만 변경한다.
- MyGame은 별도 레포이므로, IronRose 엔진 `dotnet build`와는 별도로 `dotnet build /home/alienspy/git/MyGame/MyGame.sln`으로 확인한다.
- MyGame 디렉토리 변경은 `git mv`가 아닌 일반 파일 이동으로 수행한다 (별도 레포).
