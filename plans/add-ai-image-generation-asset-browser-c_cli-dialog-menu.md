# Phase C: CLI 파이프라인 + AiImageGenerateDialog + Asset Browser 컨텍스트 메뉴 통합

## 목표

- **`AiImageGenerationService`**: 다이얼로그 입력 + Preferences를 합쳐 `cli-invoke-comfyui.py`를 백그라운드 `Process.Start`로 호출하고, 결과를 UI 스레드로 디스패치하는 정적 서비스.
- **`AiImageGenerateDialog`**: `ImGuiProjectPanel`이 소유하는 보조 팝업 모달. 프롬프트/파일명/토글/히스토리 UI 제공.
- **`ImGuiProjectPanel` 통합**: 빈 공간 컨텍스트 메뉴(line ~446)와 폴더 트리 컨텍스트 메뉴(line ~1092) 두 곳에 `Generate with AI (Texture)...` 메뉴 항목 추가. Draw 루프에 다이얼로그 렌더 + 결과 큐 드레인 추가.
- 전체 기능이 end-to-end로 동작: 우클릭 → 다이얼로그 → Generate → CLI 실행 → PNG가 현재 폴더에 떨어짐 → Reimport → 알림.

## 선행 조건

- **Phase A 완료** (Preferences 속성 5개 + Enable 토글).
- **Phase B 완료** (`AiImageHistory.Load/Entries/LastToggles/RecordSuccess`).
- `tools/invoke-comfyui/cli-invoke-comfyui.py`가 레포 루트 기준 상대경로로 존재. `ProjectContext.EngineRoot`에서 `tools/invoke-comfyui/cli-invoke-comfyui.py`로 접근 가능.
- `AssetDatabase.ReimportAsync(string)` API 존재 (`ImGuiProjectPanel.DrawAssetContextMenu` 참조).
- `EditorModal.EnqueueAlert(string)` 존재.
- `EditorPreferences.EnableAiAssetGeneration` 접근 가능.

---

## 생성할 파일

### 1. `src/IronRose.Engine/Editor/AiImageGenerationService.cs`

#### 파일 헤더

```csharp
// ------------------------------------------------------------
// @file    AiImageGenerationService.cs
// @brief   AI 이미지 생성 파이프라인. cli-invoke-comfyui.py를 백그라운드 프로세스로 호출하고,
//          결과를 UI 스레드 드레인용 ConcurrentQueue에 쌓는다.
// @deps    IronRose.Engine/EditorPreferences, IronRose.Engine/ProjectContext,
//          IronRose.Engine.Editor/AiImageHistory,
//          IronRose.AssetPipeline/AssetDatabase (UI 스레드 쪽에서만 참조),
//          IronRose.Engine.Editor.ImGuiEditor/EditorModal, RoseEngine/EditorDebug
// @exports
//   record AiImageGenerationRequest(...)
//   record AiImageGenerationResult(bool Success, string? AbsoluteOutputPath, string Message, AiImageGenerationRequest Request)
//   static class AiImageGenerationService
//     Enqueue(AiImageGenerationRequest): bool
//     DrainResults(Action<AiImageGenerationResult>): void
//     RunningCount: int
//     ResolveUniqueOutputPath(string folder, string baseName): string  (공용 helper)
//     IsInFlight(string absolutePath): bool
// @note    동일 절대 경로가 현재 실행 중이면 Enqueue 거부.
//          Process.Start 표준출력의 마지막 JSON 라인을 파싱. --json 출력 규약 참조.
// ------------------------------------------------------------
```

#### 공개 타입

```csharp
namespace IronRose.Engine.Editor
{
    public sealed record AiImageGenerationRequest(
        string TargetFolderAbsPath,   // 다이얼로그에서 선택된 폴더 (절대 경로)
        string ResolvedFileName,      // 확장자 제외, suffix 충돌 회피 적용 후 최종 이름
        string StylePrompt,
        string Prompt,
        bool Refine,
        bool Alpha);

    public sealed record AiImageGenerationResult(
        bool Success,
        string? AbsoluteOutputPath,   // 성공 시 최종 PNG 절대 경로, 실패 시 null
        string Message,               // 사용자에게 표시할 메시지 (성공/실패 공통)
        AiImageGenerationRequest Request);
}
```

#### 정적 API

