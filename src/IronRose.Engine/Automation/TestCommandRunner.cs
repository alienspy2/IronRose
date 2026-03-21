using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using IronRose.Engine.Editor;
using IronRose.Rendering;
using RoseEngine;

namespace IronRose.Engine.Automation
{
    /// <summary>
    /// JSON 기반 테스트 명령 파일을 로드하여 순차적으로 실행합니다.
    /// 디버깅 시 입력 시퀀스를 자동으로 재현하는 데 사용됩니다.
    /// </summary>
    public class TestCommandRunner
    {
        private static readonly string DefaultCommandFile = Path.Combine(".claude", "test_commands.json");

        private readonly List<TestCommand> _commands;
        private int _currentIndex;
        private double _waitRemaining;
        private bool _finished;

        private TestCommandRunner(List<TestCommand> commands)
        {
            _commands = commands;
        }

        public bool IsFinished => _finished;

        /// <summary>
        /// 기본 경로(.claude/test_commands.json)에서 명령 파일을 로드합니다.
        /// 파일이 없으면 null을 반환합니다.
        /// </summary>
        public static TestCommandRunner? TryLoad()
        {
            return TryLoad(DefaultCommandFile);
        }

        /// <summary>
        /// 지정된 경로에서 명령 파일을 로드합니다.
        /// 파일이 없으면 null을 반환합니다.
        /// </summary>
        public static TestCommandRunner? TryLoad(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var commands = new List<TestCommand>();

                if (doc.RootElement.TryGetProperty("commands", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in arr.EnumerateArray())
                    {
                        var cmd = ParseCommand(elem);
                        if (cmd != null)
                            commands.Add(cmd);
                    }
                }

                EditorDebug.Log($"[Automation] Loaded {commands.Count} commands from {path}");
                return new TestCommandRunner(commands);
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Automation] Failed to load command file '{path}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 매 프레임 호출. wait 명령은 deltaTime을 누적하여 대기합니다.
        /// </summary>
        public void Update(double deltaTime, GraphicsManager? graphicsManager)
        {
            if (_finished)
                return;

            // wait 대기 중
            if (_waitRemaining > 0)
            {
                _waitRemaining -= deltaTime;
                if (_waitRemaining > 0)
                    return;
                // wait 완료 — 다음 명령으로 진행
                _currentIndex++;
            }

            // 한 프레임에 wait가 아닌 명령은 연속 실행
            while (_currentIndex < _commands.Count)
            {
                var cmd = _commands[_currentIndex];

                try
                {
                    switch (cmd.Type)
                    {
                        case "scene.load":
                            ExecuteSceneLoad(cmd);
                            break;

                        case "input.key_press":
                            ExecuteKeyPress(cmd);
                            break;

                        case "wait":
                            _waitRemaining = cmd.Duration;
                            EditorDebug.Log($"[Automation] [{_currentIndex + 1}/{_commands.Count}] wait {cmd.Duration:F2}s");
                            return; // 다음 프레임에서 계속

                        case "screenshot":
                            ExecuteScreenshot(cmd, graphicsManager);
                            break;

                        case "play_mode":
                            ExecutePlayMode(cmd);
                            break;

                        case "quit":
                            EditorDebug.Log($"[Automation] [{_currentIndex + 1}/{_commands.Count}] quit");
                            _finished = true;
                            Application.Quit();
                            return;

                        default:
                            EditorDebug.LogWarning($"[Automation] [{_currentIndex + 1}/{_commands.Count}] Unknown command type: {cmd.Type}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    EditorDebug.LogError($"[Automation] [{_currentIndex + 1}/{_commands.Count}] Command '{cmd.Type}' failed: {ex.Message}");
                }

                _currentIndex++;
            }

            // 모든 명령 실행 완료
            if (!_finished)
            {
                _finished = true;
                EditorDebug.Log("[Automation] All commands completed.");
            }
        }

        private void ExecuteSceneLoad(TestCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.Scene))
            {
                EditorDebug.LogError($"[Automation] [{_currentIndex + 1}/{_commands.Count}] scene.load: 'scene' field is required");
                return;
            }

            EditorDebug.Log($"[Automation] [{_currentIndex + 1}/{_commands.Count}] scene.load \"{cmd.Scene}\"");
            SceneSerializer.Load(cmd.Scene);
        }

        private void ExecuteKeyPress(TestCommand cmd)
        {
            if (string.IsNullOrEmpty(cmd.Key))
            {
                EditorDebug.LogError($"[Automation] [{_currentIndex + 1}/{_commands.Count}] input.key_press: 'key' field is required");
                return;
            }

            if (Enum.TryParse<KeyCode>(cmd.Key, ignoreCase: true, out var keyCode))
            {
                EditorDebug.Log($"[Automation] [{_currentIndex + 1}/{_commands.Count}] input.key_press {cmd.Key} → {keyCode}");
                Input.SimulateKeyPress(keyCode);
            }
            else
            {
                EditorDebug.LogError($"[Automation] [{_currentIndex + 1}/{_commands.Count}] input.key_press: Unknown key '{cmd.Key}'");
            }
        }

        private void ExecuteScreenshot(TestCommand cmd, GraphicsManager? graphicsManager)
        {
            if (graphicsManager == null)
            {
                EditorDebug.LogError($"[Automation] [{_currentIndex + 1}/{_commands.Count}] screenshot: GraphicsManager is null");
                return;
            }

            var path = cmd.Path;
            if (string.IsNullOrEmpty(path))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                path = Path.Combine(".claude", "test_outputs", $"screenshot_{timestamp}.png");
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            EditorDebug.Log($"[Automation] [{_currentIndex + 1}/{_commands.Count}] screenshot → {path}");
            graphicsManager.RequestScreenshot(path);
        }

        private void ExecutePlayMode(TestCommand cmd)
        {
            var action = cmd.Action ?? "enter";
            EditorDebug.Log($"[Automation] [{_currentIndex + 1}/{_commands.Count}] play_mode {action}");

            switch (action.ToLowerInvariant())
            {
                case "enter":
                    EditorPlayMode.EnterPlayMode();
                    break;
                case "stop":
                    EditorPlayMode.StopPlayMode();
                    break;
                case "pause":
                    EditorPlayMode.PausePlayMode();
                    break;
                case "resume":
                    EditorPlayMode.ResumePlayMode();
                    break;
                default:
                    EditorDebug.LogWarning($"[Automation] Unknown play_mode action: {action}");
                    break;
            }
        }

        private static TestCommand? ParseCommand(JsonElement elem)
        {
            if (elem.ValueKind != JsonValueKind.Object)
                return null;

            var cmd = new TestCommand();

            if (elem.TryGetProperty("type", out var typeProp))
                cmd.Type = typeProp.GetString() ?? "";

            if (elem.TryGetProperty("scene", out var sceneProp))
                cmd.Scene = sceneProp.GetString();

            if (elem.TryGetProperty("key", out var keyProp))
                cmd.Key = keyProp.GetString();

            if (elem.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out var dur))
                cmd.Duration = dur;

            if (elem.TryGetProperty("path", out var pathProp))
                cmd.Path = pathProp.GetString();

            if (elem.TryGetProperty("action", out var actionProp))
                cmd.Action = actionProp.GetString();

            return cmd;
        }

        private class TestCommand
        {
            public string Type { get; set; } = "";
            public string? Scene { get; set; }
            public string? Key { get; set; }
            public double Duration { get; set; }
            public string? Path { get; set; }
            public string? Action { get; set; }
        }
    }
}
