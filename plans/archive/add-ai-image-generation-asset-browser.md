# Asset Browser AI Image Generation (Texture)

## 배경

IronRose는 이미 AI 이미지 생성을 위한 CLI 파이프라인을 갖고 있다.

- **CLI 직접 호출**: `tools/invoke-comfyui/cli-invoke-comfyui.py` — Python stdlib만 쓰는 독립 스크립트로 AlienHS `invoke-comfyui`를 호출한다. `--bypass-refine`, `--rmbg`, `--server`, `--model`, `--endpoint`, `--json` 등 본 설계에 필요한 옵션이 전부 이미 구현돼 있으며 프롬프트 정제(`--refine` 기본 ON)도 CLI가 직접 수행한다 (`tools/invoke-comfyui/cli-invoke-comfyui-README.md` 참조).

현재 에디터에는 이 CLI 경로를 에셋 생산 흐름에 연결해 주는 UI가 없다. 사용자는 CLI를 외부 셸에서 돌린 뒤 수동으로 `Assets/` 하위에 파일을 배치하고 Asset Browser에서 Reimport를 해야 한다. Asset Browser의 컨텍스트 메뉴에 "Generate with AI" 항목을 도입해 **Texture 에셋 생성 루프**를 에디터 안으로 묶는다.

관련 기반은 이미 갖춰져 있다.