```csharp
public static class AiImageGenerationService
{
    private static readonly ConcurrentQueue<AiImageGenerationResult> _pending = new();
    private static readonly HashSet<string> _inFlightPaths = new();       // 정규화된 절대 경로
    private static readonly object _inFlightLock = new();
    private static int _runningCount;

    public static int RunningCount => Volatile.Read(ref _runningCount);

    /// <summary>현재 생성 중인 절대 경로(확정 이름)인지 확인.</summary>
    public static bool IsInFlight(string absolutePath)
    {
        lock (_inFlightLock) return _inFlightPaths.Contains(Normalize(absolutePath));
    }

    /// <summary>
    /// 요청을 큐잉하고 백그라운드 Task를 시작한다.
    /// 동일 절대 경로가 이미 실행 중이면 EnqueueAlert로 거부 메시지 출력 후 false 반환.
    /// 반환 true면 호출자가 별도 확인할 필요 없다.
    /// </summary>
    public static bool Enqueue(AiImageGenerationRequest req);

    /// <summary>
    /// UI 스레드에서 매 프레임 호출해 완료된 결과를 꺼낸다.
    /// 콜백 안에서 AssetDatabase.ReimportAsync / AiImageHistory.RecordSuccess /
    /// EditorModal.EnqueueAlert를 호출하는 것은 호출자 책임.
    /// </summary>
    public static void DrainResults(Action<AiImageGenerationResult> onResult);

    /// <summary>
    /// <folder>/<baseName>.png가 이미 있으면 <baseName>_1.png, _2.png, ... 로
    /// 첫 번째로 존재하지 않는 이름을 찾아 **확장자 없는** baseName을 반환한다.
    /// 안전망: in-flight 경로와도 충돌하지 않는 이름을 반환.
    /// </summary>
    public static string ResolveUniqueFileName(string folderAbsPath, string baseName);
}
```

#### Enqueue 구현 스켈레톤

```csharp
public static bool Enqueue(AiImageGenerationRequest req)
{
    var finalPath = Path.GetFullPath(Path.Combine(req.TargetFolderAbsPath, req.ResolvedFileName + ".png"));
    var key = Normalize(finalPath);

    lock (_inFlightLock)
    {
        if (_inFlightPaths.Contains(key))
        {
            EditorModal.EnqueueAlert($"AI image generation already in progress for:\n{finalPath}");
            return false;
        }
        _inFlightPaths.Add(key);
    }

    Interlocked.Increment(ref _runningCount);

    _ = Task.Run(() =>
    {
        AiImageGenerationResult result;
        try
        {
            result = RunPipeline(req, finalPath);
        }
        catch (Exception ex)
        {
            result = new AiImageGenerationResult(false, null,
                $"AI image generation failed: {ex.GetType().Name}: {ex.Message}", req);
        }
        finally
        {
            lock (_inFlightLock) _inFlightPaths.Remove(key);
            Interlocked.Decrement(ref _runningCount);
        }

        _pending.Enqueue(result);
    });

    return true;
}
```

#### RunPipeline 구현

