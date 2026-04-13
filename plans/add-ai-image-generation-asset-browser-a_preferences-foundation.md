# Phase A: Preferences 확장 + AI Asset Generation 섹션 + Health Check

## 목표

- 사용자 전역 Preferences(`~/.ironrose/settings.toml`의 `[preferences]` 섹션)에 AI 이미지 생성 기능에 필요한 5개의 키를 추가한다.
- `Edit > Preferences...` 창에 **`AI Asset Generation`** 섹션을 추가하고, 최상위 토글 + 4개의 서브 옵션 + **Health Check 버튼**을 제공한다.
- Phase B/C가 이 속성들을 안전하게 읽고 Save할 수 있는 상태로 만든다.
- 이 Phase만으로도 빌드와 실행이 정상 동작하며, 토글/서브 입력이 TOML에 저장/복원되고 Health Check 버튼이 서버 상태를 표시해야 한다.

## 선행 조건

- 현재 `main` 브랜치 상태 (기존 Preferences 시스템이 이미 존재).
- 다음 파일들이 그대로 존재:
  - `src/IronRose.Engine/EditorPreferences.cs`
  - `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`
  - `src/IronRose.Engine/Editor/ImGui/EditorWidgets.cs` (BeginPropertyRow 정적 헬퍼 제공)

이 Phase는 다른 선행 Phase 없이 단독으로 시작 가능하다.

## 수정할 파일

### 1. `src/IronRose.Engine/EditorPreferences.cs`

**변경 내용**: 5개 정적 속성 추가 + Load/Save에 키 5개 추가 + 상단 `@exports` 주석 갱신.

#### 1-1. 상단 `@exports` 주석 갱신

기존 `EditorFont: string` 라인 다음 줄에 아래 5개를 삽입한다 (기존 `Load(): void` 줄 바로 위).

```
//     EnableAiAssetGeneration: bool             — AI 이미지 생성 기능 전체 on/off (기본 true, 메뉴/다이얼로그 노출만 제어)
//     AiAlienhsServerUrl: string                — AlienHS 서버 URL (기본 "http://localhost:25000")
//     AiPythonPath: string                      — CLI 실행 파이썬 경로 (기본 "python")
//     AiRefineEndpoint: string                  — 프롬프트 refine 엔드포인트 키 (기본 "")
//     AiRefineModel: string                     — ComfyUI 모델 파일명 (기본 "")
```

또한 @note 블록 하단 "새 preference 항목 추가 가이드"는 이미 일반 가이드이므로 그대로 둔다.

#### 1-2. 정적 속성 추가

`EditorFont` 속성 정의 블록 다음(기존 `GlobalSettingsDir` 프라이빗 프로퍼티 직전)에 아래를 추가한다:

```csharp
/// <summary>AI 이미지 생성 기능 on/off 토글. 끄면 Asset Browser 메뉴/다이얼로그가 숨겨진다.</summary>
public static bool EnableAiAssetGeneration { get; set; } = true;

/// <summary>AlienHS invoke-comfyui 서버 URL.</summary>
public static string AiAlienhsServerUrl { get; set; } = "http://localhost:25000";

/// <summary>CLI 실행에 사용할 Python 인터프리터 경로.</summary>
public static string AiPythonPath { get; set; } = "python";

/// <summary>프롬프트 refine 엔드포인트 키. 빈 문자열이면 CLI 기본값을 사용한다.</summary>
public static string AiRefineEndpoint { get; set; } = "";

/// <summary>ComfyUI 모델 파일명. 빈 문자열이면 CLI 기본값을 사용한다.</summary>
public static string AiRefineModel { get; set; } = "";
```

#### 1-3. `Load()` 메서드

기존 `Load()` 내부, `editor_font` 파싱 블록(현재 line 103~106)과 `EditorDebug.Log($"[EditorPreferences] Loaded...")` 사이에 아래를 삽입:

```csharp
// enable_ai_asset_generation
EnableAiAssetGeneration = pref.GetBool("enable_ai_asset_generation", EnableAiAssetGeneration);

// ai_alienhs_server_url
var aiServerStr = pref.GetString("ai_alienhs_server_url", "");
if (!string.IsNullOrEmpty(aiServerStr))
    AiAlienhsServerUrl = aiServerStr;

// ai_python_path
var aiPyStr = pref.GetString("ai_python_path", "");
if (!string.IsNullOrEmpty(aiPyStr))
    AiPythonPath = aiPyStr;

// ai_refine_endpoint (빈 문자열도 허용 — 그대로 복원)
AiRefineEndpoint = pref.GetString("ai_refine_endpoint", AiRefineEndpoint) ?? "";

// ai_refine_model
AiRefineModel = pref.GetString("ai_refine_model", AiRefineModel) ?? "";
```

