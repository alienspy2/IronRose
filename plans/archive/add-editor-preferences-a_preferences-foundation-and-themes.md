# Phase A: Preferences Foundation + Theme 팔레트 3종

## 목표

사용자 전역 Preferences 저장소(`EditorPreferences`)와 Rose/Dark/Light 세 가지 실제 팔레트를 갖춘 `ImGuiTheme.Apply(EditorColorTheme)` 오버로드를 도입한다. 이 phase에서는 아직 UI 패널이나 `ClaudeManager`를 만들지 않으며, 기존 UiScale/EditorFont 경로는 그대로 유지되어 런타임 동작이 깨지지 않는다.

## 선행 조건

- 없음 (최초 phase).

## 생성할 파일

### `src/IronRose.Engine/EditorPreferences.cs`

- **역할**: 사용자 전역 앱-레벨 선호 설정의 메모리 표현 + `~/.ironrose/settings.toml`의 `[preferences]` 섹션 I/O.
- **네임스페이스**: `IronRose.Engine`
- **타입 정의**:
  - `public enum EditorColorTheme { Rose, Dark, Light }` (파일 상단 네임스페이스 내부).
  - `public static class EditorPreferences`.

- **주요 멤버** (정적 속성, 기본값 포함):
  - `EditorColorTheme ColorTheme { get; set; } = EditorColorTheme.Rose;`
  - `bool EnableClaudeUsage { get; set; } = false;`
  - `float UiScale { get; set; } = 1.0f;` — 0.5~3.0 clamp (Load 시).
  - `string EditorFont { get; set; } = "Roboto";`

- **정적 메서드**:
  - `static void Load()` — `~/.ironrose/settings.toml` 로드, `[preferences]` 섹션 파싱. 파일이 없으면 기본값 유지. `TomlConfig.LoadFile(path, "[EditorPreferences]")` 사용. `color_theme`는 문자열(`"rose"|"dark"|"light"`)로 저장하며 대소문자 무시 파싱, 유효하지 않으면 `Rose` 폴백.
  - `static void Save()` — **반드시 read-modify-write**. `ProjectContext.SaveLastProjectPath()` (ProjectContext.cs line 183~209)와 동일 패턴으로 기존 `[editor] last_project` 섹션을 보존. 구현:

    ```csharp
    Directory.CreateDirectory(GlobalSettingsDir);
    var config = TomlConfig.LoadFile(GlobalSettingsPath) ?? TomlConfig.CreateEmpty();
    var pref = config.GetSection("preferences");
    if (pref == null)
    {
        pref = TomlConfig.CreateEmpty();
        config.SetSection("preferences", pref);
    }
    pref.SetValue("color_theme", ColorThemeToString(ColorTheme));
    pref.SetValue("enable_claude_usage", EnableClaudeUsage);
    pref.SetValue("ui_scale", UiScale);
    pref.SetValue("editor_font", EditorFont);
    config.SaveToFile(GlobalSettingsPath, "[EditorPreferences]");
    ```

  - `static void MigrateFromEditorState(float? legacyUiScale, string? legacyEditorFont)` — Phase B에서 호출할 1회성 훅. null이 아닌 인자만 속성에 덮어쓰고, 하나 이상 적용됐으면 `Save()` 호출. Phase A에서는 시그니처와 동작만 구현해두고 호출자는 없음.

- **내부 헬퍼**:
  - `static string GlobalSettingsDir` — `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ironrose")`.
  - `static string GlobalSettingsPath` — `Path.Combine(GlobalSettingsDir, "settings.toml")`.
  - `static string ColorThemeToString(EditorColorTheme t)` — `Rose→"rose"`, `Dark→"dark"`, `Light→"light"`.
  - `static EditorColorTheme ParseColorTheme(string s)` — `ToLowerInvariant()` 후 분기, 매칭 실패 시 `Rose`.

- **의존**: `IronRose.Engine.TomlConfig`, `RoseEngine.EditorDebug`, `System.IO`, `System`.

