# Phase 16: Project 패널 — 에셋 브라우저

## 목표

`AssetDatabase`가 시작 시 스캔한 에셋 정보를 기반으로 Unity 스타일의 **Project 패널**을 구현한다.
미리보기(썸네일) 없이 **텍스트 전용** 표시로 구성하며, 왼쪽에 폴더 트리, 오른쪽에 선택된 폴더의 에셋을 **타입:리스트** 형태로 보여준다.

---

## 현재 구조 분석

### AssetDatabase 흐름
```
EngineCore.InitAssets()
  → AssetDatabase.ScanAssets("Assets")
    → Directory.GetFiles(..., SearchOption.AllDirectories)
    → RoseMetadata.LoadOrCreate(file) → guid 추출
    → _guidToPath[guid] = filePath
```

### 핵심 제약
- `AssetDatabase._guidToPath`는 **private** → 외부에서 전체 에셋 경로 열거 불가
- 에셋 타입은 `RoseMetadata.importer["type"]`에 저장 (MeshImporter, TextureImporter, PrefabImporter 등)
- `Resources.GetAssetDatabase()`로 전역 접근 가능

### 기존 패널 패턴
- `IEditorPanel` 인터페이스: `IsOpen`, `Draw()`
- `ImGuiOverlay`에서 생성 → `Draw()` 호출
- Windows 메뉴에서 토글
- `ImGuiLayoutManager.ApplyDefaultIfNeeded()`에서 독 배치

---

## 작업 항목

### 1. AssetDatabase에 경로 열거 API 추가

`IAssetDatabase`와 `AssetDatabase`에 전체 에셋 경로를 반환하는 메서드를 추가한다.

**IAssetDatabase.cs** — 인터페이스 확장:
```csharp
/// <summary>스캔된 모든 에셋 경로를 반환.</summary>
IReadOnlyCollection<string> GetAllAssetPaths();
```

**AssetDatabase.cs** — 구현:
```csharp
public IReadOnlyCollection<string> GetAllAssetPaths()
    => _guidToPath.Values;
```

---

### 2. ImGuiProjectPanel 구현

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`

#### 2-1. 데이터 모델

에셋 경로 목록에서 폴더 트리 구조를 빌드한다. `ScanAssets` 이후 한 번만 빌드하고,
필요 시 Refresh 버튼으로 갱신.

```csharp
/// <summary>폴더 트리 노드.</summary>
private class FolderNode
{
    public string Name;                             // 폴더 이름 (예: "Textures")
    public string FullPath;                         // 전체 경로 (예: "Assets/Textures")
    public Dictionary<string, FolderNode> Children; // 하위 폴더
    public List<AssetEntry> Assets;                 // 이 폴더의 에셋 목록
}

/// <summary>에셋 엔트리.</summary>
private struct AssetEntry
{
    public string FileName;      // 파일 이름 (예: "character.glb")
    public string FullPath;      // 전체 경로
    public string ImporterType;  // "MeshImporter", "TextureImporter" 등
}
```

#### 2-2. 트리 빌드 로직

```
Assets/
├── Models/
│   ├── character.glb      → MeshImporter
│   └── environment.fbx    → MeshImporter
├── Textures/
│   ├── diffuse.png        → TextureImporter
│   └── normal.png         → TextureImporter
└── Prefabs/
    └── player.prefab      → PrefabImporter
```

`AssetDatabase.GetAllAssetPaths()`로 받은 경로들을 `/` 기준 분할 →
재귀적으로 `FolderNode` 트리를 구성한다. 에셋은 해당 폴더의 `Assets` 리스트에 추가.

#### 2-3. 왼쪽 패널: 폴더 트리

```csharp
// 2-column 스플릿
ImGui.BeginChild("FolderTree", new Vector2(treeWidth, 0), ImGuiChildFlags.Border);

// 재귀 트리 렌더링
void DrawFolderTree(FolderNode node)
{
    var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
    if (node.Children.Count == 0)
        flags |= ImGuiTreeNodeFlags.Leaf;
    if (node == _selectedFolder)
        flags |= ImGuiTreeNodeFlags.Selected;

    bool opened = ImGui.TreeNodeEx($"{node.Name}##{node.FullPath}", flags);

    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        _selectedFolder = node;

    if (opened)
    {
        foreach (var child in node.Children.Values.OrderBy(c => c.Name))
            DrawFolderTree(child);
        ImGui.TreePop();
    }
}

ImGui.EndChild();
```

#### 2-4. 오른쪽 패널: 에셋 리스트

선택된 폴더의 에셋을 **파일 이름 사전순**으로 정렬하여 플랫 리스트로 표시한다.

```
  character.glb
  diffuse.png
  environment.fbx
  normal.png
  player.prefab
  roughness.tga
