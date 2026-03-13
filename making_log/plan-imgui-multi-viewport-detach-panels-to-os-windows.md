# ImGui Multi-Viewport 구현 계획

> ImGui 패널을 메인 윈도우 밖으로 분리하여 별도 OS 윈도우로 렌더링

---

## 현재 상태 분석

| 요소 | 현황 | 파일 |
|------|------|------|
| ImGui.NET | v1.91.0.1 — multi-viewport API 포함 | `IronRose.Engine.csproj` |
| DockingEnable | ✅ 활성화 | `ImGuiOverlay.cs:227` |
| ViewportsEnable | ❌ **비활성화** | `ImGuiOverlay.cs:227` |
| Platform IO 콜백 | ❌ **미구현** | — |
| Renderer IO 콜백 | ❌ **미구현** | — |
| 렌더러 | 단일 OutputDescription 파이프라인 | `VeldridImGuiRenderer.cs:44` |
| 입력 핸들러 | 단일 IInputContext | `ImGuiInputHandler.cs` |
| 윈도우 백엔드 | Silk.NET 2.23.0 (다중 윈도우 가능) | `Program.cs` |
| 그래픽스 | Veldrid 4.9.0, Vulkan, 단일 Swapchain | `GraphicsManager.cs:64` |

### 핵심 파일 현황

**ImGuiOverlay.cs** — ImGui 생명주기 관리
- `Initialize()` (line 217): 단일 컨텍스트 생성, `DockingEnable`만 설정
- `Render()` (line 701-717): `ImGui.GetDrawData()` 한 번 호출, `_device.SwapchainFramebuffer`에만 렌더
- `UpdatePlatformWindows()` / `RenderPlatformWindowsDefault()` 호출 없음

**VeldridImGuiRenderer.cs** — Veldrid 기반 ImGui 드로우
- 생성자 (line 44): 메인 스왑체인의 `OutputDescription`으로 단일 파이프라인 생성
- `Render(CommandList, ImDrawDataPtr)` (line 173): 현재 설정된 프레임버퍼에 그리기 → **뷰포트 무관하게 재사용 가능**
- 텍스처 바인딩 캐시: `Dictionary<IntPtr, ResourceSet>`

**GraphicsManager.cs** — 그래픽스 디바이스 관리
- `GraphicsDevice.CreateVulkan()` (line 64): 단일 디바이스 + 단일 스왑체인
- `GetSwapchainSource(IWindow)` (line 81-105): Win32/X11/Wayland 지원 → **보조 윈도우에 재사용 가능**
- Veldrid는 `ResourceFactory.CreateSwapchain()`으로 추가 스왑체인 생성 가능

**ImGuiInputHandler.cs** — 입력 이벤트 → ImGui IO 전달
- `Initialize(IInputContext)`: 단일 `IInputContext`의 키보드/마우스 이벤트 구독
- 모든 마우스 좌표가 로컬 윈도우 기준 (절대 좌표 변환 없음)

**ImGuiDockBuilderNative.cs** — cimgui P/Invoke 바인딩 패턴
- `[DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]` 패턴
- 뷰포트 관련 추가 바인딩 시 이 패턴을 따름

---

## Step 1: ViewportData 클래스 (뷰포트별 상태)

**신규**: `src/IronRose.Engine/Editor/ImGui/ViewportData.cs` (~80줄)

뷰포트마다 OS 윈도우, 입력 컨텍스트, GPU 리소스를 추적하는 데이터 클래스.

```csharp
using System;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Veldrid;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// 뷰포트별 상태. ImGuiViewport.PlatformUserData에 GCHandle로 저장.
    /// </summary>
    internal sealed class ViewportData : IDisposable
    {
        public IWindow? Window { get; set; }
        public IInputContext? InputContext { get; set; }

        // Renderer 리소스 (Step 3에서 설정)
        public Swapchain? Swapchain { get; set; }
        public CommandList? CommandList { get; set; }

        // true = 우리가 생성한 보조 윈도우, false = 메인 윈도우
        public bool WindowOwned { get; set; }

        public void Dispose()
        {
            CommandList?.Dispose();
            Swapchain?.Dispose();
            InputContext?.Dispose();

            if (WindowOwned)
            {
                Window?.Reset();
                Window?.Close();
            }

            Window = null;
            InputContext = null;
            Swapchain = null;
            CommandList = null;
        }
    }
}
```