```csharp
private static AiImageGenerationResult RunPipeline(AiImageGenerationRequest req, string finalOutputPath)
{
    // 1) 프롬프트 조립
    string finalPrompt = AssembleFinalPrompt(req.StylePrompt, req.Prompt, req.Alpha);

    // 2) 경로/인자 준비
    string scriptPath = Path.Combine(ProjectContext.EngineRoot, "tools", "invoke-comfyui", "cli-invoke-comfyui.py");
    if (!File.Exists(scriptPath))
    {
        return new AiImageGenerationResult(false, null,
            $"CLI script not found: {scriptPath}", req);
    }

    var psi = new ProcessStartInfo
    {
        FileName = EditorPreferences.AiPythonPath,
        WorkingDirectory = req.TargetFolderAbsPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    // ArgumentList 사용 (OS별 quoting 자동 처리)
    psi.ArgumentList.Add(scriptPath);
    psi.ArgumentList.Add(finalPrompt);                         // 위치 인자: prompt
    psi.ArgumentList.Add("-o");
    psi.ArgumentList.Add(finalOutputPath);

    if (!req.Refine)
        psi.ArgumentList.Add("--bypass-refine");
    if (req.Alpha)
        psi.ArgumentList.Add("--rmbg");

    psi.ArgumentList.Add("--server");
    psi.ArgumentList.Add(EditorPreferences.AiAlienhsServerUrl);

    if (!string.IsNullOrWhiteSpace(EditorPreferences.AiRefineEndpoint))
    {
        psi.ArgumentList.Add("--endpoint");
        psi.ArgumentList.Add(EditorPreferences.AiRefineEndpoint);
    }
    if (!string.IsNullOrWhiteSpace(EditorPreferences.AiRefineModel))
    {
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(EditorPreferences.AiRefineModel);
    }

    psi.ArgumentList.Add("--json");

    EditorDebug.Log($"[AiImageGen] {psi.FileName} " + string.Join(" ", psi.ArgumentList));

    // 3) 프로세스 실행
    string stdout, stderr;
    int exitCode;
    using (var proc = Process.Start(psi))
    {
        if (proc == null)
            return new AiImageGenerationResult(false, null, "Failed to start python process.", req);

        stdout = proc.StandardOutput.ReadToEnd();
        stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        exitCode = proc.ExitCode;
    }

    // 4) JSON 파싱 (stdout의 마지막 JSON 라인 우선, 실패 시 stdout 전체)
    JsonDocument? doc = TryParseLastJsonLine(stdout);
    if (doc == null && exitCode != 0)
    {
        var msg = string.IsNullOrWhiteSpace(stderr) ? $"CLI exited with code {exitCode}" : stderr.Trim();
        return new AiImageGenerationResult(false, null, $"AI image generation failed: {msg}", req);
    }
    if (doc == null)
    {
        return new AiImageGenerationResult(false, null,
            "AI image generation failed: could not parse CLI JSON output.", req);
    }

    using (doc)
    {
        var root = doc.RootElement;
        bool ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        if (!ok)
        {
            string err = root.TryGetProperty("error", out var e) ? (e.GetString() ?? "") : "unknown error";
            return new AiImageGenerationResult(false, null, $"AI image generation failed: {err}", req);
        }

        // 5) 후처리 (Alpha ON 경로)
        if (req.Alpha)
        {
            string colorPath = finalOutputPath;
            string nobgPath = Path.Combine(
                Path.GetDirectoryName(finalOutputPath)!,
                Path.GetFileNameWithoutExtension(finalOutputPath) + "_nobg.png");

            try
            {
                if (File.Exists(colorPath)) File.Delete(colorPath);
                if (!File.Exists(nobgPath))
                {
                    return new AiImageGenerationResult(false, null,
                        $"AI image generation failed: expected {nobgPath} not found.", req);
                }
                File.Move(nobgPath, colorPath);
            }
            catch (Exception ex)
            {
                return new AiImageGenerationResult(false, null,
                    $"AI image generation failed during alpha post-process: {ex.Message}", req);
            }
        }

        if (!File.Exists(finalOutputPath))
        {
            return new AiImageGenerationResult(false, null,
                $"AI image generation failed: output file not found at {finalOutputPath}", req);
        }

        return new AiImageGenerationResult(true, finalOutputPath,
            $"AI image generated: {MakeRelative(finalOutputPath)}", req);
    }
}
```

#### 보조 함수들

```csharp
private static string AssembleFinalPrompt(string stylePrompt, string prompt, bool alpha)
{
    var parts = new List<string>();
    if (!string.IsNullOrWhiteSpace(stylePrompt)) parts.Add(stylePrompt.Trim());
    if (!string.IsNullOrWhiteSpace(prompt)) parts.Add(prompt.Trim());
    if (alpha) parts.Add("magenta background");
    return string.Join(", ", parts);
}

private static JsonDocument? TryParseLastJsonLine(string stdout)
{
    if (string.IsNullOrWhiteSpace(stdout)) return null;
    var lines = stdout.Split('\n');
    for (int i = lines.Length - 1; i >= 0; i--)
    {
        var line = lines[i].Trim();
        if (line.StartsWith("{") && line.EndsWith("}"))
        {
            try { return JsonDocument.Parse(line); } catch { }
        }
    }
    // fallback: 전체를 한 번 더 시도
    try { return JsonDocument.Parse(stdout); } catch { return null; }
}

public static string ResolveUniqueFileName(string folderAbsPath, string baseName)
{
    string candidate = baseName;
    int counter = 1;
    while (true)
    {
        string candPath = Path.Combine(folderAbsPath, candidate + ".png");
        bool existsOnDisk = File.Exists(candPath);
        bool inFlight;
        lock (_inFlightLock) inFlight = _inFlightPaths.Contains(Normalize(candPath));
        if (!existsOnDisk && !inFlight) return candidate;
        candidate = $"{baseName}_{counter}";
        counter++;
        if (counter > 9999) return candidate; // 방어적 탈출
    }
}

public static void DrainResults(Action<AiImageGenerationResult> onResult)
{
    while (_pending.TryDequeue(out var r))
        onResult(r);
}

private static string Normalize(string path) =>
    Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();

private static string MakeRelative(string abs)
{
    try
    {
        var rel = Path.GetRelativePath(ProjectContext.ProjectRoot, abs);
        return rel.Replace('\\', '/');
    }
    catch { return abs; }
}
```

