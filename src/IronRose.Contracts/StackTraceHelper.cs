// ------------------------------------------------------------
// @file    StackTraceHelper.cs
// @brief   StackTrace에서 로그 인프라를 건너뛰고 실제 호출자 위치를 찾는 헬퍼.
// @deps    (없음 — Contracts 레이어)
// @exports
//   internal static class StackTraceHelper
//     ResolveCallerFrame(StackTrace): (string? filePath, int line)
// @note    Debug, EditorDebug, [HideInConsoleStackTrace] 어트리뷰트 대상을 건너뜀.
// ------------------------------------------------------------
using System.Diagnostics;

namespace RoseEngine
{
    internal static class StackTraceHelper
    {
        /// <summary>
        /// <see cref="StackTrace"/>에서 로그 인프라 프레임을 건너뛰고
        /// 실제 호출자의 소스 파일 경로와 라인 번호를 반환합니다.
        /// </summary>
        internal static (string? filePath, int line) ResolveCallerFrame(StackTrace trace)
        {
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var frame = trace.GetFrame(i);
                if (frame == null) continue;

                var method = frame.GetMethod();
                if (method == null) continue;

                var declaringType = method.DeclaringType;

                // Skip known log infrastructure types
                if (declaringType == typeof(Debug) || declaringType == typeof(EditorDebug))
                    continue;

                // Skip methods marked with [HideInConsoleStackTrace]
                if (method.IsDefined(typeof(HideInConsoleStackTraceAttribute), false))
                    continue;

                // Skip types marked with [HideInConsoleStackTrace]
                if (declaringType != null && declaringType.IsDefined(typeof(HideInConsoleStackTraceAttribute), false))
                    continue;

                var filePath = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                if (!string.IsNullOrEmpty(filePath) && line > 0)
                    return (filePath, line);
            }

            return (null, 0);
        }
    }
}
