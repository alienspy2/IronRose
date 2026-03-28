// ------------------------------------------------------------
// @file    EditorDebug.cs
// @brief   에디터/엔진 내부 전용 로그 시스템. {EngineRoot}/Logs/에 기록.
//          Initialize()로 엔진 시작 시 로그 디렉토리를 설정한다.
//          Initialize() 이전 호출은 CWD/Logs/ 또는 임시 디렉토리에 버퍼링된다.
// @deps    (없음 -- Contracts 레이어, 외부 의존 없음)
// @exports
//   static class EditorDebug
//     Enabled: bool                                     -- 로그 출력 활성화 여부
//     LogSink: Action<LogEntry>?                        -- 외부 로그 수신 delegate
//     Initialize(string engineRoot): void               -- 로그 디렉토리 설정
//     Log(object): void                                 -- INFO 레벨 로그
//     LogWarning(object): void                          -- WARNING 레벨 로그
//     LogError(object): void                            -- ERROR 레벨 로그
// @note    Initialize() 호출 시 기존 버퍼 로그를 새 경로로 복사 후 원본 삭제.
//          Write()에서 _lock으로 파일 접근 동기화. IOException 발생 시 무시 (콘솔 출력은 완료).
// ------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;

namespace RoseEngine
{
    public static class EditorDebug
    {
        private static string _logPath;
        private static readonly object _lock = new();
        private static readonly string _logFileName;
        private static bool _initialized;

        /// <summary>로그 출력 활성화 여부 (기본 true)</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>상세 로그 출력 여부 (기본 false). false이면 Log()가 무시되고 Warning/Error만 출력됨.</summary>
        public static bool Verbose { get; set; } = false;

        /// <summary>에디터 Console 패널 등 외부 로그 수신용 delegate</summary>
        public static Action<LogEntry>? LogSink;

        static EditorDebug()
        {
            _logFileName = $"editor_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            try
            {
                Directory.CreateDirectory("Logs");
                _logPath = Path.Combine("Logs", _logFileName);
            }
            catch
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "IronRose", "Logs");
                try { Directory.CreateDirectory(tempDir); } catch { }
                _logPath = Path.Combine(tempDir, _logFileName);
            }
        }

        /// <summary>
        /// 엔진 루트가 확정된 후 호출. 로그 디렉토리를 {engineRoot}/Logs/로 이동한다.
        /// 기존 버퍼 로그를 새 경로로 복사 후 원본 삭제.
        /// </summary>
        public static void Initialize(string engineRoot)
        {
            lock (_lock)
            {
                var logDir = Path.Combine(engineRoot, "Logs");
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
                _initialized = true;
            }
        }

        /// <summary>EditorDebug가 초기화되었는지 여부.</summary>
        public static bool IsInitialized => _initialized;

        /// <summary>현재 로그 파일의 디렉토리 경로.</summary>
        public static string LogDirectory => Path.GetDirectoryName(_logPath) ?? "";

        public static void Log(object message, bool force = false)
        {
            if (!force && !Verbose) return;
            Write("LOG", message);
        }
        public static void LogWarning(object message) => Write("WARNING", message);
        public static void LogError(object message) => Write("ERROR", message);

        private static void Write(string level, object message)
        {
            if (!Enabled) return;

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
                    // 디스크 풀, 권한 문제 등 -- 콘솔 출력은 이미 완료되었으므로 무시
                }
            }

            var logLevel = level switch
            {
                "WARNING" => LogLevel.Warning,
                "ERROR" => LogLevel.Error,
                _ => LogLevel.Info,
            };
            var st = new StackTrace(1, true);
            var (callerFile, callerLine) = StackTraceHelper.ResolveCallerFrame(st);
            LogSink?.Invoke(new LogEntry(logLevel, LogSource.Editor, message?.ToString() ?? "null", DateTime.Now,
                st.ToString(), callerFile, callerLine));
        }
    }
}