#### 필요한 using

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IronRose.Engine;
using IronRose.Engine.Editor.ImGuiEditor; // EditorModal
using RoseEngine;                         // EditorDebug
```

---

### 2. `src/IronRose.Engine/Editor/ImGui/Panels/AiImageGenerateDialog.cs`

#### 파일 헤더

```csharp
// ------------------------------------------------------------
// @file    AiImageGenerateDialog.cs
// @brief   Asset Browser 컨텍스트 메뉴의 "Generate with AI (Texture)..." 클릭 시 열리는
//          ImGui 모달 팝업. 프롬프트/파일명/토글/히스토리를 받아 AiImageGenerationService에 Enqueue한다.
// @deps    IronRose.Engine.Editor/AiImageGenerationService,
//          IronRose.Engine.Editor/AiImageHistory, ImGuiNET
// ------------------------------------------------------------
```

#### 네임스페이스/클래스

네임스페이스: `IronRose.Engine.Editor.ImGuiEditor.Panels`.

```csharp
internal sealed class AiImageGenerateDialog
{
    private const string PopupId = "Generate with AI (Texture)##aiimg";

    private bool _wantOpen = false;
    private string _targetFolderAbs = "";

    // Form state
    private string _stylePrompt = "";
    private string _prompt = "";
    private string _fileName = "new_texture";
    private bool _refine = true;
    private bool _alpha = false;
    private int _selectedHistoryIndex = -1;

    public void Open(string targetFolderAbsPath)
    {
        _targetFolderAbs = Path.GetFullPath(targetFolderAbsPath);
        _stylePrompt = "";
        _prompt = "";
        _fileName = "new_texture";
        var toggles = AiImageHistory.LastToggles;
        _refine = toggles.Refine;
        _alpha = toggles.Alpha;
        _selectedHistoryIndex = -1;
        _wantOpen = true;
    }

