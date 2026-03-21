# Fix: ImGuiProjectPanel이 CWD 기준 상대 경로를 사용하는 문제

## 현상
- Project 패널에서 "Open Containing Folder" 클릭 시 엔진 루트의 Assets 폴더가 열림
- Material 생성 시 엔진 루트에 저장됨 (프로젝트 루트가 아닌)
- 2-레포 구조에서 CWD(엔진 루트) ≠ ProjectRoot(프로젝트 루트)일 때 발생

## 근본 원인

`ImGuiProjectPanel.RebuildTree()` (line 1877):
```csharp
_root = new FolderNode { Name = "Assets", FullPath = "Assets" };
```

`FullPath`가 `"Assets"` (상대 경로)로 설정됨. 이후 패널 전체에서:
```csharp
Path.GetFullPath(target.FullPath)  // → CWD/Assets/ (엔진 루트!)
```

`Path.GetFullPath()`는 CWD 기준으로 해석하므로, CWD가 엔진 루트이면 항상 엔진 루트의 Assets를 가리킴.

## 영향 범위

`ImGuiProjectPanel.cs`에서 `Path.GetFullPath(*.FullPath)` 사용하는 모든 곳:

| Line | 기능 | 코드 |
|------|------|------|
| 287 | Open Containing Folder (트리 컨텍스트) | `Path.GetFullPath(target.FullPath)` |
| 494 | 썸네일 경로 | `Path.GetFullPath(_selectedFolder!.FullPath)` |
| 502 | Open Containing Folder (폴더 컨텍스트) | `Path.GetFullPath(_selectedFolder!.FullPath)` |
| 840 | Open Containing Folder (에셋) | `Path.GetFullPath(asset.FullPath)` |
| 1136 | 썸네일 경로 (그리드) | `Path.GetFullPath(node.FullPath)` |
| 1144 | Open Containing Folder (그리드) | `Path.GetFullPath(node.FullPath)` |
| 1164 | Create Folder | `Path.GetFullPath(Path.Combine(target.FullPath, ...))` |
| 1194 | Create Material | `Path.GetFullPath(Path.Combine(target.FullPath, ...))` |
| 1227 | Create Animation | `Path.GetFullPath(Path.Combine(target.FullPath, ...))` |
| 1269 | Create Renderer Profile | `Path.GetFullPath(Path.Combine(target.FullPath, ...))` |
| 1301 | Create PP Profile | `Path.GetFullPath(Path.Combine(target.FullPath, ...))` |
| 1456 | Rename | `Path.GetFullPath(node.FullPath)` |
| 1596 | Delete | `Path.GetFullPath(node.FullPath)` |
| 1936 | RebuildTree 디렉토리 스캔 | `Path.GetFullPath(_root.FullPath)` |

## 수정 방법

### Option A: FolderNode.FullPath를 절대 경로로 변경 (권장)

`RebuildTree()`에서 루트 노드 생성 시 `ProjectContext.AssetsPath`를 사용:

```csharp
_root = new FolderNode { Name = "Assets", FullPath = ProjectContext.AssetsPath };
```

이러면 모든 하위 노드의 `FullPath`도 절대 경로가 되고, `Path.GetFullPath()` 호출이 CWD에 의존하지 않음. 기존 `Path.GetFullPath()` 호출을 대부분 그대로 둬도 동작함 (이미 절대 경로이므로 noop).

**확인 필요**: `FolderNode.FullPath`가 상대 경로임을 가정하는 코드가 있는지 (예: `Substring("Assets/".Length)` 같은 패턴).

### Option B: 헬퍼 메서드 추가

```csharp
private static string ToAbsolutePath(string relativePath)
    => Path.GetFullPath(Path.Combine(ProjectContext.ProjectRoot, relativePath));
```

모든 `Path.GetFullPath(*.FullPath)` 호출을 `ToAbsolutePath(*.FullPath)`로 교체. 안전하지만 수정 범위가 넓음.

## 관련 이슈

- `AssetDatabase`의 `allPaths`가 반환하는 경로가 절대 경로인지 상대 경로인지에 따라 Option A의 동작이 달라질 수 있음
- `AssetDatabase.ScanAssets(projectPath)`에서 `projectPath`는 절대 경로 → 에셋 경로도 절대 경로로 저장됨
- 따라서 `RebuildTree`의 `allPaths`는 절대 경로 → `assetsIdx`로 잘라서 상대 경로 `relativePath`를 만듦 (line 1891-1894)
- `AssetNode.FullPath`는 `allPaths`의 원본 절대 경로를 사용 (line 1930: `FullPath = fullPath`)
- **FolderNode.FullPath만 상대 경로** — 이것이 불일치의 원인

## 결론

`FolderNode.FullPath`를 절대 경로로 통일하면 (Option A) 최소 수정으로 해결됨:
1. Line 1877: `FullPath = ProjectContext.AssetsPath`
2. 하위 폴더 생성 부분도 절대 경로로 구성되는지 확인 (line 1898-1910 부근)
3. `_root.FullPath`를 `"Assets"` 문자열로 비교하는 곳이 있으면 수정
