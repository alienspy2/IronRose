using System;
using System.Numerics;
using Veldrid;
using RoseEngine;

namespace IronRose.Rendering
{
    // SSIL (Screen-Space Indirect Lighting) / GTAO compute passes.
    // Extracted from RenderSystem.Render() inline block (Phase 15 — H-1).
    public partial class RenderSystem
    {
        private void RunSSILPass(CommandList cl, Camera camera,
            System.Numerics.Matrix4x4 viewMatrix, System.Numerics.Matrix4x4 projMatrix,
            System.Numerics.Matrix4x4 unjitteredViewProj)
        {
            var ctx = _activeCtx!;
            if (RoseEngine.RenderSettings.ssilEnabled && _ssilPrefilterPipeline != null && _ssilMainPipeline != null && _ssilDenoisePipeline != null)
            {
                uint w = ctx.GBuffer!.Width;
                uint h = ctx.GBuffer.Height;

                // 1) PrefilterDepths — MIP 0
                cl.SetPipeline(_ssilPrefilterPipeline);
                cl.UpdateBuffer(_ssilPrefilterParamsBuffer!, 0, new SSILPrefilterParams
                {
                    TexelSize = new System.Numerics.Vector2(1f / w, 1f / h),
                    NearPlane = camera.nearClipPlane,
                    FarPlane = camera.farClipPlane,
                    MipLevel = 0,
                    SrcWidth = (int)w,
                    SrcHeight = (int)h,
                });
                cl.SetComputeResourceSet(0, ctx.SsilPrefilterSets[0]!);
                cl.Dispatch((w + 15) / 16, (h + 15) / 16, 1);

                // 2) PrefilterDepths — MIP 1-4
                for (int mip = 1; mip < 5; mip++)
                {
                    uint mipW = Math.Max(1, w >> mip);
                    uint mipH = Math.Max(1, h >> mip);
                    uint srcW = Math.Max(1, w >> (mip - 1));
                    uint srcH = Math.Max(1, h >> (mip - 1));

                    cl.UpdateBuffer(_ssilPrefilterParamsBuffer!, 0, new SSILPrefilterParams
                    {
                        TexelSize = new System.Numerics.Vector2(1f / mipW, 1f / mipH),
                        NearPlane = camera.nearClipPlane,
                        FarPlane = camera.farClipPlane,
                        MipLevel = mip,
                        SrcWidth = (int)srcW,
                        SrcHeight = (int)srcH,
                    });
                    cl.SetComputeResourceSet(0, ctx.SsilPrefilterSets[mip]!);
                    cl.Dispatch((mipW + 15) / 16, (mipH + 15) / 16, 1);
                }

                // 3) SSIL Main Pass
                cl.SetPipeline(_ssilMainPipeline);
                cl.UpdateBuffer(_ssilMainParamsBuffer!, 0, new SSILMainParams
                {
                    ViewMatrix = viewMatrix,
                    ProjectionMatrix = projMatrix,
                    Resolution = new System.Numerics.Vector2(w, h),
                    Radius = RoseEngine.RenderSettings.ssilRadius,
                    FalloffScale = RoseEngine.RenderSettings.ssilFalloffScale,
                    SliceCount = RoseEngine.RenderSettings.ssilSliceCount,
                    StepsPerSlice = RoseEngine.RenderSettings.ssilStepsPerSlice,
                    FrameIndex = ctx.SsilFrameIndex,
                    DepthMipSamplingOffset = 3.3f,
                });
                cl.UpdateBuffer(_ssilMainFlagsBuffer!, 0, new SSILMainFlags
                {
                    EnableIndirect = RoseEngine.RenderSettings.ssilIndirectEnabled ? 1 : 0,
                });
                cl.SetComputeResourceSet(0, ctx.SsilMainSet!);
                cl.Dispatch((w + 7) / 8, (h + 7) / 8, 1);

                // 4) Denoise — separable bilateral blur
                cl.SetPipeline(_ssilDenoisePipeline);
                cl.UpdateBuffer(_ssilDenoiseFlagsBuffer!, 0, new SSILDenoiseFlags
                {
                    HasIndirect = 1,
                });

                // H-pass
                cl.UpdateBuffer(_ssilDenoiseParamsBuffer!, 0, new SSILDenoiseParams
                {
                    TexelSize = new System.Numerics.Vector2(1f / w, 1f / h),
                    DepthThreshold = 1.5f,
                    NormalThreshold = 32f,
                    Direction = new System.Numerics.Vector2(1f, 0f),
                });
                cl.SetComputeResourceSet(0, ctx.SsilDenoiseSet!);
                cl.Dispatch((w + 7) / 8, (h + 7) / 8, 1);

                // V-pass
                cl.UpdateBuffer(_ssilDenoiseParamsBuffer!, 0, new SSILDenoiseParams
                {
                    TexelSize = new System.Numerics.Vector2(1f / w, 1f / h),
                    DepthThreshold = 1.5f,
                    NormalThreshold = 32f,
                    Direction = new System.Numerics.Vector2(0f, 1f),
                });
                cl.SetComputeResourceSet(0, ctx.SsilDenoiseSetV!);
                cl.Dispatch((w + 7) / 8, (h + 7) / 8, 1);

                // 5) Temporal filter
                if (_ssilTemporalPipeline != null && ctx.SsilTemporalSet != null)
                {
                    cl.SetPipeline(_ssilTemporalPipeline);
                    cl.UpdateBuffer(_ssilTemporalParamsBuffer!, 0, new SSILTemporalParams
                    {
                        PrevViewProj = ctx.PrevViewProj,
                        Resolution = new System.Numerics.Vector2(w, h),
                        BlendFactor = 0.9f,
                    });
                    cl.SetComputeResourceSet(0, ctx.SsilTemporalSet);
                    cl.Dispatch((w + 7) / 8, (h + 7) / 8, 1);

                    cl.CopyTexture(
                        ctx.AoTexture!, 0, 0, 0, 0, 0,
                        ctx.AoHistoryTexture!, 0, 0, 0, 0, 0,
                        w, h, 1, 1);
                    cl.CopyTexture(
                        ctx.IndirectTexture!, 0, 0, 0, 0, 0,
                        ctx.IndirectHistoryTexture!, 0, 0, 0, 0, 0,
                        w, h, 1, 1);
                }

                ctx.PrevViewProj = unjitteredViewProj;
                ctx.SsilFrameIndex++;
                ctx.SsilWasActive = true;
            }
            else if (ctx.SsilWasActive && ctx.AoRawTexture != null && ctx.IndirectRawTexture != null)
            {
                ctx.SsilWasActive = false;
                uint w = ctx.GBuffer!.Width;
                uint h = ctx.GBuffer.Height;

                var aoData = new byte[w * h];
                Array.Fill(aoData, (byte)255);
                _device!.UpdateTexture(ctx.AoRawTexture, aoData, 0, 0, 0, w, h, 1, 0, 0);

                var indData = new byte[w * h * 8];
                _device!.UpdateTexture(ctx.IndirectRawTexture, indData, 0, 0, 0, w, h, 1, 0, 0);
            }
        }
    }
}