    /// <summary>매 프레임 호출. Open()이 호출된 프레임에 팝업을 띄운다.</summary>
    public void Draw()
    {
        if (_wantOpen)
        {
            ImGui.OpenPopup(PopupId);
            ImGui.SetNextWindowSize(new Vector2(560, 520), ImGuiCond.Appearing);
            _wantOpen = false;
        }

        if (!ImGui.BeginPopupModal(PopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        // Target folder (read-only display)
        ImGui.TextDisabled("Folder: " + _targetFolderAbs);
        ImGui.Separator();

        // Style prompt (3 lines)
        ImGui.TextUnformatted("Style Prompt");
        ImGui.InputTextMultiline("##aiimg_style", ref _stylePrompt, 2048,
            new Vector2(-1, ImGui.GetTextLineHeight() * 3.5f));

        // Prompt (4 lines)
        ImGui.TextUnformatted("Prompt");
        ImGui.InputTextMultiline("##aiimg_prompt", ref _prompt, 4096,
            new Vector2(-1, ImGui.GetTextLineHeight() * 4.5f));

        // File name
        ImGui.TextUnformatted("File Name");
        ImGui.InputText("##aiimg_filename", ref _fileName, 256);

        // Resolved name preview
        string previewName = _fileName;
        string previewLabel;
        if (string.IsNullOrWhiteSpace(_fileName))
        {
            previewLabel = "(file name required)";
        }
        else
        {
            string resolved = AiImageGenerationService.ResolveUniqueFileName(_targetFolderAbs, _fileName.Trim());
            if (resolved == _fileName.Trim())
                previewLabel = $"-> {resolved}.png";
            else
                previewLabel = $"-> {_fileName.Trim()}.png exists, will save as {resolved}.png";
            previewName = resolved;
        }
        ImGui.TextDisabled(previewLabel);

        // Toggles
        ImGui.Checkbox("Refine Prompt with AI", ref _refine);
        ImGui.SameLine();
        ImGui.Checkbox("Alpha Channel", ref _alpha);

        // History
        ImGui.Separator();
        ImGui.TextUnformatted("History (click to copy into prompts)");
        var entries = AiImageHistory.Entries;
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("(empty)");
        }
        else
        {
            if (ImGui.BeginListBox("##aiimg_history", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 5.5f)))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    string preview = e.Prompt.Length > 40 ? e.Prompt.Substring(0, 40) + "..." : e.Prompt;
                    string label = string.IsNullOrEmpty(e.StylePrompt)
                        ? preview
                        : $"{e.StylePrompt} | {preview}";
                    bool selected = _selectedHistoryIndex == i;
                    if (ImGui.Selectable(label + $"##h{i}", selected))
                    {
                        _selectedHistoryIndex = i;
                        _stylePrompt = e.StylePrompt;
                        _prompt = e.Prompt;
                    }
                }
                ImGui.EndListBox();
            }
        }

        ImGui.Separator();

        // Buttons
        bool canGenerate = !string.IsNullOrWhiteSpace(_fileName) && !string.IsNullOrWhiteSpace(_prompt);
        if (!canGenerate)
            ImGui.TextDisabled("Prompt and File Name are required.");

        ImGui.BeginDisabled(!canGenerate);
        if (ImGui.Button("Generate", new Vector2(120, 0)))
        {
            string resolved = AiImageGenerationService.ResolveUniqueFileName(_targetFolderAbs, _fileName.Trim());
            var req = new AiImageGenerationRequest(
                TargetFolderAbsPath: _targetFolderAbs,
                ResolvedFileName: resolved,
                StylePrompt: _stylePrompt?.Trim() ?? "",
                Prompt: _prompt?.Trim() ?? "",
                Refine: _refine,
                Alpha: _alpha);

            if (AiImageGenerationService.Enqueue(req))
            {
                EditorModal.EnqueueAlert($"AI image generation started: {resolved}.png\n(You can continue working; a notification will appear when done.)");
            }
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
```

#### using

```csharp
using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using IronRose.Engine.Editor;
```

---

## 수정할 파일

### 3. `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`

**변경 내용 개요**:

1. 필드 2개 추가 (`_aiImageDialog`, `_openAiImageDialogForFolder`).
2. 빈 공간 컨텍스트 메뉴(`##AssetListContext`, line ~446~494)에 메뉴 항목 추가.
3. 폴더 트리 컨텍스트 메뉴(`##folderctx_`, line ~1092~1154)에 메뉴 항목 추가.
4. `Draw()` 진입부에서 `AiImageGenerationService.DrainResults(...)` 호출 + 지연 오픈 처리.
5. `Draw()` 말미에서 `_aiImageDialog.Draw()` 호출.

#### 3-1. 필드 추가

`ImGuiProjectPanel` 클래스 내, 기존 "Thumbnail generation request" 필드(line ~115) 근처 또는 private 필드 블록 하단에 추가:

```csharp
// AI image generation dialog + deferred-open trigger
private readonly AiImageGenerateDialog _aiImageDialog = new();
private string? _openAiImageDialogForFolder;
```

네임스페이스 `IronRose.Engine.Editor.ImGuiEditor.Panels` 에 이미 존재하므로 `AiImageGenerateDialog`는 동일 네임스페이스에서 참조 가능. 별도 using 불필요.

#### 3-2. 빈 공간 컨텍스트 메뉴에 항목 추가

line ~482 (`if (ImGui.MenuItem("Create Thumbnails"))`) **바로 앞**에 아래를 삽입:

```csharp
// AI image generation (only when pref toggle is on and a folder is selected)
if (EditorPreferences.EnableAiAssetGeneration && _selectedFolder != null)
{
    ImGui.Separator();
    if (ImGui.MenuItem("Generate with AI (Texture)..."))
    {
        _openAiImageDialogForFolder = Path.GetFullPath(_selectedFolder.FullPath);
    }
    ImGui.Separator();
}
```

#### 3-3. 폴더 트리 컨텍스트 메뉴에 항목 추가

line ~1139 (`ImGui.Separator();` 가 존재하고 그 다음이 `if (ImGui.MenuItem("Create Thumbnails"))`) **바로 앞**에 아래를 삽입:

```csharp
// AI image generation (only when pref toggle is on)
if (EditorPreferences.EnableAiAssetGeneration)
{
    if (ImGui.MenuItem("Generate with AI (Texture)..."))
    {
        _openAiImageDialogForFolder = Path.GetFullPath(node.FullPath);
    }
    ImGui.Separator();
}
```

#### 3-4. Draw() 진입부에 결과 드레인 + 지연 오픈

`Draw()` 메서드의 **가장 위** (기존 `if (!IsOpen) return;` 바로 다음, 또는 첫 ImGui 호출 직전)에 아래를 삽입. `DrainResults` 콜백 안에서 Reimport/History/Alert 세 동작을 순서대로 수행.

```csharp
// Drain background AI image generation results (UI thread only).
AiImageGenerationService.DrainResults(result =>
{
    if (result.Success && !string.IsNullOrEmpty(result.AbsoluteOutputPath))
    {
        var db = Resources.GetAssetDatabase() as AssetDatabase;
        db?.ReimportAsync(result.AbsoluteOutputPath);
        AiImageHistory.RecordSuccess(
            result.Request.StylePrompt,
            result.Request.Prompt,
            result.Request.Refine,
            result.Request.Alpha);
    }
    EditorModal.EnqueueAlert(result.Message);
});

// Deferred open of AI image dialog (set by context menu)
if (_openAiImageDialogForFolder != null)
{
    _aiImageDialog.Open(_openAiImageDialogForFolder);
    _openAiImageDialogForFolder = null;
}
```

> `Resources`/`AssetDatabase` 네임스페이스는 이미 `ImGuiProjectPanel.cs` 상단 using(`IronRose.AssetPipeline`, `IronRose.Engine.Editor` 등)에 포함되어 있다 (기존 `DrawAssetContextMenu`가 이미 사용 중).

#### 3-5. Draw() 말미에 다이얼로그 렌더

`Draw()` 메서드의 마지막 `ImGui.End();` **바로 앞**에 아래를 삽입 (또는 해당 패널 Begin/End 바깥이 있는 경우 "현재 패널과 독립적으로 팝업은 글로벌이므로 `End()` 이후에 호출해도 무방". 관례상 `End()` 직전에 둔다):

```csharp
_aiImageDialog.Draw();
```

> 주의: ImGui 팝업은 OpenPopup/BeginPopupModal 쌍이 매 프레임 유지되어야 하므로, `Draw()` 호출이 `IsOpen == false`로 얼리 리턴되는 경우에도 다이얼로그는 열리지 않아야 한다. 이미 `_wantOpen`이 false면 `Open()`을 호출한 적 없으므로 조용히 지나가므로 문제없음. 단, 팝업이 떠 있는 동안 `IsOpen == false`가 되면 팝업이 사라진다 — 1차 범위에서는 이 케이스를 신경 쓰지 않는다 (Preferences Panel과 동일 관례).

---

## NuGet 패키지

없음. 모두 .NET BCL (`System.Text.Json`, `System.Diagnostics.Process`, `System.Collections.Concurrent`).

---

## CLI 커맨드 조립 규칙 요약

다이얼로그에서 사용자가 입력한 값 + Preferences를 합쳐 아래 순서의 인자 배열을 만든다. `ProcessStartInfo.ArgumentList`에 순서대로 `Add`:

1. `<script path>` — `Path.Combine(ProjectContext.EngineRoot, "tools/invoke-comfyui/cli-invoke-comfyui.py")`
2. `<finalPrompt>` — 위치 인자. `AssembleFinalPrompt(style, prompt, alpha)` 결과.
3. `-o` / `<finalOutputPath>` — 확정된 파일명 기반 절대 경로.
4. `--bypass-refine` — Refine == false일 때만.
5. `--rmbg` — Alpha == true일 때만.
6. `--server` / `<AiAlienhsServerUrl>` — 항상.
7. `--endpoint` / `<AiRefineEndpoint>` — 비어있지 않을 때만.
8. `--model` / `<AiRefineModel>` — 비어있지 않을 때만.
9. `--json` — 항상.

**이스케이프**: `ArgumentList`를 사용하므로 별도 quoting 불필요. 공백·따옴표·한글 프롬프트 모두 안전.

---

## 프롬프트 조립 규칙

- 입력 `stylePrompt`, `prompt`는 `.Trim()` 후 빈 부분은 생략.
- Alpha == true 일 때 끝에 `"magenta background"` 추가.
- 최종: `", "`(콤마 + 공백)으로 join.
- 예:
  - style="cinematic", prompt="a dragon", alpha=false → `"cinematic, a dragon"`
  - style="", prompt="crate", alpha=true → `"crate, magenta background"`
  - style=" ", prompt="x", alpha=false → `"x"` (style 비어있다고 판정)

---

## 파일명 suffix 충돌 회피 알고리즘

`AiImageGenerationService.ResolveUniqueFileName(folderAbsPath, baseName)`:

1. `candidate = baseName` (확장자 없음, 여기서 baseName은 사용자가 입력한 `_fileName.Trim()`).
2. `candPath = folder/candidate.png`.
3. 디스크 존재 체크 + in-flight 집합 체크(케이스-insensitive 정규화).
4. 둘 다 없으면 `candidate` 반환.
5. 있으면 `candidate = $"{baseName}_{counter}"`, `counter++`.
6. 9999까지 시도. 초과 시 마지막 candidate 그대로 반환 (방어적).

**2중 체크**:
- 다이얼로그 `Draw()`는 매 프레임 `ResolveUniqueFileName` 호출해 프리뷰 갱신.
- Generate 버튼 클릭 순간에도 다시 호출 → 찰나의 경쟁 상태 최소화.
- `Enqueue` 직후 in-flight 집합에 등록되므로 이어지는 Generate 클릭은 다른 번호를 얻는다.

---

## Alpha 경로 후처리 (성공 시)

순서:

1. CLI 성공 완료 후 `colorPath = finalOutputPath`, `nobgPath = <stem>_nobg.png` 결정 (stem은 확장자 제외 finalOutputPath).
2. `File.Delete(colorPath)` — 존재하면 삭제.
3. `File.Exists(nobgPath)` 확인. 없으면 실패 반환.
4. `File.Move(nobgPath, colorPath)` — 덮어쓰기 대상은 방금 지운 자리.
5. 최종 `File.Exists(finalOutputPath)` 검증.

**실패 시 롤백**: 하지 않는다. 부분적 파일 잔존은 허용. 실패 메시지만 유저에게 전달 (알림).

---

## 스레딩 모델

- `Task.Run`으로 단발 백그라운드 실행. `Interlocked.Increment/Decrement`로 `_runningCount` 관리.
- `_inFlightPaths`는 `lock(_inFlightLock)`으로 보호.
- 완료 결과는 `ConcurrentQueue<AiImageGenerationResult>`로 UI 스레드에 전달.
- UI 스레드 드레인 지점: **`ImGuiProjectPanel.Draw()` 진입부**. `AssetDatabase.ReimportAsync` · `AiImageHistory.RecordSuccess` · `EditorModal.EnqueueAlert`는 모두 UI 스레드에서 호출.
- 취소 지원 없음. 동시 실행 허용 (다른 파일명).

---

## 에러 케이스 → 유저 알림 메시지

| 케이스 | 메시지 |
|---|---|
| 같은 경로가 이미 실행 중 | `AI image generation already in progress for:\n<path>` |
| CLI 스크립트 경로 없음 | `CLI script not found: <path>` |
| `Process.Start` 실패 | `Failed to start python process.` |
| CLI stdout에 JSON 없음 + exit != 0 | `AI image generation failed: <stderr or exit code>` |
| CLI JSON 파싱 실패 | `AI image generation failed: could not parse CLI JSON output.` |
| `ok: false` + `error` 문자열 | `AI image generation failed: <error>` |
| Alpha 후처리 중 파일 missing | `AI image generation failed: expected <nobgPath> not found.` |
| Alpha 후처리 IO 예외 | `AI image generation failed during alpha post-process: <msg>` |
| 최종 파일 없음 | `AI image generation failed: output file not found at <path>` |
| 성공 | `AI image generated: <relative path>` |
| Enqueue 성공 직후 (다이얼로그 닫음) | `AI image generation started: <name>.png\n(You can continue working; a notification will appear when done.)` |

모두 `EditorModal.EnqueueAlert`로 출력.

---

## Asset Browser 통합 지점 정확한 위치

| 지점 | 파일/라인 | 삽입 위치 |
|---|---|---|
| 빈 공간 컨텍스트 메뉴 | `ImGuiProjectPanel.cs` line ~482 | `if (ImGui.MenuItem("Create Thumbnails"))` **바로 앞**. Separator + MenuItem + Separator. 게이트: `EditorPreferences.EnableAiAssetGeneration && _selectedFolder != null`. 대상 경로: `_selectedFolder.FullPath`. |
| 폴더 트리 컨텍스트 메뉴 | `ImGuiProjectPanel.cs` line ~1140 | 기존 `ImGui.Separator();` 다음, `if (ImGui.MenuItem("Create Thumbnails"))` **바로 앞**. 게이트: `EditorPreferences.EnableAiAssetGeneration`. 대상 경로: `node.FullPath`. |
| 에셋 자체 메뉴 | `DrawAssetContextMenu` | **건드리지 않음**. |

---

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] Asset Browser 빈 공간 우클릭 → `Generate with AI (Texture)...` 보임 (Enable 토글 ON일 때).
- [ ] 폴더 트리 우클릭 → 동일 메뉴 보임.
- [ ] Preferences에서 Enable 토글을 끄면 두 메뉴 모두 사라진다.
- [ ] 메뉴 클릭 → 다이얼로그 오픈. Target folder 라벨이 올바른 절대 경로 표시.
- [ ] Prompt/FileName 비어있으면 Generate 버튼이 비활성 + 경고 문구.
- [ ] 같은 이름의 PNG가 폴더에 이미 존재하면 프리뷰에 `-> ..._1.png` 형태로 확정 이름 표시.
- [ ] Generate 클릭 → 다이얼로그 닫힘 → "started" 알림 + 에디터가 블로킹되지 않음.
- [ ] CLI 성공 (실 서버 기동 상태) → 토스트 알림 "AI image generated: Assets/..." + Asset Browser에 PNG가 나타남 (Reimport 완료 후).
- [ ] Alpha ON으로 생성 → `<name>_nobg.png`가 삭제되고 `<name>.png`만 최종 남는다 (RGBA 투명 배경).
- [ ] 서버 꺼진 상태에서 Generate → `Failed: ...` 알림, 에디터 크래시 없음.
- [ ] 연속으로 두 번 Generate(서로 다른 이름) → 병렬 실행, 둘 다 알림.
- [ ] 동일 이름 충돌 상황 인위적으로 만들어 Enqueue 재시도 → "already in progress" 알림.
- [ ] 생성 성공 후 다이얼로그 재오픈 → History 리스트에 방금 엔트리가 index 0으로 표시, Refine/Alpha 토글이 마지막 사용값으로 복원.
- [ ] `<ProjectRoot>/memory/ai_image_history.json` 파일이 갱신됨.