### 설계 결정
- Platform + Renderer 데이터를 한 클래스에 통합 (분리하면 GCHandle 관리가 2배)
- `WindowOwned` 플래그로 메인 뷰포트(ImGui가 자동 생성)와 보조 뷰포트 구분
- `Dispose()`에서 GPU 리소스 → 입력 → 윈도우 순서로 정리

### 의존성
없음 — 독립 데이터 클래스

---

## Step 2: ImGuiPlatformBackend (Platform IO 콜백)

**신규**: `src/IronRose.Engine/Editor/ImGui/ImGuiPlatformBackend.cs` (~350줄)

Silk.NET 윈도우 API로 ImGui Platform IO 콜백 10개를 구현한다.

### 콜백 목록

| 콜백 | 역할 | Silk.NET API |
|------|------|-------------|
| `Platform_CreateWindow` | 보조 OS 윈도우 생성 | `Window.Create()` + `Initialize()` |
| `Platform_DestroyWindow` | 윈도우 파괴 + 정리 | `ViewportData.Dispose()` |
| `Platform_ShowWindow` | 윈도우 표시 | `window.IsVisible = true` |
| `Platform_GetWindowPos` | 윈도우 위치 조회 | `window.Position` |
| `Platform_SetWindowPos` | 윈도우 위치 설정 | `window.Position = ...` |
| `Platform_GetWindowSize` | 윈도우 크기 조회 | `window.Size` |
| `Platform_SetWindowSize` | 윈도우 크기 설정 | `window.Size = ...` |
| `Platform_SetWindowFocus` | 윈도우 포커스 | GLFW `glfwFocusWindow()` |
| `Platform_GetWindowFocus` | 포커스 상태 조회 | GLFW `glfwGetWindowAttrib(FOCUSED)` |
| `Platform_GetWindowMinimized` | 최소화 여부 | `window.WindowState == Minimized` |
| `Platform_SetWindowTitle` | 제목 설정 | `window.Title = ...` |

