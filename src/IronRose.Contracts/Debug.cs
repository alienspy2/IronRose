// ------------------------------------------------------------
// @file    Debug.cs
// @brief   게임 런타임/유저 스크립트 전용 로그 시스템. {ProjectRoot}/Logs/에 기록.
//          SetLogDirectory()로 프로젝트 로드 후 로그 디렉토리를 설정한다.
//          프로젝트 미로드 시 EditorDebug로 폴백.
// @deps    (없음 — Contracts 레이어, 외부 의존 없음)
// @exports
//   static class Debug
//     Enabled: bool                                     — 로그 출력 활성화 여부
//     LogSink: Action<LogEntry>?                        — 외부 로그 수신 delegate
//     Log(object): void                                 — INFO 레벨 로그
//     LogWarning(object): void                          — WARNING 레벨 로그
//     LogError(object): void                            — ERROR 레벨 로그
//     SetLogDirectory(string logDir): void              — 로그 디렉토리 변경
// @note    SetLogDirectory() 호출 전까지 EditorDebug로 폴백.
//          Write()에서 _lock으로 파일 접근 동기화. IOException 발생 시 무시 (콘솔 출력은 완료).
// ------------------------------------------------------------
using System;
using System.IO;

namespace RoseEngine
{
    public static class Debug
    {
        private static string _logPath;
        private static readonly object _lock = new();
        private static string _logFileName;
        private static bool _projectActive;

        /// <summary>로그 출력 활성화 여부 (기본 true)</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>에디터 브릿지 등 외부 로그 수신용 delegate</summary>
        public static Action<LogEntry>? LogSink;

        static Debug()
        {
            _logFileName = $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            // 초기에는 로그 파일을 생성하지 않음 -- SetLogDirectory() 전까지 EditorDebug로 폴백
            _logPath = "";
            _projectActive = false;
        }

        /// <summary>
        /// 로그 디렉토리를 설정합니다. 프로젝트 로드 후 호출.
        /// </summary>
        public static void SetLogDirectory(string logDir)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(logDir);
                _logPath = Path.Combine(logDir, _logFileName);
                _projectActive = true;
            }
        }

        public static void Log(object message) => Write("LOG", message);
        public static void LogWarning(object message) => Write("WARNING", message);
        public static void LogError(object message) => Write("ERROR", message);

        private static void Write(string level, object message)
        {
            if (!Enabled) return;

            // 프로젝트 미로드 시 EditorDebug로 폴백
            if (!_projectActive)
            {
                switch (level)
                {
                    case "WARNING": EditorDebug.LogWarning(message); return;
                    case "ERROR": EditorDebug.LogError(message); return;
                    default: EditorDebug.Log(message); return;
                }
            }

            var line = $"[{level}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
                }
                catch (IOException)
                {
                    // 디스크 풀, 권한 문제 등 — 콘솔 출력은 이미 완료되었으므로 무시
                }
            }

            var logLevel = level switch
            {
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            LogSink?.Invoke(new LogEntry(logLevel, LogSource.Project, message?.ToString() ?? "null", DateTime.Now));
        }
    }
}
