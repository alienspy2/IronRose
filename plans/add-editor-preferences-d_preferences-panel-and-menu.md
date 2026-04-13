# Phase D: Preferences 패널 + Edit 메뉴 통합 + making_log 문서화

## 목표

`Edit > Preferences...` 메뉴에서 열리는 `ImGuiPreferencesPanel`을 구현한다. Appearance(Color Theme / UI Scale / Editor Font) + Integrations(Enable Claude Usage) 섹션을 제공하고, 값 변경 시 즉시 `EditorPreferences.Save()`와 테마/스케일/폰트 재적용이 동작한다. 또한 `making_log/`에 시스템 문서 2개를 작성한다.

## 선행 조건

- Phase A, B, C 완료.

## 생성할 파일

### `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`

- **역할**: 앱-레벨 Preferences 편집 UI.
- **네임스페이스**: `IronRose.Engine.Editor.ImGuiEditor.Panels`
- **클래스**: `public class ImGuiPreferencesPanel : IEditorPanel`

- **주요 멤버**:
  - `private bool _isOpen;`
  - `public bool IsOpen { get => _isOpen; set => _isOpen = value; }`
  - `public void Draw()`:
    ```csharp
    if (!IsOpen) return;
    var visible = ImGui.Begin("Preferences", ref _isOpen);
    PanelMaximizer.DrawTabContextMenu("Preferences");
    if (visible)
    {
        if (ImGui.CollapsingHeader("Appearance", ImGuiTreeNodeFlags.DefaultOpen))
            DrawAppearance();
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Integrations", ImGuiTreeNodeFlags.DefaultOpen))
            DrawIntegrations();
    }
    ImGui.End();
    ```
  - `private void DrawAppearance()`:
    - **Color Theme**:
      ```csharp
      string[] themeNames = { "Rose", "Dark", "Light" };
      int themeIdx = (int)EditorPreferences.ColorTheme;
      if (ImGui.Combo("Color Theme", ref themeIdx, themeNames, themeNames.Length))
      {
          EditorPreferences.ColorTheme = (EditorColorTheme)themeIdx;
          ImGuiTheme.Apply(EditorPreferences.ColorTheme);
          EditorPreferences.Save();
      }
      ```
    - **UI Scale**:
      ```csharp
      float scale = EditorPreferences.UiScale;
      if (ImGui.SliderFloat("UI Scale", ref scale, 0.5f, 3.0f, "%.2f"))
      {
          scale = Math.Clamp(scale, 0.5f, 3.0f);
          EditorPreferences.UiScale = scale;
          ImGui.GetIO().FontGlobalScale = scale;
          EditorPreferences.Save();
      }
      ```
      (실제 `ImGuiOverlay._uiScale` 동기화는 아래 Overlay 수정 섹션 참조.)
    - **Editor Font**:
      ```csharp
      string[] fontNames = { "Roboto", "ArchivoBlack" }; // ImGuiOverlay.FontNames 내용
      int fontIdx = Array.IndexOf(fontNames, EditorPreferences.EditorFont);
      if (fontIdx < 0) fontIdx = 0;
      if (ImGui.Combo("Editor Font", ref fontIdx, fontNames, fontNames.Length))
      {
          EditorPreferences.EditorFont = fontNames[fontIdx];
          EditorPreferences.Save();
      }
      ```
      **미결**: `ImGuiOverlay.FontNames`의 선언 위치/가시성을 현장 확인. `internal static readonly`라면 `ImGuiOverlay.FontNames`를 직접 참조해도 좋음. private이면 내부 복사본을 하드코딩하거나 Overlay의 접근자를 internal로 승격.
  - `private void DrawIntegrations()`:
    - **Enable Claude Usage**:
      ```csharp
      bool enabled = EditorPreferences.EnableClaudeUsage;
      if (ImGui.Checkbox("Enable Claude Usage", ref enabled))
      {
          EditorPreferences.EnableClaudeUsage = enabled;
          EditorPreferences.Save();
      }
      ImGui.TextDisabled("When enabled, Fix buttons appear in the Feedback panel and invoke claude -p.");
      ```

- **의존**: `ImGuiNET`, `IronRose.Engine`, `IronRose.Engine.Editor.ImGuiEditor`, `System`.

- **구현 힌트**:
  - `ImGui.Combo(string, ref int, string[], int)` 오버로드 존재. 없으면 `ImGui.Combo(string, ref int, string)`에 `"Rose\0Dark\0Light\0\0"` 문자열을 넘기는 방식.
  - 변경 감지는 Combo/Slider/Checkbox의 반환값이 `bool`(값 변경 시 true)이므로 그걸로 분기. 불필요한 Save 방지.

## 수정할 파일

### `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

