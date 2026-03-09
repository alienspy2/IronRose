using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// P/Invoke bindings for ImGui internal window functions not exposed by ImGui.NET.
    /// Used to keep floating overlay windows on top of docked panels.
    /// </summary>
    internal static class ImGuiWindowNative
    {
        private const string CImGuiLib = "cimgui";

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr igGetCurrentWindow();

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void igBringWindowToDisplayFront(IntPtr window);

        /// <summary>
        /// ImGui.Render() 직전에 display front로 올려야 할 윈도우 포인터 목록.
        /// 매 프레임 Begin() 직후에 등록하고, Render() 직전에 일괄 적용 후 초기화.
        /// </summary>
        private static readonly List<IntPtr> _pendingFrontWindows = new();

        /// <summary>
        /// 현재 ImGui 윈도우를 display order 최상단으로 올린다.
        /// Focus는 변경하지 않으므로, 다른 윈도우의 포커스를 빼앗지 않는다.
        /// ImGui.Begin() 호출 직후에 사용해야 한다.
        /// </summary>
        public static void BringCurrentWindowToDisplayFront()
        {
            var window = igGetCurrentWindow();
            if (window != IntPtr.Zero)
                igBringWindowToDisplayFront(window);
        }

        /// <summary>
        /// 현재 ImGui 윈도우를 "Render 직전 display front" 대기 목록에 등록한다.
        /// ImGui.Begin() 호출 직후에 사용. 실제 BringToFront는 FlushPendingFront()에서 수행.
        /// 이렇게 하면 dock host window의 focus 처리가 display order를 변경한 이후에
        /// overlay가 다시 최상단으로 올라가므로, docking Z-order 문제가 해결된다.
        /// </summary>
        public static void EnqueueCurrentWindowForDisplayFront()
        {
            var window = igGetCurrentWindow();
            if (window != IntPtr.Zero)
                _pendingFrontWindows.Add(window);
        }

        /// <summary>
        /// 대기 중인 모든 윈도우를 display front로 올리고 목록을 초기화한다.
        /// ImGui.Render() 직전에 호출해야 한다.
        /// </summary>
        public static void FlushPendingFront()
        {
            for (int i = 0; i < _pendingFrontWindows.Count; i++)
            {
                igBringWindowToDisplayFront(_pendingFrontWindows[i]);
            }
            _pendingFrontWindows.Clear();
        }
    }
}
