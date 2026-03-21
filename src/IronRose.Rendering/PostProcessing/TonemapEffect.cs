// ------------------------------------------------------------
// @file    TonemapEffect.cs
// @brief   HDR Tonemapping 포스트 프로세스. ACES 기반 톤맵 + Exposure/Saturation/Contrast/Gamma 조절.
// @deps    PostProcessEffect, ShaderCompiler, EditorDebug
// @exports
//   class TonemapEffect : PostProcessEffect
//     Exposure: float [EffectParam]     — 노출 (0.01~10)
//     Saturation: float [EffectParam]   — 채도 (0~3)
//     Contrast: float [EffectParam]     — 대비 (0.5~2)
//     WhitePoint: float [EffectParam]   — 화이트 포인트 (0.5~20)
//     Gamma: float [EffectParam]        — 감마 (1~3)
// @note    셰이더: fullscreen.vert, tonemap.frag
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Veldrid;
using RoseEngine;

namespace IronRose.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct TonemapParamsGPU
    {
        public float Exposure;
        public float Saturation;
        public float Contrast;
        public float WhitePoint;
        public float Gamma;
        public float _pad1;
        public float _pad2;
        public float _pad3;
    }

    public class TonemapEffect : PostProcessEffect
    {
        public override string Name => "Tonemap";

        [EffectParam("Exposure", Min = 0.01f, Max = 10f)]
        public float Exposure { get; set; } = 1.5f;

        [EffectParam("Saturation", Min = 0f, Max = 3f)]
        public float Saturation { get; set; } = 1.6f;

        [EffectParam("Contrast", Min = 0.5f, Max = 2f)]
        public float Contrast { get; set; } = 1f;

        [EffectParam("White Point", Min = 0.5f, Max = 20f)]
        public float WhitePoint { get; set; } = 10f;

        [EffectParam("Gamma", Min = 1.0f, Max = 3.0f)]
        public float Gamma { get; set; } = 1.2f;

        /// <summary>Tonemap 중립: Exposure=1, Saturation=1, Contrast=1, 기본 WhitePoint/Gamma.</summary>
        public override Dictionary<string, float> GetNeutralValues() => new()
        {
            ["Exposure"] = 1f,
            ["Saturation"] = 1f,
            ["Contrast"] = 1f,
            ["White Point"] = 4f,
            ["Gamma"] = 2.2f,
        };

        // Pipeline
        private Pipeline? _pipeline;
        private ResourceLayout? _layout;
        private DeviceBuffer? _paramsBuffer;
        private Shader[]? _shaders;

        protected override void OnInitialize(uint width, uint height)
        {
            var factory = Device.ResourceFactory;

            // Compile shader
            string fullscreenVert = ShaderResolver("fullscreen.vert");
            _shaders = ShaderCompiler.CompileGLSL(Device, fullscreenVert,
                ShaderResolver("tonemap.frag"));

            // Uniform buffer
            _paramsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<TonemapParamsGPU>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Resource layout
            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("TonemapParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            CreateSizeDependentResources(width, height);

            EditorDebug.Log($"[TonemapEffect] Initialized ({width}x{height})");
        }

        public override void Resize(uint width, uint height)
        {
            DeferDispose(_pipeline);
            CreateSizeDependentResources(width, height);
        }

        public override void Execute(CommandList cl, TextureView sourceView, Framebuffer destinationFB)
        {
            var factory = Device.ResourceFactory;

            using var resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _layout!, sourceView, LinearSampler, _paramsBuffer!));

            cl.UpdateBuffer(_paramsBuffer, 0, new TonemapParamsGPU
            {
                Exposure = Exposure,
                Saturation = Saturation,
                Contrast = Contrast,
                WhitePoint = WhitePoint,
                Gamma = Gamma,
            });

            cl.SetFramebuffer(destinationFB);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, resourceSet);
            cl.Draw(3, 1, 0, 0);
        }

        private void CreateSizeDependentResources(uint width, uint height)
        {
            // Tonemap now always outputs to HDR intermediate (blit handles final → swapchain)
            var hdrOutputDesc = new OutputDescription(
                null,
                new OutputAttachmentDescription(PixelFormat.R16_G16_B16_A16_Float));

            _pipeline = CreateFullscreenPipeline(_layout!, _shaders!, hdrOutputDesc);
        }

        public override void Dispose()
        {
            _pipeline?.Dispose();
            _paramsBuffer?.Dispose();
            _layout?.Dispose();

            if (_shaders != null)
                foreach (var s in _shaders) s.Dispose();

            EditorDebug.Log("[TonemapEffect] Disposed");
        }
    }
}