- **Preferences 시스템** (`src/IronRose.Engine/EditorPreferences.cs`): `~/.ironrose/settings.toml`의 `[preferences]` 섹션을 read-modify-write하며, 새 preference 속성 추가 가이드가 파일 상단 주석에 명시돼 있다. UI는 `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs`의 `CollapsingHeader` 섹션 구조를 따른다.
- **Asset Browser 컨텍스트 메뉴**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs`
  - 빈 공간 우클릭 메뉴: line 446의 `BeginPopupContextWindow("##AssetListContext", ...)` 블록 — `Create`, `Create Thumbnails`, `Open Containing Folder` 등이 있다.
  - 폴더 우클릭 메뉴: line 1092의 `BeginPopupContextItem($"##folderctx_{node.FullPath}")`.
  - 에셋 우클릭 메뉴: line 782의 `DrawAssetContextMenu(AssetEntry asset)`.
  - Texture 판정: line 2271의 `"texture" or "texture2d" => ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".exr"` 패턴 재사용 가능.
  - 선택 폴더 경로: `_selectedFolder.FullPath`.
- **모달 헬퍼**: `src/IronRose.Engine/Editor/ImGui/EditorModal.cs`의 `InputTextPopup`, `EnqueueAlert`, `DrawAlertPopups`. 현재는 단일 텍스트 입력 모달뿐이므로 다중 입력 다이얼로그는 신규 패널/팝업이 필요하다.
- **프로젝트 루트**: `IronRose.Engine.ProjectContext.ProjectRoot` — 에디터가 연 게임 프로젝트 루트 경로. 히스토리/상태 파일의 기준점.
- **에셋 재임포트 트리거**: `AssetDatabase.ReimportAsync(path)` — 기존 `DrawAssetContextMenu`의 Reimport 메뉴가 사용하는 API.

## 목표

1. Asset Browser 우클릭 메뉴에서 **Texture PNG를 AI로 생성**해 현재 폴더에 바로 떨어뜨린다.
2. Preferences에 전용 토글(`AI Asset Generation`)을 추가해 기능 전체를 on/off 할 수 있고, 서버 URL·Python 경로·Refine 모델/엔드포인트를 사용자 단위로 기억한다. 이 토글은 "메뉴/다이얼로그 노출 여부"만 제어한다.
3. 생성은 **CLI를 `Process.Start`로 백그라운드 호출**한다.
4. 생성 중에도 에디터가 블로킹되지 않는다. 완료/실패 시 토스트성 알림으로 사용자에게 알린다.
5. 마지막 토글(`refine`, `alpha`)과 최근 프롬프트 5건을 게임 프로젝트의 `memory/ai_image_history.json`에 영속화해 다음 다이얼로그 오픈 시 재사용한다.

## 현재 상태 요약

| 영역 | 현재 | 비고 |
|------|------|------|
| Preferences 저장/UI | 구현 완료 | `EditorPreferences` + `ImGuiPreferencesPanel`에 섹션 추가로 확장 |
| Asset Browser 컨텍스트 메뉴 | `ImGuiProjectPanel.cs`에 3곳 (빈 공간/폴더/에셋) | 빈 공간·폴더 메뉴에 "Generate with AI (Texture)" 항목 추가 |
| CLI | `cli-invoke-comfyui.py` 완비 (`--json`, `--bypass-refine`, `--rmbg`, `--server`, `--endpoint`, `--model`) | 파이썬 스크립트 그대로 호출, 파싱은 `--json` stdout |
| 히스토리/상태 | 없음 | 신규. `<ProjectRoot>/memory/ai_image_history.json`에 단일 파일 |
| 토스트 알림 | 전용 시스템 없음. `EditorModal.EnqueueAlert`가 가장 근접 | 1차 범위: `EnqueueAlert`로 시작, 향후 transient toast로 개선 가능 |

## 설계

### 개요

- Preferences에 **`AI Asset Generation`** 섹션을 추가하고 최상위 토글 + 서브 옵션 묶음을 둔다. 이 토글은 "메뉴/다이얼로그 노출 여부"만 제어한다.
- Asset Browser의 빈 공간 컨텍스트 메뉴(현재 폴더 대상)와 폴더 컨텍스트 메뉴(특정 폴더 대상) 두 곳에서 `Generate with AI (Texture)...` 항목을 노출한다. 에셋 자체 메뉴에는 넣지 않는다 (1차 범위는 "새 Texture 만들기"에 한정).
- 클릭 시 신규 **`AiImageGenerateDialog`**(ImGui 모달 팝업)를 연다. 히스토리·마지막 토글을 주입받아 폼을 구성한다.
- `Generate` 버튼 누르면 신규 **`AiImageGenerationService`**(정적 서비스)가 백그라운드에서 CLI를 호출한다. 서비스는 취소되지 않는 단발 Task이며 `Interlocked` 기반의 running-count로 전체 실행 중 여부를 노출한다.
- 성공 시 `AssetDatabase.ReimportAsync` + `AiImageHistory.RecordSuccess(...)` + 알림. 실패 시 히스토리 기록 없이 알림만.
- 히스토리/마지막 토글은 **`AiImageHistory`** 정적 저장소가 `<ProjectRoot>/memory/ai_image_history.json`으로 읽고 쓴다.

### 상세 설계

#### 1. Preferences 확장

**파일**: `src/IronRose.Engine/EditorPreferences.cs` (수정), `ImGuiPreferencesPanel.cs` (수정)

새 정적 속성:

- `EnableAiAssetGeneration: bool` (기본 **true**)
- `AiAlienhsServerUrl: string` (기본 `"http://localhost:25000"`)
- `AiPythonPath: string` (기본 `"python"`)
- `AiRefineEndpoint: string` (기본 `""` — 비우면 CLI 기본값 사용)
- `AiRefineModel: string` (기본 `""` — 비우면 CLI 기본값 사용)

TOML 레이아웃 (기존 `[preferences]` 섹션에 병합):

```toml
[preferences]
# 기존 preference 키들 ...
enable_ai_asset_generation = true
ai_alienhs_server_url = "http://localhost:25000"
ai_python_path = "python"
ai_refine_endpoint = ""
ai_refine_model = ""
```

`Load()` / `Save()`는 기존 패턴 그대로 키를 추가. `EditorPreferences.cs` 상단 주석의 "새 preference 항목 추가 가이드"를 따른다.

**UI (`ImGuiPreferencesPanel.cs`)**: 새 `CollapsingHeader("AI Asset Generation")` 섹션.

- 최상위 `Checkbox("Enable AI Asset Generation")` — 이 토글이 꺼지면 아래 서브 위젯은 `ImGui.BeginDisabled()`로 래핑한다.
- `InputText("AlienHS Server URL")`
- `Button("Health Check")` — 상태 표시(`OK`/`Failed: ...`)를 옆 라벨에 캐시. 내부 구현은 `System.Net.Http.HttpClient`로 **`GET <AiAlienhsServerUrl>/`** 를 호출해 HTTP 200이면 `OK`, 그 외 상태 코드/예외/타임아웃이면 `Failed: <reason>` 표시. 타임아웃은 3초(`HttpClient.Timeout = TimeSpan.FromSeconds(3)`). 기준은 `/home/alienspy/git/alienhs/healthcheck.sh`가 루트 경로의 200을 OK로 판정하는 것과 동일.
- `InputText("Python Path")`
- `InputText("Refine Endpoint")`
- `InputText("Refine Model")`

값 변경 시 기존 위젯들처럼 즉시 `EditorPreferences.Save()`.

#### 2. Asset Browser 컨텍스트 메뉴 통합

**파일**: `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs` (수정)

추가 위치:

- 빈 공간 메뉴 (`##AssetListContext`, line ~446): `Create Thumbnails` 앞 또는 `Open Containing Folder` 위에 구분선 + `"Generate with AI (Texture)..."` 항목. `_selectedFolder.FullPath`를 대상 폴더로 전달.
- 폴더 트리 메뉴 (`##folderctx_...`, line ~1092): 같은 항목 추가. 대상은 해당 `FolderNode.FullPath`.

