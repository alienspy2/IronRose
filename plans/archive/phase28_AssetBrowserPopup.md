# Phase 28 — Asset Browser Popup (Inspector GUID 필드용 Object Picker)

## Context
Inspector에서 GUID로 링크되는 에셋 필드(Texture, Material, Mesh 등)에 **Browse 버튼(◎)**을 추가하고, 클릭 시 해당 타입의 에셋만 리스팅하는 팝업 창을 띄운다. Unity의 Object Picker와 동일한 UX.

## 핵심 UX 흐름
```
1. Inspector 에셋 필드 오른쪽에 ◎ 버튼 표시
2. ◎ 클릭 → Asset Browser 모달 팝업 열림
3. 팝업 상단: 검색 입력창 (자동 포커스)
4. 리스트 맨 위: "(None)" 항목 — 선택 시 연결 끊기
5. 이하: 해당 타입에 링크 가능한 에셋 리스트 (이름 + 경로)
6. 중간어 검색 (Contains, case-insensitive, AND 로직) — Project Panel과 동일 방식
7. 항목 클릭 → 선택 하이라이트
8. OK → 선택된 에셋의 GUID로 연결 (또는 None이면 연결 해제)
9. Cancel → 변경 없이 닫기
```

## 적용 대상 (2곳)

### A. Material Editor — Texture 슬롯 (`DrawMatTextureSlot`)
- 현재: `[텍스처이름]` Selectable + Drag-Drop + `[X]` 클리어 버튼
- 변경: `[텍스처이름]` + **`[◎]`** + `[X]` — ◎ 클릭 시 `t:texture` 필터로 팝업

### B. Component Inspector — Asset Reference 필드 (`DrawPingableLabel`)
- 현재: `[에셋이름]` Selectable + Material Drag-Drop
- 변경: `[에셋이름]` + **`[◎]`** — ◎ 클릭 시 해당 `memberType`으로 필터링된 팝업
- Material, Mesh, Texture2D, Font, MipMesh 등 `AssetNameExtractors`에 등록된 타입 지원

## 수정 대상 파일 (2개, 신규 파일 없음)

### 1. `ImGuiInspectorPanel.cs` — 팝업 상태 + 팝업 UI + Browse 버튼

#### 1-1. 팝업 상태 필드 추가
```csharp
// Asset Browser Popup 상태
private bool _openAssetBrowser;
private string _assetBrowserSearch = "";
private bool _assetBrowserFocusSearch;
private string _assetBrowserTitle = "Select Asset";
private string _assetBrowserTypeFilter = "";       // "texture", "material", "mesh" 등
private int _assetBrowserSelectedIndex = -1;        // 리스트 내 선택 인덱스
private string? _assetBrowserSelectedGuid;          // 선택된 에셋의 GUID (null = None)

// 콜백: OK 시 호출 (GUID 문자열 전달, 빈 문자열 = None)
private Action<string>? _assetBrowserOnConfirm;
```

#### 1-2. `OpenAssetBrowser()` 헬퍼 메서드
```csharp
private void OpenAssetBrowser(string title, string typeFilter, string? currentGuid,
    Action<string> onConfirm)
{
    _openAssetBrowser = true;
    _assetBrowserSearch = "";
    _assetBrowserFocusSearch = true;
    _assetBrowserTitle = title;
    _assetBrowserTypeFilter = typeFilter;
    _assetBrowserSelectedIndex = -1;
    _assetBrowserSelectedGuid = currentGuid;
    _assetBrowserOnConfirm = onConfirm;
}
```

#### 1-3. `DrawAssetBrowserPopup()` — 팝업 렌더링 (메인 메서드)
- `Draw()` 말미에서 호출 (모달은 윈도우 스코프 밖에서도 동작)
- 패턴: 기존 `Create Folder##Modal`, `Delete Asset?##Modal`과 동일

```
구조:
┌─────────────────────────────────┐
│  Select Texture                 │  ← 타이틀
├─────────────────────────────────┤
│  🔍 [Search...              ]  │  ← InputTextWithHint, 자동 포커스
├─────────────────────────────────┤
│  ▸ (None)                       │  ← 항상 맨 위, 선택 시 연결 해제
│    Gravel020                    │
│    Wood069                      │
│  ▸ BrickWall_Normal             │  ← 선택 시 하이라이트
│    StoneTile_Albedo             │
│    ...                          │
├─────────────────────────────────┤
│           [OK]  [Cancel]        │
└─────────────────────────────────┘
```

