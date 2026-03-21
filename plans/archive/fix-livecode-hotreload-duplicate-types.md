# LiveCode 핫 리로드 중복 타입 문제 해결

## 배경

`dotnet build`로 솔루션을 빌드하면 `LiveCode.csproj`도 함께 빌드되어 `LiveCode.dll`이 출력 디렉토리에 생성된다.
런타임에서는 `LiveCodeManager`가 Roslyn을 사용하여 `LiveCode/*.cs` 파일을 독립적으로 컴파일하고 `AssemblyLoadContext`에 로드한다.
그 결과, **빌드 타임 `LiveCode.dll`**(Default ALC에 의해 자동 로드)과 **Roslyn 컴파일 어셈블리**(커스텀 ALC에 로드)가 동시에 존재하여 동일한 타입이 두 개씩 등록되는 문제가 발생한다.

이로 인해:
- 컴포넌트 타입 검색 시 중복 타입이 발견됨
- `MigrateEditorComponents`에서 타입 매칭 혼란 발생 가능
- Add Component 메뉴에 동일 컴포넌트가 두 번 표시될 수 있음

## 목표

- 런타임에 LiveCode 타입이 **오직 Roslyn 컴파일 버전만** 존재하도록 보장
- IDE IntelliSense 지원을 위해 `LiveCode.csproj`는 솔루션에 유지
- 빌드 시 `LiveCode.dll`이 런타임 출력 디렉토리에 복사되지 않도록 방지

## 현재 상태

### 솔루션 구성 (`IronRose.sln`)

- `LiveCode` 프로젝트(GUID: `{E210D90A-...}`)는 솔루션에 포함되어 있음
- `Build.0` 라인이 이미 제거되어 있어 솔루션 레벨 빌드에서는 빌드 대상에서 제외됨
- 그러나 `ActiveCfg` 라인은 남아 있어 IDE에서 프로젝트를 인식함

### LiveCode.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    ...
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\src\IronRose.Engine\IronRose.Engine.csproj" />
    <ProjectReference Include="..\src\IronRose.Contracts\IronRose.Contracts.csproj" />
  </ItemGroup>