#### 1-4. `Save()` 메서드

기존 `Save()` 내부 `pref.SetValue("editor_font", EditorFont);` 라인 다음에 아래 5줄을 삽입:

```csharp
pref.SetValue("enable_ai_asset_generation", EnableAiAssetGeneration);
pref.SetValue("ai_alienhs_server_url", AiAlienhsServerUrl);
pref.SetValue("ai_python_path", AiPythonPath);
pref.SetValue("ai_refine_endpoint", AiRefineEndpoint);
pref.SetValue("ai_refine_model", AiRefineModel);
```

#### 1-5. TOML 직렬화 규칙 요약

```toml
[preferences]
color_theme = "rose"
enable_claude_usage = false
ui_scale = 1.0
editor_font = "Roboto"
enable_ai_asset_generation = true
ai_alienhs_server_url = "http://localhost:25000"
ai_python_path = "python"
ai_refine_endpoint = ""
ai_refine_model = ""
```

- 키는 모두 `snake_case`.
- bool: `true`/`false`.
- 문자열: 큰따옴표, Tomlyn이 그대로 처리.
- 빈 문자열도 명시적으로 저장된다 (Load 시 비어있으면 Load 내부에서는 기존 기본값을 그대로 둠 — 위 파싱 로직 참조).

---

### 2. `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`

**변경 내용**: `System.Net.Http` using 추가, `AI Asset Generation` 섹션 렌더 메서드 추가, Draw()에 CollapsingHeader 호출 추가, Health Check 상태 캐시 필드 추가.

#### 2-1. using 추가

파일 상단에 아래 두 using을 추가한다 (`using ImGuiNET;` 바로 아래):

```csharp
using System.Net.Http;
using System.Threading.Tasks;
```

#### 2-2. 상단 주석 @exports/@deps 갱신

기존 파일 헤더의 `@brief`를 다음과 같이 수정:

```
// @brief   앱-레벨 사용자 Preferences 편집 UI. Edit > Preferences... 메뉴에서 열리며
//          Appearance(Color Theme / UI Scale / Editor Font), Integrations(Enable Claude Usage),
//          AI Asset Generation(토글 + 서버 URL + Health Check + Python Path + Refine Endpoint/Model)
//          섹션을 제공한다.
```

`@deps`에 `System.Net.Http`를 추가한다.

#### 2-3. Health Check 상태 캐시 필드

`ImGuiPreferencesPanel` 클래스 내부, 기존 `ThemeNames` 정적 배열 다음에 아래 인스턴스 필드 4개 추가:

```csharp
// Health Check 상태 캐시 (세션 전용, 영속화 없음)
private string _healthCheckLabel = "";      // 화면에 표시할 결과 라벨 ("OK", "Failed: ..." 등)
private uint _healthCheckColor = 0xFFFFFFFF; // 라벨 색상 (ImGui ABGR). 0 이면 TextUnformatted 기본색
private bool _healthCheckRunning = false;    // 중복 클릭 방지
private static readonly HttpClient _healthHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
```

> 주의: `HttpClient`는 정적으로 재사용한다 (소켓 leak 방지). `using System;` 은 이미 존재.

#### 2-4. Draw() 수정

기존 `DrawIntegrations()` 호출 바로 다음에 새 섹션을 추가한다:

```csharp
ImGui.Spacing();

if (ImGui.CollapsingHeader("AI Asset Generation", ImGuiTreeNodeFlags.DefaultOpen))
    DrawAiAssetGeneration();
```

#### 2-5. `DrawAiAssetGeneration()` 메서드 구현

`DrawIntegrations()` 메서드 아래에 신규 메서드를 추가한다:

