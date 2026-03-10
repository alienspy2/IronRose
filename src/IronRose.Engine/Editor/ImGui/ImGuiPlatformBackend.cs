using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Veldrid;
using Debug = RoseEngine.Debug;
using SilkGlfw = Silk.NET.GLFW;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// ImGui Platform IO 콜백을 Silk.NET 윈도우 API로 구현.
    /// 보조 뷰포트의 OS 윈도우 생명주기를 관리한다.
    ///
    /// 주의: 모든 delegate 시그니처는 IntPtr을 사용한다.
    /// ImGuiViewportPtr는 unsafe 포인터를 포함하는 구조체라 reverse P/Invoke 마샬링 시
    /// SIGSEGV를 일으킬 수 있으므로, IntPtr로 받아서 수동으로 ImGuiViewportPtr를 생성한다.
    /// </summary>
    internal sealed class ImGuiPlatformBackend : IDisposable
    {
        // cimgui P/Invoke: GetWindowPos/GetWindowSize는 C++에서 ImVec2를 by-value 반환하므로,
        // cimgui가 제공하는 전용 setter를 통해 등록해야 한다.
        // 직접 PlatformIO 필드에 쓰면 C++↔C 호출 규약 불일치로 SIGSEGV 발생.
        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ImGuiPlatformIO_Set_Platform_GetWindowPos(
            IntPtr platformIO, IntPtr funcPtr);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ImGuiPlatformIO_Set_Platform_GetWindowSize(
            IntPtr platformIO, IntPtr funcPtr);

        // 모든 콜백은 IntPtr (네이티브 ImGuiViewport*)로 받는다
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformCreateWindowDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformDestroyWindowDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformShowWindowDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformGetWindowPosDelegate(IntPtr vpPtr, IntPtr outPos);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformSetWindowPosDelegate(IntPtr vpPtr, Vector2 pos);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformGetWindowSizeDelegate(IntPtr vpPtr, IntPtr outSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformSetWindowSizeDelegate(IntPtr vpPtr, Vector2 size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformSetWindowFocusDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte PlatformGetWindowFocusDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte PlatformGetWindowMinimizedDelegate(IntPtr vpPtr);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PlatformSetWindowTitleDelegate(IntPtr vpPtr, IntPtr title);

        private readonly IWindow _mainWindow;
        private readonly GraphicsDevice _device;
        private ImGuiInputHandler? _inputHandler;

        // GCHandle로 delegate를 alive 유지 (GC 방지)
        private readonly List<GCHandle> _delegateHandles = new();

        // 뷰포트 ID → ViewportData 매핑
        private readonly Dictionary<uint, ViewportData> _viewportDataMap = new();

        // 메인 뷰포트 GCHandle
        private GCHandle _mainViewportHandle;

        // 포커스 추적: FocusChanged 이벤트를 통해 업데이트
        private readonly HashSet<IWindow> _focusedWindows = new();

        // GLFW API (모니터 열거용) — Silk.NET GLFW 바인딩 사용
        private SilkGlfw.Glfw? _glfw;

        // 모니터 정보 네이티브 메모리 (ImGui MemAlloc으로 할당)
        private IntPtr _monitorData;

        public ImGuiPlatformBackend(IWindow mainWindow, GraphicsDevice device)
        {
            _mainWindow = mainWindow;
            _device = device;
        }

        public void Initialize(ImGuiInputHandler? inputHandler)
        {
            _inputHandler = inputHandler;

            // GLFW API 획득 (모니터 열거용)
            try
            {
                _glfw = SilkGlfw.Glfw.GetApi();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ImGui] GLFW API unavailable — monitor info disabled: {ex.Message}");
            }

            // 메인 뷰포트(#0)에 ViewportData 등록
            var mainViewport = ImGui.GetMainViewport();
            var mainData = new ViewportData
            {
                Window = _mainWindow,
                WindowOwned = false,
            };
            _mainViewportHandle = GCHandle.Alloc(mainData);
            mainViewport.PlatformUserData = GCHandle.ToIntPtr(_mainViewportHandle);
            _viewportDataMap[mainViewport.ID] = mainData;

            // 메인 뷰포트에 네이티브 핸들 설정 — ImGui가 메인 윈도우를 추적하는 데 필요
            SetPlatformHandleRaw(mainViewport, _mainWindow);

            // 메인 윈도우 포커스 추적
            _focusedWindows.Add(_mainWindow); // 시작 시 메인 윈도우가 포커스 상태
            _mainWindow.FocusChanged += focused =>
            {
                if (focused) _focusedWindows.Add(_mainWindow);
                else _focusedWindows.Remove(_mainWindow);
            };

            // 콜백 등록
            var pio = ImGui.GetPlatformIO();
            pio.Platform_CreateWindow = PinDelegate<PlatformCreateWindowDelegate>(OnCreateWindow);
            pio.Platform_DestroyWindow = PinDelegate<PlatformDestroyWindowDelegate>(OnDestroyWindow);
            pio.Platform_ShowWindow = PinDelegate<PlatformShowWindowDelegate>(OnShowWindow);
            pio.Platform_SetWindowPos = PinDelegate<PlatformSetWindowPosDelegate>(OnSetWindowPos);
            pio.Platform_SetWindowSize = PinDelegate<PlatformSetWindowSizeDelegate>(OnSetWindowSize);
            pio.Platform_SetWindowFocus = PinDelegate<PlatformSetWindowFocusDelegate>(OnSetWindowFocus);
            pio.Platform_GetWindowFocus = PinDelegate<PlatformGetWindowFocusDelegate>(OnGetWindowFocus);
            pio.Platform_GetWindowMinimized = PinDelegate<PlatformGetWindowMinimizedDelegate>(OnGetWindowMinimized);
            pio.Platform_SetWindowTitle = PinDelegate<PlatformSetWindowTitleDelegate>(OnSetWindowTitle);

            // GetWindowPos/GetWindowSize는 cimgui의 전용 setter로 등록 (C++ return-by-value 호환)
            unsafe
            {
                ImGuiPlatformIO_Set_Platform_GetWindowPos(
                    (IntPtr)pio.NativePtr,
                    PinDelegate<PlatformGetWindowPosDelegate>(OnGetWindowPos));
                ImGuiPlatformIO_Set_Platform_GetWindowSize(
                    (IntPtr)pio.NativePtr,
                    PinDelegate<PlatformGetWindowSizeDelegate>(OnGetWindowSize));
            }

            // 모니터 정보 설정 — ImGui가 보조 뷰포트 배치에 사용
            UpdateMonitors();

            Debug.Log("[ImGui] Platform backend initialized");
        }

        /// <summary>delegate를 GCHandle로 pinning하고 함수 포인터를 반환.</summary>
        private IntPtr PinDelegate<T>(T callback) where T : Delegate
        {
            var handle = GCHandle.Alloc(callback);
            _delegateHandles.Add(handle);
            return Marshal.GetFunctionPointerForDelegate(callback);
        }

        // ── Helper: IntPtr → ImGuiViewportPtr 변환 ──

        private static unsafe ImGuiViewportPtr WrapViewport(IntPtr ptr)
            => new ImGuiViewportPtr((ImGuiViewport*)ptr);

        /// <summary>뷰포트에 네이티브 윈도우 핸들을 설정.</summary>
        private static void SetPlatformHandleRaw(ImGuiViewportPtr vp, IWindow? window)
        {
            if (window?.Native == null) return;
            var native = window.Native;
            if (native.X11.HasValue)
                vp.PlatformHandleRaw = (IntPtr)native.X11.Value.Window;
            else if (native.Win32.HasValue)
                vp.PlatformHandleRaw = native.Win32.Value.Hwnd;
            else if (native.Wayland.HasValue)
                vp.PlatformHandleRaw = native.Wayland.Value.Surface;
        }

        // ── Monitor & per-frame update ──

        /// <summary>GLFW 모니터 목록을 읽어 PlatformIO.Monitors에 설정.</summary>
        private void UpdateMonitors()
        {
            if (_glfw == null) return;

            try
            {
                unsafe
                {
                    var monitors = _glfw.GetMonitors(out int count);
                    if (monitors == null || count <= 0) return;

                    // 기존 데이터 해제
                    var pio = ImGui.GetPlatformIO();
                    if (_monitorData != IntPtr.Zero)
                    {
                        ImGui.MemFree(_monitorData);
                        _monitorData = IntPtr.Zero;
                    }

                    int structSize = sizeof(ImGuiPlatformMonitor);
                    _monitorData = ImGui.MemAlloc((uint)(count * structSize));
                    var pMon = (ImGuiPlatformMonitor*)_monitorData;

                    for (int i = 0; i < count; i++)
                    {
                        _glfw.GetMonitorPos(monitors[i], out int mx, out int my);
                        var vidMode = _glfw.GetVideoMode(monitors[i]);
                        _glfw.GetMonitorWorkarea(monitors[i], out int wx, out int wy, out int ww, out int wh);
                        _glfw.GetMonitorContentScale(monitors[i], out float xs, out float ys);

                        pMon[i].MainPos = new Vector2(mx, my);
                        pMon[i].MainSize = new Vector2(vidMode->Width, vidMode->Height);
                        pMon[i].WorkPos = (ww > 0 && wh > 0)
                            ? new Vector2(wx, wy)
                            : pMon[i].MainPos;
                        pMon[i].WorkSize = (ww > 0 && wh > 0)
                            ? new Vector2(ww, wh)
                            : pMon[i].MainSize;
                        pMon[i].DpiScale = xs > 0 ? xs : 1.0f;
                        pMon[i].PlatformHandle = null;
                    }

                    // ImVector는 readonly struct이므로 raw 포인터로 직접 기록
                    var rawVec = (int*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                        ref System.Runtime.CompilerServices.Unsafe.AsRef(in pio.NativePtr->Monitors));
                    rawVec[0] = count;                        // Size
                    rawVec[1] = count;                        // Capacity
                    *(IntPtr*)(rawVec + 2) = _monitorData;    // Data

                    Debug.Log($"[ImGui] Monitors updated: {count} monitor(s)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ImGui] Monitor enumeration failed: {ex.Message}");
            }
        }

        /// <summary>매 프레임 호출 — 모니터 변경 감지 (현재는 no-op, 필요 시 UpdateMonitors 호출).</summary>
        public void UpdatePerFrame()
        {
            // 현재는 모니터 핫플러그를 처리하지 않음.
            // 필요 시 여기서 UpdateMonitors()를 매 N프레임마다 호출할 수 있음.
        }

        // ── Callback implementations ──

        private void OnCreateWindow(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);

                var opts = WindowOptions.Default;
                opts.Size = new Vector2D<int>((int)vp.Size.X, (int)vp.Size.Y);
                opts.Position = new Vector2D<int>((int)vp.Pos.X, (int)vp.Pos.Y);
                opts.Title = "IronRose Viewport";
                opts.API = GraphicsAPI.None;
                opts.IsVisible = false;
                opts.ShouldSwapAutomatically = false;
                opts.SharedContext = null;

                if ((vp.Flags & ImGuiViewportFlags.NoDecoration) != 0)
                    opts.WindowBorder = WindowBorder.Hidden;

                var window = Window.Create(opts);
                window.Initialize();

                var data = new ViewportData
                {
                    Window = window,
                    WindowOwned = true,
                    InputContext = window.CreateInput(),
                };

                var handle = GCHandle.Alloc(data);
                vp.PlatformUserData = GCHandle.ToIntPtr(handle);
                _viewportDataMap[vp.ID] = data;

                // 보조 뷰포트에 네이티브 핸들 설정
                SetPlatformHandleRaw(vp, window);

                // 보조 윈도우 입력 등록
                if (data.InputContext != null && _inputHandler != null)
                    _inputHandler.AddSecondaryInput(data.InputContext, window);

                // 분리된 창을 항상 메인 창 위에 유지 (Always on Top)
                if (_glfw != null && window.Native?.Glfw != null)
                {
                    unsafe
                    {
                        var glfwHandle = (SilkGlfw.WindowHandle*)window.Native.Glfw.Value;
                        _glfw.SetWindowAttrib(glfwHandle, SilkGlfw.WindowAttributeSetter.Floating, true);
                    }
                }

                // 보조 윈도우 포커스 추적
                var capturedWin = window;
                window.FocusChanged += focused =>
                {
                    if (focused) _focusedWindows.Add(capturedWin);
                    else _focusedWindows.Remove(capturedWin);
                };

                Debug.Log($"[ImGui] Viewport created: {vp.ID} ({vp.Size.X}x{vp.Size.Y})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnCreateWindow failed: {ex}");
            }
        }

        private void OnDestroyWindow(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data == null) return;

                // 입력 해제
                if (data.InputContext != null && _inputHandler != null)
                    _inputHandler.RemoveSecondaryInput(data.InputContext);

                // 포커스 추적 해제
                if (data.Window != null)
                    _focusedWindows.Remove(data.Window);

                _viewportDataMap.Remove(vp.ID);
                data.Dispose();

                if (vp.PlatformUserData != IntPtr.Zero)
                {
                    var handle = GCHandle.FromIntPtr(vp.PlatformUserData);
                    if (handle.IsAllocated) handle.Free();
                    vp.PlatformUserData = IntPtr.Zero;
                }

                Debug.Log($"[ImGui] Viewport destroyed: {vp.ID}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnDestroyWindow failed: {ex}");
            }
        }

        private void OnShowWindow(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data?.Window != null)
                    data.Window.IsVisible = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnShowWindow failed: {ex}");
            }
        }

        private void OnGetWindowPos(IntPtr vpPtr, IntPtr outPos)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                unsafe
                {
                    var p = (float*)outPos;
                    if (data?.Window != null)
                    {
                        p[0] = data.Window.Position.X;
                        p[1] = data.Window.Position.Y;
                    }
                    else
                    {
                        p[0] = 0f;
                        p[1] = 0f;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnGetWindowPos failed: {ex}");
            }
        }

        private void OnSetWindowPos(IntPtr vpPtr, Vector2 pos)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data?.Window != null)
                    data.Window.Position = new Vector2D<int>((int)pos.X, (int)pos.Y);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnSetWindowPos failed: {ex}");
            }
        }

        private void OnGetWindowSize(IntPtr vpPtr, IntPtr outSize)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                unsafe
                {
                    var p = (float*)outSize;
                    if (data?.Window != null)
                    {
                        p[0] = data.Window.Size.X;
                        p[1] = data.Window.Size.Y;
                    }
                    else
                    {
                        p[0] = 0f;
                        p[1] = 0f;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnGetWindowSize failed: {ex}");
            }
        }

        private void OnSetWindowSize(IntPtr vpPtr, Vector2 size)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data?.Window != null)
                    data.Window.Size = new Vector2D<int>((int)size.X, (int)size.Y);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnSetWindowSize failed: {ex}");
            }
        }

        private void OnSetWindowFocus(IntPtr vpPtr)
        {
            // Silk.NET IWindow에 Focus()가 없으므로 no-op
        }

        private byte OnGetWindowFocus(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data?.Window == null) return 0;
                return (byte)(_focusedWindows.Contains(data.Window) ? 1 : 0);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnGetWindowFocus failed: {ex}");
                return 0;
            }
        }

        private byte OnGetWindowMinimized(IntPtr vpPtr)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data?.Window == null) return 0;
                return (byte)(data.Window.WindowState == WindowState.Minimized ? 1 : 0);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnGetWindowMinimized failed: {ex}");
                return 0;
            }
        }

        private void OnSetWindowTitle(IntPtr vpPtr, IntPtr title)
        {
            try
            {
                var vp = WrapViewport(vpPtr);
                var data = GetViewportData(vp);
                if (data?.Window != null)
                    data.Window.Title = Marshal.PtrToStringUTF8(title) ?? "IronRose Viewport";
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ImGui] OnSetWindowTitle failed: {ex}");
            }
        }

        // ── Helpers ──

        public ViewportData? GetViewportData(ImGuiViewportPtr vp)
        {
            if (vp.PlatformUserData == IntPtr.Zero) return null;
            var handle = GCHandle.FromIntPtr(vp.PlatformUserData);
            return handle.IsAllocated ? handle.Target as ViewportData : null;
        }

        public void Dispose()
        {
            // 보조 뷰포트 정리
            foreach (var kvp in _viewportDataMap)
            {
                if (kvp.Value.WindowOwned)
                    kvp.Value.Dispose();
            }
            _viewportDataMap.Clear();
            _focusedWindows.Clear();

            // 모니터 네이티브 메모리 해제
            if (_monitorData != IntPtr.Zero)
            {
                ImGui.MemFree(_monitorData);
                _monitorData = IntPtr.Zero;
            }

            // 메인 뷰포트 GCHandle 해제
            if (_mainViewportHandle.IsAllocated)
                _mainViewportHandle.Free();

            // delegate GCHandle 해제
            foreach (var h in _delegateHandles)
            {
                if (h.IsAllocated) h.Free();
            }
            _delegateHandles.Clear();

            // GLFW API는 Dispose하지 않음 — Silk.NET Windowing이 관리
        }
    }
}
