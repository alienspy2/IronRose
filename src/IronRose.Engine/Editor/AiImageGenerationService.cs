// ------------------------------------------------------------
// @file    AiImageGenerationService.cs
// @brief   AI 이미지 생성 파이프라인. cli-invoke-comfyui.py를 백그라운드 프로세스로 호출하고,
//          결과를 UI 스레드 드레인용 ConcurrentQueue에 쌓는다.
// @deps    IronRose.Engine/EditorPreferences, IronRose.Engine/ProjectContext,
//          IronRose.Engine.Editor/AiImageHistory,
//          IronRose.AssetPipeline/AssetDatabase (UI 스레드 쪽에서만 참조),
//          IronRose.Engine.Editor.ImGuiEditor/EditorModal, RoseEngine/EditorDebug
// @exports
//   record AiImageGenerationRequest(TargetFolderAbsPath, ResolvedFileName, StylePrompt, Prompt, Refine, Alpha)
//   record AiImageGenerationResult(Success, AbsoluteOutputPath, Message, Request)
//   static class AiImageGenerationService
//     RunningCount: int                                        — 현재 실행 중인 작업 수
//     IsInFlight(string absolutePath): bool                    — 해당 절대 경로가 실행 중인지 확인
//     Enqueue(AiImageGenerationRequest): bool                  — 백그라운드 Task 실행, 중복 경로는 false 반환
//     DrainResults(Action<AiImageGenerationResult>): void      — UI 스레드에서 매 프레임 호출하여 결과 소비
//     ResolveUniqueFileName(string folder, string baseName): string — 충돌 회피된 파일명(확장자 제외) 반환
// @note    동일 절대 경로가 현재 실행 중이면 Enqueue 거부.
//          Process.Start 표준출력의 마지막 JSON 라인을 파싱. --json 출력 규약 참조.
//          Alpha=true인 경우 CLI 완료 후 원본 <stem>.png 삭제 → <stem>_nobg.png → <stem>.png 리네임.
// ------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IronRose.Engine.Editor.ImGuiEditor;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 다이얼로그에서 확정된 AI 이미지 생성 요청. 경로와 프롬프트/토글을 함께 전달한다.
    /// </summary>
    public sealed record AiImageGenerationRequest(
        string TargetFolderAbsPath,   // 다이얼로그에서 선택된 폴더 (절대 경로)
        string ResolvedFileName,      // 확장자 제외, suffix 충돌 회피 적용 후 최종 이름
        string StylePrompt,
        string Prompt,
        bool Refine,
        bool Alpha);

    /// <summary>
    /// AI 이미지 생성 결과. UI 스레드에서 DrainResults를 통해 소비된다.
    /// </summary>
    public sealed record AiImageGenerationResult(
        bool Success,
        string? AbsoluteOutputPath,   // 성공 시 최종 PNG 절대 경로, 실패 시 null
        string Message,               // 사용자에게 표시할 메시지 (성공/실패 공통)
        AiImageGenerationRequest Request);

    /// <summary>
    /// AI 이미지 생성 파이프라인의 정적 진입점.
    /// Enqueue()로 백그라운드 실행을 트리거하고, DrainResults()로 UI 스레드에서 결과를 수집한다.
    /// </summary>
    public static class AiImageGenerationService
    {
        private static readonly ConcurrentQueue<AiImageGenerationResult> _pending = new();
        private static readonly HashSet<string> _inFlightPaths = new();       // 정규화된 절대 경로
        private static readonly object _inFlightLock = new();
        private static int _runningCount;

        /// <summary>현재 실행 중인 생성 작업 수.</summary>
        public static int RunningCount => Volatile.Read(ref _runningCount);

        /// <summary>현재 생성 중인 절대 경로(확정 이름)인지 확인.</summary>
        public static bool IsInFlight(string absolutePath)
        {
            lock (_inFlightLock) return _inFlightPaths.Contains(Normalize(absolutePath));
        }

        /// <summary>
        /// 요청을 큐잉하고 백그라운드 Task를 시작한다.
        /// 동일 절대 경로가 이미 실행 중이면 EnqueueAlert로 거부 메시지 출력 후 false 반환.
        /// </summary>
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

        /// <summary>
        /// UI 스레드에서 매 프레임 호출해 완료된 결과를 꺼낸다.
        /// 콜백 안에서 Reimport / History / Alert 호출은 호출자 책임.
        /// </summary>
        public static void DrainResults(Action<AiImageGenerationResult> onResult)
        {
            while (_pending.TryDequeue(out var r))
                onResult(r);
        }

        /// <summary>
        /// &lt;folder&gt;/&lt;baseName&gt;.png가 이미 있으면 &lt;baseName&gt;_1.png, _2.png, ... 로
        /// 첫 번째로 존재하지 않는 이름을 찾아 **확장자 없는** baseName을 반환한다.
        /// 안전망: in-flight 경로와도 충돌하지 않는 이름을 반환.
        /// </summary>
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

        // ----------------------------------------------------------------
        // 내부 구현
        // ----------------------------------------------------------------

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
            psi.ArgumentList.Add(EditorPreferences.AiAlienhsServerUrl ?? "");

            if (!string.IsNullOrWhiteSpace(EditorPreferences.AiComfyUrl))
            {
                psi.ArgumentList.Add("--comfy-url");
                psi.ArgumentList.Add(EditorPreferences.AiComfyUrl);
            }

            if (!string.IsNullOrWhiteSpace(EditorPreferences.AiGenerationModel))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(EditorPreferences.AiGenerationModel);
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
    }
}