- **변경 내용**:
  1. **필드 추가** (line 72 `_startupPanel` 근처):
     ```csharp
     private ImGuiPreferencesPanel? _preferencesPanel;
     ```
  2. **인스턴스 생성** (line 372 `_startupPanel = new ImGuiStartupPanel();` 다음 줄):
     ```csharp
     _preferencesPanel = new ImGuiPreferencesPanel();
     ```
  3. **Draw 호출**: 다른 패널들이 매 프레임 `Draw()`되는 지점을 찾아 (예: `DrawPanels()`, `RenderPanels()`, 또는 ImGuiOverlay의 메인 루프 내 패널 Draw 블록) `_preferencesPanel?.Draw();` 한 줄 추가. **정확한 위치는 `_feedback?.Draw();` 등 기존 패널 Draw 호출과 동일 블록에 붙일 것** (구현자가 grep으로 확인).
  4. **Edit 메뉴에 항목 추가** (line 555~568 사이). Redo MenuItem 다음에 구분선 + Preferences:
     ```csharp
     if (ImGui.MenuItem(redoLabel, "Ctrl+Shift+Z", false, rDesc != null))
         UndoSystem.PerformRedo();

     ImGui.Separator();

     if (ImGui.MenuItem("Preferences..."))
         _preferencesPanel!.IsOpen = true;

     ImGui.EndMenu();
     ```
  5. **Preferences → Overlay 역동기화** (Preferences 패널에서 UiScale/Font 변경 시 기존 메뉴 상태와 일치하도록). `Update(double, IWindow)` 메서드 시작부에 다음 블록 삽입:
     ```csharp
     // Preferences 패널에서 변경된 값 반영
     if (Math.Abs(_uiScale - EditorPreferences.UiScale) > 0.0001f)
     {
         _uiScale = EditorPreferences.UiScale;
         ImGui.GetIO().FontGlobalScale = _uiScale;
     }
     if (_currentFont != EditorPreferences.EditorFont)
     {
         _currentFont = EditorPreferences.EditorFont;
     }
     ```
     테마는 Preferences 패널이 직접 `ImGuiTheme.Apply`를 호출하므로 별도 동기화 불필요.
  6. **Font 배열 가시성**: 만약 `FontNames`가 private이라면 `internal static readonly string[] FontNames`로 승격(한 줄 수정).

- **이유**: plan 섹션 3 (Preferences 패널), 섹션 4 (Edit 메뉴 통합).

### `src/IronRose.Engine/Editor/EditorState.cs`

- **변경 내용**: **없음**. Preferences 창의 가시성은 세션 간 영속화하지 않으므로 `PanelPreferences` 필드를 **추가하지 않는다** (plan 섹션 3 명시).

## 생성할 파일 (문서화)

### `making_log/_system-editor-preferences.md`

- **내용 요강**:
  - 개요: 앱-레벨 사용자 Preferences 저장소.
  - 파일 위치: `~/.ironrose/settings.toml`의 `[preferences]` 섹션.
  - 관리 항목: `color_theme`, `enable_claude_usage`, `ui_scale`, `editor_font`.
  - 저장 패턴: read-modify-write (ProjectContext와 같은 파일을 섹션으로 공유).
  - 확장 가이드 (5단계): 속성 추가 → Load 파싱 → Save 쓰기 → UI 위젯 추가 → 값 변경 시 Save 호출.
  - EditorState → Preferences 1회성 마이그레이션(ui_scale/editor_font).
  - 관련 파일: `EditorPreferences.cs`, `ImGuiPreferencesPanel.cs`, `ImGuiTheme.cs`, `ImGuiOverlay.cs`.

### `making_log/_system-claude-manager.md`

- **내용 요강**:
  - 개요: Claude 연동 단일 진입점.
  - 허용 호출: `claude -p --verbose --output-format stream-json`만.
  - 게이트: `EditorPreferences.EnableClaudeUsage`가 false면 API가 null 반환, Feedback UI는 Fix/Stop/출력 영역을 렌더링하지 않음.
  - `ClaudeSession` 라이프사이클: Start → IsRunning 동안 SnapshotOutput 폴링 → Stop 또는 자연 종료 → Dispose.
  - stream-json 이벤트 타입: `content_block_delta` / `assistant` content 블록 / `result` (fallback: raw line).
  - 현재 사용처: `ImGuiFeedbackPanel.StartFix`.
  - 관련 파일: `ClaudeManager.cs`, `ImGuiFeedbackPanel.cs`.

## NuGet 패키지

- 추가 없음.

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] `Edit > Preferences...` 클릭 시 Preferences 창이 열린다.
- [ ] Color Theme을 Dark/Light로 변경하면 **즉시 팔레트가 바뀌고** 재시작 후에도 유지된다.
- [ ] UI Scale 슬라이더 조작 시 상단 `View > UI > UI Scale` 메뉴의 현재 체크 값이 일치한다 (양방향 동기화).
- [ ] Editor Font 변경 시 선택한 폰트가 반영된다.
- [ ] `Enable Claude Usage` 체크를 토글하면 Feedback 패널의 Fix 버튼이 즉시 나타나고/사라진다 (파일 재시작 불필요).
- [ ] Preferences 창은 닫은 후 재시작하면 **닫힌 상태**로 시작한다 (영속화 없음).
- [ ] `~/.ironrose/settings.toml`의 `[editor] last_project`가 Preferences Save 후에도 보존된다.
- [ ] `making_log/_system-editor-preferences.md`, `making_log/_system-claude-manager.md` 두 파일이 생성됨.

## 참고

- plan 섹션 3~6 전부.
- Preferences 창의 `_isOpen` 플래그는 런타임 전용 (EditorState에 새 필드 추가 금지).
- 폰트 리소스 재빌드 없이 `_currentFont` 문자열만 바꾸면 `ImGuiOverlay.PushCurrentFont`/`PopCurrentFont` (line 1678~1689)가 해당 폰트로 렌더링.
- 미결: `ImGuiOverlay.FontNames` 선언 위치/가시성 현장 확인. private이면 internal 승격 또는 Preferences 패널 내부 하드코딩 중 택1.
- `ImGui.Combo` 오버로드 가용성은 ImGuiNET 버전에 따라 다름. `string[]` 오버로드가 없으면 `"Rose\0Dark\0Light\0\0"` 형식 사용.