```

```csharp
ImGui.BeginChild("AssetList", Vector2.Zero, ImGuiChildFlags.Border);

if (_selectedFolder != null)
{
    foreach (var asset in _selectedFolder.Assets.OrderBy(a => a.FileName))
    {
        bool selected = _selectedAssetPath == asset.FullPath;
        if (ImGui.Selectable($"  {asset.FileName}", selected))
            _selectedAssetPath = asset.FullPath;
    }
}

ImGui.EndChild();
```

---

### 3. ImGuiOverlay에 Project 패널 등록

**ImGuiOverlay.cs** 수정:

```csharp
// 필드 추가
private ImGuiProjectPanel? _project;

// Initialize()에서 생성
_project = new ImGuiProjectPanel();

// Draw panels 영역에 추가
_project?.Draw();

// Windows 메뉴에 항목 추가
bool p = _project!.IsOpen;
if (ImGui.MenuItem("Project", null, ref p))
    _project.IsOpen = p;
```

---

### 4. 기본 레이아웃 업데이트

**ImGuiLayoutManager.ApplyDefaultIfNeeded()** 시그니처에 `ImGuiProjectPanel` 파라미터 추가.

현재 레이아웃:
```
┌─────────────────────────────────────────┐
│ Left(18%) │  Center(55%)  │ Right(27%)  │
│ Hierarchy │  Game View    │ Inspector   │
│           │               │ RenderSet   │
├───────────┴───────────────┴─────────────┤
│              Bottom(25%) — Console      │
└─────────────────────────────────────────┘
```

변경 후 레이아웃 — Bottom 영역을 좌우 분할:
```
┌─────────────────────────────────────────┐
│ Left(18%) │  Center(55%)  │ Right(27%)  │
│ Hierarchy │  Game View    │ Inspector   │
│           │               │ RenderSet   │
├───────────┬─────────────────────────────┤
│BottomL    │    BottomR(60%) — Console   │
│(40%)      │                             │
│ Project   │                             │
└───────────┴─────────────────────────────┘
```

```csharp
// 기존 bottom split 이후:
ImGuiDockBuilder.SplitNode(bottomId, ImGuiDockBuilder.DirLeft, 0.40f,
    out uint bottomLeftId, out uint bottomRightId);

ImGuiDockBuilder.DockWindow("Project", bottomLeftId);
ImGuiDockBuilder.DockWindow("Console", bottomRightId);
```

---

### 5. Refresh 기능

패널 상단에 **Refresh** 버튼을 배치하여, 에셋 폴더에 변경이 있을 때 수동 갱신 가능하게 한다.

```csharp
if (ImGui.Button("Refresh"))
    RebuildTree();
```

`RebuildTree()`는 `Resources.GetAssetDatabase()?.GetAllAssetPaths()`를 다시 읽어 트리를 재구성한다.

---

## 파일 변경 목록

| 파일 | 변경 내용 |
|---|---|
| `AssetPipeline/IAssetDatabase.cs` | `GetAllAssetPaths()` 메서드 추가 |
| `AssetPipeline/AssetDatabase.cs` | `GetAllAssetPaths()` 구현 |
| `Editor/ImGui/Panels/ImGuiProjectPanel.cs` | **신규** — Project 패널 전체 구현 |
| `Editor/ImGui/ImGuiOverlay.cs` | 패널 필드/생성/Draw/메뉴 추가 |
| `Editor/ImGui/ImGuiLayoutManager.cs` | 시그니처 확장 + 기본 레이아웃 변경 |

---

## 검증 기준

- [ ] 에디터(F11) 활성화 시 Project 패널이 기본 레이아웃에 표시됨
- [ ] 왼쪽 트리에서 폴더 클릭 → 오른쪽에 해당 폴더의 에셋이 타입별로 그룹 표시
- [ ] 에셋이 없는 폴더 선택 시 오른쪽에 "Empty folder" 텍스트 표시
- [ ] Refresh 버튼 클릭 시 트리가 최신 상태로 갱신
- [ ] Windows → Project 메뉴로 패널 열기/닫기 토글
- [ ] 기존 패널(Hierarchy, Inspector, Console, GameView, RenderSettings)에 영향 없음
- [ ] `dotnet build` 성공

---

## 다음 단계

→ 미리보기 썸네일 지원 (Phase 17 이후)
→ 에셋 더블클릭 시 Inspector에 상세 정보 표시
→ 검색/필터 기능
→ 드래그 앤 드롭으로 씬에 에셋 배치
