# Editor Preferences (App-Level) 추가

## 배경

IronRose 에디터에는 현재 세 종류의 설정이 존재한다.

| 구분 | 범위 | 파일 | 관리 클래스 |
|------|------|------|------------|
| 프로젝트별 설정 | 프로젝트 | `rose_projectSettings.toml` | `ProjectSettings` |
| 에디터 세션 상태 | 프로젝트 | `.rose_editor_state.toml` | `EditorState` |
| 글로벌(앱) 설정 | 사용자 | `~/.ironrose/settings.toml` | `ProjectContext` (부분) |

그러나 "사용자 전역"에 해당하는 설정을 관리하는 전담 시스템은 없다. 현재 `~/.ironrose/settings.toml`은
`ProjectContext`가 마지막 열린 프로젝트 경로(`[editor] last_project`) 하나만 저장하는 용도로 쓰이고 있다.

앞으로 사용자가 에디터를 어떻게 쓸지(테마, 외부 연동 기능 on/off 등)를 기억해야 할 "앱-레벨 선호 항목"이
지속적으로 추가될 예정이므로, 이를 체계적으로 관리할 **Editor Preferences** 시스템이 필요하다.

## 목표

1. 사용자 전역(앱 레벨)에서 유지되는 Preferences 저장소를 도입한다. 프로젝트가 바뀌어도 유지된다.
2. 에디터 UI(ImGui 창)에서 Preferences를 보고 편집할 수 있다.
3. 초기 항목을 지원한다.
   - `color_theme` — 컬러 테마 선택 (Rose/Dark/Light, **각 팔레트 실제 구현 포함**)
   - `enable_claude_usage` — Claude 연동 사용 여부 (bool). 이 플래그는 **`ClaudeManager`(신규)를 통한 모든 Claude 관련 UI/기능의 가시성·동작 게이트**로 작동.
   - `ui_scale`, `editor_font` — `EditorState`에서 **이동**하여 사용자 전역에서 관리.
4. `ClaudeManager`(신규)를 도입하여 Claude 연동 호출을 일원화한다. 연동 방식은 **`claude -p` CLI만 사용**.
5. 새로운 preference 항목을 쉽게 추가할 수 있는 구조로 설계한다.
6. 기존 `ProjectSettings` / `EditorState` / `ProjectContext`와 역할이 겹치지 않도록 경계를 명확히 한다.

## 현재 상태

### 기존 설정 시스템 정리

- **`ProjectContext`** (`src/IronRose.Engine/ProjectContext.cs`)
  - `~/.ironrose/settings.toml`의 `[editor] last_project`만 read/write.
  - `SaveLastProjectPath()`는 read-modify-write 패턴으로 다른 섹션을 보존하므로,
    같은 파일에 섹션을 추가해도 안전하다.

- **`ProjectSettings`** (`src/IronRose.Engine/ProjectSettings.cs`)
  - 프로젝트 루트의 `rose_projectSettings.toml`을 관리.
  - 정적 클래스 + `Load()`/`Save()` 패턴. `TomlConfig`로 로드, `Save()`는 문자열 조합.

- **`EditorState`** (`src/IronRose.Engine/Editor/EditorState.cs`)
  - 프로젝트 루트의 `.rose_editor_state.toml`을 관리. 창 위치, UI 스케일, 패널 가시성 등 세션 상태.
  - `UiScale`, `EditorFont`는 **본 plan에서 Preferences로 이동**한다. EditorState에서 해당 필드는 제거.

- **`ImGuiFeedbackPanel`** (`src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs`)
  - `StartFix()` (line 317-402)에서 `claude -p --verbose --output-format stream-json`을
    `Process.Start`로 직접 실행, stdin으로 `aca-fix: {content}` 전달. stream-json 출력 파싱까지 포함.
  - 본 plan에서는 이 호출을 신규 `ClaudeManager`로 이관한다.
  - 코드베이스 내 Claude 호출은 **이 파일이 유일**.

### UI 패턴

- 패널은 `IEditorPanel` 구현 + `ImGuiOverlay`에서 생성/등록/토글.
  참고: `ImGuiProjectSettingsPanel` (`src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectSettingsPanel.cs`).
- 메뉴 바는 `ImGuiOverlay.cs`의 `File / Edit / View / Layout / Tools` 구조.
  `Edit` 메뉴는 현재 Undo/Redo만 존재하여 `Preferences...` 항목 추가에 적합.