핵심 로직:
1. `_openAssetBrowser` true이면 `ImGui.OpenPopup("Select Asset##BrowserModal")`
2. `ImGui.BeginPopupModal()` + `AlwaysAutoResize`
3. 검색창: `ImGui.InputTextWithHint("##AssetBrowserSearch", "Search...", ref _assetBrowserSearch, 256)`
4. `ImGui.IsWindowAppearing()` → `SetKeyboardFocusHere()` (자동 포커스)
5. `ImGui.BeginChild("##AssetList", new Vector2(350, 300))` — 스크롤 가능 리스트 영역
6. `(None)` 항목: `ImGui.Selectable("(None)", _assetBrowserSelectedGuid == null || _assetBrowserSelectedGuid == "")`
7. 에셋 리스트: `CollectBrowsableAssets()` → 검색 필터 → `ImGui.Selectable()`
8. 더블클릭: `ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(0)` → 즉시 확정
9. `ImGui.EndChild()`
10. OK 버튼: `_assetBrowserOnConfirm?.Invoke(_assetBrowserSelectedGuid ?? "")` → `CloseCurrentPopup()`
11. Cancel 버튼: `CloseCurrentPopup()`
12. Enter 키: OK와 동일 동작
13. Escape 키: Cancel과 동일 (BeginPopupModal 기본 동작)

#### 1-4. `CollectBrowsableAssets()` — 타입별 에셋 수집
```csharp
private List<(string displayName, string guid, string path)> CollectBrowsableAssets()
```
- `AssetDatabase.GetAllAssetPaths()` 순회
- `_assetBrowserTypeFilter`에 따라 필터링:
  - `"texture"` → `.png`, `.jpg`, `.jpeg`, `.tga`, `.bmp`, `.hdr`, `.exr`
  - `"material"` → `.mat` 파일 + SubAsset 중 type=="Material"
  - `"mesh"` → SubAsset 중 type=="Mesh"
  - `"font"` → `.ttf`, `.otf`
- 각 에셋의 GUID 조회: `db.GetGuidFromPath(path)` 또는 SubAsset의 `sub.guid`
- `displayName`으로 정렬 (case-insensitive)
- 검색 필터: `_assetBrowserSearch` 의 각 토큰이 `displayName`에 Contains (AND, case-insensitive) — Project Panel `DrawSearchResults()`와 동일 패턴

#### 1-5. `DrawMatTextureSlot()` 수정 — ◎ 버튼 추가
현재 레이아웃: `[Label] [텍스처이름...........] [X]`
변경 레이아웃: `[Label] [텍스처이름.......] [◎] [X]`

- `clearBtnW` 옆에 `browseBtnW = 20f` 추가
- `slotW` 계산에 `browseBtnW + 4f` 반영
- 기존 Selectable/Button + DragDrop 이후, `[X]` 이전에:
```csharp
ImGui.SameLine();
ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 0));
if (ImGui.Button($"\u25ce##{key}_browse"))   // ◎ 문자
{
    OpenAssetBrowser($"Select {label}", "texture", guid,
        newGuid => SetTextureGuid(key, label, newGuid));
}
ImGui.PopStyleVar();
```

#### 1-6. `DrawPingableLabel()` 수정 — ◎ 버튼 추가
- `static` → **인스턴스 메서드**로 변경 (팝업 상태 접근 필요)
  - 호출부 (`DrawAssetReferences`) 도 함께 수정
- Selectable/Button 오른쪽에 ◎ 버튼 추가
- `memberType`으로 typeFilter 결정:
  - `typeof(Material)` → `"material"`
  - `typeof(Mesh)` / `typeof(MipMesh)` → `"mesh"`
  - `typeof(Texture2D)` / `typeof(Sprite)` → `"texture"`
  - `typeof(Font)` → `"font"`
- 콜백: 선택 시 `db.LoadByGuid<T>(guid)` → setter 호출 + Undo 기록

