// ------------------------------------------------------------
// @file    GraphicsManager.cs
// @brief   Veldrid 기반 그래픽스 디바이스 초기화, 프레임 시작/종료, 스크린샷 캡처를 관리한다.
//          Silk.NET 윈도우에서 네이티브 핸들을 추출하여 Vulkan 스왑체인을 생성한다.
// @deps    (프로젝트 내부 없음 — IronRose.Contracts의 EditorDebug만 사용)
// @exports
//   class GraphicsManager
//     Device: GraphicsDevice?                              — Veldrid 그래픽스 디바이스
//     CommandList: CommandList?                            — 프레임별 커맨드 리스트
//     Window: IWindow?                                    — Silk.NET 윈도우 참조
//     AspectRatio: float                                  — 현재 윈도우 종횡비
//     Resized: event Action<uint, uint>                   — 윈도우 리사이즈 이벤트
//     SetClearColor(float, float, float): void            — 클리어 색상 설정
//     Initialize(IWindow): void                           — 디바이스 및 커맨드 리스트 생성
//     BeginFrame(): void                                  — 프레임 시작 (커맨드 리스트 Begin)
//     EndFrame(): void                                    — 프레임 종료 (Submit + SwapBuffers)
//     Render(): void                                      — BeginFrame + EndFrame 단축 호출
//     RequestScreenshot(string): void                     — 다음 EndFrame에서 스크린샷 캡처 요청
//     Dispose(): void                                     — GPU 리소스 정리
// @note    Vulkan 백엔드 전용. BeginFrame에서 VeldridException 발생 시 커맨드 리스트 재생성으로 복구.
// ------------------------------------------------------------
using Veldrid;
using Silk.NET.Windowing;
using Veldrid.ImageSharp;
using System;
using System.IO;
using RoseEngine;

namespace IronRose.Rendering
{
    public class GraphicsManager
    {
        private GraphicsDevice? _graphicsDevice;
        private CommandList? _commandList;
        private IWindow? _window;
        private string? _pendingScreenshot;
        private RgbaFloat _clearColor = new RgbaFloat(0.902f, 0.863f, 0.824f, 1.0f);

        public GraphicsDevice? Device => _graphicsDevice;
        public CommandList? CommandList => _commandList;
        public IWindow? Window => _window;

        public event Action<uint, uint>? Resized;

        public float AspectRatio
        {
            get
            {
                if (_window == null || _window.Size.Y == 0) return 16f / 9f;
                return (float)_window.Size.X / _window.Size.Y;
            }
        }

        public void SetClearColor(float r, float g, float b)
        {
            _clearColor = new RgbaFloat(r, g, b, 1.0f);
        }

        public void Initialize(IWindow window)
        {
            EditorDebug.Log("[Renderer] Initializing graphics device...");

            _window = window;
            EditorDebug.Log($"[Renderer] Using window: {_window.Size.X}x{_window.Size.Y}");

            // Silk.NET 네이티브 핸들 → Veldrid SwapchainSource
            var swapchainSource = GetSwapchainSource(_window);

            GraphicsDeviceOptions options = new GraphicsDeviceOptions
            {
                PreferStandardClipSpaceYDirection = true,
                PreferDepthRangeZeroToOne = true,
                Debug = false
            };

            var scDesc = new SwapchainDescription(
                swapchainSource,
                (uint)_window.Size.X,
                (uint)_window.Size.Y,
                PixelFormat.D32_Float_S8_UInt,  // Depth buffer enabled
                false,  // vsync
                false   // srgb
            );

            _graphicsDevice = GraphicsDevice.CreateVulkan(options, scDesc);
            EditorDebug.Log($"[Renderer] Graphics Device Created: {_graphicsDevice.BackendType}");

            _commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
            EditorDebug.Log("[Renderer] Command list created");

            // 윈도우 리사이즈 처리
            _window.Resize += size =>
            {
                if (size.X > 0 && size.Y > 0)
                {
                    _graphicsDevice.ResizeMainWindow((uint)size.X, (uint)size.Y);
                    Resized?.Invoke((uint)size.X, (uint)size.Y);
                }
            };
        }

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