- 테마는 `ImGuiTheme.Apply()` (`src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs`) — 현재 Rose(밝은 베이지) 단일 테마.

## 설계

### 개요

**앱-레벨 Preferences 전용 정적 클래스 `EditorPreferences`를 신설**하고, 저장 파일은
기존 글로벌 파일 `~/.ironrose/settings.toml`의 새로운 섹션 `[preferences]`에 통합한다.

- 파일 위치: `~/.ironrose/settings.toml` (이미 존재, `ProjectContext`와 공유)
- 섹션: `[preferences]` (다른 섹션 — `[editor] last_project` — 과 독립)
- UI: 새 패널 `ImGuiPreferencesPanel`을 만들고, `Edit > Preferences...` 메뉴로 토글.

이 방식의 이점.

1. 사용자가 관리할 "전역 파일"이 하나로 유지된다 (`~/.ironrose/settings.toml` 하나만 보면 됨).
2. `ProjectContext`가 이미 같은 파일을 read-modify-write로 다루고 있어 섹션 공존이 안전하다.
3. 장래 `[editor] last_project`를 `[recent] projects`처럼 확장해도 영향 없음.

**확장성**을 위해 Preferences는 "항목 정의(메타데이터) + 값(storage)" 이분 구조 대신,
**단순한 정적 속성 + 수동 Load/Save 패턴**을 채택한다 (기존 `ProjectSettings`와 동일).
속성 추가가 쉽고 타입 안전하다. 단, 항목 수가 크게 늘어날 경우 UI 자동 생성을 고려할 수 있으나
현 시점에서는 오버엔지니어링이므로 수동 UI 렌더링으로 간다.

### 상세 설계

#### 1. 데이터 모델: `EditorPreferences` 정적 클래스

- 위치: `src/IronRose.Engine/EditorPreferences.cs` (Engine 어셈블리 루트).
- 역할: 앱 전역 사용자 Preferences의 메모리 표현 + 파일 I/O.
- 대략의 구조 (세부 시그니처는 aca-archi가 확정).

  - 속성: `ColorTheme`, `EnableClaudeUsage`, `UiScale`, `EditorFont`.
  - `ColorTheme`은 enum으로 정의 (`EditorColorTheme { Rose, Dark, Light }`, 확장 가능).
    - **세 테마 모두 실제 팔레트를 구현**한다. `ImGuiTheme.Apply(EditorColorTheme)`가 enum에 따라
      ImGuiStyle 색상을 분기 적용.
  - `UiScale`(float), `EditorFont`(string)는 `EditorState`에서 이전. 기본값/유효 범위는 기존 값 유지
    (UiScale 0.5~3.0, EditorFont는 기존 디폴트).
  - 정적 메서드: `Load()`, `Save()`.
  - 저장 방식: `ProjectContext.SaveLastProjectPath()`와 동일한 read-modify-write 패턴.
    기존 `[editor] last_project` 같은 섹션을 절대 덮어쓰지 않는다.

- TOML 레이아웃.

  ```toml
  [editor]
  last_project = "..."  # 기존, ProjectContext 관할

  [preferences]
  color_theme = "rose"          # rose | dark | light
  enable_claude_usage = false
  ui_scale = 1.0
  editor_font = "..."
  ```

  **`EditorState` 마이그레이션**: 첫 로드 시 `.rose_editor_state.toml`에 `ui_scale`/`editor_font` 키가
  남아있다면 값을 읽어 Preferences로 이식 후 다음 `EditorState.Save()`부터 해당 키를 기록하지 않도록
  한다 (필드 제거). 단일 사용자 환경이므로 복잡한 버전 마이그레이션은 불필요.

#### 2. 초기화 흐름

- 진입점: `Program.cs` 혹은 `ImGuiOverlay` 생성 직전.
- `ProjectContext.Initialize()`와 **독립적으로** `EditorPreferences.Load()`가 호출되어야 한다
  (ProjectContext가 실패해도 Preferences는 로드되어야 하고, 역도 성립).
- `ImGuiTheme.Apply(EditorPreferences.ColorTheme)` 호출.
- `EditorState.Load()` 직후 `ui_scale`/`editor_font` 마이그레이션 1회 처리.

세부 호출 지점(어느 초기화 단계에 끼울지)은 aca-archi가 정한다.

#### 2-1. `ClaudeManager` (신규)

