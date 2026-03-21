// ------------------------------------------------------------
// @file    CliCommandDispatcher.cs
// @brief   CLI 요청 평문을 파싱하여 적절한 핸들러를 호출하고 JSON 응답을 반환한다.
//          메인 스레드 실행이 필요한 명령은 큐에 넣고 결과를 대기한다.
// @deps    System.Text.Json, RoseEngine/SceneManager, IronRose.Engine/ProjectContext
// @exports
//   class CliCommandDispatcher
//     Dispatch(string requestLine): string  -- 요청 처리 후 응답 JSON 반환
//     ProcessMainThreadQueue(): void        -- 메인 스레드에서 호출하여 대기 중 명령 실행
// @note    백그라운드 스레드(Pipe)에서 Dispatch()가 호출된다.
//          메인 스레드 접근이 필요한 명령은 _mainThreadQueue에 넣고
//          ManualResetEventSlim으로 완료를 대기한다 (타임아웃 5초).
// ------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public class CliCommandDispatcher
    {
        private readonly Dictionary<string, Func<string[], string>> _handlers = new();
        private readonly ConcurrentQueue<MainThreadTask> _mainThreadQueue = new();
        private readonly CliLogBuffer _logBuffer;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private class MainThreadTask
        {
            public required Func<string> Execute { get; init; }
            public ManualResetEventSlim Done { get; } = new(false);
            public string? Result { get; set; }
        }

        public CliCommandDispatcher(CliLogBuffer logBuffer)
        {
            _logBuffer = logBuffer;
            RegisterHandlers();
        }

        public string Dispatch(string requestLine)
        {
            try
            {
                var tokens = ParseArgs(requestLine.Trim());
                if (tokens.Length == 0)
                    return JsonError("Empty command");

                var command = tokens[0].ToLowerInvariant();
                var args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

                if (_handlers.TryGetValue(command, out var handler))
                    return handler(args);

                return JsonError($"Unknown command: {command}");
            }
            catch (Exception ex)
            {
                return JsonError($"Dispatch error: {ex.Message}");
            }
        }

        public void ProcessMainThreadQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var task))
            {
                try
                {
                    task.Result = task.Execute();
                }
                catch (Exception ex)
                {
                    task.Result = JsonError(ex.Message);
                }
                finally
                {
                    task.Done.Set();
                }
            }
        }

        private void RegisterHandlers()
        {
            // ping -- 백그라운드 스레드에서 직접 실행
            _handlers["ping"] = args => JsonOk(new { pong = true, project = ProjectContext.ProjectName });

            // scene.info -- 메인 스레드 필요
            _handlers["scene.info"] = args => ExecuteOnMainThread(() =>
            {
                var scene = SceneManager.GetActiveScene();
                return JsonOk(new
                {
                    name = scene.name,
                    path = scene.path ?? "",
                    isDirty = scene.isDirty,
                    gameObjectCount = SceneManager.AllGameObjects.Count
                });
            });

            // scene.list -- 메인 스레드 필요
            _handlers["scene.list"] = args => ExecuteOnMainThread(() =>
            {
                var gos = SceneManager.AllGameObjects;
                var list = new List<object>();
                foreach (var go in gos)
                {
                    if (go._isDestroyed) continue;
                    list.Add(new
                    {
                        id = go.GetInstanceID(),
                        name = go.name,
                        active = go.activeSelf,
                        parentId = go.transform.parent?.gameObject.GetInstanceID()
                    });
                }
                return JsonOk(new { gameObjects = list });
            });
        }

        private string ExecuteOnMainThread(Func<string> action)
        {
            var task = new MainThreadTask { Execute = action };
            _mainThreadQueue.Enqueue(task);
            if (!task.Done.Wait(TimeSpan.FromSeconds(5)))
                return JsonError("Main thread timeout (5s)");
            return task.Result!;
        }

        private static string[] ParseArgs(string requestLine)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < requestLine.Length; i++)
            {
                char c = requestLine[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (c == ' ' && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                current.Append(c);
            }
            if (current.Length > 0)
                args.Add(current.ToString());

            return args.ToArray();
        }

        private static string JsonOk(object data)
        {
            return JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions);
        }

        private static string JsonError(string message)
        {
            return JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions);
        }
    }
}
