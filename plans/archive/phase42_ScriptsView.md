# Phase 42: Scripts View 패널

## 개요

IronRose는 Unity와 달리 `Assets/` 폴더에 `.cs` 스크립트를 배치할 수 없다.
스크립트는 반드시 **LiveCode** (런타임 핫리로드) 또는 **FrozenCode** (컴파일타임) 프로젝트에 위치해야 한다.

현재 에디터에는 이 스크립트 파일들을 탐색/관리할 UI가 없어, 사용자가 외부 편집기나 파일 탐색기에 의존해야 한다.
이 Phase에서 **Scripts View** 패널을 추가하여 에디터 내에서 스크립트를 관리할 수 있도록 한다.

## 목표

- 에디터에 **Scripts** 패널 추가
- **LiveCode**, **FrozenCode** 두 루트 폴더를 트리 형태로 표시
- 각 폴더 하위의 `.cs` 파일을 탐색 가능
- 컨텍스트 메뉴: 새 스크립트 만들기, 복제하기

---

## 구현 계획

### 1단계: ImGuiScriptsPanel 기본 구조

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiScriptsPanel.cs`

```
ImGuiScriptsPanel : IEditorPanel
├── IsOpen: bool
├── Draw()
│   ├── 툴바 (검색, 새로 만들기 버튼)
│   └── 트리 뷰 영역
│       ├── 📁 LiveCode (루트 노드)
│       │   ├── 📁 하위폴더들...
│       │   └── 📄 *.cs 파일들
│       └── 📁 FrozenCode (루트 노드)
│           ├── 📁 하위폴더들...
│           └── 📄 *.cs 파일들
```

**구현 내용**:
- `IEditorPanel` 인터페이스 구현
- `ImGui.Begin("Scripts", ref _isOpen)` 윈도우
- 루트 폴더 2개를 각각 `ImGui.TreeNodeEx()`로 표시
- 하위 디렉토리는 재귀적 트리 노드
- `.cs` 파일은 Leaf 노드 (`ImGuiTreeNodeFlags.Leaf`)
- `.csproj` 등 비-스크립트 파일은 표시하지 않음

**디렉토리 경로 탐색**:
- `LiveCodeManager`가 이미 LiveCode 경로를 알고 있음
- FrozenCode 경로는 LiveCode와 동일 레벨 (`EngineDirectories`에 상수 추가 또는 직접 탐색)
- 루트 경로: `LiveCode/`, `FrozenCode/` (프로젝트 루트 기준)

### 2단계: 파일 시스템 스캔 및 트리 빌드

**내부 데이터 구조** (ImGuiProjectPanel의 FolderNode 패턴 참고):

```csharp
private class ScriptFolderNode
{
    public string Name;
    public string FullPath;
    public ScriptFolderNode? Parent;
    public List<ScriptFolderNode> SubFolders = new();
    public List<ScriptFileEntry> Scripts = new();
}

private struct ScriptFileEntry
{
    public string FileName;     // "MyComponent.cs"
    public string FullPath;     // 절대 경로
    public bool IsLiveCode;     // LiveCode 소속 여부
}
```

**트리 빌드**:
- `Directory.GetDirectories()` + `Directory.GetFiles("*.cs")` 재귀 스캔
- `bin/`, `obj/` 폴더 제외
- FileSystemWatcher로 변경 감지 → 자동 리프레시 (LiveCodeManager의 기존 watcher 활용 가능)

**리프레시 전략**:
- 패널 최초 열림 시 전체 스캔
- FileSystemWatcher 이벤트 수신 시 `_needsRebuild = true` 플래그
- Draw() 진입 시 플래그 확인 후 리빌드

### 3단계: 트리 뷰 렌더링

**UI 레이아웃**:

```
┌─ Scripts ──────────────────────────┐
│ [🔍 검색...]                       │
│ ─────────────────────────────────  │
│ ▼ LiveCode                         │
│   ├─ TestComponent.cs              │
│   └─ Test2.cs                      │
│ ▼ FrozenCode                       │
│   ├─ ▶ SubFolder/                  │
│   │   └─ SomeScript.cs            │
│   └─ DemoScene.cs                  │
└────────────────────────────────────┘
```

**기능**:
- 폴더 접기/펼치기 (`TreeNodeEx` + `OpenOnArrow`)
- 파일 단일 선택 (클릭 → 하이라이트)
- 검색 필터: 파일 이름 부분 일치 필터링
- 선택된 파일 경로를 외부에서 조회할 수 있도록 프로퍼티 노출
  - `SelectedScriptPath` — Inspector 등에서 스크립트 미리보기에 활용 가능 (추후)

### 4단계: 컨텍스트 메뉴

**폴더 우클릭 (LiveCode/FrozenCode 루트 및 하위 폴더)**:

| 메뉴 항목 | 동작 |
|-----------|------|
| **Create Script** | MonoBehaviour 템플릿으로 새 `.cs` 파일 생성 |
| **Create Folder** | 해당 폴더 아래 새 하위 폴더 생성 |

**파일 우클릭**:

| 메뉴 항목 | 동작 |
|-----------|------|
| **Duplicate** | 같은 폴더에 `{Name}_Copy.cs`로 복제 (클래스명도 변경) |
| **Rename** | 인라인 리네임 (EditorModal.InputTextPopup 활용) |
| **Delete** | 확인 모달 후 삭제 |
| **Open Containing Folder** | OS 파일 탐색기에서 열기 |

**빈 영역 우클릭** (`BeginPopupContextWindow`):

| 메뉴 항목 | 동작 |
|-----------|------|
| **Refresh** | 트리 강제 리빌드 |

### 5단계: 새 스크립트 생성

**MonoBehaviour 템플릿**:

```csharp
using RoseEngine;

