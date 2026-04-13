# EditorPreferences 시스템

## 개요
앱-레벨(사용자 전역) 에디터 Preferences 저장소. 프로젝트가 바뀌어도 유지되는 사용자 취향 값을
하나의 글로벌 파일에 보관한다. `ProjectSettings`(프로젝트별), `EditorState`(프로젝트별 세션)과는
역할이 명확히 분리된다.

## 구조
- `src/IronRose.Engine/EditorPreferences.cs` — 정적 클래스. 앱-레벨 Preferences의 단일 진입점.
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs` — Preferences 편집 UI (`IEditorPanel`).
- `src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs` — `Apply(EditorColorTheme)`가 `EditorPreferences.ColorTheme`
  값에 따라 Rose/Dark/Light 팔레트를 분기 적용.
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs` — Preferences 패널 생성/Draw, Edit 메뉴 항목,
  Preferences → Overlay 역동기화(UiScale/EditorFont) 블록.

## 파일 위치
- `~/.ironrose/settings.toml`의 `[preferences]` 섹션.
- 같은 파일의 `[editor] last_project` 섹션은 `ProjectContext`가 관할하며 서로 영향을 주지 않는다.

## 관리 항목
| 키 | 타입 | 기본값 | 설명 |
|----|------|--------|------|
| `color_theme` | string(`rose`/`dark`/`light`) | `rose` | `EditorColorTheme` enum. UI Combo로 선택. |
| `enable_claude_usage` | bool | `false` | Claude 연동(Feedback Fix 버튼) 활성화 여부. |
| `ui_scale` | float(0.5~3.0) | `1.0` | `ImGui.GetIO().FontGlobalScale`에 반영. |
| `editor_font` | string | `Roboto` | 에디터 폰트 (`ImGuiOverlay.FontNames` 중 하나). |

## 핵심 동작

### Load() 흐름
1. `~/.ironrose/settings.toml`을 `TomlConfig.LoadFile`로 로드 (없으면 기본값 유지).
2. `[preferences]` 섹션의 각 키를 파싱하여 정적 속성에 반영.
3. `ui_scale`은 `[0.5, 3.0]`으로 클램프.
4. `color_theme` 문자열은 `ParseColorTheme`로 enum 변환 (대소문자 무시, 실패 시 `Rose`).
5. 파일 누락/파싱 실패 시 기본값 유지하고 경고만 남긴다.

### Save() 흐름 (read-modify-write)
1. `~/.ironrose/` 디렉토리 보장 생성.
2. 기존 `settings.toml`을 로드하거나 빈 config 생성.
3. `[preferences]` 섹션을 가져오거나 생성한 뒤 각 키를 `SetValue`.
4. 전체 config를 파일에 저장.
5. **`[editor] last_project` 등 다른 섹션은 건드리지 않는다** (`ProjectContext`와 공존 보장).

### 초기화 순서
- `EngineCore.Initialize()`에서 `ProjectContext.Initialize()`와 **독립적으로** `EditorPreferences.Load()` 호출.
- 한쪽 실패가 다른 쪽을 막지 않는다.

### EditorState → Preferences 마이그레이션 (Phase B)
- `UiScale`, `EditorFont`는 과거 `EditorState`(프로젝트별)에 있었으나, 본질적으로 사용자 취향이므로
  `EditorPreferences`로 이관되었다.
- `EditorPreferences.MigrateFromEditorState(float?, string?)`는 null이 아닌 인자만 덮어쓰고
  변경이 있으면 `Save()`를 호출하는 1회성 훅이다. Phase B에서 EditorState Load 시점에 호출된다.

## UI 통합

### 메뉴 위치
`Edit > Preferences...` — 메뉴 클릭 시 `ImGuiPreferencesPanel.IsOpen = true`로 창을 연다.
`View > Windows`에 두지 않는 이유: Preferences는 프로젝트 창이 아닌 앱 전역 설정이므로 `Edit`가 업계 관행
(VS Code, Rider)과 일치한다.

### 섹션 구성
- **Appearance** — Color Theme(Combo), UI Scale(Slider 0.5~3.0), Editor Font(Combo).
- **Integrations** — Enable Claude Usage(Checkbox) + 설명 텍스트.

