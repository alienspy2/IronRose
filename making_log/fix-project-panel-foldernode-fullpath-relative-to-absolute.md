# Fix: ImGuiProjectPanel FolderNode.FullPath를 절대 경로로 변경

## 유저 보고 내용
- 2-레포 구조(CWD=엔진루트, ProjectRoot=프로젝트루트)에서 Project 패널의 "Open Containing Folder" 클릭 시 엔진 루트의 Assets 폴더가 열림
- Material/Animation 등 에셋 생성 시 엔진 루트에 저장됨 (프로젝트 루트가 아닌)
- `FolderNode.FullPath`가 상대 경로("Assets")로 설정되어 `Path.GetFullPath()`가 CWD 기준으로 해석하는 것이 원인

## 원인
`ImGuiProjectPanel.RebuildTree()`에서 루트 노드를 생성할 때:
```csharp
_root = new FolderNode { Name = "Assets", FullPath = "Assets" };
```
`FullPath`가 `"Assets"` (상대 경로)로 설정됨.

이후 패널 전체에서 `Path.GetFullPath(node.FullPath)` 호출 시 CWD 기준으로 해석되어, CWD가 엔진 루트이면 엔진 루트의 Assets를 가리킴.

하위 FolderNode 생성 시에도 `string.Join("/", parts, 0, i + 1)` 패턴으로 상대 경로를 구성했음.

참고: `AssetNode.FullPath`는 이미 절대 경로였으므로, `FolderNode.FullPath`만 불일치 상태였음.

## 수정 내용

### 1. 루트 노드 FullPath를 절대 경로로 변경 (line 1877)
```csharp
// Before
_root = new FolderNode { Name = "Assets", FullPath = "Assets" };
// After
_root = new FolderNode { Name = "Assets", FullPath = ProjectContext.AssetsPath };
```

### 2. 하위 FolderNode 생성 시 부모 노드 기반 절대 경로 구성 (line 1905)
```csharp
// Before
FullPath = string.Join("/", parts, 0, i + 1),
// After
FullPath = Path.Combine(current.FullPath, parts[i]),
```

이 두 변경으로 모든 FolderNode.FullPath가 절대 경로로 통일됨. 기존 `Path.GetFullPath()` 호출은 절대 경로에 대해 noop이므로 그대로 두어도 정상 동작.

## 변경된 파일
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs` -- RebuildTree() 내 FolderNode.FullPath를 절대 경로로 변경 (2개소)

## 검증
- 정적 분석으로 원인과 수정 범위를 확인
- `dotnet build` 성공 (오류 0개)
- 런타임 검증은 유저 확인 필요 (2-레포 구조에서 Project 패널 동작 테스트)
