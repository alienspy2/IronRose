// ------------------------------------------------------------
// @file    BloomEffect.cs
// @brief   HDR Bloom 포스트 프로세스. Threshold -> Gaussian Blur -> Composite 4-pass 파이프라인.
// @deps    PostProcessEffect, ShaderCompiler
// @exports
//   class BloomEffect : PostProcessEffect
//     Threshold: float [EffectParam]   — 밝기 임계값 (0~10)
//     SoftKnee: float [EffectParam]    — 소프트 니 (0~1)
//     Intensity: float [EffectParam]   — 블룸 강도 (0~5)
// @note    Half-res 텍스처에서 블러 처리 후 원본과 합성.
//          셰이더: fullscreen.vert, bloom_threshold.frag, gaussian_blur.frag, bloom_composite.frag
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using RoseEngine;

namespace IronRose.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BloomParamsGPU
    {
        public float Threshold;
        public float SoftKnee;
        public float _pad1;
        public float _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlurParamsGPU
    {
        public Vector2 Direction;
        public float _pad1;
        public float _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BloomCompositeParamsGPU
    {
        public float BloomIntensity;
        public float _pad1;
        public float _pad2;
        public float _pad3;
    }

    public class BloomEffect : PostProcessEffect
    {
        public override string Name => "Bloom";

        [EffectParam("Threshold", Min = 0f, Max = 10f)]
        public float Threshold { get; set; } = 0.8f;

        [EffectParam("Soft Knee", Min = 0f, Max = 1f)]
        public float SoftKnee { get; set; } = 0.5f;

        [EffectParam("Intensity", Min = 0f, Max = 5f)]
        public float Intensity { get; set; } = 0.5f;

        /// <summary>Bloom 비활성: 높은 Threshold, SoftKnee/Intensity 0 → 블룸 없음.</summary>
        public override Dictionary<string, float> GetNeutralValues() => new()
        {
            ["Threshold"] = 10f,
            ["Soft Knee"] = 0f,
            ["Intensity"] = 0f,
        };

        // Pipelines
        private Pipeline? _thresholdPipeline;
        private Pipeline? _blurPipeline;
        private Pipeline? _compositePipeline;

        // Resource layouts
        private ResourceLayout? _thresholdLayout;
        private ResourceLayout? _blurLayout;
        private ResourceLayout? _compositeLayout;

        // Internal textures (half-res)
        private Texture? _bloomHalf;
        private TextureView? _bloomHalfView;
        private Framebuffer? _bloomHalfFB;

        private Texture? _blurTemp;
        private TextureView? _blurTempView;
        private Framebuffer? _blurTempFB;

        // Uniform buffers
        private DeviceBuffer? _bloomParamsBuffer;
        private DeviceBuffer? _blurParamsBuffer;
        private DeviceBuffer? _compositeParamsBuffer;

        // Shaders
        private Shader[]? _thresholdShaders;
        private Shader[]? _blurShaders;
        private Shader[]? _compositeShaders;

        private uint _halfWidth;
        private uint _halfHeight;

        protected override void OnInitialize(uint width, uint height)
        {
            var factory = Device.ResourceFactory;

            // Compile shaders
            string fullscreenVert = ShaderResolver("fullscreen.vert");
            _thresholdShaders = ShaderCompiler.CompileGLSL(Device, fullscreenVert,
                ShaderResolver("bloom_threshold.frag"));
            _blurShaders = ShaderCompiler.CompileGLSL(Device, fullscreenVert,
                ShaderResolver("gaussian_blur.frag"));
            _compositeShaders = ShaderCompiler.CompileGLSL(Device, fullscreenVert,
                ShaderResolver("bloom_composite.frag"));

            // Uniform buffers
            _bloomParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<BloomParamsGPU>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _blurParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<BlurParamsGPU>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _compositeParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<BloomCompositeParamsGPU>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Resource layouts
            _thresholdLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("BloomParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            _blurLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("BlurParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            _compositeLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SceneTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("BloomTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("TexSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("BloomCompositeParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            CreateSizeDependentResources(width, height);

            Debug.Log($"[BloomEffect] Initialized ({width}x{height})");
        }

        public override void Resize(uint width, uint height)
        {
            // Defer disposal: resources may still be referenced by in-flight CommandList
            DeferDispose(_thresholdPipeline);
            DeferDispose(_blurPipeline);
            DeferDispose(_compositePipeline);
            DeferDispose(_bloomHalfView);
            DeferDispose(_bloomHalfFB);
            DeferDispose(_bloomHalf);
            DeferDispose(_blurTempView);
            DeferDispose(_blurTempFB);
            DeferDispose(_blurTemp);
            CreateSizeDependentResources(width, height);
        }

        public override void Execute(CommandList cl, TextureView sourceView, Framebuffer destinationFB)
        {
            // Create resource sets on the fly (sourceView changes each frame via ping-pong)
            var factory = Device.ResourceFactory;

            using var thresholdSet = factory.CreateResourceSet(new ResourceSetDescription(
                _thresholdLayout!, sourceView, LinearSampler, _bloomParamsBuffer!));
            using var blurHSet = factory.CreateResourceSet(new ResourceSetDescription(
                _blurLayout!, _bloomHalfView!, LinearSampler, _blurParamsBuffer!));
            using var blurVSet = factory.CreateResourceSet(new ResourceSetDescription(
                _blurLayout!, _blurTempView!, LinearSampler, _blurParamsBuffer!));
            using var compositeSet = factory.CreateResourceSet(new ResourceSetDescription(
                _compositeLayout!, sourceView, _bloomHalfView!, LinearSampler, _compositeParamsBuffer!));

            // Pass 1: Threshold → _bloomHalf (half-res)
            cl.UpdateBuffer(_bloomParamsBuffer, 0, new BloomParamsGPU
            {
                Threshold = Threshold,
                SoftKnee = SoftKnee,
            });
            cl.SetFramebuffer(_bloomHalfFB);
            cl.SetPipeline(_thresholdPipeline);
            cl.SetGraphicsResourceSet(0, thresholdSet);
            cl.Draw(3, 1, 0, 0);

            // Pass 2: Blur H → _blurTemp
            cl.UpdateBuffer(_blurParamsBuffer, 0, new BlurParamsGPU
            {
                Direction = new Vector2(1f / _halfWidth, 0f),
            });
            cl.SetFramebuffer(_blurTempFB);
            cl.SetPipeline(_blurPipeline);
            cl.SetGraphicsResourceSet(0, blurHSet);
            cl.Draw(3, 1, 0, 0);

            // Pass 3: Blur V → _bloomHalf
            cl.UpdateBuffer(_blurParamsBuffer, 0, new BlurParamsGPU
            {
                Direction = new Vector2(0f, 1f / _halfHeight),
            });
            cl.SetFramebuffer(_bloomHalfFB);
            cl.SetPipeline(_blurPipeline);
            cl.SetGraphicsResourceSet(0, blurVSet);
            cl.Draw(3, 1, 0, 0);

            // Pass 4: Composite (scene + bloom) → destination
            cl.UpdateBuffer(_compositeParamsBuffer, 0, new BloomCompositeParamsGPU
            {
                BloomIntensity = Intensity,
            });
            cl.SetFramebuffer(destinationFB);
            cl.SetPipeline(_compositePipeline);
            cl.SetGraphicsResourceSet(0, compositeSet);
            cl.Draw(3, 1, 0, 0);
        }

        private void CreateSizeDependentResources(uint width, uint height)
        {
            var factory = Device.ResourceFactory;
            _halfWidth = Math.Max(width / 2, 1);
            _halfHeight = Math.Max(height / 2, 1);

            // Bloom half-res texture
            _bloomHalf = factory.CreateTexture(TextureDescription.Texture2D(
                _halfWidth, _halfHeight, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _bloomHalfView = factory.CreateTextureView(_bloomHalf);
            _bloomHalfFB = factory.CreateFramebuffer(new FramebufferDescription(null, _bloomHalf));

            // Blur temp texture
            _blurTemp = factory.CreateTexture(TextureDescription.Texture2D(
                _halfWidth, _halfHeight, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _blurTempView = factory.CreateTextureView(_blurTemp);
            _blurTempFB = factory.CreateFramebuffer(new FramebufferDescription(null, _blurTemp));

            // Threshold pipeline → bloom half-res output
            _thresholdPipeline?.Dispose();
            _thresholdPipeline = CreateFullscreenPipeline(_thresholdLayout!, _thresholdShaders!,
                _bloomHalfFB.OutputDescription);

            // Blur pipeline → blur temp output (shared for H and V via different framebuffers)
            _blurPipeline?.Dispose();
            _blurPipeline = CreateFullscreenPipeline(_blurLayout!, _blurShaders!,
                _blurTempFB.OutputDescription);

            // Composite pipeline → outputs to HDR ping-pong buffer (same format as bloom half)
            _compositePipeline?.Dispose();
            _compositePipeline = CreateFullscreenPipeline(_compositeLayout!, _compositeShaders!,
                _bloomHalfFB.OutputDescription);
        }

        private void DisposeSizeDependentResources()
        {
            _thresholdPipeline?.Dispose();
            _blurPipeline?.Dispose();
            _compositePipeline?.Dispose();

            _bloomHalfView?.Dispose();
            _bloomHalfFB?.Dispose();
            _bloomHalf?.Dispose();
            _blurTempView?.Dispose();
            _blurTempFB?.Dispose();
            _blurTemp?.Dispose();
        }

        public override void Dispose()
        {
            DisposeSizeDependentResources();

            _bloomParamsBuffer?.Dispose();
            _blurParamsBuffer?.Dispose();
            _compositeParamsBuffer?.Dispose();
            _thresholdLayout?.Dispose();
            _blurLayout?.Dispose();
            _compositeLayout?.Dispose();

            if (_thresholdShaders != null)
                foreach (var s in _thresholdShaders) s.Dispose();
            if (_blurShaders != null)
                foreach (var s in _blurShaders) s.Dispose();
            if (_compositeShaders != null)
                foreach (var s in _compositeShaders) s.Dispose();

            Debug.Log("[BloomEffect] Disposed");
        }
    }
}