---

## 참고

- 설계 문서의 "2. Asset Browser 컨텍스트 메뉴 통합", "3. 신규 모달 다이얼로그", "4. 생성 파이프라인 서비스", "7. 백그라운드 실행 모델 요약"을 그대로 구현한다.
- `cli-invoke-comfyui.py --json` 출력 스키마: `{"ok": bool, "paths": [...], "server_filenames": [...], "refined_prompt": str, "prompt_id": str, "nobg_paths": [...]}` — ok/paths/nobg_paths/error만 참조.
- Alpha 경로에서 CLI는 `--rmbg` 옵션 시 `<stem>.png`(원본)와 `<stem>_nobg.png`(배경 제거) 둘 다 저장한다. 사용자가 기대하는 최종 산출물은 **알파 채널이 있는 하나의 PNG**이므로 후처리로 원본을 덮어쓴다.
- `HttpClient`를 사용한 Health Check는 이미 Phase A에서 구현됨. Phase C는 이를 건드리지 않는다.
- `ALIENHS_SERVER` 환경변수는 CLI가 자체 fallback으로 사용. Preferences `AiAlienhsServerUrl`이 항상 `--server`로 명시 전달되므로 Preferences가 우선이다.
- `making_log/_system-ai-image-generation.md` 작성은 구현 완료 후 별도 커밋에서. 본 Phase 범위는 **코드 구현까지**.

