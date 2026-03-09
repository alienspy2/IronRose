using System.Numerics;
using Veldrid;
using RoseEngine;

namespace IronRose.Rendering
{
    // Debug overlay rendering (G-Buffer thumbnails, shadow atlas preview).
    public partial class RenderSystem
    {
        private void RenderDebugOverlay(CommandList cl, Framebuffer targetFB)
        {
            if (_debugOverlayPipeline == null || _debugOverlayLayout == null ||
                _debugOverlayParamsBuffer == null || _debugOverlaySampler == null ||
                _device == null || _activeCtx?.GBuffer == null)
                return;

            var gBuffer = _activeCtx.GBuffer;
            var factory = _device.ResourceFactory;
            uint screenW = targetFB.Width;
            uint screenH = targetFB.Height;

            cl.SetFramebuffer(targetFB);
            cl.SetFullViewports();
            cl.SetFullScissorRects();
            cl.SetPipeline(_debugOverlayPipeline);

            if (DebugOverlaySettings.overlay == DebugOverlay.GBuffer)
            {
                uint thumbH = screenH / 4;
                uint thumbW = screenW / 4;
                uint thumbY = screenH - thumbH;

                var textures = new (TextureView view, float mode)[]
                {
                    (gBuffer.AlbedoView, 0f),
                    (gBuffer.NormalView, 1f),
                    (gBuffer.MaterialView, 2f),
                    (gBuffer.WorldPosView, 3f),
                };

                for (int i = 0; i < textures.Length; i++)
                {
                    uint thumbX = (uint)i * thumbW;

                    using var resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                        _debugOverlayLayout, textures[i].view, _debugOverlaySampler, _debugOverlayParamsBuffer));

                    cl.UpdateBuffer(_debugOverlayParamsBuffer, 0, new DebugOverlayParamsGPU { Mode = textures[i].mode });
                    cl.SetViewport(0, new Viewport(thumbX, thumbY, thumbW, thumbH, 0f, 1f));
                    cl.SetScissorRect(0, thumbX, thumbY, thumbW, thumbH);
                    cl.SetGraphicsResourceSet(0, resourceSet);
                    cl.Draw(3, 1, 0, 0);
                }
            }
            else if (DebugOverlaySettings.overlay == DebugOverlay.ShadowMap)
            {
                if (_atlasView == null) return;

                uint thumbSize = (uint)(screenH * 0.3f);

                using var resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                    _debugOverlayLayout, _atlasView, _debugOverlaySampler, _debugOverlayParamsBuffer));

                cl.UpdateBuffer(_debugOverlayParamsBuffer, 0, new DebugOverlayParamsGPU { Mode = 4f });
                cl.SetViewport(0, new Viewport(0, screenH - thumbSize, thumbSize, thumbSize, 0f, 1f));
                cl.SetScissorRect(0, 0, screenH - thumbSize, thumbSize, thumbSize);
                cl.SetGraphicsResourceSet(0, resourceSet);
                cl.Draw(3, 1, 0, 0);
            }

            cl.SetFullViewports();
            cl.SetFullScissorRects();
        }
    }
}
