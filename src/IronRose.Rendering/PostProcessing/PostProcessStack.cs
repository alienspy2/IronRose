// ------------------------------------------------------------
// @file    PostProcessStack.cs
// @brief   포스트 프로세스 이펙트 체인 관리. Ping-pong HDR 버퍼를 사용하여 이펙트를 순차 실행하고,
//          최종 결과를 스왑체인에 블릿한다.
// @deps    PostProcessEffect, ShaderCompiler, EditorDebug
// @exports
//   class PostProcessStack : IDisposable
//     Initialize(GraphicsDevice, uint, uint, Func<string,string>): void — 초기화 (셰이더 리졸버 델리게이트 전달)
//     AddEffect(PostProcessEffect): void               — 이펙트 추가
//     InsertEffect(int, PostProcessEffect): void        — 이펙트 삽입
//     RemoveEffect(PostProcessEffect): bool             — 이펙트 제거
//     MoveEffect(int, int): void                        — 이펙트 순서 변경
//     GetEffect<T>(): T?                                — 타입별 이펙트 검색
//     Execute(CommandList, TextureView, Framebuffer): void — 전체 실행 + 스왑체인 블릿
//     ExecuteEffectsOnly(CommandList, TextureView): TextureView — 이펙트만 실행, HDR 결과 반환
//     BlitToSwapchain(CommandList, TextureView, Framebuffer): void — 감마 보정 블릿
//     Resize(uint, uint): void                          — 해상도 변경
//     PendingDisposal: List<IDisposable>                — 지연 해제 대기열
// @note    셰이더 경로는 Func<string, string> 델리게이트로 전달받으며,
//          IronRose.Engine의 ShaderRegistry.Resolve와 연결된다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Veldrid;
using RoseEngine;

namespace IronRose.Rendering
{
    public class PostProcessStack : IDisposable
    {
        private GraphicsDevice _device = null!;
        private Func<string, string> _shaderResolver = _ => "";
        private Sampler? _linearSampler;

        // Ping-pong HDR textures for chaining effects
        private Texture? _pingTexture;
        private TextureView? _pingView;
        private Framebuffer? _pingFB;

        private Texture? _pongTexture;
        private TextureView? _pongView;
        private Framebuffer? _pongFB;

        // Final blit: HDR intermediate → swapchain (handles format conversion)
        private Pipeline? _blitPipeline;
        private ResourceLayout? _blitLayout;
        private Shader[]? _blitShaders;

        private uint _width;
        private uint _height;

        private readonly List<PostProcessEffect> _effects = new();

        /// <summary>
        /// Pending disposals collected during Resize. Flushed externally by RenderSystem.
        /// </summary>
        public readonly List<IDisposable> PendingDisposal = new();

        private void DeferDispose(IDisposable? resource)
        {
            if (resource != null)
                PendingDisposal.Add(resource);
        }

        public IReadOnlyList<PostProcessEffect> Effects => _effects;

        public void Initialize(GraphicsDevice device, uint width, uint height, Func<string, string> shaderResolver)
        {
            _device = device;
            _shaderResolver = shaderResolver;
            _width = width;
            _height = height;

            _linearSampler = device.ResourceFactory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));

