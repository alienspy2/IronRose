using System;
using Veldrid;
using RoseEngine;

namespace IronRose.Rendering
{
    public class GBuffer : IDisposable
    {
        public Texture AlbedoTexture { get; private set; } = null!;
        public Texture NormalTexture { get; private set; } = null!;
        public Texture MaterialTexture { get; private set; } = null!;
        public Texture DepthTexture { get; private set; } = null!;
        public Texture WorldPosTexture { get; private set; } = null!;
        public Texture DepthCopyTexture { get; private set; } = null!;  // R32F copy for compute sampling
        public Texture VelocityTexture { get; private set; } = null!;  // RG16F screen-space motion vectors

        public TextureView AlbedoView { get; private set; } = null!;
        public TextureView NormalView { get; private set; } = null!;
        public TextureView MaterialView { get; private set; } = null!;
        public TextureView WorldPosView { get; private set; } = null!;
        public TextureView DepthView { get; private set; } = null!;        // Depth sampling for SSIL
        public TextureView DepthCopyView { get; private set; } = null!;
        public TextureView VelocityView { get; private set; } = null!;

        public Framebuffer Framebuffer { get; private set; } = null!;

        public uint Width { get; private set; }
        public uint Height { get; private set; }

        /// <summary>
        /// Pending disposals collected by DeferDispose. Flushed externally by RenderSystem.
        /// </summary>
        public readonly System.Collections.Generic.List<IDisposable> PendingDisposal = new();

        private void DeferDispose(IDisposable? resource)
        {
            if (resource != null)
                PendingDisposal.Add(resource);
        }

        public void Initialize(GraphicsDevice device, uint width, uint height)
        {
            if (Width == width && Height == height)
                return;

            // Defer disposal of old resources instead of immediate Dispose
            DeferDispose(AlbedoView);
            DeferDispose(NormalView);
            DeferDispose(MaterialView);
            DeferDispose(WorldPosView);
            DeferDispose(DepthView);
            DeferDispose(DepthCopyView);
            DeferDispose(VelocityView);
            DeferDispose(Framebuffer);
            DeferDispose(AlbedoTexture);
            DeferDispose(NormalTexture);
            DeferDispose(MaterialTexture);
            DeferDispose(DepthTexture);
            DeferDispose(WorldPosTexture);
            DeferDispose(DepthCopyTexture);
            DeferDispose(VelocityTexture);

            Width = width;
            Height = height;
            var factory = device.ResourceFactory;

            // RT0: Albedo (RGBA8)
            AlbedoTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // RT1: Normal + Roughness (RGBA16F)
            NormalTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // RT2: Material (RGBA8)
            MaterialTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // Depth-Stencil (render + sample for SSIL compute)
            DepthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.D32_Float_S8_UInt,
                TextureUsage.DepthStencil | TextureUsage.Sampled));

            // RT3: World Position (RGBA16F — written directly by geometry shader)
            WorldPosTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // Depth copy (R32F — for compute shader sampling, cl.CopyTexture from D32S8)
            DepthCopyTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R32_Float,
                TextureUsage.Sampled | TextureUsage.RenderTarget));

            // RT4: Velocity (RG16F — screen-space motion vectors for temporal upscaling)
            VelocityTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));

            // TextureViews
            AlbedoView = factory.CreateTextureView(AlbedoTexture);
            NormalView = factory.CreateTextureView(NormalTexture);
            MaterialView = factory.CreateTextureView(MaterialTexture);
            WorldPosView = factory.CreateTextureView(WorldPosTexture);
            DepthView = factory.CreateTextureView(DepthTexture);
            DepthCopyView = factory.CreateTextureView(DepthCopyTexture);
            VelocityView = factory.CreateTextureView(VelocityTexture);

            // Framebuffer (5 color + 1 depth)
            Framebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                DepthTexture,
                AlbedoTexture,
                NormalTexture,
                MaterialTexture,
                WorldPosTexture,
                VelocityTexture));

            Debug.Log($"[GBuffer] Initialized ({width}x{height})");
        }

        public void Dispose()
        {
            AlbedoView?.Dispose();
            NormalView?.Dispose();
            MaterialView?.Dispose();
            WorldPosView?.Dispose();
            DepthView?.Dispose();
            DepthCopyView?.Dispose();
            VelocityView?.Dispose();
            Framebuffer?.Dispose();
            AlbedoTexture?.Dispose();
            NormalTexture?.Dispose();
            MaterialTexture?.Dispose();
            DepthTexture?.Dispose();
            WorldPosTexture?.Dispose();
            DepthCopyTexture?.Dispose();
            VelocityTexture?.Dispose();
        }
    }
}