---

## 체크리스트 (순서대로)

구현자(coder)가 하나씩 체크:

- [ ] `AiImageGenerationService.cs` 신규 작성 (record 2종 + 정적 클래스 + RunPipeline + 보조 함수 + using)
- [ ] `AiImageGenerateDialog.cs` 신규 작성 (Open/Draw + 필드 + using)
- [ ] `ImGuiProjectPanel.cs`에 필드 2개 추가
- [ ] 빈 공간 컨텍스트 메뉴에 Generate 메뉴 추가 (line ~482 앞)
- [ ] 폴더 트리 컨텍스트 메뉴에 Generate 메뉴 추가 (line ~1140 앞)
- [ ] `Draw()` 진입부에 `AiImageGenerationService.DrainResults` + 지연 오픈 로직 삽입
- [ ] `Draw()` 말미에 `_aiImageDialog.Draw()` 호출 삽입
- [ ] `dotnet build` 성공
- [ ] (수동 QA) Enable ON/OFF 토글 반영 확인
- [ ] (수동 QA) 다이얼로그 필드 동작 확인 (prompt 비우기 → 버튼 비활성, 기존 파일명 입력 → 프리뷰 _1 suffix)
- [ ] (수동 QA) 서버 on 상태 end-to-end 성공 + Reimport + 알림 + 히스토리 기록
- [ ] (수동 QA) 서버 off 상태 실패 알림 (크래시 없음)
- [ ] (수동 QA) Alpha ON 성공 시 `_nobg` 리네임/삭제 확인
- [ ] (수동 QA) 동일 이름 동시 Enqueue → 거부 알림
- [ ] (수동 QA) 히스토리 파일 JSON 스키마 확인