            CreatePingPongBuffers(width, height);
            CreateBlitPipeline();
        }

        public void AddEffect(PostProcessEffect effect)
        {
            effect.InitializeBase(_device, _shaderResolver, _linearSampler!, _width, _height);
            _effects.Add(effect);
        }

        public void InsertEffect(int index, PostProcessEffect effect)
        {
            effect.InitializeBase(_device, _shaderResolver, _linearSampler!, _width, _height);
            _effects.Insert(index, effect);
        }

        public bool RemoveEffect(PostProcessEffect effect)
        {
            if (_effects.Remove(effect))
            {
                effect.Dispose();
                return true;
            }
            return false;
        }

        public void MoveEffect(int fromIndex, int toIndex)
        {
            var effect = _effects[fromIndex];
            _effects.RemoveAt(fromIndex);
            _effects.Insert(toIndex, effect);
        }

        public T? GetEffect<T>() where T : PostProcessEffect
        {
            return _effects.OfType<T>().FirstOrDefault();
        }

        public void Execute(CommandList cl, TextureView hdrSourceView, Framebuffer swapchainFB)
        {
            var finalSource = ExecuteEffectsOnly(cl, hdrSourceView);

            // Final blit: HDR intermediate → swapchain (with gamma correction)
            BlitToSwapchain(cl, finalSource, swapchainFB);
        }

        /// <summary>
        /// Run all enabled post-process effects and return the final HDR TextureView.
        /// Does NOT blit to swapchain — caller is responsible for that.
        /// </summary>
        public TextureView ExecuteEffectsOnly(CommandList cl, TextureView hdrSourceView)
        {
            var enabled = _effects.Where(e => e.Enabled).ToList();

            if (enabled.Count == 0)
                return hdrSourceView;

            var currentSource = hdrSourceView;
            bool usePing = true;

            for (int i = 0; i < enabled.Count; i++)
            {
                var destFB = usePing ? _pingFB! : _pongFB!;
                enabled[i].Execute(cl, currentSource, destFB);
                currentSource = usePing ? _pingView! : _pongView!;
                usePing = !usePing;
            }

            return currentSource;
        }

        /// <summary>
        /// Blit an HDR source to the swapchain framebuffer with gamma correction.
        /// </summary>
        public void BlitToSwapchain(CommandList cl, TextureView source, Framebuffer swapchainFB)
        {
            var factory = _device.ResourceFactory;
            using var resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _blitLayout!, source, _linearSampler!));

            cl.SetFramebuffer(swapchainFB);
            cl.SetFullViewports();
            cl.SetPipeline(_blitPipeline);
            cl.SetGraphicsResourceSet(0, resourceSet);
            cl.Draw(3, 1, 0, 0);
        }

        public void Resize(uint width, uint height)
        {
            _width = width;
            _height = height;
            DeferDisposePingPong();
            CreatePingPongBuffers(width, height);

            DeferDispose(_blitPipeline);
            CreateBlitPipeline();

            foreach (var effect in _effects)
                effect.Resize(width, height);
        }

        private void DeferDisposePingPong()
        {
            DeferDispose(_pingView);
            DeferDispose(_pingFB);
            DeferDispose(_pingTexture);
            DeferDispose(_pongView);
            DeferDispose(_pongFB);
            DeferDispose(_pongTexture);
        }

        private void CreateBlitPipeline()
        {
            var factory = _device.ResourceFactory;

            if (_blitShaders == null)
            {
                string fullscreenVert = _shaderResolver("fullscreen.vert");
                string blitFrag = _shaderResolver("blit.frag");
                _blitShaders = ShaderCompiler.CompileGLSL(_device, fullscreenVert, blitFrag);
            }

            if (_blitLayout == null)
            {
                _blitLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                    new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                    new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
            }

            var blend = new BlendStateDescription(
                RgbaFloat.Black,
                new BlendAttachmentDescription(
                    blendEnabled: true,
                    sourceColorFactor: BlendFactor.SourceAlpha,
                    destinationColorFactor: BlendFactor.InverseSourceAlpha,
                    colorFunction: BlendFunction.Add,
                    sourceAlphaFactor: BlendFactor.One,
                    destinationAlphaFactor: BlendFactor.InverseSourceAlpha,
                    alphaFunction: BlendFunction.Add));

            _blitPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _blitLayout },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                    shaders: _blitShaders),
                Outputs = _device.SwapchainFramebuffer.OutputDescription,
            });
        }

        private void CreatePingPongBuffers(uint width, uint height)
        {
            var factory = _device.ResourceFactory;

            _pingTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _pingView = factory.CreateTextureView(_pingTexture);
            _pingFB = factory.CreateFramebuffer(new FramebufferDescription(null, _pingTexture));

            _pongTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _pongView = factory.CreateTextureView(_pongTexture);
            _pongFB = factory.CreateFramebuffer(new FramebufferDescription(null, _pongTexture));
        }

        private void DisposePingPongBuffers()
        {
            _pingView?.Dispose();
            _pingFB?.Dispose();
            _pingTexture?.Dispose();
            _pongView?.Dispose();
            _pongFB?.Dispose();
            _pongTexture?.Dispose();
        }

        public void Dispose()
        {
            foreach (var effect in _effects)
                effect.Dispose();
            _effects.Clear();

            DisposePingPongBuffers();

            _blitPipeline?.Dispose();
            _blitLayout?.Dispose();
            if (_blitShaders != null)
                foreach (var s in _blitShaders) s.Dispose();

            _linearSampler?.Dispose();

            EditorDebug.Log("[PostProcessStack] Disposed");
        }
    }
}