### 코드 구조

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Veldrid;
using Debug = RoseEngine.Debug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    internal sealed class ImGuiPlatformBackend : IDisposable
    {
        private readonly IWindow _mainWindow;
        private readonly GraphicsDevice _device;
        private readonly Dictionary<uint, ViewportData> _viewportDataMap = new();

        public ImGuiPlatformBackend(IWindow mainWindow, GraphicsDevice device) { ... }

        public unsafe void Initialize()
        {
            var platformIO = ImGui.GetPlatformIO();

            // 메인 뷰포트(#0)에 ViewportData 등록
            var mainViewport = ImGui.GetMainViewport();
            var mainData = new ViewportData { Window = _mainWindow, WindowOwned = false };
            var mainHandle = GCHandle.Alloc(mainData);
            mainViewport.PlatformUserData = (IntPtr)mainHandle;
            _viewportDataMap[mainViewport.ID] = mainData;

            // 콜백 등록
            platformIO.Platform_CreateWindow  = CreateWindow;
            platformIO.Platform_DestroyWindow = DestroyWindow;
            platformIO.Platform_ShowWindow    = ShowWindow;
            platformIO.Platform_SetWindowPos  = SetWindowPos;
            platformIO.Platform_GetWindowPos  = GetWindowPos;
            platformIO.Platform_SetWindowSize = SetWindowSize;
            platformIO.Platform_GetWindowSize = GetWindowSize;
            platformIO.Platform_SetWindowFocus    = SetWindowFocus;
            platformIO.Platform_GetWindowFocus    = GetWindowFocus;
            platformIO.Platform_GetWindowMinimized = GetWindowMinimized;
            platformIO.Platform_SetWindowTitle = SetWindowTitle;
        }

        // ── CreateWindow ──
        private unsafe void CreateWindow(ImGuiViewportPtr vp)
        {
            var opts = WindowOptions.DefaultVulkan;
            opts.Size = new Vector2D<int>((int)vp.Size.X, (int)vp.Size.Y);
            opts.Position = new Vector2D<int>((int)vp.Pos.X, (int)vp.Pos.Y);
            opts.Title = "IronRose Viewport";
            opts.API = GraphicsAPI.None;          // Vulkan은 Veldrid가 관리
            opts.IsVisible = false;
            opts.ShouldSwapAutomatically = false;

            // ImGui가 제목표시줄 관리하는 경우 장식 없는 윈도우
            if ((vp.Flags & ImGuiViewportFlags.NoDecoration) != 0)
                opts.WindowBorder = WindowBorder.Hidden;

            var window = Window.Create(opts);
            window.Initialize();  // 네이티브 핸들 확보 (Swapchain 생성에 필요)

            var data = new ViewportData
            {
                Window = window,
                WindowOwned = true,
                InputContext = window.CreateInput(),
            };

            var handle = GCHandle.Alloc(data);
            vp.PlatformUserData = (IntPtr)handle;
            _viewportDataMap[vp.ID] = data;

            Debug.Log($"[ImGui] Viewport created: {vp.ID} ({vp.Size.X}x{vp.Size.Y})");
        }

        // ── DestroyWindow ──
        private unsafe void DestroyWindow(ImGuiViewportPtr vp)
        {
            if (vp.PlatformUserData == IntPtr.Zero) return;
            var handle = GCHandle.FromIntPtr(vp.PlatformUserData);
            if (handle.Target is ViewportData data)
            {
                _viewportDataMap.Remove(vp.ID);
                data.Dispose();
            }
            handle.Free();
            vp.PlatformUserData = IntPtr.Zero;
            Debug.Log($"[ImGui] Viewport destroyed: {vp.ID}");
        }

        // ── 위치/크기/포커스 콜백 ──
        // (각 콜백은 ViewportData.Window의 Silk.NET 프로퍼티를 읽기/쓰기)
        // SetWindowFocus / GetWindowFocus만 GLFW P/Invoke 필요:
        //   var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
        //   glfw.FocusWindow((WindowHandle*)data.Window.Native.Glfw.Value);

        // ── Helper ──
        public ViewportData? GetViewportData(ImGuiViewportPtr vp) { ... }
    }
}
```

### 핵심 주의사항
- `Window.Create()` + `Initialize()`는 **메인 스레드**에서만 가능 → ImGui 콜백이 메인 스레드에서 호출되므로 OK
- `opts.API = GraphicsAPI.None` → GLFW가 GL 컨텍스트를 만들지 않음 (Vulkan은 Veldrid 관리)
- GCHandle로 `ViewportData`를 alive 유지해야 GC가 수거하지 않음
- `SetWindowFocus` / `GetWindowFocus`는 `Silk.NET.GLFW.GlfwProvider`로 직접 GLFW API 호출

### 의존성
- Step 1 (ViewportData)

---

## Step 3: ImGuiRendererBackend (Renderer IO 콜백)

**신규**: `src/IronRose.Engine/Editor/ImGui/ImGuiRendererBackend.cs` (~250줄)

Veldrid로 Renderer IO 콜백 5개를 구현한다. 핵심은 보조 뷰포트마다 **별도 Swapchain**을 생성하고, 기존 `VeldridImGuiRenderer.Render()`를 재사용하는 것.

### 콜백 목록

| 콜백 | 역할 | Veldrid API |
|------|------|------------|
| `Renderer_CreateWindow` | 보조 뷰포트용 Swapchain + CL 생성 | `ResourceFactory.CreateSwapchain()` |
| `Renderer_DestroyWindow` | GPU 리소스 정리 | `Swapchain.Dispose()` |
| `Renderer_SetWindowSize` | Swapchain 리사이즈 | `Swapchain.Resize()` |
| `Renderer_RenderWindow` | 뷰포트 DrawData 렌더 | `VeldridImGuiRenderer.Render()` |
| `Renderer_SwapBuffers` | 뷰포트별 Present | `Swapchain.SwapBuffers()` ← 개별 present |

### 코드 구조

```csharp
using System;
using ImGuiNET;
using Silk.NET.Windowing;
using Veldrid;
using Debug = RoseEngine.Debug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    internal sealed class ImGuiRendererBackend : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly VeldridImGuiRenderer _renderer;
        private readonly ImGuiPlatformBackend _platformBackend;

        public ImGuiRendererBackend(
            GraphicsDevice device,
            VeldridImGuiRenderer renderer,
            ImGuiPlatformBackend platformBackend) { ... }

        public void Initialize()
        {
            var platformIO = ImGui.GetPlatformIO();
            platformIO.Renderer_CreateWindow  = RendererCreateWindow;
            platformIO.Renderer_DestroyWindow = RendererDestroyWindow;
            platformIO.Renderer_SetWindowSize = RendererSetWindowSize;
            platformIO.Renderer_RenderWindow  = RendererRenderWindow;
            platformIO.Renderer_SwapBuffers   = RendererSwapBuffers;
        }

        // ── RendererCreateWindow ──
        private void RendererCreateWindow(ImGuiViewportPtr vp)
        {
            var data = _platformBackend.GetViewportData(vp);
            if (data?.Window == null) return;

            // GraphicsManager.GetSwapchainSource() 패턴 재사용
            var swapchainSource = GetSwapchainSource(data.Window);
            var scDesc = new SwapchainDescription(
                swapchainSource,
                (uint)data.Window.Size.X,
                (uint)data.Window.Size.Y,
                null,    // depth 불필요 (ImGui 뷰포트)
                false,   // vsync off
                false    // no sRGB
            );

            data.Swapchain = _device.ResourceFactory.CreateSwapchain(scDesc);
            data.CommandList = _device.ResourceFactory.CreateCommandList();
            Debug.Log($"[ImGui] Renderer: swapchain created for viewport {vp.ID}");
        }

        // ── RendererRenderWindow ──
        // 핵심: 기존 VeldridImGuiRenderer.Render()를 그대로 사용
        private void RendererRenderWindow(ImGuiViewportPtr vp, IntPtr renderArg)
        {
            var data = _platformBackend.GetViewportData(vp);
            if (data?.Swapchain == null || data.CommandList == null) return;

            var cl = data.CommandList;
            cl.Begin();
            cl.SetFramebuffer(data.Swapchain.Framebuffer);
            cl.ClearColorTarget(0, new RgbaFloat(0f, 0f, 0f, 1f));
            cl.SetFullViewports();

            _renderer.Render(cl, vp.DrawData);  // ← 뷰포트별 DrawData

            cl.End();
            _device.SubmitCommands(cl);
        }

        // ── RendererSwapBuffers ──
        private void RendererSwapBuffers(ImGuiViewportPtr vp, IntPtr renderArg)
        {
            var data = _platformBackend.GetViewportData(vp);
            data?.Swapchain?.SwapBuffers();  // 개별 뷰포트 present
        }

        // GetSwapchainSource() — GraphicsManager.cs:81-105 복제
        private static SwapchainSource GetSwapchainSource(IWindow window) { ... }
    }
}
```

### 파이프라인 호환성

`VeldridImGuiRenderer`의 파이프라인은 메인 스왑체인의 `OutputDescription`으로 생성됨.
Vulkan에서 동일 디바이스의 모든 스왑체인은 같은 서피스 포맷(`B8G8R8A8_UNorm`)을 사용하므로 **파이프라인 공유 가능**.

만약 포맷이 다른 환경이 발견되면 per-viewport 파이프라인 생성이 필요 (리스크 항목 참조).

### GetSwapchainSource 중복 해결

`GraphicsManager.GetSwapchainSource()`가 `private static`이므로 두 가지 선택지:
1. **복제** — 현재 계획. 단순하지만 코드 중복
2. **리팩터** — `GraphicsManager.GetSwapchainSource()`를 `internal static`으로 변경 → 후속 작업으로

### 의존성
- Step 1 (ViewportData)
- Step 2 (ImGuiPlatformBackend — `GetViewportData()`)

---

## Step 4: ImGuiOverlay 통합

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiOverlay.cs`