```csharp
private void DrawAiAssetGeneration()
{
    // Enable 토글
    bool enabled = EditorPreferences.EnableAiAssetGeneration;
    string enableLabel = EditorWidgets.BeginPropertyRow("Enable AI Asset Generation");
    if (ImGui.Checkbox(enableLabel, ref enabled))
    {
        EditorPreferences.EnableAiAssetGeneration = enabled;
        EditorPreferences.Save();
    }
    ImGui.TextDisabled("When enabled, \"Generate with AI (Texture)...\" appears in the Asset Browser context menu.");

    // 토글이 꺼지면 하위 위젯은 비활성화 (값 편집은 가능하되 시각적으로 disabled 표시)
    ImGui.BeginDisabled(!enabled);

    // AlienHS Server URL
    {
        string serverLabel = EditorWidgets.BeginPropertyRow("AlienHS Server URL");
        string buf = EditorPreferences.AiAlienhsServerUrl ?? "";
        if (ImGui.InputText(serverLabel, ref buf, 512))
        {
            EditorPreferences.AiAlienhsServerUrl = buf;
            EditorPreferences.Save();
        }
    }

    // Health Check 버튼 + 결과 라벨
    {
        string hcLabel = EditorWidgets.BeginPropertyRow("Health Check");
        ImGui.BeginDisabled(_healthCheckRunning);
        if (ImGui.Button(_healthCheckRunning ? "Checking...##healthcheck" : "Check##healthcheck"))
        {
            RunHealthCheck(EditorPreferences.AiAlienhsServerUrl);
        }
        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(_healthCheckLabel))
        {
            ImGui.SameLine();
            if (_healthCheckColor != 0)
                ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(_healthCheckColor), _healthCheckLabel);
            else
                ImGui.TextUnformatted(_healthCheckLabel);
        }
    }

    // Python Path
    {
        string pyLabel = EditorWidgets.BeginPropertyRow("Python Path");
        string buf = EditorPreferences.AiPythonPath ?? "";
        if (ImGui.InputText(pyLabel, ref buf, 512))
        {
            EditorPreferences.AiPythonPath = buf;
            EditorPreferences.Save();
        }
    }

    // Refine Endpoint
    {
        string epLabel = EditorWidgets.BeginPropertyRow("Refine Endpoint");
        string buf = EditorPreferences.AiRefineEndpoint ?? "";
        if (ImGui.InputText(epLabel, ref buf, 256))
        {
            EditorPreferences.AiRefineEndpoint = buf;
            EditorPreferences.Save();
        }
        ImGui.TextDisabled("Empty = CLI default.");
    }

    // Refine Model
    {
        string modelLabel = EditorWidgets.BeginPropertyRow("Refine Model");
        string buf = EditorPreferences.AiRefineModel ?? "";
        if (ImGui.InputText(modelLabel, ref buf, 256))
        {
            EditorPreferences.AiRefineModel = buf;
            EditorPreferences.Save();
        }
        ImGui.TextDisabled("Empty = CLI default.");
    }

    ImGui.EndDisabled();
}
```

> 주의: `InputText`는 InputText 콜백 없이 `ref string buf` 오버로드를 사용 (ImGuiNET 관례). 다른 패널의 InputText 호출 방식을 그대로 따른다. `buf` 로컬 변수를 받아 반환값이 true일 때만 속성과 Save를 호출한다.

#### 2-6. `RunHealthCheck()` 메서드

```csharp
/// <summary>
/// AlienHS 서버의 루트 경로에 GET 요청을 보내 200이면 OK, 그 외는 Failed로 표시.
/// 3초 타임아웃. 백그라운드 Task에서 실행하며 결과는 다음 프레임 렌더에 반영된다.
/// </summary>
/// <param name="serverUrl">Preferences.AiAlienhsServerUrl 값.</param>
private void RunHealthCheck(string serverUrl)
{
    if (_healthCheckRunning) return;
    _healthCheckRunning = true;
    _healthCheckLabel = "Checking...";
    _healthCheckColor = 0xFFAAAAAA; // 회색

    string url = (serverUrl ?? "").TrimEnd('/') + "/";
    _ = Task.Run(async () =>
    {
        try
        {
            using var resp = await _healthHttp.GetAsync(url);
            int code = (int)resp.StatusCode;
            if (code == 200)
            {
                _healthCheckLabel = "OK";
                _healthCheckColor = 0xFF00FF00; // 녹색 (ABGR)
            }
            else
            {
                _healthCheckLabel = $"Failed: HTTP {code}";
                _healthCheckColor = 0xFF0000FF; // 빨강
            }
        }
        catch (TaskCanceledException)
        {
            _healthCheckLabel = "Failed: timeout";
            _healthCheckColor = 0xFF0000FF;
        }
        catch (Exception ex)
        {
            _healthCheckLabel = $"Failed: {ex.Message}";
            _healthCheckColor = 0xFF0000FF;
        }
        finally
        {
            _healthCheckRunning = false;
        }
    });
}
```

> 주의사항:
> - ImGui의 색 변환: `ImGui.ColorConvertU32ToFloat4` 시그니처는 `Vector4 ColorConvertU32ToFloat4(uint)` 이다. ABGR 바이트 순서(0xAABBGGRR). 위 값은 표준 ABGR 순서. ImGuiNET 환경에서 혹시 맞지 않으면 `new System.Numerics.Vector4(0f, 1f, 0f, 1f)` (녹색) / `new System.Numerics.Vector4(1f, 0f, 0f, 1f)` (빨강) 으로 직접 전달하는 대안도 가능.
> - `Task.Run`으로 HTTP 호출은 ThreadPool에서 실행된다. `_healthCheckLabel`/`_healthCheckColor`는 ImGui 스레드에서 읽히므로 Interlocked/lock 없이도 `bool`/`uint`/`string`의 tear-safe 범위이지만, 시인성을 위해 `volatile` 없이도 ImGui 루프가 매 프레임 다시 읽으므로 실전 문제는 없다.

