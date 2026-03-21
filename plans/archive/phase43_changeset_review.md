# Phase 43 중간 포스트모템

> Phase43 (에셋/에디터 분리 대작업) 커밋 범위: `03e05ae` ~ `55bb0b4` (16개 커밋)

---

## 1. 큰 변경점 간 모순 검토

### 1.1 EngineDirectories vs ProjectContext — 이중 체계 잔존

**상태**: `EngineConstants.cs`의 `EngineDirectories`(폴더명 상수)와 `ProjectContext`(절대 경로 프로퍼티)가 공존.

**모순 아님, 단 역할 분리가 불완전**:
- `EngineDirectories.LiveCodePath` = `"LiveCode"` (폴더명 문자열)
- `ProjectContext.LiveCodePath` = `Path.Combine(ProjectRoot, "LiveCode")` (절대 경로)
- 문서 주석에서 "폴더명만 제공, 경로 조합은 ProjectContext 담당"으로 명시함.
- **문제**: `ImGuiScriptsPanel.cs`와 `LiveCodeManager.cs`가 아직 `EngineDirectories`를 직접 사용 중 → 아래 잔여 이슈 참조.

### 1.2 EditorAssetsPath 소유권 — 설계 vs 구현 불일치

**설계 문서** (`editor-assets-repo-separation.md`): `EditorAssetsPath` = `ProjectRoot/EditorAssets`
**실제 구현**: `EditorAssetsPath` = `EngineRoot/EditorAssets` (`internal static`)

이는 의도된 설계 변경이며, 에디터 에셋은 엔진이 소유한다는 결론에 도달한 것으로 보인다. **모순은 아니지만, 설계 문서가 업데이트되지 않았음**.

### 1.3 ProjectContext.Initialize() 재귀 호출 — 잠재적 무한 루프

```csharp
// project.toml이 없을 때:
var lastProjectPath = ReadLastProjectPath();
if (lastProjectPath != null)
{
    Initialize(lastProjectPath);   // ← 재귀 호출!
    if (IsProjectLoaded) return;
}
```

**위험도: 낮음 (현재는 안전)**
- `lastProjectPath`가 null이 아닌 경우에만 재귀 진입
- 재귀 진입 시 `projectRoot` 파라미터가 명시적으로 전달되므로 `FindProjectRoot()` 스킵
- 해당 경로에 `project.toml`이 있으면 toml 분기로 진입 → 재귀 없음
- 해당 경로에 `project.toml`이 없으면 다시 `ReadLastProjectPath()` 호출하지만, 같은 경로를 반환받아도 `File.Exists(Path.Combine(pathStr, "project.toml"))` 검증에서 걸러짐 → null 반환 → 재귀 안 함

**그러나**: `settings.toml`에 저장된 경로의 `project.toml`이 파싱 에러를 내는 경우 catch 블록에서 `IsProjectLoaded = false`가 되고, `return`하지 않고 다시 else 블록으로 빠지진 않으므로(if-else 구조) 안전. **단, 코드 가독성과 방어적 프로그래밍 측면에서 재귀 대신 반복 or 메서드 분리가 바람직**.

### 1.4 `IsProjectLoaded` 가드 일관성 — 양호

`EngineCore.Initialize()`에서:
```csharp
if (ProjectContext.IsProjectLoaded)
{
    InitAssets();
    InitLiveCode();
    InitGpuCompressor();
}
```
에디터 패널들도 각각 `if (!ProjectContext.IsProjectLoaded) return;` 가드가 있음. **일관되게 적용됨**.

### 1.5 SaveLastProjectPath()의 기존 설정 덮어쓰기

```csharp
var content = $"[editor]\nlast_project = \"{...}\"\n";
File.WriteAllText(GlobalSettingsPath, content);
```

`settings.toml`에 `[editor].last_project` 외에 다른 섹션이 추가되면 **전체 덮어쓰기로 기존 설정이 유실됨**. 현재는 `last_project`만 사용하므로 문제없지만, 향후 글로벌 설정이 확장되면 반드시 read-modify-write 패턴으로 변경 필요.

---

## 2. 잔여 이슈 (마이그레이션 미완료)