### 4-A. 필드 추가

```csharp
// line 30 부근
private ImGuiPlatformBackend? _platformBackend;
private ImGuiRendererBackend? _rendererBackend;
```

### 4-B. Initialize() — ViewportsEnable 플래그

```csharp
// line 227 변경
io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;

// 뷰포트 모드 스타일 조정
if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
{
    var style = ImGui.GetStyle();
    style.WindowRounding = 0.0f;
    style.Colors[(int)ImGuiCol.WindowBg].W = 1.0f;  // 불투명
}
```

### 4-C. Initialize() 끝에 백엔드 초기화

```csharp
// _renderer 생성 이후 (line 291 이후)
_platformBackend = new ImGuiPlatformBackend(_window, device);
_platformBackend.Initialize();
_rendererBackend = new ImGuiRendererBackend(device, _renderer!, _platformBackend);
_rendererBackend.Initialize();
Debug.Log("[ImGui] Multi-Viewport backends initialized");
```

### 4-D. Render() 수정

```csharp
public void Render(CommandList cl)
{
    if (!IsVisible) return;

    ImGui.SetCurrentContext(_context);
    ImGui.Render();

    // 메인 뷰포트 렌더 (기존 로직)
    var drawData = ImGui.GetDrawData();
    if (drawData.CmdListsCount > 0)
    {
        cl.SetFramebuffer(_device.SwapchainFramebuffer);
        cl.ClearColorTarget(0, new RgbaFloat(0.902f, 0.863f, 0.824f, 1f));
        cl.ClearDepthStencil(1f);
        cl.SetFullViewports();
        _renderer?.Render(cl, drawData);
    }

    // 보조 뷰포트 업데이트 + 렌더
    var io = ImGui.GetIO();
    if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
    {
        ImGui.UpdatePlatformWindows();
        ImGui.RenderPlatformWindowsDefault();
    }
}
```