</Project>
```

- OutputType 미지정 (기본값: Library) -> `LiveCode.dll` 생성
- `IronRose.Engine`과 `IronRose.Contracts`를 참조 (Roslyn 컴파일 시에도 동일 참조 필요)

### RoseEditor.csproj

- LiveCode에 대한 `ProjectReference` 없음 (확인 완료)
- `FrozenCode`만 `ProjectReference`로 참조

### LiveCodeManager (`LiveCodeManager.cs`)

- `FindLiveCodeDirectories()`: `src/*/LiveCode/` 경로를 탐색하고, 없으면 루트 `LiveCode/` 폴더를 폴백으로 사용
- `CompileAllLiveCode()`: Roslyn `ScriptCompiler`로 `*.cs` 파일을 컴파일하여 byte[]로 받음
- `ScriptDomain.LoadScripts()`: 새 `AssemblyLoadContext`(collectible)에 로드
- `RegisterLiveCodeBehaviours()`: MonoBehaviour 파생 타입을 검색하여 `LiveCodeDemoTypes`에 등록

### ScriptDomain (`ScriptDomain.cs`)

- collectible `AssemblyLoadContext` 사용 -> 언로드/리로드 가능
- `Resolving` 이벤트에서 Default ALC 폴백으로 엔진 어셈블리 참조 해결
- `Reload()` 시 이전 ALC를 `Unload()` 후 GC 강제 실행

## 설계

### 개요

**핵심 전략**: `LiveCode.csproj`의 빌드 출력이 런타임 디렉토리에 도달하지 않도록 csproj 수준에서 차단한다.

세 가지 변경을 조합한다:

1. **`LiveCode.csproj`에 빌드 출력 억제 속성 추가** -- 빌드 자체는 되더라도(IntelliSense 필요) dll이 다른 프로젝트의 출력에 복사되지 않도록 함
2. **방어적 코드 추가** -- 만약 `LiveCode.dll`이 Default ALC에 로드되었더라도 해당 타입을 무시하는 로직 추가
3. **`FindLiveCodeDirectories` 정리** -- 루트 `LiveCode/` 경로를 주요 탐색 대상으로 승격

### 상세 설계

#### 변경 1: LiveCode.csproj 수정

`LiveCode.csproj`에 다음 속성을 추가하여, 빌드 산출물이 다른 프로젝트의 출력 디렉토리에 복사되는 것을 방지한다.

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
    <ProjectReference Include="..\src\IronRose.Engine\IronRose.Engine.csproj" />
    <ProjectReference Include="..\src\IronRose.Contracts\IronRose.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`LiveCode.csproj`는 RoseEditor/Standalone에서 `ProjectReference`로 참조하지 않으므로, 위 속성만으로도 dll 복사가 발생하지 않아야 한다. 그러나 `dotnet build` 시 솔루션 레벨에서 빌드될 경우를 대비한 방어 조치이다.

**추가로**, 솔루션의 `Build.0` 라인이 이미 제거되어 있어 `dotnet build IronRose.sln` 시 LiveCode가 빌드 대상에서 제외된다. 만약 사용자가 `dotnet build LiveCode/LiveCode.csproj`를 직접 실행하는 경우에만 빌드되며, 그 산출물은 `LiveCode/bin/` 에만 남는다.

#### 변경 2: LiveCodeManager에 방어적 중복 타입 감지 추가

만약 어떤 이유로 `LiveCode.dll`이 Default ALC에 로드되었을 경우를 방어하기 위해, `LiveCodeManager.Initialize()` 시점에 경고를 출력하고 해당 어셈블리의 타입을 무시하는 로직을 추가한다.

**`LiveCodeManager.cs` -- `Initialize()` 메서드 시작부에 추가**:

```csharp
// 빌드 타임 LiveCode.dll이 Default ALC에 로드되었는지 확인
var buildTimeLiveCode = AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "LiveCode");
if (buildTimeLiveCode != null)
{
    RoseEngine.Debug.LogWarning(
        "[Engine] Build-time LiveCode.dll detected in Default ALC! " +
        "This may cause duplicate types. " +
        "Ensure LiveCode.csproj is excluded from build output. " +
        $"Location: {buildTimeLiveCode.Location}");
}
```

이 경고는 문제 발생 시 원인 파악을 쉽게 해준다. 타입 자체를 차단하는 것은 `RegisterLiveCodeBehaviours()`에서 이미 Roslyn 컴파일된 `ScriptDomain`의 타입만 참조하고 있으므로 추가 필터링은 불필요하다.

#### 변경 3: FindLiveCodeDirectories 경로 탐색 개선

현재 `FindLiveCodeDirectories()`는 `src/*/LiveCode/` 패턴을 우선 탐색하고 루트 `LiveCode/`를 폴백으로 사용한다. 실제 프로젝트 구조에서는 루트 `LiveCode/`가 주요 경로이므로 이를 반영한다.

**`LiveCodeManager.cs` -- `FindLiveCodeDirectories()` 수정**:

```csharp
private void FindLiveCodeDirectories()
{
    // 1) 루트 LiveCode/ 디렉토리 탐색 (주요 경로)
    string[] searchRoots = { ".", "..", "../.." };
    foreach (var root in searchRoots)
    {
        string rootLiveCode = Path.GetFullPath(
            Path.Combine(root, RoseEngine.EngineDirectories.LiveCodePath));
        if (Directory.Exists(rootLiveCode) && !_liveCodePaths.Contains(rootLiveCode))
        {
            _liveCodePaths.Add(rootLiveCode);
            RoseEngine.Debug.Log($"[Engine] Found LiveCode directory: {rootLiveCode}");
            break;
        }
    }

    // 2) src/*/LiveCode/ 하위 디렉토리도 추가 탐색 (확장성)
    foreach (var root in searchRoots)
    {
        string srcDir = Path.GetFullPath(Path.Combine(root, "src"));
        if (!Directory.Exists(srcDir)) continue;

        foreach (var projectDir in Directory.GetDirectories(srcDir))
        {
            string liveCodeDir = Path.Combine(
                projectDir, RoseEngine.EngineDirectories.LiveCodePath);
            if (!Directory.Exists(liveCodeDir)) continue;

            string fullPath = Path.GetFullPath(liveCodeDir);
            if (!_liveCodePaths.Contains(fullPath))
            {
                _liveCodePaths.Add(fullPath);
                RoseEngine.Debug.Log($"[Engine] Found LiveCode directory: {fullPath}");
            }
        }
        break;
    }

    // 3) 아무것도 못 찾으면 생성
    if (_liveCodePaths.Count == 0)
    {
        string fallback = Path.GetFullPath(RoseEngine.EngineDirectories.LiveCodePath);
        Directory.CreateDirectory(fallback);
        _liveCodePaths.Add(fallback);
        RoseEngine.Debug.Log($"[Engine] Created LiveCode directory: {fallback}");
    }
}
```

### 영향 범위

| 파일 | 변경 유형 | 설명 |
|------|----------|------|
| `LiveCode/LiveCode.csproj` | 수정 | `ProduceReferenceAssembly`, `CopyLocalLockFileAssemblies` 속성 추가 |
| `src/IronRose.Engine/LiveCodeManager.cs` | 수정 | `Initialize()`에 중복 감지 경고 추가, `FindLiveCodeDirectories()` 탐색 순서 변경 |

- 기존 기능에 미치는 영향: 없음. LiveCode의 Roslyn 컴파일/핫 리로드 동작은 변경되지 않음.
- IDE IntelliSense: 영향 없음. `LiveCode.csproj`는 솔루션에 유지되어 코드 편집 시 타입 해석 정상 작동.

## 구현 단계

- [ ] 단계 1: `LiveCode/LiveCode.csproj`에 빌드 출력 억제 속성 추가
- [ ] 단계 2: `LiveCodeManager.Initialize()`에 빌드 타임 LiveCode.dll 감지 경고 추가
- [ ] 단계 3: `FindLiveCodeDirectories()` 탐색 순서를 루트 `LiveCode/` 우선으로 변경
- [ ] 단계 4: `dotnet build` 후 RoseEditor 출력 디렉토리에 `LiveCode.dll`이 없는지 확인
- [ ] 단계 5: `dotnet run --project src/IronRose.RoseEditor` 실행하여 핫 리로드 정상 동작 확인
- [ ] 단계 6: 경고 로그가 출력되지 않는지 확인 (빌드 타임 dll이 로드되지 않았음을 검증)

## 대안 검토

### 대안 A: 솔루션에서 LiveCode 프로젝트 제거

- 장점: 가장 확실한 차단
- 단점: IDE에서 LiveCode 파일의 IntelliSense가 동작하지 않음. 개발 경험 저하.
- **불채택 이유**: 사용자가 IntelliSense 유지를 원함

### 대안 B: LiveCodeManager에서 Default ALC의 LiveCode 타입을 런타임 필터링

- 장점: csproj 변경 없이 코드만으로 해결 가능
- 단점: 근본 원인(dll 존재)을 해결하지 않음. Default ALC에 로드된 어셈블리는 언로드할 수 없어 메모리 낭비.
- **불채택 이유**: 증상 치료에 불과하며 근본 해결이 아님. 다만 경고 로그는 방어 코드로 채택.

### 대안 C: `Directory.Build.props`에서 LiveCode 빌드 조건 설정

- 장점: 중앙 집중식 관리
- 단점: 이미 `Build.0` 라인이 제거되어 있어 추가 효과가 제한적. 조건부 빌드 로직이 복잡해짐.
- **불채택 이유**: 현재 구조에서는 csproj 직접 수정이 더 명확함

## 미결 사항

없음. 사용자의 답변으로 모든 불확실한 사항이 해소되었다.
