using System;
using System.Collections.Generic;

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Windowing;
using Veldrid;
using Debug = RoseEngine.EditorDebug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// ImGui Renderer IO 콜백을 Veldrid로 구현.
    /// 보조 뷰포트마다 별도 Swapchain을 생성하고,
    /// 기존 VeldridImGuiRenderer.Render()를 재사용한다.
    ///
    /// delegate 시그니처는 IntPtr을 사용 (ImGuiViewportPtr 마샬링 문제 방지).
    /// </summary>
    internal sealed class ImGuiRendererBackend : IDisposable
    {
        // Renderer callback delegates — IntPtr로 ImGuiViewport* 수신
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RendererCreateWindowDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RendererDestroyWindowDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RendererSetWindowSizeDelegate(IntPtr vpPtr, Vector2 size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RendererRenderWindowDelegate(IntPtr vpPtr, IntPtr renderArg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RendererSwapBuffersDelegate(IntPtr vpPtr, IntPtr renderArg);

        private readonly GraphicsDevice _device;
        private readonly VeldridImGuiRenderer _renderer;
        private readonly ImGuiPlatformBackend _platformBackend;
        private readonly List<GCHandle> _delegateHandles = new();

        public ImGuiRendererBackend(
            GraphicsDevice device,
            VeldridImGuiRenderer renderer,
            ImGuiPlatformBackend platformBackend)
        {
            _device = device;
            _renderer = renderer;
            _platformBackend = platformBackend;
        }

        public void Initialize()
        {
            var pio = ImGui.GetPlatformIO();
            pio.Renderer_CreateWindow = PinDelegate<RendererCreateWindowDelegate>(OnRendererCreateWindow);
            pio.Renderer_DestroyWindow = PinDelegate<RendererDestroyWindowDelegate>(OnRendererDestroyWindow);
            pio.Renderer_SetWindowSize = PinDelegate<RendererSetWindowSizeDelegate>(OnRendererSetWindowSize);
            pio.Renderer_RenderWindow = PinDelegate<RendererRenderWindowDelegate>(OnRendererRenderWindow);
            pio.Renderer_SwapBuffers = PinDelegate<RendererSwapBuffersDelegate>(OnRendererSwapBuffers);

            Debug.Log("[ImGui] Renderer backend initialized");
        }

        private IntPtr PinDelegate<T>(T callback) where T : Delegate
        {
            var handle = GCHandle.Alloc(callback);
            _delegateHandles.Add(handle);
            return Marshal.GetFunctionPointerForDelegate(callback);
        }

        private static unsafe ImGuiViewportPtr WrapViewport(IntPtr ptr)
            => new ImGuiViewportPtr((ImGuiViewport*)ptr);

        // ── Callback implementations ──

        private void OnRendererCreateWindow(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = _platformBackend.GetViewportData(vp);
                if (data?.Window == null) return;

                var swapchainSource = GetSwapchainSource(data.Window);
                var scDesc = new SwapchainDescription(
                    swapchainSource,
                    (uint)data.Window.Size.X,
                    (uint)data.Window.Size.Y,
                    PixelFormat.D32_Float_S8_UInt,
                    false,
                    false
                );

                data.Swapchain = _device.ResourceFactory.CreateSwapchain(scDesc);
                data.CommandList = _device.ResourceFactory.CreateCommandList();

                Debug.Log($"[ImGui] Renderer: swapchain created for viewport {vp.ID}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnRendererCreateWindow failed: {ex}");
            }
        }

        private void OnRendererDestroyWindow(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = _platformBackend.GetViewportData(vp);
                if (data == null) return;

                _device.WaitForIdle();

                data.CommandList?.Dispose();
                data.CommandList = null;
                data.Swapchain?.Dispose();
                data.Swapchain = null;

                Debug.Log($"[ImGui] Renderer: swapchain destroyed for viewport {vp.ID}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnRendererDestroyWindow failed: {ex}");
            }
        }

        private void OnRendererSetWindowSize(IntPtr vpPtr, Vector2 size)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = _platformBackend.GetViewportData(vp);
                if (data?.Swapchain == null) return;

                var w = (uint)Math.Max(size.X, 1);
                var h = (uint)Math.Max(size.Y, 1);
                data.Swapchain.Resize(w, h);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnRendererSetWindowSize failed: {ex}");
            }
        }

        private void OnRendererRenderWindow(IntPtr vpPtr, IntPtr renderArg)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = _platformBackend.GetViewportData(vp);
                if (data?.Swapchain == null || data.CommandList == null) return;

                // 공유 vertex/index 버퍼 충돌 방지 — 이전 CL이 완료될 때까지 대기
                _device.WaitForIdle();

                var cl = data.CommandList;
                cl.Begin();
                cl.SetFramebuffer(data.Swapchain.Framebuffer);
                cl.ClearColorTarget(0, new RgbaFloat(0f, 0f, 0f, 1f));
                cl.SetFullViewports();

                unsafe
                {
                    if ((IntPtr)vp.DrawData.NativePtr != IntPtr.Zero && vp.DrawData.CmdListsCount > 0)
                        _renderer.Render(cl, vp.DrawData);
                }

                cl.End();
                _device.SubmitCommands(cl);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnRendererRenderWindow failed: {ex}");
            }
        }

        private void OnRendererSwapBuffers(IntPtr vpPtr, IntPtr renderArg)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = _platformBackend.GetViewportData(vp);
                if (data?.Swapchain != null)
                    _device.SwapBuffers(data.Swapchain);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnRendererSwapBuffers failed: {ex}");
            }
        }

        // ── Helpers ──

        private static SwapchainSource GetSwapchainSource(IWindow window)
        {
            var native = window.Native
                ?? throw new PlatformNotSupportedException("Cannot get native window handle");

            if (native.Win32.HasValue)
            {
                var w = native.Win32.Value;
                return SwapchainSource.CreateWin32(w.Hwnd, w.HInstance);
            }

            if (native.X11.HasValue)
            {
                var x = native.X11.Value;
                return SwapchainSource.CreateXlib(x.Display, (nint)x.Window);
            }

            if (native.Wayland.HasValue)
            {
                var w = native.Wayland.Value;
                return SwapchainSource.CreateWayland(w.Display, w.Surface);
            }

            throw new PlatformNotSupportedException("Unsupported windowing platform");
        }

        public void Dispose()
        {
            foreach (var h in _delegateHandles)
            {
                if (h.IsAllocated) h.Free();
            }
            _delegateHandles.Clear();
        }
    }
}