### 4-E. Dispose() 수정

```csharp
// 기존 _inputHandler?.Dispose() 뒤에 추가
_rendererBackend?.Dispose();
_platformBackend?.Dispose();
```

**정리 순서**: RendererBackend → PlatformBackend → Renderer → ImGui.DestroyContext()

### 의존성
- Step 1, 2, 3

---

## Step 5: 입력 라우팅

**수정**: `src/IronRose.Engine/Editor/ImGui/ImGuiInputHandler.cs`

현재 단일 `IInputContext`만 처리한다. 보조 뷰포트의 입력을 처리하려면:

### 5-A. ImGuiInputHandler 확장

```csharp
private IWindow? _mainWindow;
private readonly List<(IInputContext ctx, IWindow win)> _secondaryInputs = new();

// Initialize 시그니처 변경
public void Initialize(IInputContext inputContext, IWindow mainWindow)
{
    _mainWindow = mainWindow;
    // ... 기존 코드 ...
}

// 보조 뷰포트 입력 등록
public void AddSecondaryInput(IInputContext ctx, IWindow window)
{
    _secondaryInputs.Add((ctx, window));
    foreach (var kb in ctx.Keyboards)
    {
        kb.KeyDown += OnKeyDown;
        kb.KeyUp += OnKeyUp;
        kb.KeyChar += OnKeyChar;
    }
    foreach (var mouse in ctx.Mice)
    {
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.MouseMove += (m, localPos) =>
        {
            // 로컬 → 절대 스크린 좌표
            float absX = window.Position.X + localPos.X;
            float absY = window.Position.Y + localPos.Y;
            ImGui.GetIO().AddMousePosEvent(absX, absY);
        };
        mouse.Scroll += OnScroll;
    }
}

// 보조 뷰포트 입력 해제
public void RemoveSecondaryInput(IInputContext ctx) { ... }
```