### 즉시 반영 흐름
| 위젯 | 변경 시 동작 |
|------|--------------|
| Color Theme Combo | `ImGuiTheme.Apply(theme)` 재호출 + `EditorPreferences.Save()`. 다음 프레임부터 팔레트 전환. |
| UI Scale Slider | `ImGui.GetIO().FontGlobalScale = scale` + `Save()`. Overlay의 `_uiScale`은 `Update()` 시작부 역동기화 블록에서 맞춘다. |
| Editor Font Combo | `EditorPreferences.EditorFont` 갱신 + `Save()`. Overlay의 `_currentFont`는 `Update()` 역동기화 블록에서 맞춘다. |
| Enable Claude Usage Checkbox | 값 갱신 + `Save()`. `ClaudeManager.IsEnabled`가 즉시 false/true로 전환되어 Feedback UI의 Fix 버튼 가시성이 바로 바뀐다. |

### 가시성 정책
Preferences 창의 on/off 상태는 **세션 간 영속화하지 않는다**.
`EditorState`에 `PanelPreferences` 필드를 추가하지 않았고, `ImGuiPreferencesPanel._isOpen`만
런타임 상태로 존재한다. 재시작 시 항상 닫힌 상태로 시작한다.

### Preferences → Overlay 역동기화
`ImGuiOverlay.Update(float)` 시작부에 다음 블록이 있다.

```
if (Math.Abs(_uiScale - EditorPreferences.UiScale) > 0.0001f) { _uiScale = ...; FontGlobalScale = ...; }
if (_currentFont != EditorPreferences.EditorFont)              { _currentFont = ...; }
```

이는 Preferences 패널이 값을 바꾸었을 때 Overlay의 로컬 상태 및 `View > UI > UI Scale`/`Font` 메뉴의
체크 표시가 다음 프레임에 자동으로 일치하도록 한다. 반대 방향(메뉴에서 변경 → Preferences 값 갱신)은
`SetUiScale`/`SetFont`가 직접 `EditorPreferences.UiScale`/`EditorFont`를 갱신 + `Save()`하여 달성된다.

## 확장 가이드 (새 preference 항목 추가 5단계)
1. `EditorPreferences`에 정적 속성 추가 (기본값 포함).
2. `Load()`에서 `pref.GetXxx(...)` 호출로 해당 키 파싱 추가.
3. `Save()`에서 `pref.SetValue(key, ...)` 추가.
4. `ImGuiPreferencesPanel`에 UI 위젯 추가 (필요 시 새 `CollapsingHeader` 섹션).
5. 위젯의 값 변경 이벤트에서 `EditorPreferences.Save()` 호출.

enum 항목을 추가할 경우 `ColorTheme`을 참고하여 `ToString`/`Parse` 분기 + `ImGuiTheme.Apply` 분기
+ Combo 문자열 배열에 항목 추가.

## 주의사항
- **동일 파일 공유**: `ProjectContext.SaveLastProjectPath()`와 같은 파일을 read-modify-write로 다루므로,
  둘 중 하나가 전체 덮어쓰기로 변경되면 다른 섹션이 유실된다. 항상 `TomlConfig.LoadFile` → `SetSection`
  → `SaveToFile` 패턴을 유지해야 한다.
- **폰트 목록 동기화**: `ImGuiPreferencesPanel.FontNames`는 `ImGuiOverlay.FontNames`와 일치해야 한다.
  Overlay 쪽 배열이 변경되면 패널 쪽도 함께 업데이트할 것.
- **테마 확장 범위**: `EditorColorTheme.Dark`/`Light`는 초기 최소 팔레트만 구현되어 있다. 구체 색상 튜닝은
  별도 plan에서 다룬다.
- **`enable_claude_usage`의 소비처**: 현재는 `ClaudeManager.IsEnabled`와 `ImGuiFeedbackPanel`의 Fix UI
  렌더링 분기에서만 참조된다. 새 Claude 연동 기능을 붙일 때도 이 값을 게이트로 사용할 것.

## 관련 파일
- `src/IronRose.Engine/EditorPreferences.cs`
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`
- `src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs`
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`
- `src/IronRose.Engine/Editor/EditorState.cs` (레거시 값 마이그레이션 소스)
- `src/IronRose.Engine/ProjectContext.cs` (같은 settings.toml 공유)