        public void BeginFrame()
        {
            if (_graphicsDevice == null || _commandList == null) return;

            try
            {
                _commandList.Begin();
            }
            catch (VeldridException)
            {
                // Previous frame crashed before End() — recreate command list to recover
                EditorDebug.LogWarning("[Renderer] CommandList in invalid state — recreating to recover");
                _commandList.Dispose();
                _commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
                _commandList.Begin();
            }

            _commandList.SetFramebuffer(_graphicsDevice.SwapchainFramebuffer);
            _commandList.ClearColorTarget(0, _clearColor);
            _commandList.ClearDepthStencil(1f);
        }

        public void EndFrame()
        {
            if (_graphicsDevice == null || _commandList == null) return;

            _commandList.End();
            _graphicsDevice.SubmitCommands(_commandList);

            // 스크린샷 요청이 있으면 SwapBuffers() 전에 캡처
            if (_pendingScreenshot != null)
            {
                CaptureScreenshotInternal(_pendingScreenshot);
                _pendingScreenshot = null;
            }

            _graphicsDevice.SwapBuffers();
        }

        public void Render()
        {
            BeginFrame();
            EndFrame();
        }

        public void RequestScreenshot(string filename)
        {
            _pendingScreenshot = filename;
        }

        private void CaptureScreenshotInternal(string filename)
        {
            if (_graphicsDevice == null)
            {
                EditorDebug.LogError("[Renderer] ERROR: Cannot capture screenshot, GraphicsDevice is null");
                return;
            }

            try
            {
                var swapchainFB = _graphicsDevice.SwapchainFramebuffer;
                var colorTexture = swapchainFB.ColorTargets[0].Target;

                // Staging texture 생성 (CPU로 읽기 가능)
                var stagingTexture = _graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription(
                    colorTexture.Width,
                    colorTexture.Height,
                    1, 1, 1,
                    colorTexture.Format,
                    TextureUsage.Staging,
                    colorTexture.Type
                ));

                // GPU → CPU 복사
                var commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
                commandList.Begin();
                commandList.CopyTexture(colorTexture, stagingTexture);
                commandList.End();
                _graphicsDevice.SubmitCommands(commandList);
                _graphicsDevice.WaitForIdle();

                // 픽셀 데이터 읽기
                var map = _graphicsDevice.Map(stagingTexture, MapMode.Read);
                var pixelSizeInBytes = 4; // BGRA8 = 4 bytes
                var rowPitch = (int)map.RowPitch;
                var width = (int)colorTexture.Width;
                var height = (int)colorTexture.Height;

                // ImageSharp 이미지 생성
                using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32>(width, height);

                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        var rowSpan = accessor.GetRowSpan(y);
                        unsafe
                        {
                            var sourcePtr = (byte*)map.Data.ToPointer() + (y * rowPitch);
                            fixed (SixLabors.ImageSharp.PixelFormats.Bgra32* destPtr = rowSpan)
                            {
                                Buffer.MemoryCopy(sourcePtr, destPtr, rowPitch, width * pixelSizeInBytes);
                            }
                        }
                    }
                });

                _graphicsDevice.Unmap(stagingTexture);

                // 파일 저장
                Directory.CreateDirectory(Path.GetDirectoryName(filename) ?? ".");
                using (var fileStream = File.Create(filename))
                {
                    image.Save(fileStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                }
                EditorDebug.Log($"[Renderer] Screenshot saved: {filename}");

                // 정리
                stagingTexture.Dispose();
                commandList.Dispose();
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Renderer] ERROR capturing screenshot: {ex.Message}");
                EditorDebug.LogError(ex.StackTrace ?? "(no stack trace)");
            }
        }

        public void Dispose()
        {
            EditorDebug.Log("[Renderer] Disposing graphics resources...");

            try
            {
                if (_graphicsDevice != null)
                {
                    _graphicsDevice.WaitForIdle();
                    EditorDebug.Log("[Renderer] GPU idle");
                }

                _commandList?.Dispose();
                EditorDebug.Log("[Renderer] CommandList disposed");

                _graphicsDevice?.Dispose();
                EditorDebug.Log("[Renderer] GraphicsDevice disposed");

                _window = null;
                EditorDebug.Log("[Renderer] Window reference cleared");
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Renderer] ERROR during Dispose: {ex.Message}");
            }
        }
    }
}
