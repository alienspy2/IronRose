// ------------------------------------------------------------
// @file    CliLogBuffer.cs
// @brief   CLI에서 조회할 수 있도록 최근 로그 엔트리를 링 버퍼에 저장한다.
// @deps    RoseEngine/LogEntry
// @exports
//   class CliLogBuffer
//     Push(LogEntry): void           -- 로그 추가 (EditorDebug.LogSink에서 호출)
//     GetRecent(int count): List<LogEntry>  -- 최근 N개 로그 반환
//     MAX_SIZE: int                  -- 링 버퍼 최대 크기 (1000)
// @note    스레드 안전 (lock 기반). 여러 스레드에서 Push가 호출될 수 있다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using RoseEngine;

namespace IronRose.Engine.Cli
{
    public class CliLogBuffer
    {
        public const int MAX_SIZE = 1000;

        private readonly LogEntry[] _buffer = new LogEntry[MAX_SIZE];
        private int _head;
        private int _count;
        private readonly object _lock = new();

        public void Push(LogEntry entry)
        {
            lock (_lock)
            {
                _buffer[_head] = entry;
                _head = (_head + 1) % MAX_SIZE;
                _count = Math.Min(_count + 1, MAX_SIZE);
            }
        }

        public List<LogEntry> GetRecent(int count)
        {
            lock (_lock)
            {
                count = Math.Min(count, _count);
                var result = new List<LogEntry>(count);

                // 가장 오래된 엔트리의 시작 인덱스
                int start = (_head - _count + MAX_SIZE) % MAX_SIZE;
                // 최근 count개만 필요하므로 skip할 개수 계산
                int skip = _count - count;
                int begin = (start + skip) % MAX_SIZE;

                for (int i = 0; i < count; i++)
                {
                    int idx = (begin + i) % MAX_SIZE;
                    result.Add(_buffer[idx]);
                }

                return result;
            }
        }
    }
}