- **구현 힌트**:
  - `TomlConfig.LoadFile`은 파일 없으면 null 반환. `??` 연산자로 `CreateEmpty()`로 폴백.
  - `SetValue`는 `object`를 받으므로 `bool`, `float`, `string`을 그대로 전달. TOML 직렬화는 TomlConfig에 위임.
  - Load 시 `UiScale`은 `Math.Clamp(..., 0.5f, 3.0f)`로 방어.
  - 파일 상단 `// @file` 블록에 plan "섹션 6. 확장 가이드라인" 5단계(속성 추가 → Load 파싱 → Save 쓰기 → UI 위젯 → 값 변경 시 Save 호출)를 주석으로 포함.

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/ImGuiTheme.cs`

- **변경 내용**:
  1. 기존 `public static void Apply()`는 **유지**하되 몸체를 `Apply(EditorColorTheme.Rose);` 한 줄로 위임.
  2. 새 오버로드 `public static void Apply(EditorColorTheme theme)` 추가. 공통 스타일(Rounding/Spacing/ItemSpacing/GrabMinSize 등 기존 line 15~28 블록)은 먼저 적용한 뒤, `switch (theme)`로 색상 테이블만 분기:
     - `EditorColorTheme.Rose` → `ApplyRosePalette(style)`
     - `EditorColorTheme.Dark` → `ApplyDarkPalette(style)`
     - `EditorColorTheme.Light` → `ApplyLightPalette(style)`
  3. 세 팔레트 팩토리 private 메서드 추가:
     - `ApplyRosePalette(ImGuiStylePtr style)` — 기존 line 32~95 색상 블록을 그대로 이동.
     - `ApplyDarkPalette(ImGuiStylePtr style)` — ImGui 기본 Dark 팔레트 기반. 참고값: `WindowBg = (0.10f, 0.10f, 0.11f, 1f)`, `ChildBg = (0f, 0f, 0f, 0f)`, `PopupBg = (0.08f, 0.08f, 0.08f, 0.94f)`, `Border = (0.43f, 0.43f, 0.50f, 0.50f)`, `FrameBg = (0.20f, 0.21f, 0.22f, 0.54f)`, `FrameBgHovered = (0.40f, 0.40f, 0.40f, 0.40f)`, `FrameBgActive = (0.18f, 0.50f, 0.83f, 0.67f)`, `TitleBg = (0.04f, 0.04f, 0.04f, 1f)`, `TitleBgActive = (0.16f, 0.29f, 0.48f, 1f)`, `MenuBarBg = (0.14f, 0.14f, 0.14f, 1f)`, `Text = (1f, 1f, 1f, 1f)`, `TextDisabled = (0.50f, 0.50f, 0.50f, 1f)`, 악센트 `accent = (0.26f, 0.59f, 0.98f, 1f)` (파란색 계열), `CheckMark/SliderGrab = accent`, `Button = (accent, 0.40f)`, `ButtonHovered = accent`, `ButtonActive = (0.06f, 0.53f, 0.98f, 1f)`, `Header/HeaderHovered/HeaderActive = accent` 알파 0.31/0.80/1.0. Rose와 **동일한 ImGuiCol 키 집합 전부 빠짐없이 채움**(특히 `DockingPreview`, `TabSelected`, `TabDimmed`, `TabDimmedSelected`, `TableHeaderBg`, `TableBorderStrong`, `TableBorderLight`, `TableRowBg`, `TableRowBgAlt`, `ScrollbarBg/Grab/GrabHovered/GrabActive`, `ResizeGrip/ResizeGripHovered/ResizeGripActive`, `Separator/SeparatorHovered/SeparatorActive`).
     - `ApplyLightPalette(ImGuiStylePtr style)` — ImGui 기본 Light 팔레트 기반. 참고값: `WindowBg = (0.94f, 0.94f, 0.94f, 1f)`, `ChildBg = (0f, 0f, 0f, 0f)`, `PopupBg = (1f, 1f, 1f, 0.98f)`, `Border = (0f, 0f, 0f, 0.30f)`, `FrameBg = (1f, 1f, 1f, 1f)`, `FrameBgHovered = (0.26f, 0.59f, 0.98f, 0.40f)`, `FrameBgActive = (0.26f, 0.59f, 0.98f, 0.67f)`, `TitleBg = (0.96f, 0.96f, 0.96f, 1f)`, `TitleBgActive = (0.82f, 0.82f, 0.82f, 1f)`, `MenuBarBg = (0.86f, 0.86f, 0.86f, 1f)`, `Text = (0f, 0f, 0f, 1f)`, `TextDisabled = (0.60f, 0.60f, 0.60f, 1f)`, 악센트 `accent = (0.26f, 0.59f, 0.98f, 1f)`, 나머지 키는 Dark 구조와 동일한 라벨로 채우되 밝은 톤 기준. Rose와 동일한 키 집합 전부 채움.
  4. `using IronRose.Engine;` 추가 (enum 참조 때문).

- **이유**: plan "결정된 사항 1" — Rose/Dark/Light 실제 팔레트 구현.

- **주의**: Rose 팔레트 값은 기존 값을 그대로 `ApplyRosePalette`로 옮기고 수치 변경 금지(회귀 방지). 각 팔레트 메서드 상단에 1~2줄 주석으로 테마 성격 기술. Dark/Light 정확한 값 출처는 ImGui 원본 `StyleColorsDark()` / `StyleColorsLight()` (imgui_draw.cpp) — 필요 시 코드에서 그대로 복사 가능.

## NuGet 패키지

- 추가 없음.

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] 에디터 실행 시 기존과 동일하게 Rose 테마가 적용됨 (회귀 없음).
- [ ] `~/.ironrose/settings.toml`에 `[preferences]` 섹션이 **아직 생성되지 않음** (Save 호출이 아직 없으므로).
- [ ] (내부 확인) `EditorPreferences.Load()` / `Save()`를 임시 Main에서 호출해보면 파일 read-modify-write가 정상 동작.

## 참고

- plan 섹션 1, "결정된 사항 1" 참조.
- `ImGuiOverlay.cs` line 327에서 현재 `ImGuiTheme.Apply()` (무인자) 호출 중. Phase A에서는 그 호출을 바꾸지 않는다 (호환성). Phase B에서 `Apply(EditorPreferences.ColorTheme)`로 교체.
- `TomlConfig.CreateEmpty/LoadFile/GetSection/SetSection/SetValue/SaveToFile` API는 `src/IronRose.Engine/TomlConfig.cs`에 존재 확인됨.
