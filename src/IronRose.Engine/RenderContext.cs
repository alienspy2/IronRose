using System;
using System.Collections.Generic;
using System.Numerics;
using Veldrid;
using IronRose.Rendering;

namespace IronRose.Rendering
{
    /// <summary>
    /// Encapsulates all resolution-dependent GPU resources for a single rendering viewport.
    /// RenderSystem holds one RenderContext per viewport (Game View, Scene View).
    /// Pipelines, shaders, layouts, samplers, UBO buffers, and shadow atlas are SHARED
    /// across contexts and remain on RenderSystem.
    /// </summary>
    public sealed class RenderContext : IDisposable
    {
        public string Name { get; }

        // --- Dimensions ---
        public uint DisplayWidth;
        public uint DisplayHeight;
        public uint RenderWidth;
        public uint RenderHeight;

        // --- GBuffer ---
        public GBuffer? GBuffer;

        // --- HDR intermediate ---
        public Texture? HdrTexture;
        public TextureView? HdrView;
        public Framebuffer? HdrFramebuffer;

        // --- SSIL / GTAO ---
        public Texture? DepthMipTexture;
        public TextureView? DepthMipFullView;
        public TextureView?[] DepthMipLevelViews = new TextureView?[5];
        public ResourceSet?[] SsilPrefilterSets = new ResourceSet?[5];
        public ResourceSet? SsilMainSet;
        public ResourceSet? SsilDenoiseSet;
        public ResourceSet? SsilDenoiseSetV;
        public Texture? AoRawTexture;
        public TextureView? AoRawView;
        public Texture? AoTexture;
        public TextureView? AoView;
        public Texture? IndirectRawTexture;
        public TextureView? IndirectRawView;
        public Texture? IndirectTexture;
        public TextureView? IndirectView;

        // SSIL Temporal
        public ResourceSet? SsilTemporalSet;
        public Texture? AoHistoryTexture;
        public TextureView? AoHistoryView;
        public Texture? IndirectHistoryTexture;
        public TextureView? IndirectHistoryView;

        // SSIL output for ambient pass
        public ResourceSet? SsilOutputSet;

        // SSIL temporal state
        public Matrix4x4 PrevViewProj;
        public int SsilFrameIndex;
        public bool SsilWasActive;

        // --- GBuffer resource set ---
        public ResourceSet? GBufferResourceSet;

        // --- FSR ---
        public Texture? UpscaledTexture;
        public TextureView? UpscaledView;
        public Texture? HistoryTexture;
        public TextureView? HistoryView;
        public ResourceSet? FsrUpscaleSet;
        public int FsrFrameIndex;

        // --- CAS ---
        public Texture? CasTexture;
        public TextureView? CasView;
        public ResourceSet? CasSet;

        // --- PostProcessStack ---
        public PostProcessStack? PostProcessStack;

        // --- Deferred disposal ---
        public readonly List<IDisposable> PendingDisposal = new();

        public RenderContext(string name)
        {
            Name = name;
        }

        public void DeferDispose(IDisposable? resource)
        {
            if (resource != null)
                PendingDisposal.Add(resource);
        }

        public bool HasPendingDisposal()
        {
            if (PendingDisposal.Count > 0) return true;
            if (GBuffer?.PendingDisposal.Count > 0) return true;
            if (PostProcessStack?.PendingDisposal.Count > 0) return true;
            if (PostProcessStack != null)
            {
                foreach (var effect in PostProcessStack.Effects)
                    if (effect.PendingDisposal.Count > 0) return true;
            }
            return false;
        }

        public void FlushPendingDisposal()
        {
            foreach (var r in PendingDisposal) r.Dispose();
            PendingDisposal.Clear();

            if (GBuffer != null)
            {
                foreach (var r in GBuffer.PendingDisposal) r.Dispose();
                GBuffer.PendingDisposal.Clear();
            }

            if (PostProcessStack != null)
            {
                foreach (var r in PostProcessStack.PendingDisposal) r.Dispose();
                PostProcessStack.PendingDisposal.Clear();

                foreach (var effect in PostProcessStack.Effects)
                {
                    foreach (var r in effect.PendingDisposal) r.Dispose();
                    effect.PendingDisposal.Clear();
                }
            }
        }

        public void Dispose()
        {
            PostProcessStack?.Dispose();

            GBufferResourceSet?.Dispose();
            SsilOutputSet?.Dispose();
            SsilMainSet?.Dispose();
            SsilDenoiseSet?.Dispose();
            SsilDenoiseSetV?.Dispose();
            SsilTemporalSet?.Dispose();
            for (int i = 0; i < 5; i++)
            {
                SsilPrefilterSets[i]?.Dispose();
                DepthMipLevelViews[i]?.Dispose();
            }
            DepthMipFullView?.Dispose();
            DepthMipTexture?.Dispose();
            AoRawView?.Dispose();
            AoRawTexture?.Dispose();
            AoView?.Dispose();
            AoTexture?.Dispose();
            IndirectRawView?.Dispose();
            IndirectRawTexture?.Dispose();
            IndirectView?.Dispose();
            IndirectTexture?.Dispose();
            AoHistoryView?.Dispose();
            AoHistoryTexture?.Dispose();
            IndirectHistoryView?.Dispose();
            IndirectHistoryTexture?.Dispose();

            FsrUpscaleSet?.Dispose();
            UpscaledView?.Dispose();
            UpscaledTexture?.Dispose();
            HistoryView?.Dispose();
            HistoryTexture?.Dispose();
            CasSet?.Dispose();
            CasView?.Dispose();
            CasTexture?.Dispose();

            HdrView?.Dispose();
            HdrFramebuffer?.Dispose();
            HdrTexture?.Dispose();

            GBuffer?.Dispose();
        }
    }
}