### 5-B. 메인 윈도우 마우스 좌표도 절대 좌표로

뷰포트 활성화 시 ImGui는 **절대 스크린 좌표**를 기대한다:

```csharp
private void OnMouseMove(IMouse mouse, Vector2 pos)
{
    var io = ImGui.GetIO();
    if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
    {
        // 뷰포트 모드: 절대 좌표
        float absX = _mainWindow!.Position.X + pos.X;
        float absY = _mainWindow!.Position.Y + pos.Y;
        io.AddMousePosEvent(absX, absY);
    }
    else
    {
        io.AddMousePosEvent(pos.X, pos.Y);
    }
}
```

### 5-C. PlatformBackend에서 입력 등록/해제 호출

```csharp
// CreateWindow() 끝에
if (data.InputContext != null)
    _inputHandler.AddSecondaryInput(data.InputContext, window);

// DestroyWindow() 시작에
if (data.InputContext != null)
    _inputHandler.RemoveSecondaryInput(data.InputContext);
```

→ `ImGuiPlatformBackend`가 `ImGuiInputHandler` 참조를 받아야 함 (생성자에 추가)

### 5-D. ImGuiOverlay.Initialize() 호출 수정

```csharp
// 기존
_inputHandler.Initialize(inputContext);
// 변경
_inputHandler.Initialize(inputContext, window);
```

### 의존성
- Step 2 (PlatformBackend에서 입력 등록 호출)
- Step 4 (Initialize 시그니처 변경)

---

## Step 6: 안정화

### 6-A. GLFW 이벤트 루프 통합

GLFW `glfwPollEvents()`는 **모든 윈도우**의 이벤트를 한 번에 처리한다.
Silk.NET의 메인 `Window.Run()` 루프에서 `glfwPollEvents()`가 호출되므로,
보조 윈도우도 자동으로 이벤트를 받는다.

단, Silk.NET의 `IWindow` 콜백 라우팅이 보조 윈도우에도 정상 동작하는지 검증 필요.
문제가 있으면 각 보조 윈도우에 `window.DoEvents()` 명시 호출.

### 6-B. DPI / 스케일 처리

초기 구현은 DPI 1.0 고정:

```csharp
// Platform_GetWindowDpiScale (optional)
private float GetWindowDpiScale(ImGuiViewportPtr vp)
{
    return 1.0f;  // 초기 구현 — 추후 per-monitor DPI 지원
}
```

### 6-C. 윈도우 투명도

```csharp
// Platform_SetWindowAlpha (optional)
private void SetWindowAlpha(ImGuiViewportPtr vp, float alpha)
{
    // no-op — GLFW glfwSetWindowOpacity() 매핑 가능하지만 초기 구현에서는 생략
}
```

### 6-D. EditorState / 레이아웃 저장

ImGui `ViewportsEnable` 활성화 시 `imgui.ini`에 뷰포트 위치/크기가 자동 저장됨.
현재 `ImGuiLayoutManager`의 수동 INI 관리와 충돌 가능성 검토 필요.

→ 보조 뷰포트의 위치/크기는 ImGui 자동 관리에 위임, `ImGuiLayoutManager`는 도킹 레이아웃만 관리

### 6-E. 리소스 정리 순서

