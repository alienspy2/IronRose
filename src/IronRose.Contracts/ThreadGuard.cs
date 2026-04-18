// ------------------------------------------------------------
// @file    ThreadGuard.cs
// @brief   엔진 전역 메인 스레드 검증 유틸리티. CaptureMainThread()로 메인 스레드
//          ID를 기록하고, CheckMainThread(context)로 호출 스레드가 메인인지 검증한다.
// @deps    RoseEngine/EditorDebug
// @exports
//   static class ThreadGuard
//     CaptureMainThread(): void                              -- 메인 스레드 ID 기록 (엔진 초기화 시 1회)
//     MainThreadId: int                                      -- 캡처된 메인 스레드 ID (없으면 -1)
//     IsMainThread: bool                                     -- 현재 스레드가 메인인지
//     CheckMainThread(string context): bool                  -- 검증. 위반 시 LogError 후 false 반환
//     DebugCheckMainThread(string context): void             -- Debug 빌드에서만 체크, Release는 no-op
// @note    throw 금지: 위반 감지 시 EditorDebug.LogError만 호출하고 false 반환한다.
//          호출자는 반환값을 보고 안전하게 fallback 할 수 있다 (데드락/크래시 회피).
//          동일 context 문자열은 5초 쿨다운으로 로그 홍수를 방지한다 (ConcurrentDictionary 기반).
//          _mainThreadId == -1 (캡처 전) 상태에서는 체크를 스킵하고 true 를 반환한다.
// ------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace RoseEngine
{
    public static class ThreadGuard
    {
        private static int _mainThreadId = -1;
        private static readonly ConcurrentDictionary<string, long> _lastLogTicks = new();
        private const long LogCooldownTicks = TimeSpan.TicksPerSecond * 5;

        /// <summary>
        /// 메인 스레드에서 1회 호출하여 해당 스레드의 ManagedThreadId를 기록한다.
        /// EngineCore.Initialize(IWindow) 진입 직후 최상단에서 호출된다.
        /// </summary>
        public static void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>캡처된 메인 스레드 ID. 아직 캡처되지 않았으면 -1.</summary>
        public static int MainThreadId => _mainThreadId;

        /// <summary>현재 스레드가 메인 스레드인지 여부 (캡처 전에는 false).</summary>
        public static bool IsMainThread =>
            _mainThreadId != -1 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// 현재 호출이 메인 스레드에서 발생했는지 검증한다.
        /// 위반 시 EditorDebug.LogError로 기록하고 false를 반환한다. throw 하지 않는다.
        /// context는 call site 식별자(예: "AssetDatabase.Reimport").
        /// 동일 context는 5초간 중복 로그가 억제된다.
        /// _mainThreadId == -1 (캡처 전)이면 체크를 스킵하고 true를 반환한다.
        /// </summary>
        public static bool CheckMainThread(string context)
        {
            if (_mainThreadId == -1) return true;
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) return true;

            var now = DateTime.UtcNow.Ticks;
            if (_lastLogTicks.TryGetValue(context, out var last) && (now - last) < LogCooldownTicks)
                return false;
            _lastLogTicks[context] = now;

            EditorDebug.LogError(
                $"[ThreadGuard] {context} must be called on main thread " +
                $"(called from thread {Thread.CurrentThread.ManagedThreadId}, " +
                $"main={_mainThreadId}). Continuing in unsafe mode.");
            return false;
        }

        /// <summary>Debug 빌드에서만 CheckMainThread를 호출한다. Release 빌드에서는 no-op.</summary>
        [Conditional("DEBUG")]
        public static void DebugCheckMainThread(string context) => CheckMainThread(context);
    }
}