- 위치: `src/IronRose.Engine/Editor/ClaudeManager.cs` (정적 클래스).
- 역할: Claude 연동 호출의 **단일 진입점**. 현재는 `aca-fix` 한 가지만 지원하지만 구조를 일반화.
- API (대략, 세부는 archi가 확정).

  - `bool IsEnabled => EditorPreferences.EnableClaudeUsage;`
  - `Task RunFixAsync(string prompt, string workDir, Action<ClaudeStreamEvent> onEvent, CancellationToken ct)`
    또는 기존 `ImGuiFeedbackPanel` 스트리밍 구조를 그대로 옮길 수 있는 콜백 시그니처.
  - stream-json 파싱 로직(`ProcessStreamLine` 등)도 `ClaudeManager`로 이관.
- 명령 실행은 **`claude -p --verbose --output-format stream-json`만** 허용. 다른 형태의 Claude 호출은
  추가하지 않는다.
- `IsEnabled == false`이면 호출 시도를 거부(즉시 예외 또는 no-op)하여 UI가 가드에 실패해도 보호.

#### 2-2. `ImGuiFeedbackPanel` 가드

- Fix 버튼, Fix 진행 상태 표시, 스트리밍 출력 영역 등 **Claude 관련 UI는 `ClaudeManager.IsEnabled`가
  true일 때만 렌더링**. false일 때는 해당 위젯이 아예 보이지 않음(조건부 분기).
- 기존 `StartFix()` 내부의 프로세스 실행/파싱 로직은 `ClaudeManager`로 이관하고, 패널은 결과 이벤트를
  받아 UI 상태만 갱신.

#### 3. UI: `ImGuiPreferencesPanel`

- 위치: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`.
- `IEditorPanel` 구현, `ImGuiProjectSettingsPanel`의 구조를 참고.
- 창 제목: `"Preferences"`.
- 섹션 구성 (`CollapsingHeader`로 그룹핑, 앞으로 항목이 늘어도 그대로 확장 가능).

  - **Appearance**
    - `Color Theme`: Combo (Rose/Dark/Light).
    - 선택 변경 시 즉시 `ImGuiTheme.Apply(...)` 재호출 + `EditorPreferences.Save()`.
    - `UI Scale`: Slider (0.5~3.0). 변경 시 `Save()` + 기존 UiScale 적용 경로 트리거.
    - `Editor Font`: Combo 또는 입력 (기존 EditorState의 폰트 선택 UI 로직 이관).
  - **Integrations**
    - `Enable Claude Usage`: Checkbox. 변경 시 `Save()`.
    - 토글 OFF → ON 시 즉시 피드백 패널의 Claude UI가 나타나고, ON → OFF 시 즉시 숨김.

- **가시성 관리**: `ProjectSettings` 패널과 달리, Preferences 창의 on/off 상태는
  세션 간 영속화하지 않는다 (수동으로 여닫는 창). 따라서 `EditorState`에 새 `PanelPreferences`
  필드를 **추가하지 않는다**. 런타임 `bool _isOpen`만 사용.

#### 4. 메뉴 통합

- `ImGuiOverlay.cs`의 `Edit` 메뉴 (현재 Undo/Redo만 있음)에 구분선 + `Preferences...` 항목 추가.
- 클릭 시 `ImGuiPreferencesPanel.IsOpen = true`.
- 이유: `View > Windows`는 프로젝트 관련 창이 모여있고, Preferences는 앱 전역 성격이라 `Edit`이 더 적합.
  (업계 관행: VS Code, Rider도 `File/Edit > Preferences`에 배치.)

#### 5. `enable_claude_usage` 게이트

- `ClaudeManager.IsEnabled`는 이 플래그를 그대로 반환.
- `ImGuiFeedbackPanel`은 이 값으로 Claude 관련 UI를 조건부 렌더링.
- `ClaudeManager` 호출 API는 `IsEnabled == false`일 때 실행을 거부.
- 결과적으로 **UI와 실제 호출 양쪽에 게이트**가 걸림.

#### 6. 확장 가이드라인 (주석으로 파일 상단에 명시)

새 preference 항목을 추가하려면.

1. `EditorPreferences`에 정적 속성 추가 (기본값 포함).
2. `Load()`에서 해당 섹션/키 파싱 추가.
3. `Save()`에서 해당 키 쓰기 추가.
4. `ImGuiPreferencesPanel`에 UI 위젯 추가 (필요 시 새 `CollapsingHeader` 섹션 생성).
5. 값 변경 시 `Save()` 호출.

### 영향 범위

**신규 파일**
- `src/IronRose.Engine/EditorPreferences.cs`
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`
- `src/IronRose.Engine/Editor/ClaudeManager.cs`