public class {ClassName} : MonoBehaviour
{
    void Start()
    {

    }

    void Update()
    {

    }
}
```

**생성 흐름**:
1. 컨텍스트 메뉴 → "Create Script" 클릭
2. `EditorModal.InputTextPopup`으로 스크립트 이름 입력
3. 유효성 검사: C# 식별자 규칙, 중복 파일명 체크
4. 템플릿에서 `{ClassName}` 치환 후 UTF-8 BOM으로 파일 작성
5. 트리 리빌드 → 새 파일 자동 선택
6. LiveCode 폴더인 경우 → LiveCodeManager가 자동 감지하여 핫 리로드

### 6단계: 스크립트 복제

**복제 흐름**:
1. 컨텍스트 메뉴 → "Duplicate" 클릭
2. 원본 파일 읽기
3. 새 파일명 생성: `{Name}_Copy.cs` (중복 시 `_Copy2`, `_Copy3`...)
4. 파일 내용에서 클래스명을 새 파일명에 맞게 치환 (단순 텍스트 치환 — 원본 클래스명 → 새 클래스명)
5. UTF-8 BOM으로 저장
6. 트리 리빌드

### 7단계: ImGuiOverlay 통합

**변경 파일**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

- `ImGuiScriptsPanel` 인스턴스 필드 추가
- `Initialize()`에서 생성
- `Update()`에서 `Draw()` 호출
- View 메뉴에 "Scripts" 항목 추가

**변경 파일**: `src/IronRose.Engine/Editor/ImGui/ImGuiLayoutManager.cs`

- 기본 레이아웃에 Scripts 패널 위치 지정 (Project 패널 옆 또는 탭으로)

### 8단계: EngineConstants 업데이트

**변경 파일**: `src/IronRose.Engine/RoseEngine/EngineConstants.cs`

- `EngineDirectories.FrozenCodePath = "FrozenCode"` 상수 추가

---

## 변경 파일 목록

| 파일 | 변경 유형 | 설명 |
|------|-----------|------|
| `ImGuiScriptsPanel.cs` | **신규** | Scripts View 패널 전체 구현 |
| `ImGuiOverlay.cs` | 수정 | 패널 인스턴스 생성 및 Draw 호출 |
| `ImGuiLayoutManager.cs` | 수정 | 기본 레이아웃에 Scripts 패널 추가 |
| `EngineConstants.cs` | 수정 | FrozenCodePath 상수 추가 |

---

## 미구현 (향후 확장)

- **스크립트 미리보기**: Inspector에서 선택된 `.cs` 파일의 소스코드 표시
- **드래그 앤 드롭**: Scripts 패널에서 Inspector의 스크립트 필드로 드래그
- **LiveCode ↔ FrozenCode 이동**: 패널 간 드래그로 `/digest` 기능 대체
- **외부 편집기 연동**: 더블클릭 시 VS Code 등에서 열기
- **멀티 선택**: Ctrl/Shift 클릭으로 여러 파일 동시 선택 및 일괄 작업
