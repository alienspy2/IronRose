using System;

namespace RoseEngine
{
    public enum LogLevel { Info, Warning, Error }
    public enum LogSource { Editor, Project }

    public record LogEntry(LogLevel Level, LogSource Source, string Message, DateTime Timestamp,
        string? StackInfo = null, string? CallerFilePath = null, int CallerLine = 0,
        bool IsBuildError = false);
}
