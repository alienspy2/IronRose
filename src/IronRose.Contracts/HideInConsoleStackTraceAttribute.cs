// ------------------------------------------------------------
// @file    HideInConsoleStackTraceAttribute.cs
// @brief   콘솔 더블클릭 시 스택트레이스에서 건너뛸 메서드/클래스를 지정하는 어트리뷰트.
//          Debug.Log를 래핑하는 커스텀 로거에 사용.
// @deps    (없음 — Contracts 레이어)
// @exports
//   [HideInConsoleStackTrace] attribute
// @note    메서드 또는 클래스에 적용 가능. 클래스에 적용 시 해당 클래스의 모든 메서드가 건너뜀 대상.
// ------------------------------------------------------------
using System;

namespace RoseEngine
{
    /// <summary>
    /// 이 어트리뷰트가 붙은 메서드 또는 클래스는 콘솔 더블클릭 시 스택트레이스에서 건너뜁니다.
    /// <c>Debug.Log</c>를 래핑하는 커스텀 로거에 사용하세요.
    /// <example>
    /// <code>
    /// [HideInConsoleStackTrace]
    /// public static class MyLogger
    /// {
    ///     public static void Info(string msg) => Debug.Log($"[MyGame] {msg}");
    /// }
    /// </code>
    /// </example>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
    public sealed class HideInConsoleStackTraceAttribute : Attribute { }
}