가시성 규칙:

```
if (EditorPreferences.EnableAiAssetGeneration) {
    if (ImGui.MenuItem("Generate with AI (Texture)...")) _openAiImageDialogForFolder = targetFolderPath;
}
```

패널 필드: `_openAiImageDialogForFolder: string?`, `_aiImageDialog: AiImageGenerateDialog`. Draw 루프에서 `_openAiImageDialogForFolder != null`이면 다이얼로그를 열고 필드를 null로 리셋.

에셋 자체 우클릭 메뉴(`DrawAssetContextMenu`)에는 이번 범위에서 아무것도 추가하지 않는다.

#### 3. 신규 모달 다이얼로그

**파일 (신규)**: `src/IronRose.Engine/Editor/ImGui/Panels/AiImageGenerateDialog.cs`

`IEditorPanel`을 굳이 구현하지 않고, `ImGuiProjectPanel`이 소유하는 보조 위젯 클래스로 둔다. 이유는 "프로젝트 패널의 우클릭 흐름에 종속된 임시 팝업"이기 때문이며, 도구창처럼 독립 토글이 필요하지 않다.

API (시그니처 세부는 아키텍트 단계에서 확정):

- `Open(string targetFolderAbsPath)`
- `Draw()` — `ImGui.OpenPopup` + `BeginPopupModal("Generate with AI (Texture)##aiimg")`로 렌더
- 내부 상태: `stylePrompt, prompt, fileName, refine, alpha, selectedHistoryIndex, isBusy`

폼 필드:

| 필드 | 위젯 | 기본값 |
|------|------|--------|
| Style Prompt | multi-line InputTextMultiline (3줄) | 빈 문자열 |
| Prompt | multi-line InputTextMultiline (4줄) | 빈 문자열 |
| File Name | InputText (256) + 바로 아래 실제 저장 경로 프리뷰 | `new_texture` |
| Refine Prompt with AI | Checkbox | `AiImageHistory.LastToggles.Refine` |
| Alpha Channel | Checkbox | `AiImageHistory.LastToggles.Alpha` |
| History | `ImGui.BeginListBox`로 최대 5개 엔트리 표시 — 각 엔트리는 `"<style> \| <prompt 앞 40자>"`. 선택 시 `stylePrompt`/`prompt` 입력 필드에만 복사 | 로드된 히스토리 |
| Generate / Cancel | 버튼 | - |

동작:

- `Generate` 클릭 시 `AiImageGenerationService.Enqueue(...)` 호출 후 다이얼로그 닫음.
- 빈 `fileName` 또는 빈 `prompt`면 inline 경고 + 버튼 비활성.
- 저장 경로 프리뷰: `<targetFolder>/<fileName>.png`. **덮어쓰지 않음**. 파일이 이미 존재하면 `<fileName>_1.png`, `<fileName>_2.png` … 로 suffix를 1부터 증가시켜 최초로 존재하지 않는 이름을 **실제 사용 이름**으로 확정한다. 다이얼로그 프리뷰에도 확정된 실제 이름을 함께 보여준다(예: `"new_texture.png → new_texture_2.png (existing)"`). 이 "해결된 파일명"이 Generate 요청의 `fileName`으로 서비스에 전달된다.

#### 4. 생성 파이프라인 서비스

**파일 (신규)**: `src/IronRose.Engine/Editor/AiImageGenerationService.cs` (정적 클래스)

책임:

