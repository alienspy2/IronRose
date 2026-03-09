using System;

namespace RoseEngine
{
    public enum LogLevel { Info, Warning, Error }

    public record LogEntry(LogLevel Level, string Message, DateTime Timestamp);
}
