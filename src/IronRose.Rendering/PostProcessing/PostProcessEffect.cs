// ------------------------------------------------------------
// @file    PostProcessEffect.cs
// @brief   포스트 프로세스 이펙트의 추상 기본 클래스. 셰이더 리졸버, GPU 디바이스, 샘플러를 공통 관리.
//          파생 클래스는 OnInitialize/Resize/Execute/Dispose를 구현한다.
// @deps    EffectParamAttribute, EffectParameterInfo
// @exports
//   abstract class PostProcessEffect : IDisposable
//     Name: string                                         — 이펙트 이름
//     Enabled: bool                                        — 활성/비활성
//     InitializeBase(GraphicsDevice, Func<string,string>, Sampler, uint, uint): void — 기본 초기화
//     GetParameters(): IReadOnlyList<EffectParameterInfo>   — 리플렉션 기반 파라미터 목록
//     GetNeutralValues(): Dictionary<string, float>         — Volume 블렌딩 중립값
//     Resize(uint, uint): void                              — 해상도 변경
//     Execute(CommandList, TextureView, Framebuffer): void   — 이펙트 실행
//     PendingDisposal: List<IDisposable>                    — 지연 해제 대기열
// @note    셰이더 경로는 Func<string, string> ShaderResolver 델리게이트로 전달받는다.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Veldrid;

namespace IronRose.Rendering
{
    public abstract class PostProcessEffect : IDisposable
    {
        public abstract string Name { get; }
        public bool Enabled { get; set; } = true;

        protected GraphicsDevice Device = null!;
        protected Sampler LinearSampler = null!;
        protected Func<string, string> ShaderResolver = _ => "";

        /// <summary>
        /// Pending disposals collected during Resize. Flushed externally by RenderSystem.
        /// </summary>
        public readonly List<IDisposable> PendingDisposal = new();

        protected void DeferDispose(IDisposable? resource)
        {
            if (resource != null)
                PendingDisposal.Add(resource);
        }

        private List<EffectParameterInfo>? _cachedParams;

        public void InitializeBase(GraphicsDevice device, Func<string, string> shaderResolver, Sampler linearSampler, uint width, uint height)
        {
            Device = device;
            ShaderResolver = shaderResolver;
            LinearSampler = linearSampler;
            OnInitialize(width, height);
        }

        protected abstract void OnInitialize(uint width, uint height);
        public abstract void Resize(uint width, uint height);
        public abstract void Execute(CommandList cl, TextureView sourceView, Framebuffer destinationFB);
        public abstract void Dispose();

        public IReadOnlyList<EffectParameterInfo> GetParameters()
        {
            if (_cachedParams != null) return _cachedParams;

            _cachedParams = new List<EffectParameterInfo>();
            var props = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                var attr = prop.GetCustomAttribute<EffectParamAttribute>();
                if (attr == null) continue;

                var p = prop; // capture for lambda
                _cachedParams.Add(new EffectParameterInfo(
                    attr.DisplayName,
                    p.PropertyType,
                    attr.Min,
                    attr.Max,
                    () => p.GetValue(this)!,
                    v => p.SetValue(this, v)));
            }

            return _cachedParams;
        }

        /// <summary>
        /// 이펙트가 "적용되지 않은" 상태의 파라미터 값.
        /// Volume 블렌딩에서 Volume 밖일 때 기준값으로 사용.
        /// 기본 구현: 각 [EffectParam] 프로퍼티의 기본 인스턴스 값을 반환.
        /// </summary>
        public virtual Dictionary<string, float> GetNeutralValues()
        {
            var result = new Dictionary<string, float>();
            foreach (var param in GetParameters())
            {
                if (param.ValueType == typeof(float))
                    result[param.Name] = 0f;
                else if (param.ValueType == typeof(int))
                    result[param.Name] = 0f;
                else if (param.ValueType == typeof(bool))
                    result[param.Name] = 0f;
            }
            return result;
        }

        protected Pipeline CreateFullscreenPipeline(ResourceLayout layout, Shader[] shaders,
            OutputDescription outputDesc, BlendStateDescription? blendState = null)
        {
            return Device.ResourceFactory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blendState ?? BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                    shaders: shaders),
                Outputs = outputDesc,
            });
        }
    }
}
