using System;
using System.Runtime.InteropServices;

namespace RoseEngine
{
    /// <summary>
    /// OS 시스템 클립보드 접근 (GLFW 백엔드).
    /// EngineCore 초기화 시 Initialize()로 GLFW 윈도우 핸들 등록 필요.
    /// </summary>
    public static class SystemClipboard
    {
        private static nint _glfwWindow;

        internal static void Initialize(nint glfwWindowHandle)
        {
            _glfwWindow = glfwWindowHandle;
        }

        /// <summary>시스템 클립보드에서 텍스트를 가져온다.</summary>
        public static string GetText()
        {
            if (_glfwWindow == 0) return "";
            try
            {
                unsafe
                {
                    var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
                    return glfw.GetClipboardString((Silk.NET.GLFW.WindowHandle*)_glfwWindow) ?? "";
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>시스템 클립보드에 텍스트를 설정한다.</summary>
        public static void SetText(string text)
        {
            if (_glfwWindow == 0 || text == null) return;
            try
            {
                unsafe
                {
                    var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
                    glfw.SetClipboardString((Silk.NET.GLFW.WindowHandle*)_glfwWindow, text);
                }
            }
            catch
            {
                // Clipboard access failed silently
            }
        }
    }
}