### 2.1 ImGuiScriptsPanel.FindRootDirectories() — CWD 기반 탐색 (B9 미완료)

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs:512~540`

```csharp
string[] searchRoots = { ".", "..", "../.." };  // CWD 기반!
// EngineDirectories.LiveCodePath / FrozenCodePath 직접 사용
```

**문제**: ProjectContext가 초기화된 상태에서도 CWD 기반으로 탐색하므로, CWD와 ProjectRoot가 다른 환경에서 LiveCode/FrozenCode를 찾지 못할 수 있음.

**수정 방향**: `ProjectContext.LiveCodePath` / `ProjectContext.FrozenCodePath` 직접 사용으로 변경.

### 2.2 ImGuiProjectSettingsPanel — 상대 경로 하드코딩 (B11 미완료)

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs:185`

```csharp
var scenesDir = Path.Combine("Assets", "Scenes");  // CWD 기준 상대!
```

**수정 방향**: `Path.Combine(ProjectContext.AssetsPath, "Scenes")` 사용.

### 2.3 RoseEditor Program.cs — .reimport_all 경로 (B12 부분 미완료)

**파일**: `src/IronRose.RoseEditor/Program.cs:46`

```csharp
var sentinelPath = Path.Combine(Directory.GetCurrentDirectory(), ".reimport_all");
```

`DefaultScenePath`는 이미 `ProjectContext.AssetsPath` 기반이지만, sentinel 파일 경로만 아직 CWD 기반. `ProjectContext.ProjectRoot` 사용이 적절.

### 2.4 Standalone Program.cs — Assets 경로 하드코딩

**파일**: `src/IronRose.Standalone/Program.cs:56`

```csharp
scenePath = Path.GetFullPath(Path.Combine("Assets", "Scenes", "DefaultScene.scene"));
```

`ProjectContext.AssetsPath` 기반으로 변경 필요.

### 2.5 IronRose.Editor EditorUtils.cs — CWD + 하드코딩

**파일**: `src/IronRose.Editor/EditorUtils.cs:28-29`

```csharp
var fontPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Fonts", "NotoSans_eng.ttf");
```

**주의**: `IronRose.Editor` 프로젝트가 `IronRose.Engine`을 참조하는지 확인 필요. 참조하면 `ProjectContext.AssetsPath` 사용 가능. 참조하지 않으면 별도 대응 필요.

### 2.6 templates/default/Program.cs — 템플릿 내 CWD 사용

**파일**: `templates/default/Program.cs:46`

```csharp
var sentinelPath = Path.Combine(Directory.GetCurrentDirectory(), ".reimport_all");
```

`src/IronRose.RoseEditor/Program.cs`와 동일 이슈. 템플릿도 함께 업데이트 필요.

---

## 3. 부작용 우려 사항

### 3.1 NativeFileDialog fallback — 무해하지만 불일치

`NativeFileDialog.cs:157,174,188`에서 `initialDir ?? Directory.GetCurrentDirectory()` 사용. 이는 다이얼로그의 초기 디렉토리 제안용이므로 기능적 문제는 없으나, `ProjectContext.AssetsPath`나 `ProjectRoot`가 더 적절한 기본값이 될 수 있음.

### 3.2 로그 디렉토리 전환 타이밍

```csharp
// EngineCore.cs:125~133
ProjectContext.Initialize();
if (ProjectContext.IsProjectLoaded)
{
    var projectLogDir = Path.Combine(ProjectContext.ProjectRoot, "Logs");
    RoseEngine.Debug.SetLogDirectory(projectLogDir);
}
```

`Debug`의 초기 로그 파일(`Logs/` in CWD)은 `Initialize()` 호출 전에 이미 열려 있으므로, 프로젝트 로드 성공 시 로그 파일이 중간에 전환됨. **초기 로그 몇 줄이 CWD의 Logs/에 남고 나머지는 ProjectRoot/Logs/에 기록되는 분산 현상** 발생 가능. 디버깅 시 혼동 우려.

### 3.3 LiveCodeManager의 EngineDirectories 직접 참조

`LiveCodeManager.cs:162`에서 `EngineDirectories.LiveCodePath`를 폴더명으로 사용하는 것은 의미적으로 맞으나, `Path.Combine(projectDir, EngineDirectories.LiveCodePath)` 패턴은 `projectDir`가 `src/*/` 하위이므로 `ProjectContext.LiveCodePath`와는 다른 경로를 가리킴. 이는 의도된 동작(src 하위의 LiveCode 탐색)이지만, `EngineDirectories`가 제거되면 인라인 `"LiveCode"` 리터럴로 대체해야 하므로 제거 시점 주의 필요.

