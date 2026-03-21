// ------------------------------------------------------------
// @file    Debug.cs
// @brief   전역 로그 시스템. LOG/WARNING/ERROR 레벨로 콘솔 및 파일에 기록.
//          초기에는 CWD/logs/에 기록하며, SetLogDirectory()로 프로젝트 폴더로 전환 가능.
// @deps    (없음 — Contracts 레이어, 외부 의존 없음)
// @exports
//   static class Debug
//     Enabled: bool                                     — 로그 출력 활성화 여부
//     LogSink: Action<LogEntry>?                        — 외부 로그 수신 delegate
//     Log(object): void                                 — INFO 레벨 로그
//     LogWarning(object): void                          — WARNING 레벨 로그
//     LogError(object): void                            — ERROR 레벨 로그
//     SetLogDirectory(string logDir): void              — 로그 디렉토리 변경 (기존 내용 복사)
// @note    정적 생성자에서 CWD/logs/ fallback 경로로 초기화.
//          SetLogDirectory() 호출 시 기존 로그 내용을 새 경로로 복사 후 원본 삭제.
//          Write()에서 _lock으로 파일 접근 동기화.
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

        /// <summary>로그 출력 활성화 여부 (기본 true)</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>에디터 브릿지 등 외부 로그 수신용 delegate</summary>
        public static Action<LogEntry>? LogSink;

        static Debug()
        {
            _logFileName = $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            Directory.CreateDirectory("Logs");
            _logPath = Path.Combine("Logs", _logFileName);
        }

        /// <summary>
        /// 로그 디렉토리를 변경합니다. 기존 로그 파일의 내용은 새 경로로 복사됩니다.
        /// </summary>
        public static void SetLogDirectory(string logDir)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(logDir);
                var newPath = Path.Combine(logDir, _logFileName);

                if (File.Exists(_logPath) && _logPath != newPath)
                {
                    try
                    {
                        var existingContent = File.ReadAllText(_logPath);
                        File.WriteAllText(newPath, existingContent);
                        try { File.Delete(_logPath); } catch { }
                    }
                    catch { }
                }

                _logPath = newPath;
            }
        }

        public static void Log(object message) => Write("LOG", message);
        public static void LogWarning(object message) => Write("WARNING", message);
        public static void LogError(object message) => Write("ERROR", message);

        private static void Write(string level, object message)
        {
            if (!Enabled) return;

            var line = $"[{level}] {message}";
            Console.WriteLine(line);

            lock (_lock)
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }

            var logLevel = level switch
            {
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            LogSink?.Invoke(new LogEntry(logLevel, message?.ToString() ?? "null", DateTime.Now));
        }
    }
}
