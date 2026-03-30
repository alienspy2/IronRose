# Phase 47d: 정리 -- ScriptCompiler 삭제, Roslyn 패키지 제거, 문서 갱신

## 목표
- `ScriptCompiler.cs` 파일 삭제
- `IronRose.Scripting.csproj`에서 `Microsoft.CodeAnalysis.CSharp` 패키지 참조 제거
- `doc/ScriptHotReloading.md` 전면 재작성
- `.claude/commands/digest.md` 삭제
- `CLAUDE.md` 및 `doc/ProjectStructure.md`의 LiveCode/FrozenCode 용어를 Scripts로 변경

## 선행 조건
- Phase 47c 완료 (EngineCore 통합 완료, ScriptCompiler 사용처가 모두 제거됨)
- `ScriptCompiler` 타입을 참조하는 코드가 없음

## 삭제할 파일

### `src/IronRose.Scripting/ScriptCompiler.cs`
- **역할**: Roslyn 기반 C# 스크립트 동적 컴파일러
- **삭제 이유**: dotnet build 방식으로 전환됨에 따라 Roslyn 인메모리 컴파일이 불필요
- **포함 클래스**: `ScriptCompiler`, `CompilationResult`, `CompileError` (파일 내 모든 클래스)

### `.claude/commands/digest.md`
- **역할**: LiveCode -> FrozenCode 스크립트 승격 커맨드
- **삭제 이유**: Scripts 단일 구조로 통합되어 LiveCode -> FrozenCode 이동이 불필요

## 수정할 파일

### `src/IronRose.Scripting/IronRose.Scripting.csproj`
- **변경 내용**: `Microsoft.CodeAnalysis.CSharp` 패키지 참조 제거
- **변경 전**:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\IronRose.Contracts\IronRose.Contracts.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```
- **변경 후**:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\IronRose.Contracts\IronRose.Contracts.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

### `doc/ScriptHotReloading.md`
- **변경 내용**: 전면 재작성
- **변경 후** (전체 파일):
```markdown
# IronRose 스크립트 핫 리로드

## Scripts 프로젝트 구조

```
IronRose.Engine  <---- Scripts (csproj 참조, 실행 시 직접 참조하지 않음)
IronRose.Contracts <-+
                       |
                  ScriptReloadManager -- dotnet build + DLL 로드 (FileSystemWatcher 감시)
                  Standalone -- Scripts (ProjectReference)
```

- **Scripts** -- 게임 스크립트 단일 프로젝트. `dotnet build`로 컴파일되며, 에디터에서는 ScriptReloadManager가 핫 리로드 수행.
- Standalone 빌드에서는 ProjectReference로 직접 참조.

## 스크립트 핫 리로드

```
1. Scripts/*.cs 수정
2. FileSystemWatcher 감지 (0.5초 trailing edge debounce)
3. dotnet build --no-restore Scripts.csproj
4. File.ReadAllBytes(Scripts.dll) -> ALC 로드
5. MigrateEditorComponents() -> 씬 컴포넌트 마이그레이션
```

## Play Mode 동작

- Play mode 진입 시: FileSystemWatcher 중단
- Play mode 종료 시: FileSystemWatcher 재활성화, 변경 감지 시 일괄 빌드/리로드
```

### `CLAUDE.md`
- **변경 내용**: "LiveCode" 관련 용어를 "Scripts"로 변경
- **변경 위치**: 45행 부근
- **변경 전**:
```markdown
IronRose는 개발 중인 엔진이다. 게임(LiveCode) 구현 중 문제가 발생했을 때, 그 원인이 엔진이나 에디터의 미비한 기능/버그에 있다면 **게임 코드에서 우회(workaround)하지 말고 엔진/에디터 쪽을 먼저 개선**할 것.
```
- **변경 후**:
```markdown
IronRose는 개발 중인 엔진이다. 게임(Scripts) 구현 중 문제가 발생했을 때, 그 원인이 엔진이나 에디터의 미비한 기능/버그에 있다면 **게임 코드에서 우회(workaround)하지 말고 엔진/에디터 쪽을 먼저 개선**할 것.
```

### `doc/ProjectStructure.md`
- **변경 내용**: LiveCode/FrozenCode 관련 설명을 Scripts로 변경
- **주요 변경 위치들**:
  1. 2-레포 아키텍처 디렉토리 구조 (8~18행):
     ```
     MyGame/                <- 에셋 프로젝트 (별도 레포)
       ...
       Scripts/
       ...
     ```
     `FrozenCode/`, `LiveCode/` 행을 `Scripts/` 하나로 변경
  2. `IronRose.Scripting` 설명 (60행): "Roslyn 런타임 컴파일 (핫 리로드)" --> "스크립트 핫 리로드 (ALC 도메인 관리)"
  3. 프로젝트 템플릿 설명 (82~84행):
     - `FrozenCode/FrozenCode.csproj -- 안정 스크립트` 삭제
     - `LiveCode/LiveCode.csproj -- 실험 스크립트 (Roslyn 핫 리로드)` --> `Scripts/Scripts.csproj -- 게임 스크립트 (dotnet build 핫 리로드)`
  4. 경로 시스템 테이블 (99~101행):
     - `LiveCodePath` / `FrozenCodePath` 행을 `ScriptsPath` 하나로 변경:
       ```
       | `ProjectContext.ScriptsPath` | `ProjectRoot/Scripts` |
       ```

## 검증 기준
- [ ] `dotnet build` 성공 (전체 솔루션)
- [ ] `dotnet build` 성공 (MyGame 솔루션)
- [ ] `src/IronRose.Scripting/ScriptCompiler.cs` 파일이 삭제됨
- [ ] `.claude/commands/digest.md` 파일이 삭제됨
- [ ] `IronRose.Scripting.csproj`에 `Microsoft.CodeAnalysis.CSharp` 참조가 없음
- [ ] grep으로 전체 `src/` 하위에서 `ScriptCompiler` 참조가 없음 확인
- [ ] grep으로 전체 `src/` 하위에서 `LiveCode` 문자열이 없음 확인 (주석 포함)
- [ ] grep으로 전체 `src/` 하위에서 `FrozenCode` 문자열이 없음 확인
- [ ] 에디터 실행 후 Scripts 패널이 정상 표시됨 (선택 사항 -- 수동 검증)

## 참고
- `ScriptCompiler.cs` 삭제 시, 이 파일의 `CompilationResult` 클래스와 `CompileError` 클래스도 함께 삭제된다. Phase 47b~02에서 이미 사용처가 모두 제거되었으므로 빌드 오류가 발생하지 않아야 한다.
- Roslyn 패키지(`Microsoft.CodeAnalysis.CSharp`)를 제거하면 빌드 시간과 출력 크기가 줄어든다.
- `doc/ProjectStructure.md`와 `CLAUDE.md`는 개발자 가이드 문서이므로, 최신 상태를 정확히 반영해야 한다.
- `.claude/commands/digest.md` 삭제 후, 이 커맨드를 참조하는 다른 문서가 있는지 확인할 것 (`doc/ScriptHotReloading.md`에서 `/digest` 참조가 있었으나 이미 재작성됨).
- `IronRose.Scripting` 프로젝트 자체는 유지된다 (ScriptDomain, StateManager, IHotReloadable이 여전히 필요).