1. 다이얼로그 입력과 Preferences 값을 합쳐 "작업 요청"을 만든다.
2. CLI를 백그라운드로 호출한다.
3. `Task.Run`으로 백그라운드 실행. `Interlocked.Increment`로 실행 중 카운트 증가/감소.
4. 완료 시 성공/실패를 UI 스레드에 전달 — `ConcurrentQueue<AiImageResult>`에 넣고, `ImGuiProjectPanel.Draw()`(또는 Overlay Update)가 매 프레임 큐를 드레인해 `EditorModal.EnqueueAlert` + `AssetDatabase.ReimportAsync` + `AiImageHistory.RecordSuccess`를 호출한다. **파일 I/O·리임포트는 반드시 UI 스레드에서** 실행 (기존 Reimport 호출부가 그러함).

##### 4-1. 프롬프트 조립

- Alpha OFF: `finalPrompt = f"{stylePrompt}, {prompt}"` (앞뒤 trim, 빈 부분 생략)
- Alpha ON: `finalPrompt = f"{stylePrompt}, {prompt}, magenta background"` (영문 상수; 키 구분자 콤마)

##### 4-2. CLI 호출

`<fileName>`은 Phase C의 suffix-충돌 회피 로직으로 이미 확정된 최종 파일명(`new_texture`, `new_texture_1`, `new_texture_2` 등)이다. CLI는 이 확정된 이름 그대로 `-o`에 전달받으며, 서비스 측에서 추가 리네임은 하지 않는다.

커맨드 조립:

```
<AiPythonPath> <IronRoseRoot>/tools/invoke-comfyui/cli-invoke-comfyui.py \
  "<finalPrompt>" \
  -o "<targetFolder>/<fileName>.png" \
  [--bypass-refine]          # refine == false 일 때
  [--rmbg]                   # alpha == true 일 때
  --server <AiAlienhsServerUrl> \
  [--endpoint <AiRefineEndpoint>]  # 비어있지 않을 때만
  [--model <AiRefineModel>]        # 비어있지 않을 때만
  --json
```

- `IronRoseRoot`: `ProjectContext.EngineRoot` (엔진 레포 루트). 현재 `ProjectContext.cs`에 노출돼 있음.
- `Process.Start(ProcessStartInfo)` + `WorkingDirectory = <targetFolder>` (절대 경로 -o를 쓰므로 무관하지만 일관성).
- stdout 전체를 읽고 마지막 라인(혹은 첫 JSON 라인)을 `System.Text.Json.JsonDocument`로 파싱:
  - `{"ok": true, "paths": ["..."], "nobg_paths": [...] }`
  - `ok == false`면 `error` 문자열을 실패 메시지로 사용.
- exit code != 0 + 빈 stdout이면 stderr를 메시지로 사용.

후처리 (성공 시):

- Alpha OFF: `paths[0]`만 확인하고 종료. 확정된 `<fileName>.png`가 그대로 생성됐을 것이므로 추가 리네임 없음.
- Alpha ON: CLI는 확정된 `<fileName>.png`(원본 컬러)와 `<fileName>_nobg.png`(배경제거)를 둘 다 저장한다.
  - 원본 컬러 `<targetFolder>/<fileName>.png`를 **삭제** (`File.Delete`).
  - `<targetFolder>/<fileName>_nobg.png` → `<targetFolder>/<fileName>.png`로 **리네임** (`File.Move`). 이 단계의 덮어쓰기 대상은 방금 삭제한 자기 자신뿐이므로 정상.
- 둘 다 확정된 `<fileName>.png` 파일이 최종 결과물. 이 리네임/삭제 단계는 Phase C의 suffix 로직으로 선택된 이름을 기준으로 동작한다.

##### 4-3. 공통 후처리

- 성공 시 순서: `AssetDatabase.ReimportAsync(finalPath)` → `AiImageHistory.RecordSuccess(stylePrompt, prompt, refine, alpha)` → `EditorModal.EnqueueAlert($"AI image generated: {relativePath}")`.
- 실패 시: `EditorModal.EnqueueAlert($"AI image generation failed: {message}")`. 히스토리/토글 변경 없음.

#### 5. 히스토리/상태 저장소

**파일 (신규)**: `src/IronRose.Engine/Editor/AiImageHistory.cs` (정적 클래스)

- 경로: `Path.Combine(ProjectContext.ProjectRoot, "memory", "ai_image_history.json")`. 프로젝트가 열리지 않았으면 no-op.
- JSON 스키마:

  ```json
  {
    "history": [
      {"style_prompt": "...", "prompt": "..."}
    ],
    "last_toggles": { "refine": true, "alpha": false }
  }
  ```