### 3.4 ShaderRegistry — Exception throw로 인한 크래시 가능성

```csharp
throw new DirectoryNotFoundException(
    "[ShaderRegistry] Shaders directory not found. ...");
```

`ShaderRegistry.Initialize()`는 `EngineCore.Initialize()` 안에서 호출되므로, Shaders 디렉토리가 없으면 **엔진 전체가 시작 불가**. 프로젝트 미로드 상태(`IsProjectLoaded = false`)에서도 `ShaderRegistry.Initialize()`가 호출되므로, CWD에 Shaders/가 없는 환경(예: 사용자 프로젝트 디렉토리에서 바로 실행)에서 크래시 위험.

**참고**: `InitAssets()`/`InitLiveCode()`/`InitGpuCompressor()`는 `IsProjectLoaded` 가드가 있지만, `ShaderRegistry.Initialize()`에는 가드가 없음. 의도적일 수 있음(셰이더는 프로젝트 무관하게 필요).

### 3.5 A2 (TomlConfig 래퍼) 미구현 — 기술 부채

ProjectContext와 기타 TOML 사용처(EditorState, RoseConfig, ProjectSettings)가 모두 Tomlyn 직접 사용 패턴. 당장 문제는 아니지만, TOML 파싱 에러 핸들링이 각 파일에서 개별적으로 구현되어 있어 일관성 리스크.

### 3.6 E1/E2 미완료 — 엔진 레포에 에셋 프로젝트 잔재

설계 문서의 Phase E1(엔진 레포에서 Assets, EditorAssets, FrozenCode, LiveCode 제거)과 E2(IronRose.RoseEditor 제거)가 미완료. 현재 엔진 레포에 이 디렉토리들이 남아있어 `FindProjectRoot(CWD)` 시 엔진 레포 자체가 프로젝트로 인식될 가능성은 없음(project.toml이 없으므로), 하지만 `IsProjectLoaded = false`일 때 `EngineRoot = ProjectRoot = CWD`가 되어 엔진 레포의 Assets/를 에셋 경로로 사용하게 됨. **의도된 하위 호환 동작**이지만, E1 적용 시 이 경로가 사라지므로 기존 개발 워크플로우가 깨질 수 있음. E1 적용 전에 개발용 project.toml 추가 필요.

---

## 4. 요약: 우선순위별 조치 항목

| 우선순위 | 항목 | 위치 | 위험도 |
|----------|------|------|--------|
| **높음** | ShaderRegistry 크래시 가능성 (IsProjectLoaded 가드 없음) | ShaderRegistry.cs | 프로젝트 미로드 시 크래시 |
| **높음** | ImGuiScriptsPanel CWD 기반 탐색 (B9 미완료) | ImGuiScriptsPanel.cs:512 | CWD≠ProjectRoot 시 실패 |
| **중간** | ImGuiProjectSettingsPanel 상대 경로 (B11) | ImGuiProjectSettingsPanel.cs:185 | CWD≠ProjectRoot 시 실패 |
| **중간** | SaveLastProjectPath 전체 덮어쓰기 | ProjectContext.cs:202 | 향후 설정 확장 시 유실 |
| **중간** | Standalone Program.cs 하드코딩 | Standalone/Program.cs:56 | CWD≠ProjectRoot 시 실패 |
| **낮음** | RoseEditor .reimport_all 경로 | RoseEditor/Program.cs:46 | CWD≠ProjectRoot 시 실패 |
| **낮음** | EditorUtils.cs 폰트 경로 | EditorUtils.cs:29 | fallback이 있어 크래시 안함 |
| **낮음** | 로그 디렉토리 전환 분산 | EngineCore.cs:130 | 디버깅 혼동만 |
| **낮음** | 설계 문서 미갱신 (EditorAssetsPath 소유권) | plans/editor-assets-repo-separation.md | 문서와 코드 불일치 |
| **참고** | A2 TomlConfig 미구현 | — | 기술 부채 |
| **참고** | E1/E2 미완료 | — | 계획된 후속 작업 |
