# Phase B: AiImageHistory 저장소

## 목표

- **`<ProjectRoot>/memory/ai_image_history.json`**에 최근 AI 이미지 생성 프롬프트 5건과 마지막 토글 상태(Refine/Alpha)를 영속화하는 정적 저장소를 신설한다.
- 프로젝트 로드 훅에 `AiImageHistory.Load()`를 연결해, 에디터 시작 시(또는 프로젝트 전환 시) 히스토리가 메모리에 올라오도록 한다.
- Phase C의 다이얼로그가 이 저장소를 읽어 폼 기본값으로 사용하고, 성공 시 `RecordSuccess`를 호출해 갱신한다.
- 이 Phase 단독으로는 UI 변화는 없고, 빌드 성공 + Load/Save 경로 확인이 목표.

## 선행 조건

- **Phase A 완료** (빌드 가능한 상태 유지 위해 권장. 기능 의존성은 없음).
- `src/IronRose.Engine/ProjectContext.cs`의 `ProjectContext.ProjectRoot`/`IsProjectLoaded` API가 그대로 존재.
- `src/IronRose.Engine/EngineCore.cs`의 초기화 시퀀스(`ProjectContext.Initialize()` → `EditorPreferences.Load()`)가 그대로 존재.

## 생성할 파일

### `src/IronRose.Engine/Editor/AiImageHistory.cs`

- **역할**: AI 이미지 생성 다이얼로그에서 참조/갱신하는 프로젝트-로컬 히스토리 저장소. 최근 5개의 (style_prompt, prompt) 엔트리 + 마지막 (refine, alpha) 토글.
- **타입**: `internal static class AiImageHistory` (네임스페이스 `IronRose.Engine.Editor`).
- **레코드 타입**: 파일 내에 `public sealed record AiImageHistoryEntry(string StylePrompt, string Prompt)` 를 함께 정의한다.

#### 공개 API

```csharp
namespace IronRose.Engine.Editor
{
    public sealed record AiImageHistoryEntry(string StylePrompt, string Prompt);

    public static class AiImageHistory
    {
        /// <summary>
        /// 프로젝트가 로드된 경우 memory/ai_image_history.json을 읽어 메모리로 올린다.
        /// IsProjectLoaded == false이면 no-op.
        /// </summary>
        public static void Load();

        /// <summary>최근 엔트리 (최신이 index 0, 최대 5개). 복사본이 아닌 ReadOnlyList 뷰.</summary>
        public static IReadOnlyList<AiImageHistoryEntry> Entries { get; }

        /// <summary>마지막 사용 토글. 기본 (true, false). Load 실패 시에도 이 기본값.</summary>
        public static (bool Refine, bool Alpha) LastToggles { get; }

        /// <summary>
        /// 생성 성공 시 호출. FIFO 5건 유지, 중복 엔트리는 맨 앞으로 승격, 빈 prompt는 무시.
        /// 토글 덮어쓰기 후 즉시 파일로 flush.
        /// </summary>
        /// <param name="stylePrompt">Style 프롬프트 (빈 문자열 허용).</param>
        /// <param name="prompt">본 프롬프트 (빈 문자열이면 기록하지 않고 토글만 갱신).</param>
        /// <param name="refine">이번 생성에서 사용한 Refine 토글.</param>
        /// <param name="alpha">이번 생성에서 사용한 Alpha 토글.</param>
        public static void RecordSuccess(string stylePrompt, string prompt, bool refine, bool alpha);
    }
}
```

#### 내부 상태

```csharp
private const int MaxEntries = 5;
private static readonly List<AiImageHistoryEntry> _entries = new();
private static (bool Refine, bool Alpha) _lastToggles = (true, false);
private static readonly object _lock = new();
```

- 정적 상태이므로 프로젝트 전환 시 `Load()`가 다시 호출되면 `_entries.Clear()` + 파일 재파싱해야 한다.
- 파일 I/O 실패는 **삼켜서** 로그만 남긴다 (사용자 작업을 막지 않음).