**수정 파일**
- `src/IronRose.Engine/Editor/EditorState.cs`
  - `UiScale`, `EditorFont` 필드 제거(또는 deprecated 처리하지 않고 완전 제거).
  - Load 시 TOML에 남아있는 키를 읽어 `EditorPreferences`로 이식하는 1회성 마이그레이션 훅 추가.
- `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`
  - Preferences 패널 필드/생성/Draw 호출 추가.
  - `Edit` 메뉴에 `Preferences...` 항목 추가.
  - UiScale/EditorFont 참조를 `EditorPreferences`로 갱신.
- `src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs`
  - `Apply(EditorColorTheme)` 오버로드 추가, Rose/Dark/Light 팔레트 각각 실제 값으로 구현.
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiFeedbackPanel.cs`
  - `StartFix()`의 Process 실행/stream-json 파싱 로직을 `ClaudeManager`로 이관.
  - Claude 관련 UI(Fix 버튼, 진행/출력 영역)를 `ClaudeManager.IsEnabled` 분기로 감쌈.
- `src/IronRose.Engine/RoseEditor/Program.cs` (혹은 에디터 초기화 진입점)
  - `EditorPreferences.Load()` 호출 삽입.
- `making_log/` — 구현 후 시스템 문서 추가 (`_system-editor-preferences.md`, `_system-claude-manager.md`).

**기존 기능 영향**
- `ProjectContext`의 `~/.ironrose/settings.toml` read/write는 기존처럼 `[editor]` 섹션만 건드리므로
  충돌 없음. `EditorPreferences`도 `[preferences]`만 수정하는 read-modify-write를 준수해야 함.
- 기존 EditorState 파일에 남아있는 `ui_scale`/`editor_font`는 첫 실행 시 Preferences로 이식되고
  이후 EditorState에서는 기록되지 않음.
- Claude 관련 UI는 기본값(`enable_claude_usage = false`)에서는 **보이지 않는다**. 기존 사용자가
  Claude Fix 기능을 계속 쓰려면 Preferences에서 활성화해야 함 (릴리스 노트 필요).

## 대안 검토

### A. `~/.ironrose/preferences.toml`로 별도 파일 분리

- 장점: 파일 단위로 책임이 분리됨.
- 단점: 사용자가 봐야 할 글로벌 설정 파일이 늘어남. `ProjectContext`의 last_project와 섞어 보는 게
  오히려 직관적. 구현 복잡도 증가.
- 결론: **채택하지 않음**. 같은 파일에 섹션으로 분리하는 것이 더 단순하고 충분히 안전.

### B. 항목 메타데이터 기반 리플렉션/자동 UI

- 각 preference를 `PreferenceItem<T> { Key, DefaultValue, Label, Tooltip, ... }`로 정의하고
  UI를 자동 생성하는 방식.
- 장점: 항목이 매우 많아지면 중복 코드 제거.
- 단점: 현재 초기 항목 2개에 비해 과설계. 타입별 UI(enum combo, bool checkbox, slider 등) 분기를
  일반화하느라 오히려 복잡도 증가.
- 결론: **현 시점 미채택**. 항목이 수십 개로 늘어나면 그때 리팩터링 고려.

### C. `ProjectSettings`에 흡수

- 장점: 기존 코드 패턴 재활용.
- 단점: "프로젝트별"이라는 `ProjectSettings`의 정의와 정면으로 충돌. 프로젝트가 바뀌면 사라짐.
- 결론: **채택하지 않음**. 사용자 전역 요구와 맞지 않음.

## 결정된 사항 (유저 승인 완료)

1. **Dark/Light 테마 팔레트 실제 구현 포함** — Rose/Dark/Light 세 테마 모두 실제 값으로 `ImGuiTheme.Apply` 분기.
2. **`UiScale`, `EditorFont`를 Preferences로 이동** — EditorState에서 제거, 첫 로드 시 1회성 마이그레이션.
3. **`ClaudeManager` 신규 + 피드백 패널 UI 게이트** — `claude -p` CLI 호출만 허용. `EnableClaudeUsage`가
   false면 Fix 관련 UI가 보이지 않고 호출도 거부.
4. **메뉴 위치: `Edit > Preferences...`**.
