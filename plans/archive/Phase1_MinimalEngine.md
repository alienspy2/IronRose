# Phase 1: 최소 실행 가능 엔진

## 목표
IronRose.Engine(EXE)에서 SDL 윈도우를 열고 Veldrid로 화면을 클리어합니다.

---

## 작업 항목

### 1.1 윈도우 생성 (IronRose.Engine)

**Program.cs 구현:**
```csharp
using System;
using System.Diagnostics;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace IronRose.Engine
{
    class Program
    {
        private static Sdl2Window? _window;

        static void Main(string[] args)
        {
            Console.WriteLine("[IronRose] Engine Starting...");

            CreateWindow();
            MainLoop();

            _window?.Close();
        }

        static void CreateWindow()
        {
            WindowCreateInfo windowCI = new WindowCreateInfo()
            {
                X = 100, Y = 100,
                WindowWidth = 1280, WindowHeight = 720,
                WindowTitle = "IronRose Engine"
            };
            _window = VeldridStartup.CreateWindow(ref windowCI);
        }

        static void MainLoop()
        {
            while (_window != null && _window.Exists)
            {
                _window.PumpEvents();
                // TODO: 렌더링 (Phase 1.2에서 구현)
                System.Threading.Thread.Sleep(16); // ~60 FPS
            }
        }
    }
}
```

---

### 1.2 Veldrid 그래픽 디바이스 초기화

**GraphicsManager.cs 생성 (IronRose.Rendering):**
```csharp
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace IronRose.Rendering
{
    public class GraphicsManager
    {
        private GraphicsDevice? _graphicsDevice;
        private CommandList? _commandList;
        private Sdl2Window? _window;

        public void Initialize(object? windowHandle = null)
        {
            if (windowHandle is Sdl2Window window)
                _window = window;

            var options = new GraphicsDeviceOptions
            {
                PreferStandardClipSpaceYDirection = true,
                PreferDepthRangeZeroToOne = true,
                Debug = true
            };

            _graphicsDevice = VeldridStartup.CreateGraphicsDevice(_window, options, GraphicsBackend.Vulkan);
            _commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
        }

        public void Render()
        {
            _commandList!.Begin();
            _commandList.SetFramebuffer(_graphicsDevice!.SwapchainFramebuffer);
            _commandList.ClearColorTarget(0, new RgbaFloat(0.902f, 0.863f, 0.824f, 1.0f));
            _commandList.End();

            _graphicsDevice.SubmitCommands(_commandList);
            _graphicsDevice.SwapBuffers();
        }

        public void Dispose()
        {
            _commandList?.Dispose();
            _graphicsDevice?.Dispose();
        }
    }
}
```

**EngineCore.cs (IronRose.Engine):**
```csharp
using IronRose.Rendering;
using Veldrid.Sdl2;

namespace IronRose.Engine
{
    public class EngineCore
    {
        private GraphicsManager? _graphicsManager;

        public void Initialize(Sdl2Window? window = null)
        {
            _graphicsManager = new GraphicsManager();
            _graphicsManager.Initialize(window);
        }

        public void Update(double deltaTime) { }
        public void Render() => _graphicsManager?.Render();
        public void Shutdown() => _graphicsManager?.Dispose();
    }
}
```

---

### 1.3 기본 렌더링 루프

**타이밍 시스템 추가:**
```csharp
using System.Diagnostics;

private static Stopwatch _timer = Stopwatch.StartNew();
private static double _lastTime = 0;
private static double _deltaTime = 0;

static void MainLoop()
{
    while (_running)
    {
        // 델타 타임 계산
        double currentTime = _timer.Elapsed.TotalSeconds;
        _deltaTime = currentTime - _lastTime;
        _lastTime = currentTime;

        // 이벤트 처리
        ProcessEvents();

        // 렌더링
        _graphics.Render();

        // 프레임 제한 (60 FPS)
        double frameTime = _timer.Elapsed.TotalSeconds - currentTime;
        if (frameTime < 1.0 / 60.0)
        {
            Thread.Sleep((int)((1.0 / 60.0 - frameTime) * 1000));
        }
    }
}
```

---

## 검증 기준

✅ 1280x720 윈도우가 열림
✅ 파란색 배경 화면이 렌더링됨
✅ ESC 키를 누르면 프로그램이 종료됨
✅ 윈도우 닫기 버튼으로 종료 가능
✅ 약 60 FPS로 동작 (콘솔에 FPS 출력)

---

## 트러블슈팅

### Vulkan 초기화 실패
- Vulkan 드라이버가 설치되어 있는지 확인
- `GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan)` 체크
- 대안: Direct3D 11로 대체 (Windows만 지원)

### SDL3 DLL 누락
- NuGet 패키지에서 자동으로 복사되어야 함
- 수동으로 SDL3.dll을 출력 디렉토리에 복사

---

## 예상 소요 시간
**2-3일**

---

## 다음 단계
→ [Phase 2: Roslyn 핫 리로딩 시스템](Phase2_HotReloading.md)
