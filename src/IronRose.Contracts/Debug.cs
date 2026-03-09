using System;
using System.IO;

namespace RoseEngine
{
    public static class Debug
    {
        private static readonly string _logPath;
        private static readonly object _lock = new();

        /// <summary>로그 출력 활성화 여부 (기본 true)</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>에디터 브릿지 등 외부 로그 수신용 delegate</summary>
        public static Action<LogEntry>? LogSink;

        static Debug()
        {
            Directory.CreateDirectory("logs");
            _logPath = Path.Combine("logs", $"ironrose_{DateTime.Now:yyyyMMdd_HHmmss}.log");
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
