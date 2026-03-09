using System;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Veldrid;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// 뷰포트별 상태. ImGuiViewport.PlatformUserData에 GCHandle로 저장.
    /// OS 윈도우, 입력 컨텍스트, GPU 리소스를 추적한다.
    /// </summary>
    internal sealed class ViewportData : IDisposable
    {
        public IWindow? Window { get; set; }
        public IInputContext? InputContext { get; set; }

        // Renderer 리소스
        public Swapchain? Swapchain { get; set; }
        public CommandList? CommandList { get; set; }

        // true = 우리가 생성한 보조 윈도우, false = 메인 윈도우
        public bool WindowOwned { get; set; }

        public void Dispose()
        {
            CommandList?.Dispose();
            CommandList = null;

            Swapchain?.Dispose();
            Swapchain = null;

            InputContext?.Dispose();
            InputContext = null;

            if (WindowOwned && Window != null)
            {
                Window.Reset();
                Window.Close();
            }
            Window = null;
        }
    }
}