- API:
  - `static void Load()` — 프로젝트 로드 훅에서 1회 호출.
  - `static IReadOnlyList<HistoryEntry> Entries { get; }`
  - `static (bool Refine, bool Alpha) LastToggles { get; }` (기본 `refine=true, alpha=false`)
  - `static void RecordSuccess(string stylePrompt, string prompt, bool refine, bool alpha)` — 히스토리 FIFO 5개 유지 + 마지막 토글 덮어쓰기 + 파일 저장. 중복 엔트리는 앞으로 끌어올림(LRU). 빈 prompt는 기록하지 않음.
- 저장은 `System.Text.Json.JsonSerializer`를 그대로 사용. `memory/` 폴더가 없으면 생성.

#### 6. 초기화 흐름 (기존 지점에 훅)

- `EditorPreferences.Load()` 호출은 이미 에디터 부팅 경로에 있음. 새 preference 속성들은 자동으로 읽힌다.
- `AiImageHistory.Load()`는 **프로젝트가 열린 직후** 호출. 현재 `ProjectContext.Initialize` 또는 `ImGuiProjectPanel`의 프로젝트 로드 후처리 지점 둘 다 후보 — 아키텍트 단계에서 하나 선택.
- `ImGuiPreferencesPanel`의 새 섹션은 기존 `CollapsingHeader` 추가와 동일.
- `ImGuiProjectPanel`에 `_aiImageDialog` 필드 + `Draw()` 말미에 `_aiImageDialog.Draw()` 호출.

#### 7. 백그라운드 실행 모델 요약

- 단일 구성: `Task.Run(() => RunPipeline(request))`.
- 동시 실행 허용 (1차 범위): 사용자가 여러 번 눌러도 서로 다른 이름이면 병렬 가능. 동일 파일명 충돌은 Enqueue 시점에 거부한다(같은 `targetFolder/fileName.png`가 이미 실행 중이면 `EnqueueAlert` + 거부).
- 취소 지원은 **1차 범위 외**.
- UI 결과 디스패치: `ConcurrentQueue<AiImageResult>`를 `AiImageGenerationService.PendingResults`로 노출. `ImGuiOverlay.Update()` 혹은 `ImGuiProjectPanel.Draw()` 진입에 `DrainResults()`를 호출해 UI 스레드에서만 `Reimport`·`EnqueueAlert`·`RecordSuccess`를 실행.

#### 8. 영향 범위

**수정**

