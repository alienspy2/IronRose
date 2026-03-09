using System;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using Veldrid;
using Debug = RoseEngine.Debug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Game View용 오프스크린 렌더 타겟 생성/리사이즈/해제를 관리.
    /// ImGuiOverlay에서 분리 (Phase 15 — M-4).
    /// </summary>
    internal sealed class ImGuiRenderTargetManager : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly VeldridImGuiRenderer _renderer;
        private readonly ImGuiGameViewPanel _gameView;

        private Texture? _offscreenColor;
        private TextureView? _offscreenColorView;
        private Texture? _offscreenDepth;
        private Framebuffer? _offscreenFB;
        private IntPtr _gameViewTextureId;
        private uint _offscreenWidth;
        private uint _offscreenHeight;

        // Debounce
        private uint _pendingRTWidth, _pendingRTHeight;
        private float _resizeStableTimer;
        private const float ResizeStableDelay = 0.15f;
        private const float ResizeThresholdRatio = 0.08f;

        public Framebuffer? Framebuffer => _offscreenFB;

        public ImGuiRenderTargetManager(GraphicsDevice device, VeldridImGuiRenderer renderer, ImGuiGameViewPanel gameView)
        {
            _device = device;
            _renderer = renderer;
            _gameView = gameView;
        }

        public void CreateInitial()
        {
            CreateOffscreenRT(_device.SwapchainFramebuffer.Width, _device.SwapchainFramebuffer.Height);
        }

        /// <summary>
        /// 선택 해상도에 맞춰 오프스크린 RT 크기를 보장.
        /// 매 프레임 RenderSystem.Render() 전에 호출.
        /// </summary>
        public void EnsureMatchesResolution()
        {
            uint swapW = _device.SwapchainFramebuffer.Width;
            uint swapH = _device.SwapchainFramebuffer.Height;
            var (targetW, targetH) = _gameView.GetRenderTargetSize(swapW, swapH);

            if (targetW == 0 || targetH == 0)
                return;

            if (_offscreenFB != null && _offscreenWidth == targetW && _offscreenHeight == targetH)
            {
                _pendingRTWidth = 0;
                _pendingRTHeight = 0;
                return;
            }

            if (_offscreenFB == null)
            {
                CreateOffscreenRT(targetW, targetH);
                return;
            }

            float dw = Math.Abs((float)targetW - _offscreenWidth) / _offscreenWidth;
            float dh = Math.Abs((float)targetH - _offscreenHeight) / _offscreenHeight;
            if (dw > ResizeThresholdRatio || dh > ResizeThresholdRatio)
            {
                CreateOffscreenRT(targetW, targetH);
                _pendingRTWidth = 0;
                _pendingRTHeight = 0;
                _resizeStableTimer = 0;
                return;
            }

            if (_pendingRTWidth != targetW || _pendingRTHeight != targetH)
            {
                _pendingRTWidth = targetW;
                _pendingRTHeight = targetH;
                _resizeStableTimer = 0;
                return;
            }

            _resizeStableTimer += 1f / 60f;
            if (_resizeStableTimer >= ResizeStableDelay)
            {
                CreateOffscreenRT(targetW, targetH);
                _pendingRTWidth = 0;
                _pendingRTHeight = 0;
                _resizeStableTimer = 0;
            }
        }

        private void CreateOffscreenRT(uint width, uint height)
        {
            if (width == 0 || height == 0) return;

            DisposeRT();

            var factory = _device.ResourceFactory;
            var swapDesc = _device.SwapchainFramebuffer.OutputDescription;

            var colorFormat = swapDesc.ColorAttachments[0].Format;
            _offscreenColor = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                colorFormat,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _offscreenColorView = factory.CreateTextureView(_offscreenColor);

            if (swapDesc.DepthAttachment.HasValue)
            {
                var depthFormat = swapDesc.DepthAttachment.Value.Format;
                _offscreenDepth = factory.CreateTexture(TextureDescription.Texture2D(
                    width, height, 1, 1,
                    depthFormat,
                    TextureUsage.DepthStencil));
                _offscreenFB = factory.CreateFramebuffer(new FramebufferDescription(
                    _offscreenDepth, _offscreenColor));
            }
            else
            {
                _offscreenFB = factory.CreateFramebuffer(new FramebufferDescription(
                    null, _offscreenColor));
            }

            _offscreenWidth = width;
            _offscreenHeight = height;

            if (_gameViewTextureId != IntPtr.Zero)
            {
                _renderer.UpdateImGuiBinding(_gameViewTextureId, _offscreenColorView);
            }
            else
            {
                _gameViewTextureId = _renderer.GetOrCreateImGuiBinding(_offscreenColorView);
            }
            _gameView.SetTextureId(_gameViewTextureId);

            Debug.Log($"[ImGui] Offscreen RT created: {width}x{height}");
        }

        private void DisposeRT()
        {
            _offscreenFB?.Dispose();
            _offscreenFB = null;
            _offscreenDepth?.Dispose();
            _offscreenDepth = null;
            _offscreenColorView?.Dispose();
            _offscreenColorView = null;
            _offscreenColor?.Dispose();
            _offscreenColor = null;
        }

        public void Dispose()
        {
            DisposeRT();
        }
    }
}