```
Dispose 순서:
1. _rendererBackend.Dispose()   — 보조 swapchain / CommandList 정리
2. _platformBackend.Dispose()   — 보조 윈도우 닫기, GCHandle 해제
3. _renderer.Dispose()          — 메인 렌더러 (파이프라인, 폰트 등)
4. ImGui.DestroyContext()       — ImGui 컨텍스트 파괴
```

---

## 리스크 평가

| 리스크 | 심각도 | 완화 방안 |
|--------|--------|-----------|
| Silk.NET `Window.Create()` + `Initialize()`가 GLFW 컨텍스트 공유 문제 | 높음 | `GraphicsAPI.None`으로 GL 컨텍스트 생성 방지 |
| 보조 Swapchain `OutputDescription`이 메인과 불일치 → 파이프라인 크래시 | 중간 | Vulkan은 동일 포맷 예상. 다르면 per-viewport 파이프라인 생성 |
| GLFW 보조 윈도우 이벤트가 Silk.NET 콜백으로 라우팅 안 됨 | 높음 | `window.DoEvents()` 명시 호출 또는 GLFW raw API 직접 사용 |
| `Window.Create()` / `Initialize()`가 메인 스레드에서만 가능 | 높음 | ImGui 콜백은 메인 스레드에서 호출 → 문제 없음 |
| Wayland에서 `SetWindowPos` 미지원 (compositor가 위치 결정) | 중간 | Wayland 감지 시 multi-viewport 비활성화 fallback |
| `_device.WaitForIdle()` in DestroyWindow가 프레임 스톨 유발 | 중간 | Deferred 삭제 큐 패턴으로 개선 (후속 최적화) |
| ImGui delegate를 GC가 수거 → 콜백 시 크래시 | 높음 | GCHandle로 모든 delegate를 alive 유지 |
| macOS 미지원 (Cocoa 백엔드 미검토) | 낮음 | Linux/Windows 우선, macOS는 향후 검토 |

---

## 파일 변경 요약

| 파일 | 작업 | Step |
|------|------|------|
| `Editor/ImGui/ViewportData.cs` | **신규** ~80줄 | 1 |
| `Editor/ImGui/ImGuiPlatformBackend.cs` | **신규** ~350줄 | 2 |
| `Editor/ImGui/ImGuiRendererBackend.cs` | **신규** ~250줄 | 3 |
| `Editor/ImGui/ImGuiOverlay.cs` | **수정** (Initialize, Render, Dispose) | 4 |
| `Editor/ImGui/ImGuiInputHandler.cs` | **수정** (multi-input, 절대 좌표) | 5 |
| `Rendering/GraphicsManager.cs` | **수정** (GetSwapchainSource를 internal로, 선택사항) | 3 |

### 의존성 그래프

```
Step 1: ViewportData
  └─ Step 2: ImGuiPlatformBackend
       └─ Step 3: ImGuiRendererBackend
            └─ Step 4: ImGuiOverlay 통합
                 └─ Step 5: 입력 라우팅
                      └─ Step 6: 안정화
```

### 총 예상 코드량
- 신규: ~680줄 (3개 파일)
- 수정: ~100줄 (3개 파일)

---

## 테스트 체크리스트

- [ ] `dotnet build` 성공
- [ ] 에디터 실행 시 메인 윈도우 정상 표시
- [ ] Inspector 패널을 메인 윈도우 밖으로 드래그 → 새 OS 윈도우 생성
- [ ] 보조 윈도우에서 ImGui 패널 내용 정상 렌더링
- [ ] 보조 윈도우에서 마우스 클릭/드래그 동작
- [ ] 보조 윈도우에서 키보드 입력 (텍스트 편집, 단축키)
- [ ] 패널을 메인 윈도우로 다시 드래그 → 보조 윈도우 자동 파괴
- [ ] 메인 윈도우 닫기 → 모든 보조 윈도우 정리
- [ ] 에디터 재시작 시 레이아웃 복원 (도킹 위치)
- [ ] 보조 윈도우 리사이즈 시 콘텐츠 정상 갱신