- `src/IronRose.Engine/EditorPreferences.cs` — 속성 5개, Load/Save 키 추가.
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiPreferencesPanel.cs` — `CollapsingHeader("AI Asset Generation")` 추가, Health Check 버튼.
- `src/IronRose.Engine/Editor/ImGui/Panels/ImGuiProjectPanel.cs` — 두 컨텍스트 메뉴에 메뉴 항목 추가, `_aiImageDialog` 소유, 결과 큐 드레인.

**신규**

- `src/IronRose.Engine/Editor/ImGui/Panels/AiImageGenerateDialog.cs`
- `src/IronRose.Engine/Editor/AiImageGenerationService.cs`
- `src/IronRose.Engine/Editor/AiImageHistory.cs`
- `making_log/_system-ai-image-generation.md` — 구현 완료 후.

**기존 기능 영향**

- Preferences 파일(`~/.ironrose/settings.toml`)에 키 5개 추가 — 기존 키를 건드리지 않으므로 안전.
- 게임 프로젝트의 `memory/` 폴더에 `ai_image_history.json` 신규 파일.

### Phase 분해 (아키텍트에 넘길 초안)

| Phase | 범위 | 의존 |
|-------|------|------|
| **A. Preferences 확장** | 속성 5개 추가, TOML Load/Save, `ImGuiPreferencesPanel`에 섹션 + Health Check 버튼 (동기 `GET <server>/`, 3초 타임아웃, 200 OK 기준, 결과 라벨 캐시) | 기존 EditorPreferences만 있으면 됨 |
| **B. 히스토리 저장소** | `AiImageHistory` 정적 클래스, JSON I/O, 프로젝트 로드 훅 연결 | `ProjectContext.ProjectRoot` |
| **C. CLI 파이프라인 + 다이얼로그 + 컨텍스트 메뉴** | `AiImageGenerationService` CLI 호출, `AiImageGenerateDialog`, `ImGuiProjectPanel` 통합, 결과 큐 드레인, Reimport, EnqueueAlert. **파일명 충돌 회피**: `<fileName>.png` 이미 존재 시 `_1`, `_2`, … suffix 부여 로직을 다이얼로그(Generate 클릭 시점)와 서비스(안전망) 양쪽에 배치. 최종 확정 이름이 CLI `-o`로 전달되며, Alpha ON 경로의 원본 삭제/`_nobg` 리네임도 이 확정 이름 기준 | A, B |

각 Phase는 빌드·수동 QA가 가능한 단위로 끊는다. A는 독립 가능, B도 독립 가능, C가 실제 기능 완성.

## 대안 검토

### 에셋 자체 우클릭 메뉴에 "Regenerate with AI" 넣기

- 장점: 재생성 루프가 짧아짐.
- 단점: 사용자 요청은 "새 Texture 생성"에 집중. 재생성은 원본 프롬프트 보존이 전제되어 정의가 더 커진다. 1차 범위 밖.

### 전용 AI 패널(도킹 가능한 창)

- 장점: 생성 이력, 여러 잡 진행 상황을 한눈에 볼 수 있음.
- 단점: 1차 요구는 "컨텍스트 메뉴 + 다이얼로그"에 한정. 오버엔지니어링. 후속 작업으로 유보.

### CLI 대신 `invoke-comfyui` 서버를 C#에서 직접 HTTP 호출

- 장점: Python 의존 제거.
- 단점: `cli-invoke-comfyui.py`가 이미 업로드/리파인/RMBG 전체 파이프를 캡슐화하고 있고 stdlib만 쓴다. 중복 구현은 유지보수 부담만 늘린다. 채택하지 않음.

### 히스토리 저장 위치를 Preferences TOML에 흡수

- 장점: 파일 개수 감소.
- 단점: Preferences는 "사용자 전역"이고 히스토리는 "프로젝트별". 경계 위반. 채택하지 않음. 프로젝트별 `memory/` 파일이 적절.

## 리스크 및 확정 사항

1. **Health Check 엔드포인트** — *확정*. `/home/alienspy/git/alienhs/healthcheck.sh`가 `GET <server>/`에 대한 HTTP 200을 OK 기준으로 판정한다. Preferences의 Health Check 버튼도 동일하게 동기 `HttpClient.GetAsync("<AiAlienhsServerUrl>/").Result`(또는 `.GetAwaiter().GetResult()`)로 호출해 status 200이면 OK, 그 외/예외/타임아웃이면 FAIL로 라벨에 표시한다. 타임아웃은 3초.
2. **동일 파일명 충돌 정책** — *확정*. **덮어쓰지 않는다.** `<fileName>.png`가 이미 있으면 `<fileName>_1.png`, `<fileName>_2.png` … 로 suffix를 1부터 증가시켜 첫 번째로 존재하지 않는 이름을 찾는다. 이 확정된 이름을 CLI `-o` 경로에 전달한다. Alpha ON 경로의 원본 삭제 + `_nobg` 리네임 단계도 이 확정된 이름(`<fileName>`, `<fileName>_nobg`)을 기준으로 동작한다. 충돌 회피는 다이얼로그 Generate 시점에 1차 수행하고 서비스 진입 시점에 안전망으로 한 번 더 확인한다.

남아있는 (비-블로킹) 리스크:

- **UiScale과 모달 크기**: 프롬프트 입력이 긴 multiline이라 UiScale에 따라 창이 작아질 수 있음. `SetNextWindowSize`로 초기 크기 고정 후 사용자 조정 가능하게.
- **`ALIENHS_SERVER` 환경변수와의 우선순위**: Preferences의 `AiAlienhsServerUrl`이 항상 이기도록 설계(환경변수보다 우선). 문서화 필요.

## 범위 외

- **Texture 이외의 자산 타입** (재질, 프리팹, 오디오 등).
- **에셋 자체 우클릭의 "Regenerate" / "Remove Background"** 같은 후처리 메뉴.
- **진행률 바, 취소 버튼, 동시 작업 목록 창** — 알림은 완료/실패 토스트만 제공.
- **Seed/크기/모델 프리셋 UI** — 모델은 Preferences의 `AiRefineModel`로만 노출.
- **프롬프트 번역/한국어 지원** — CLI와 동일하게 영어 프롬프트를 전제.
