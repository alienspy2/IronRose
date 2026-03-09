# Phase 18: RoseEditor 독립 실행 프로젝트 + EditorFunction 분리

## Context

현재 에디터 UI는 `IronRose.Engine` 내부에 구현되어 있고, `IronRose.Demo`에서 F11로 토글하는 방식으로만 접근 가능하다. 에디터를 기본 진입점으로 사용하는 독립 실행 프로젝트가 없어, 에디터 중심 워크플로우가 불편하다. Demo에 있는 공용 유틸리티(카메라 생성, 폰트 로딩)도 분리가 필요하다.

## Phase 18 이후 의존성 구조

```
IronRose.RoseEditor (exe) ─┬─> IronRose.Editor (lib)
                           ├─> IronRose.Engine (lib)
                           ├─> IronRose.Contracts (lib)
                           └─> Silk.NET.Windowing

IronRose.Demo (exe) ───────┬─> IronRose.Editor (lib)  ← 신규 참조
                           ├─> IronRose.Engine (lib)
                           ├─> IronRose.Contracts (lib)
                           └─> Silk.NET.Windowing

IronRose.Editor (lib) ─────└─> IronRose.Engine (lib)
```

## 구현 단계

### Step 1. EngineCore에 `ShowEditor()` 공개 메서드 추가

**파일:** `src/IronRose.Engine/EngineCore.cs`

`Shutdown()` 메서드 아래(~243줄)에 추가:

```csharp
/// <summary>에디터 오버레이를 표시합니다. 이미 표시 중이면 무시합니다.</summary>
public void ShowEditor()
{
    if (_imguiOverlay != null && !_imguiOverlay.IsVisible)
        _imguiOverlay.Toggle();
}
```

기존 F11 토글 동작은 그대로 유지.

---

### Step 2. IronRose.Editor에 EditorUtils 생성 (공유 유틸리티)

**파일 유지:** `src/IronRose.Editor/IronRose.Editor.csproj` (변경 없음 — 이미 IronRose.Engine 참조)

**신규 파일:** `src/IronRose.Editor/EditorUtils.cs`

- `DemoUtils.CreateCamera()` → `EditorUtils.CreateCamera()` 이전
- `DemoUtils.LoadFont()` → `EditorUtils.LoadFont()` 이전
- `EditorUtils.CreateDefaultSceneCamera()` 추가 (RoseEditor 기본 씬용)
- namespace: `IronRose.Editor`

---

### Step 3. DemoUtils를 EditorUtils 위임으로 변경

**파일:** `src/IronRose.Demo/DemoUtils.cs`

기존 구현을 `EditorUtils` 호출로 대체 (메서드 시그니처 동일, 기존 호출처 변경 불필요):

```csharp
using IronRose.Editor;

public static class DemoUtils
{
    public static (Camera cam, Transform transform) CreateCamera(...)
        => EditorUtils.CreateCamera(...);

    public static Font LoadFont(int size = 32)
        => EditorUtils.LoadFont(size);
}
```

---

### Step 4. IronRose.Demo.csproj에 IronRose.Editor 참조 추가

**파일:** `src/IronRose.Demo/IronRose.Demo.csproj`

```xml
<ProjectReference Include="..\IronRose.Editor\IronRose.Editor.csproj" />
```

---

### Step 5. IronRose.RoseEditor 프로젝트 생성

**신규 디렉토리:** `src/IronRose.RoseEditor/`

**신규 파일:** `src/IronRose.RoseEditor/IronRose.RoseEditor.csproj`
- OutputType: Exe
- 참조: IronRose.Engine, IronRose.Contracts, IronRose.Editor, Silk.NET.Windowing

**신규 파일:** `src/IronRose.RoseEditor/Program.cs`
- Demo의 Program.cs와 동일한 구조 (Silk.NET 윈도우 + EngineCore 라이프사이클)
- 차이점:
  - 윈도우 타이틀: `"RoseEditor"`
  - `Initialize()` 직후 `_engine.ShowEditor()` 호출 → 에디터 즉시 표시
  - `OnWarmUpComplete`에서 `EditorUtils.CreateDefaultSceneCamera()` → 기본 빈 씬
  - DemoLauncher 없음 (데모 선택기 불필요)
  - OnAfterReload 미설정 (LiveCode 데모 없음)

---

### Step 6. IronRose.sln 업데이트

`dotnet sln` 명령으로 두 프로젝트 추가:

```bash
dotnet sln IronRose.sln add src/IronRose.Editor/IronRose.Editor.csproj --solution-folder src
dotnet sln IronRose.sln add src/IronRose.RoseEditor/IronRose.RoseEditor.csproj --solution-folder src
```

---

## 변경 파일 요약

| 파일 | 액션 | 설명 |
|------|------|------|
| `src/IronRose.Engine/EngineCore.cs` | 수정 | `ShowEditor()` 공개 메서드 추가 (~4줄) |
| `src/IronRose.Editor/EditorUtils.cs` | 신규 | 공유 유틸리티 (CreateCamera, LoadFont, CreateDefaultSceneCamera) |
| `src/IronRose.Demo/DemoUtils.cs` | 수정 | EditorUtils 위임으로 변경 |
| `src/IronRose.Demo/IronRose.Demo.csproj` | 수정 | IronRose.Editor 참조 추가 |
| `src/IronRose.RoseEditor/IronRose.RoseEditor.csproj` | 신규 | Exe 프로젝트 |
| `src/IronRose.RoseEditor/Program.cs` | 신규 | 에디터 진입점 (ShowEditor + 기본 씬) |
| `IronRose.sln` | 수정 | Editor + RoseEditor 프로젝트 등록 |

## 검증

1. `dotnet build IronRose.sln` — 전체 빌드 성공 확인
2. `IronRose.Demo` 실행 — 기존 동작 유지 (데모 선택, F11 에디터 토글)
3. `IronRose.RoseEditor` 실행 — 윈도우 타이틀 "RoseEditor", 에디터 즉시 표시, Game View에 기본 씬(스카이박스+카메라), 모든 패널 정상 동작
