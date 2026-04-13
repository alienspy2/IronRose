# Phase B: EditorState 마이그레이션 + 초기화 배선

## 목표

`EditorState`에서 `UiScale`, `EditorFont`를 제거하고 `EditorPreferences`로 이관한다. 기존 `.rose_editor_state.toml`에 남아있는 값은 최초 Load 시 1회 마이그레이션되고 이후 Save에서 더 이상 기록되지 않는다. 에디터 초기화 경로에 `EditorPreferences.Load()`와 `ImGuiTheme.Apply(EditorPreferences.ColorTheme)` 호출을 끼워넣고, `ImGuiOverlay`의 UiScale/EditorFont 참조를 모두 `EditorPreferences`로 갱신한다.

## 선행 조건

- Phase A 완료 (`EditorPreferences` 클래스와 `ImGuiTheme.Apply(EditorColorTheme)` 오버로드 존재).

## 수정할 파일

### `src/IronRose.Engine/Editor/EditorState.cs`

- **변경 내용**:
  1. **속성 제거** (line 58~62):
     - `public static float UiScale { get; set; } = 1.0f;` 삭제.
     - `public static string EditorFont { get; set; } = "Roboto";` 삭제.
  2. **헤더 주석 정리** (line 1~31):
     - `@exports`에서 `UiScale`, `EditorFont` 항목 삭제.
     - 간단한 메모 추가: "UiScale/EditorFont는 EditorPreferences로 이전됨 (1회 마이그레이션)".
  3. **`Load()` 수정** (line 161~): `editor` 섹션 파싱 블록 내부에서 line 175~178 수정:
     - `UiScale = Math.Clamp(editor.GetFloat("ui_scale", UiScale), 0.5f, 3.0f);` 줄 삭제.
     - `editor_font` 파싱 3줄(176~178) 삭제.
     - 대신 아래 마이그레이션 로직 삽입 (동일 위치):
       ```csharp
       // 1회성 마이그레이션: 레거시 ui_scale/editor_font 키가 남아있다면 EditorPreferences로 이식.
       float? legacyScale = editor.HasKey("ui_scale") ? (float?)editor.GetFloat("ui_scale", 1.0f) : null;
       string? legacyFont = editor.HasKey("editor_font") ? editor.GetString("editor_font", "") : null;
       if (string.IsNullOrEmpty(legacyFont)) legacyFont = null;
       if (legacyScale.HasValue || legacyFont != null)
           EditorPreferences.MigrateFromEditorState(legacyScale, legacyFont);
       ```
     - `TomlConfig.HasKey`는 `EditorState.cs` 내부에서 이미 사용 중(line 202 `window.HasKey("x")`)이므로 사용 가능.
  4. **`Save()` 수정** (line 245~): line 253 `toml += $"ui_scale = {UiScale:F1}\n";` 삭제, line 254 `toml += $"editor_font = \"{EditorFont}\"\n";` 삭제. 이후 Save부터는 이 두 키가 `.rose_editor_state.toml`에 기록되지 않는다.
  5. `using IronRose.Engine;`은 이미 import되어 있음 확인됨 (line 36).

- **이유**: plan "결정된 사항 2" — UiScale/EditorFont를 Preferences로 이동.

### `src/IronRose.Engine/EngineCore.cs`

- **변경 내용**:
  - Line 148 `ProjectContext.Initialize();` 바로 뒤 줄에 다음을 삽입:
    ```csharp
    EditorPreferences.Load();
    ```
  - Line 174 `EditorState.Load();`는 그대로. 이 호출 내부에서 레거시 키 감지 시 `EditorPreferences.MigrateFromEditorState`가 자동 실행됨.
  - Line 33 근처 헤더 주석의 "EditorState.Load()는..." 문장에 한 줄 추가:
    ```
    //          EditorPreferences.Load()는 ProjectContext.Initialize() 직후 호출되어 테마/UI스케일/폰트를 복원.
    ```

- **이유**: plan 섹션 2 "초기화 흐름" — Preferences는 ProjectContext와 독립적으로 초기화.

### `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

- **변경 내용**:
  1. **Line 324**: `_currentFont = EditorState.EditorFont;` → `_currentFont = EditorPreferences.EditorFont;`.
  2. **Line 327**: `ImGuiTheme.Apply();` → `ImGuiTheme.Apply(EditorPreferences.ColorTheme);`.
  3. **Line 330**: `_uiScale = EditorState.UiScale;` → `_uiScale = EditorPreferences.UiScale;`.
  4. **Line 1699~1700** (`SetFont` 내부):
     ```csharp
     EditorState.EditorFont = fontName;
     EditorState.Save();
     ```
     → 교체:
     ```csharp
     EditorPreferences.EditorFont = fontName;
     EditorPreferences.Save();
     ```
  5. **Line 1740~1741** (`SetUiScale` 내부):
     ```csharp
     EditorState.UiScale = _uiScale;
     EditorState.Save();
     ```
     → 교체:
     ```csharp
     EditorPreferences.UiScale = _uiScale;
     EditorPreferences.Save();
     ```
  6. `using IronRose.Engine;`는 line 30에 이미 존재.

- **이유**: UiScale/EditorFont 저장소 이동에 따른 참조 일괄 갱신. 컴파일 에러 방지.

## NuGet 패키지

- 추가 없음.

## 검증 기준

- [ ] `dotnet build` 성공 (EditorState에 `UiScale`/`EditorFont` 참조가 남아있으면 컴파일 에러; grep으로 재확인).
- [ ] 첫 실행 시 `~/.ironrose/settings.toml`에 `[preferences]` 섹션이 생성되고 `color_theme`, `enable_claude_usage`, `ui_scale`, `editor_font` 4개 키가 기록됨.
- [ ] 기존 프로젝트의 `.rose_editor_state.toml`에 `ui_scale = 1.5` 같은 값이 있으면 첫 실행 후 Preferences로 이식되고, `.rose_editor_state.toml`의 다음 Save에서는 해당 키가 사라진다.
- [ ] UI Scale 메뉴 / Set Font 메뉴가 기존과 동일하게 동작하며, 선택 값이 재시작 후에도 유지된다.
- [ ] `~/.ironrose/settings.toml`의 `[editor] last_project` 키는 Preferences Save 후에도 **보존된다**.
- [ ] Rose 테마 외관이 Phase A와 동일 (Dark/Light는 아직 선택할 UI 없음).

## 참고

- `TomlConfig.HasKey` 존재 확인됨.
- `MigrateFromEditorState`는 레거시 키 감지 첫 실행에서만 의미 있는 동작을 하지만, 이후 실행에서 호출돼도 인자가 모두 null이면 no-op. 호출 가드(`if (legacyScale.HasValue || legacyFont != null)`)로 불필요한 Save를 피함.
- 마이그레이션 후 `EditorState.Save()`는 Phase B의 Save 수정 결과로 자동으로 `ui_scale/editor_font`를 쓰지 않으므로 키가 영구 삭제된다.
- Preferences에서 변경한 값이 `ImGuiOverlay._uiScale`과 양방향 동기화되도록 하는 작업은 Phase D에서 처리 (UI 패널이 생길 때 의미 있음).