```csharp
// memberType → typeFilter 매핑
private static string GetTypeFilter(Type memberType)
{
    if (memberType == typeof(Material)) return "material";
    if (memberType == typeof(Mesh) || memberType == typeof(MipMesh)) return "mesh";
    if (memberType == typeof(Texture2D) || memberType == typeof(Sprite)) return "texture";
    if (memberType == typeof(Font)) return "font";
    return "";
}
```

- ◎ 버튼 클릭 콜백 (Material 예시):
```csharp
OpenAssetBrowser($"Select {label}", typeFilter, currentGuid, newGuid =>
{
    if (string.IsNullOrEmpty(newGuid))
    {
        var old = asset;
        setter(null);
        UndoSystem.Record(new SetPropertyAction(..., old, null));
    }
    else
    {
        var newAsset = db.LoadByGuid<Material>(newGuid);
        if (newAsset != null)
        {
            var old = asset;
            setter(newAsset);
            UndoSystem.Record(new SetPropertyAction(..., old, newAsset));
        }
    }
});
```

### 2. `AssetDatabase.cs` — 타입별 에셋 조회 헬퍼 (선택적)

현재 `GetAllAssetPaths()` + `GetSubAssets()` + `GetGuidFromPath()` 조합으로 충분하지만, 편의를 위해:

```csharp
/// <summary>
/// 지정 타입 필터에 해당하는 모든 에셋을 (displayName, guid, path) 튜플로 반환.
/// </summary>
public List<(string displayName, string guid, string path)> FindAssetsByTypeFilter(string typeFilter)
```
- Inspector 팝업과 향후 다른 곳에서 재사용 가능
- 구현은 `CollectBrowsableAssets()`와 동일 로직을 AssetDatabase 레벨로 이동
- **선택 사항**: 1차 구현에서는 Inspector 내부 private 메서드로 충분. 필요 시 리팩터링.

## 기존 패턴 재사용
| 패턴 | 출처 | 용도 |
|------|------|------|
| `BeginPopupModal` + 상태 플래그 | `Create Folder##Modal` (ProjectPanel) | 팝업 열기/닫기 |
| `IsWindowAppearing` + `SetKeyboardFocusHere` | AddComponent 팝업 (InspectorPanel:480) | 검색창 자동 포커스 |
| `Contains` 중간어 검색 (AND, case-insensitive) | `DrawSearchResults()` (ProjectPanel:1849) | 에셋 필터링 |
| `SetTextureGuid()` + Undo | `DrawMatTextureSlot()` (InspectorPanel:1968) | 텍스처 GUID 설정 |
| `SetPropertyAction` + Undo | `DrawPingableLabel()` (InspectorPanel:2633) | 컴포넌트 에셋 설정 |
| `AssetDatabase.GetAllAssetPaths()` + `GetSubAssets()` | `RebuildTree()` (ProjectPanel:1677) | 에셋 목록 수집 |

## 구현 순서
1. **팝업 상태 필드 + OpenAssetBrowser 헬퍼** 추가
2. **CollectBrowsableAssets** 구현 — texture 필터 먼저
3. **DrawAssetBrowserPopup** 구현 — 모달 + 검색 + 리스트 + OK/Cancel
4. **DrawMatTextureSlot에 ◎ 버튼** 추가 + 콜백 연결
5. 빌드 + 텍스처 브라우저 테스트
6. **DrawPingableLabel에 ◎ 버튼** 추가 (static → instance 전환)
7. material, mesh, font 필터 추가 + 테스트
8. 더블클릭 즉시 확정 + Enter/Escape 키 처리

## 검증 방법
1. 빌드 확인: `dotnet build`
2. 런타임 테스트:
   - Material Inspector의 Texture 슬롯 옆 ◎ 클릭 → 팝업에 텍스처만 리스팅 확인
   - 검색창에 중간어 입력 → 실시간 필터링 확인 (예: "brick" → BrickWall 매치)
   - (None) 선택 + OK → 텍스처 연결 해제 + Inspector 반영 확인
   - 에셋 선택 + OK → GUID 연결 + 텍스처 변경 확인
   - Cancel → 변경 없음 확인
   - 더블클릭 → OK 없이 즉시 적용 확인
   - Ctrl+Z → Undo 동작 확인
   - Component Inspector의 Material 필드 ◎ 클릭 → Material만 리스팅 확인
   - MeshRenderer의 mesh 필드 o → Mesh만 리스팅 확인