#### JSON 스키마

파일 경로: `Path.Combine(ProjectContext.ProjectRoot, "memory", "ai_image_history.json")`

```json
{
  "history": [
    { "style_prompt": "cinematic, dramatic lighting", "prompt": "a knight in rain" },
    { "style_prompt": "", "prompt": "wooden crate texture" }
  ],
  "last_toggles": { "refine": true, "alpha": false }
}
```

- **루트 객체**.
- `history`: 배열, 최대 5개, index 0이 가장 최신.
- 각 엔트리: `style_prompt` (string, required), `prompt` (string, required, non-empty).
- `last_toggles`: 객체, `refine` (bool), `alpha` (bool). 누락 시 `(true, false)`.

#### 구현 힌트

- `System.Text.Json`을 사용. 파일 상단:

  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Text.Json;
  using System.Text.Json.Serialization;
  using IronRose.Engine;          // ProjectContext
  using RoseEngine;               // EditorDebug
  ```

- **DTO**: 외부 공개 record(`AiImageHistoryEntry`)는 `PascalCase` 이름을 가지지만 JSON은 `snake_case`로 쓰므로, 내부 전용 DTO 클래스를 별도로 두는 것이 단순하다.

  ```csharp
  private sealed class HistoryFileDto
  {
      [JsonPropertyName("history")]
      public List<EntryDto> History { get; set; } = new();

      [JsonPropertyName("last_toggles")]
      public TogglesDto LastToggles { get; set; } = new();
  }

  private sealed class EntryDto
  {
      [JsonPropertyName("style_prompt")] public string StylePrompt { get; set; } = "";
      [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
  }

  private sealed class TogglesDto
  {
      [JsonPropertyName("refine")] public bool Refine { get; set; } = true;
      [JsonPropertyName("alpha")] public bool Alpha { get; set; } = false;
  }
  ```

- **JsonSerializerOptions**:

  ```csharp
  private static readonly JsonSerializerOptions _jsonOpt = new()
  {
      WriteIndented = true,
      PropertyNamingPolicy = null,   // JsonPropertyName 직접 사용
      DefaultIgnoreCondition = JsonIgnoreCondition.Never,
  };
  ```

- **Entries 프로퍼티**:

  ```csharp
  public static IReadOnlyList<AiImageHistoryEntry> Entries
  {
      get { lock (_lock) return _entries.ToArray(); }
  }
  ```

  `ToArray()`로 스냅샷 반환 — ImGui 루프가 Entries를 반복하는 동안 RecordSuccess로 수정되어도 안전.

- **LastToggles 프로퍼티**:

  ```csharp
  public static (bool Refine, bool Alpha) LastToggles
  {
      get { lock (_lock) return _lastToggles; }
  }
  ```

- **Load() 구현**:

  ```csharp
  public static void Load()
  {
      lock (_lock)
      {
          _entries.Clear();
          _lastToggles = (true, false);

          if (!ProjectContext.IsProjectLoaded)
              return;

          var path = GetHistoryPath();
          if (!File.Exists(path))
          {
              EditorDebug.Log($"[AiImageHistory] No history file yet: {path}");
              return;
          }

          try
          {
              var json = File.ReadAllText(path);
              var dto = JsonSerializer.Deserialize<HistoryFileDto>(json, _jsonOpt);
              if (dto == null) return;

              foreach (var e in dto.History)
              {
                  if (string.IsNullOrWhiteSpace(e.Prompt)) continue;
                  _entries.Add(new AiImageHistoryEntry(e.StylePrompt ?? "", e.Prompt));
                  if (_entries.Count >= MaxEntries) break;
              }

              _lastToggles = (dto.LastToggles.Refine, dto.LastToggles.Alpha);
              EditorDebug.Log($"[AiImageHistory] Loaded {_entries.Count} entries from {path}");
          }
          catch (Exception ex)
          {
              EditorDebug.LogWarning($"[AiImageHistory] Failed to load: {ex.Message}");
          }
      }
  }
  ```

- **RecordSuccess() 구현**:

  ```csharp
  public static void RecordSuccess(string stylePrompt, string prompt, bool refine, bool alpha)
  {
      lock (_lock)
      {
          stylePrompt = stylePrompt ?? "";
          prompt = prompt ?? "";

          _lastToggles = (refine, alpha);

          if (!string.IsNullOrWhiteSpace(prompt))
          {
              // 중복 제거 (정확 일치 비교)
              _entries.RemoveAll(e => e.StylePrompt == stylePrompt && e.Prompt == prompt);
              _entries.Insert(0, new AiImageHistoryEntry(stylePrompt, prompt));
              while (_entries.Count > MaxEntries)
                  _entries.RemoveAt(_entries.Count - 1);
          }

          SaveLocked();
      }
  }

  private static void SaveLocked()
  {
      if (!ProjectContext.IsProjectLoaded) return;
      var path = GetHistoryPath();
      try
      {
          Directory.CreateDirectory(Path.GetDirectoryName(path)!);
          var dto = new HistoryFileDto
          {
              History = _entries.Select(e => new EntryDto
              {
                  StylePrompt = e.StylePrompt,
                  Prompt = e.Prompt,
              }).ToList(),
              LastToggles = new TogglesDto
              {
                  Refine = _lastToggles.Refine,
                  Alpha = _lastToggles.Alpha,
              },
          };
          var json = JsonSerializer.Serialize(dto, _jsonOpt);
          File.WriteAllText(path, json);
      }
      catch (Exception ex)
      {
          EditorDebug.LogWarning($"[AiImageHistory] Failed to save: {ex.Message}");
      }
  }

  private static string GetHistoryPath() =>
      Path.Combine(ProjectContext.ProjectRoot, "memory", "ai_image_history.json");
  ```

- **상단 파일 헤더 주석** (프로젝트 관례에 맞춰):

  ```csharp
  // ------------------------------------------------------------
  // @file    AiImageHistory.cs
  // @brief   프로젝트별 AI 이미지 생성 히스토리 저장소.
  //          <ProjectRoot>/memory/ai_image_history.json에 최근 5건의
  //          (style_prompt, prompt)와 마지막 (refine, alpha) 토글을 영속화한다.
  // @deps    IronRose.Engine/ProjectContext, RoseEngine/EditorDebug, System.Text.Json
  // @exports
  //   record AiImageHistoryEntry(string StylePrompt, string Prompt)
  //   static class AiImageHistory
  //     Load(): void                                         — 프로젝트 로드 직후 1회 호출
  //     Entries: IReadOnlyList<AiImageHistoryEntry>           — 최신이 index 0
  //     LastToggles: (bool Refine, bool Alpha)                — 기본 (true, false)
  //     RecordSuccess(string, string, bool, bool): void       — 생성 성공 시 호출, 즉시 flush
  // @note    동시성: 내부 lock으로 직렬화. UI 스레드에서 Entries 스냅샷을 얻어 반복.
  //          프로젝트 전환 시 Load() 재호출하면 상태가 초기화된다.
  //          빈 prompt는 기록 대상에서 제외. 중복(정확 일치)은 앞으로 승격(LRU).
  // ------------------------------------------------------------
  ```

---

## 수정할 파일

### `src/IronRose.Engine/EngineCore.cs`

**변경 내용**: 프로젝트 로드 직후 `AiImageHistory.Load()` 호출 추가.

현재 EngineCore.cs line 149 주변:

```csharp
// 프로젝트 컨텍스트 초기화 (EngineRoot 확정)
ProjectContext.Initialize();

// 사용자 전역 Preferences 로드 (ProjectContext와 독립, 테마/UI스케일/폰트 복원용)
EditorPreferences.Load();
```

**삽입 위치**: `EditorPreferences.Load();` 바로 다음 라인에 아래를 추가:

```csharp
// AI 이미지 생성 히스토리 로드 (프로젝트별, memory/ai_image_history.json)
IronRose.Engine.Editor.AiImageHistory.Load();
```

> 주의:
> - `AiImageHistory.Load()` 내부에서 `IsProjectLoaded` false 인 경우 no-op이므로, `ProjectContext.Initialize()` 이후 호출하면 안전.
> - 네임스페이스가 `IronRose.Engine.Editor`이고 EngineCore.cs는 `IronRose.Engine`이다. EngineCore.cs 상단에 이미 `using ...` 블록이 있는지 확인 후 없으면 `using IronRose.Engine.Editor;` 추가 또는 fully-qualified 호출 사용. Fully-qualified 호출이 간섭이 적다.

---

## 검증 기준

- [ ] `dotnet build` 성공.
- [ ] 에디터 실행 직후, 프로젝트가 로드된 경우:
  - 처음 실행 시 `<ProjectRoot>/memory/` 폴더와 파일이 **아직 생성되지 않아도 된다** (Load만으로는 파일을 만들지 않음).
  - 로그에 `[AiImageHistory] No history file yet: ...` 가 출력되거나 (파일 없음) `[AiImageHistory] Loaded N entries from ...` 가 출력된다 (파일 존재).
- [ ] 수동 테스트용 스텁: 에디터 콘솔 / Bash로 `<ProjectRoot>/memory/ai_image_history.json`을 직접 작성(위 스키마) 후 재실행 → 로드된 항목 수가 로그에 정확히 표시된다.
- [ ] 프로젝트가 로드되지 않은 상태(엔진 레포 단독 실행)에서도 예외 없이 조용히 지나간다 (`IsProjectLoaded == false` 분기).
- [ ] 손상된 JSON을 넣어도 예외를 삼켜 `LogWarning`만 남기고 에디터는 정상 동작한다.

## 참고

- 설계 문서의 "5. 히스토리/상태 저장소" 섹션을 그대로 반영.
- Phase C는 이 저장소의 `Entries`/`LastToggles`를 다이얼로그 초기값으로 사용하고, CLI 호출 성공 시 UI 스레드에서 `RecordSuccess`를 호출한다.
- "프로젝트 로드 훅"은 설계 문서에서 `ProjectContext.Initialize` 또는 `ImGuiProjectPanel`의 프로젝트 로드 후처리 중 택일이라고 남겼다. 본 Phase는 **EngineCore.cs**를 선택한다 (이유: `EditorPreferences.Load()`와 동일한 타이밍에 명시적으로 묶이며, 프로젝트 전환 시 EngineCore 재초기화 경로에서 자연스레 함께 재호출됨).
- 미결: 프로젝트 전환(런타임 중 다른 프로젝트 열기) 기능이 생기면 그 훅에서도 `AiImageHistory.Load()`를 재호출해야 한다. 현재는 EngineCore.Initialize 1회만 보장.

## 체크리스트 (순서대로)

- [ ] `src/IronRose.Engine/Editor/AiImageHistory.cs` 신규 파일 작성 (파일 헤더 주석 포함)
- [ ] `AiImageHistoryEntry` record 정의
- [ ] 내부 DTO 3종 + JsonSerializerOptions 정의
- [ ] `Load()` 구현 (파일 없음 / 프로젝트 미로드 / 파싱 실패 3가지 안전 분기)
- [ ] `Entries` / `LastToggles` 프로퍼티 구현 (lock 스냅샷)
- [ ] `RecordSuccess()` + `SaveLocked()` + `GetHistoryPath()` 구현
- [ ] `EngineCore.cs`에 `AiImageHistory.Load()` 호출 1줄 추가
- [ ] `dotnet build` 성공
- [ ] 로그에 `[AiImageHistory] ...` 메시지 확인
- [ ] `memory/ai_image_history.json` 샘플 파일 만들어 재기동 → 로드 카운트 확인
- [ ] 손상된 JSON 주입 → 에디터 정상 동작 + Warning 로그 확인
