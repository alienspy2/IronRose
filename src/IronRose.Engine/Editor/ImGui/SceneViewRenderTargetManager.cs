using System;
using IronRose.Engine.Editor.ImGuiEditor.Panels;
using IronRose.Rendering;
using Veldrid;
using Debug = RoseEngine.Debug;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Scene View용 렌더 타겟 관리.
    /// SceneViewRenderer가 소유한 프레임버퍼/컬러 텍스처를 ImGui에 바인딩.
    /// 디바운스를 통해 잦은 리사이즈를 방지.
    /// </summary>
    internal sealed class SceneViewRenderTargetManager : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly VeldridImGuiRenderer _renderer;
        private readonly ImGuiSceneViewPanel _sceneView;
        private readonly SceneViewRenderer _sceneRenderer;

        private IntPtr _textureId;
        private uint _currentWidth;
        private uint _currentHeight;

        // Debounce
        private uint _pendingRTWidth, _pendingRTHeight;
        private float _resizeStableTimer;
        private const float ResizeStableDelay = 0.15f;
        private const float ResizeThresholdRatio = 0.08f;

        public SceneViewRenderTargetManager(
            GraphicsDevice device, VeldridImGuiRenderer renderer,
            ImGuiSceneViewPanel sceneView, SceneViewRenderer sceneRenderer)
        {
            _device = device;
            _renderer = renderer;
            _sceneView = sceneView;
            _sceneRenderer = sceneRenderer;
        }

        public void CreateInitial()
        {
            ResizeAndBind(_device.SwapchainFramebuffer.Width, _device.SwapchainFramebuffer.Height);
        }

        public void EnsureMatchesResolution()
        {
            uint swapW = _device.SwapchainFramebuffer.Width;
            uint swapH = _device.SwapchainFramebuffer.Height;
            var (targetW, targetH) = _sceneView.GetRenderTargetSize(swapW, swapH);

            if (targetW == 0 || targetH == 0) return;

            if (_currentWidth == targetW && _currentHeight == targetH)
            {
                _pendingRTWidth = 0;
                _pendingRTHeight = 0;
                return;
            }

            if (_currentWidth == 0 || _currentHeight == 0)
            {
                ResizeAndBind(targetW, targetH);
                return;
            }

            float dw = Math.Abs((float)targetW - _currentWidth) / _currentWidth;
            float dh = Math.Abs((float)targetH - _currentHeight) / _currentHeight;
            if (dw > ResizeThresholdRatio || dh > ResizeThresholdRatio)
            {
                ResizeAndBind(targetW, targetH);
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
                ResizeAndBind(targetW, targetH);
                _pendingRTWidth = 0;
                _pendingRTHeight = 0;
                _resizeStableTimer = 0;
            }
        }

        private void ResizeAndBind(uint width, uint height)
        {
            if (width == 0 || height == 0) return;

            // Resize the renderer (which recreates its internal framebuffer + color texture)
            _sceneRenderer.Resize(width, height);

            _currentWidth = width;
            _currentHeight = height;

            // Bind the renderer's color texture view to ImGui
            var colorView = _sceneRenderer.ColorTextureView;
            if (colorView == null) return;

            if (_textureId != IntPtr.Zero)
            {
                _renderer.UpdateImGuiBinding(_textureId, colorView);
            }
            else
            {
                _textureId = _renderer.GetOrCreateImGuiBinding(colorView);
            }
            _sceneView.SetTextureId(_textureId);

            Debug.Log($"[SceneView] RT bound: {width}x{height}");
        }

        public void Dispose()
        {
            // SceneViewRenderer owns the actual GPU resources — nothing to dispose here.
        }
    }
}