---

## 생성할 파일

없음 (이 Phase는 기존 2개 파일만 수정).

## NuGet 패키지

없음. `System.Net.Http`는 .NET 기본 제공.

## 검증 기준

- [ ] `dotnet build src/IronRose.Engine/IronRose.Engine.csproj` 성공 (솔루션 전체 빌드도 성공).
- [ ] 에디터 실행 후 `Edit > Preferences...` 열면 **AI Asset Generation** CollapsingHeader가 표시된다.
- [ ] 기본값이 보인다: Enable = ON, Server = `http://localhost:25000`, Python = `python`, Refine Endpoint/Model 모두 빈칸.
- [ ] Enable 토글을 끄면 하위 위젯이 `BeginDisabled` 효과로 회색 처리된다.
- [ ] 각 InputText/Checkbox 변경 직후 `~/.ironrose/settings.toml`을 열어보면 5개의 `ai_*` + `enable_ai_asset_generation` 키가 저장돼 있다.
- [ ] 서버를 끈 상태에서 **Check** 버튼을 누르면 3초 이내에 `Failed: ...` 라벨이 표시된다.
- [ ] 서버가 살아있을 때 (`healthcheck.sh` 통과 가능한 상태) 누르면 `OK` 라벨이 녹색으로 표시된다.
- [ ] 에디터 재시작 후에도 저장된 값들이 복원된다.
- [ ] 기존 Preferences 항목(Color Theme, UI Scale, Editor Font, Enable Claude Usage)은 영향 없이 그대로 동작한다.

## 참고

- 설계 문서의 "1. Preferences 확장" 섹션과 "리스크 및 확정 사항 - Health Check 엔드포인트" 확정 사항을 구현한다.
- Health Check는 `/home/alienspy/git/alienhs/healthcheck.sh` 기준: GET `<server>/` → 200 OK.
- 이 Phase는 아직 **Asset Browser 메뉴/다이얼로그/CLI 파이프라인/히스토리와 연결되지 않는다**. 단지 Preferences 영속화와 Health Check UI만 추가한다. 실제 메뉴 노출 게이트(`if (EnableAiAssetGeneration)`)는 Phase C에서 붙인다.
- 미결 사항: `RunHealthCheck`의 `Task.Run` 결과를 UI 스레드로 마샬링하지 않고 필드만 덮어쓰는 구조는 컴파일러 메모리 재정렬 관점에서 이론적으로 issue가 있으나, ImGui 렌더 루프가 매 프레임 재-read 하므로 사용자 체감상 지연 1~2 프레임 내에 정상 표시된다. 추후 필요 시 `EditorModal.EnqueueAlert` 혹은 `ConcurrentQueue` 패턴으로 교체 가능.
- ImGui 색 상수가 플랫폼에 따라 다르게 보일 수 있으므로, 확인 후 필요 시 `ImGui.TextColored(new Vector4(...))` 직접 사용으로 전환해도 무방하다.

## 체크리스트 (순서대로)

- [ ] EditorPreferences.cs 헤더 주석의 @exports에 5개 라인 추가
- [ ] EditorPreferences.cs에 5개 정적 속성 추가
- [ ] EditorPreferences.cs Load() 확장 (5개 키 파싱)
- [ ] EditorPreferences.cs Save() 확장 (5개 키 SetValue)
- [ ] ImGuiPreferencesPanel.cs 헤더 주석 @brief 갱신
- [ ] ImGuiPreferencesPanel.cs using 2개 추가 (System.Net.Http, System.Threading.Tasks)
- [ ] ImGuiPreferencesPanel.cs Health Check 상태 필드 4개 추가
- [ ] ImGuiPreferencesPanel.cs Draw()에 CollapsingHeader + DrawAiAssetGeneration() 호출 추가
- [ ] ImGuiPreferencesPanel.cs DrawAiAssetGeneration() 구현
- [ ] ImGuiPreferencesPanel.cs RunHealthCheck() 구현
- [ ] `dotnet build` 성공
- [ ] 에디터 실행 → Preferences 창에서 모든 위젯 동작 확인
- [ ] `~/.ironrose/settings.toml` 직접 열어 키 저장 확인
- [ ] Health Check OK/Fail 동작 확인 (서버 on/off 전환)
