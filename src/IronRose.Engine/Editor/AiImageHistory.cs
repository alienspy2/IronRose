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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronRose.Engine;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    public sealed record AiImageHistoryEntry(string StylePrompt, string Prompt);

    public static class AiImageHistory
    {
        private const int MaxEntries = 5;
        private static readonly List<AiImageHistoryEntry> _entries = new();
        private static (bool Refine, bool Alpha) _lastToggles = (true, false);
        private static readonly object _lock = new();

        private static readonly JsonSerializerOptions _jsonOpt = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        /// <summary>최근 엔트리 (최신이 index 0, 최대 5개). lock 하에 스냅샷으로 반환.</summary>
        public static IReadOnlyList<AiImageHistoryEntry> Entries
        {
            get { lock (_lock) return _entries.ToArray(); }
        }

        /// <summary>마지막 사용 토글. 기본 (true, false). Load 실패 시에도 이 기본값.</summary>
        public static (bool Refine, bool Alpha) LastToggles
        {
            get { lock (_lock) return _lastToggles; }
        }

        /// <summary>
        /// 프로젝트가 로드된 경우 memory/ai_image_history.json을 읽어 메모리로 올린다.
        /// IsProjectLoaded == false이면 no-op.
        /// </summary>
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

                    if (dto.History != null)
                    {
                        foreach (var e in dto.History)
                        {
                            if (e == null) continue;
                            if (string.IsNullOrWhiteSpace(e.Prompt)) continue;
                            _entries.Add(new AiImageHistoryEntry(e.StylePrompt ?? "", e.Prompt));
                            if (_entries.Count >= MaxEntries) break;
                        }
                    }

                    if (dto.LastToggles != null)
                        _lastToggles = (dto.LastToggles.Refine, dto.LastToggles.Alpha);

                    EditorDebug.Log($"[AiImageHistory] Loaded {_entries.Count} entries from {path}");
                }
                catch (Exception ex)
                {
                    EditorDebug.LogWarning($"[AiImageHistory] Failed to load: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 생성 성공 시 호출. FIFO 5건 유지, 중복 엔트리는 맨 앞으로 승격, 빈 prompt는 무시.
        /// 토글 덮어쓰기 후 즉시 파일로 flush.
        /// </summary>
        public static void RecordSuccess(string stylePrompt, string prompt, bool refine, bool alpha)
        {
            lock (_lock)
            {
                stylePrompt ??= "";
                prompt ??= "";

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
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

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

        // ---- Internal DTOs ----
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
    }
}
