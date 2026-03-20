using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using RoseEngine;
using IronRose.Engine;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct TransformUniforms
    {
        public System.Numerics.Matrix4x4 World;
        public System.Numerics.Matrix4x4 ViewProjection;
        public System.Numerics.Matrix4x4 PrevViewProjection;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MaterialUniforms
    {
        public Vector4 Color;
        public Vector4 Emission;
        public float HasTexture;
        public float Metallic;
        public float Roughness;
        public float Occlusion;
        public float NormalMapStrength;
        public float HasNormalMap;
        public float HasMROMap;
        private float _pad1;
        public System.Numerics.Vector2 TextureOffset;
        public System.Numerics.Vector2 TextureScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LightInfoGPU
    {
        public Vector4 PositionOrDirection; // xyz = pos/dir, w = type (0=dir, 1=point, 2=spot)
        public Vector4 ColorIntensity;      // rgb = color, a = intensity
        public Vector4 Params;              // x = range, y = cosInnerAngle (spot), z = cosOuterAngle (spot)
        public Vector4 SpotDirection;       // xyz = spot forward direction (spot only)
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LightUniforms
    {
        public Vector4 CameraPos;   // xyz = camera pos, w = unused
        public int LightCount;
        public int _pad1;
        public int _pad2;
        public int _pad3;
        public LightInfoGPU Light0;
        public LightInfoGPU Light1;
        public LightInfoGPU Light2;
        public LightInfoGPU Light3;
        public LightInfoGPU Light4;
        public LightInfoGPU Light5;
        public LightInfoGPU Light6;
        public LightInfoGPU Light7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AmbientUniforms
    {
        public Vector4 CameraPos;       // 16 bytes
        public Vector4 SkyAmbientColor; // 16 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LightVolumeUniforms
    {
        public System.Numerics.Matrix4x4 WorldViewProjection;  // 64 bytes
        public System.Numerics.Matrix4x4 LightViewProjection;  // 64 bytes (shadow map VP)
        public Vector4 CameraPos;                               // 16 bytes
        public Vector4 ScreenParams;                            // 16 bytes (x=width, y=height)
        public Vector4 ShadowParams;                            // 16 bytes (x=hasShadow, y=bias, z=normalBias, w=strength)
        public Vector4 ShadowAtlasParams;                       // 16 bytes (xy=tileOffset, zw=tileScale)
        public LightInfoGPU Light;                              // 64 bytes
        // Point light face data (6 faces for cubemap → atlas mapping):
        public System.Numerics.Matrix4x4 FaceVP0;              // 64 bytes
        public System.Numerics.Matrix4x4 FaceVP1;              // 64 bytes
        public System.Numerics.Matrix4x4 FaceVP2;              // 64 bytes
        public System.Numerics.Matrix4x4 FaceVP3;              // 64 bytes
        public System.Numerics.Matrix4x4 FaceVP4;              // 64 bytes
        public System.Numerics.Matrix4x4 FaceVP5;              // 64 bytes
        public Vector4 FaceAtlasParams0;                        // 16 bytes
        public Vector4 FaceAtlasParams1;                        // 16 bytes
        public Vector4 FaceAtlasParams2;                        // 16 bytes
        public Vector4 FaceAtlasParams3;                        // 16 bytes
        public Vector4 FaceAtlasParams4;                        // 16 bytes
        public Vector4 FaceAtlasParams5;                        // 16 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShadowTransformUniforms
    {
        public System.Numerics.Matrix4x4 LightMVP;  // 64 bytes
        public Vector4 DepthParams;                   // 16 bytes: x=useLinearDepth(1/0), y=near, z=far
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShadowBlurParams
    {
        public Vector4 Direction;    // xy = texel step direction (scaled by softness)
        public Vector4 TileParams;   // xy = tile offset in atlas UV, zw = tile scale
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DebugOverlayParamsGPU
    {
        public float Mode;
        public float _pad1;
        public float _pad2;
        public float _pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SkyboxUniforms
    {
        public System.Numerics.Matrix4x4 InverseViewProjection;  // 64 bytes
        public Vector4 SunDirection;                              // 16 bytes (xyz=dir, w=unused)
        public Vector4 SkyParams;                                 // 16 bytes (x=zenithIntensity, y=horizonIntensity, z=sunAngularRadius, w=sunIntensity)
        public Vector4 ZenithColor;                               // 16 bytes
        public Vector4 HorizonColor;                              // 16 bytes
        public Vector4 TextureParams;                             // 16 bytes (x=hasTexture, y=exposure, z=rotation, w=unused)
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EnvMapUniforms
    {
        public Vector4 TextureParams;   // 16 bytes (x=hasTexture, y=exposure, z=rotation(rad), w=unused)
        public Vector4 SunDirection;    // 16 bytes (xyz=direction toward sun, w=unused)
        public Vector4 SkyParams;       // 16 bytes (x=zenithIntensity, y=horizonIntensity, z=sunAngularRadius, w=sunIntensity)
        public Vector4 ZenithColor;     // 16 bytes (rgb=zenith color)
        public Vector4 HorizonColor;    // 16 bytes (rgb=horizon color)
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILPrefilterParams
    {
        public System.Numerics.Vector2 TexelSize;
        public float NearPlane;
        public float FarPlane;
        public int MipLevel;
        public int SrcWidth;
        public int SrcHeight;
        public float _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILMainParams
    {
        public System.Numerics.Matrix4x4 ViewMatrix;
        public System.Numerics.Matrix4x4 ProjectionMatrix;
        public System.Numerics.Vector2 Resolution;
        public float Radius;
        public float FalloffScale;
        public int SliceCount;
        public int StepsPerSlice;
        public int FrameIndex;
        public float DepthMipSamplingOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILMainFlags
    {
        public int EnableIndirect;
        public int _pad0;
        public int _pad1;
        public int _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILDenoiseParams
    {
        public System.Numerics.Vector2 TexelSize;
        public float DepthThreshold;
        public float NormalThreshold;
        public System.Numerics.Vector2 Direction;
        public float _pad1;
        public float _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILDenoiseFlags
    {
        public int HasIndirect;
        public int _pad1;
        public int _pad2;
        public int _pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILOutputParams
    {
        public float IndirectBoost;
        public float SaturationBoost;
        public float AoIntensity;
        public float _pad0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSILTemporalParams
    {
        public System.Numerics.Matrix4x4 PrevViewProj;
        public System.Numerics.Vector2 Resolution;
        public float BlendFactor;
        public float _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FsrUpscaleParams
    {
        public System.Numerics.Vector2 RenderSize;
        public System.Numerics.Vector2 DisplaySize;
        public System.Numerics.Vector2 RenderSizeRcp;
        public System.Numerics.Vector2 DisplaySizeRcp;
        public System.Numerics.Vector2 JitterOffset;
        public int FrameIndex;
        public float _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CasParams
    {
        public System.Numerics.Vector2 ImageSize;
        public System.Numerics.Vector2 ImageSizeRcp;
        public float Sharpness;
        public float _pad0;
        public float _pad1;
        public float _pad2;
    }

    public partial class RenderSystem : IDisposable
    {
        private GraphicsDevice? _device;

        // --- Deferred resource disposal ---
        // Resources are NOT disposed immediately during Resize, because the current frame's
        // CommandList may still reference them (not yet submitted). Instead, they're queued
        // here and disposed at the start of the next Render() call, when the previous frame
        // has been fully submitted and completed.
        private readonly List<IDisposable> _pendingDisposal = new();

        private void DeferDispose(IDisposable? resource)
        {
            if (resource != null)
                _pendingDisposal.Add(resource);
        }

        /// <summary>
        /// Flush deferred disposals from RenderSystem and all RenderContexts.
        /// Call at a safe point (start of Render, after previous frame submitted).
        /// </summary>
        private void FlushPendingDisposal()
        {
            bool hasAny = _pendingDisposal.Count > 0;
            if (_gameViewContext?.HasPendingDisposal() == true) hasAny = true;
            if (_sceneViewContext?.HasPendingDisposal() == true) hasAny = true;

            if (!hasAny) return;

            _device?.WaitForIdle();

            foreach (var r in _pendingDisposal) r.Dispose();
            _pendingDisposal.Clear();

            _gameViewContext?.FlushPendingDisposal();
            _sceneViewContext?.FlushPendingDisposal();
        }

        // --- Shared resources ---
        private DeviceBuffer? _transformBuffer;
        private DeviceBuffer? _materialBuffer;
        private ResourceLayout? _perObjectLayout;
        private Sampler? _defaultSampler;
        private Texture2D? _whiteTexture;
        private Texture2D? _defaultNormalTexture;
        private Texture2D? _defaultMROTexture;
        private Cubemap? _whiteCubemap;
        private readonly Dictionary<(TextureView, TextureView, TextureView), ResourceSet> _resourceSetCache = new();
        private ResourceSet? _defaultResourceSet;

        // --- Forward rendering (sprites/text/wireframe) ---
        private Veldrid.Shader[]? _forwardShaders;
        private Pipeline? _forwardPipeline;
        private Pipeline? _wireframePipeline;
        private Pipeline? _spritePipeline;
        private DeviceBuffer? _lightBuffer;
        private ResourceLayout? _perFrameLayout;
        private ResourceSet? _perFrameResourceSet;

        // --- RenderContext (per-viewport resources) ---
        private RenderContext? _gameViewContext;
        private RenderContext? _sceneViewContext;
        public RenderContext? GameViewContext => _gameViewContext;
        public RenderContext? SceneViewContext => _sceneViewContext;

        // Set during Render() for use by partial class methods (SSIL, Draw, Lighting, Debug)
        private RenderContext? _activeCtx;

        // --- Deferred rendering ---
        private Veldrid.Shader[]? _geometryShaders;
        private Pipeline? _geometryPipeline;

        // --- Light volume rendering ---
        private Veldrid.Shader[]? _ambientShaders;
        private Veldrid.Shader[]? _directionalLightShaders;
        private Veldrid.Shader[]? _pointLightShaders;
        private Veldrid.Shader[]? _spotLightShaders;

        private Pipeline? _ambientPipeline;
        private Pipeline? _directionalLightPipeline;
        private Pipeline? _pointLightPipeline;
        private Pipeline? _spotLightPipeline;

        private ResourceLayout? _gBufferLayout;
        private ResourceLayout? _ambientLayout;
        private ResourceLayout? _lightVolumeShadowLayout; // All light types (UBO + Texture2D + Sampler)

        private ResourceSet? _ambientResourceSet;
        private ResourceSet? _atlasShadowSet;             // Unified atlas resource set for all lights

        private DeviceBuffer? _ambientBuffer;
        private DeviceBuffer? _lightVolumeBuffer;
        private DeviceBuffer? _envMapBuffer;

        // --- Shadow atlas ---
        private const int AtlasSize = 4096;
        private Veldrid.Shader[]? _shadowAtlasShaders;
        private Pipeline? _shadowAtlasBackCullPipeline;
        private Pipeline? _shadowAtlasFrontCullPipeline;
        private Pipeline? _shadowAtlasNoCullPipeline;
        private ResourceLayout? _shadowLayout;
        private DeviceBuffer? _shadowTransformBuffer;
        private Sampler? _shadowSampler;
        private Texture? _atlasTexture;
        private Texture? _atlasDepthTexture;
        private TextureView? _atlasView;
        private Framebuffer? _atlasFramebuffer;

        // --- VSM blur ---
        private Veldrid.Shader[]? _shadowBlurShaders;
        private Pipeline? _shadowBlurPipeline;
        private ResourceLayout? _shadowBlurLayout;
        private DeviceBuffer? _shadowBlurBuffer;
        private Texture? _atlasTempTexture;
        private TextureView? _atlasTempView;
        private Framebuffer? _atlasTempFramebuffer;
        private Framebuffer? _atlasBlurFramebuffer; // atlas texture without depth (for V pass write-back)
        private ResourceSet? _shadowBlurSetH;       // H pass: read atlas
        private ResourceSet? _shadowBlurSetV;       // V pass: read temp

        // Per-frame shadow tile info (rebuilt each shadow pass)
        private struct FrameShadowTile
        {
            public System.Numerics.Matrix4x4 LightVP;
            public Vector4 AtlasParams;          // xy=offset, zw=scale
            public System.Numerics.Matrix4x4[]? FaceVPs;         // Point: 6
            public Vector4[]? FaceAtlasParams;   // Point: 6
        }
        private readonly Dictionary<Light, FrameShadowTile> _frameShadows = new();

        // Atlas tile allocator state
        private int _atlasPackX, _atlasPackY, _atlasRowHeight;

        private Mesh? _lightSphereMesh;
        private Mesh? _lightConeMesh;
        private TextureView? _currentAmbientEnvMapView;

        // --- Skybox ---
        private Veldrid.Shader[]? _skyboxShaders;
        private Pipeline? _skyboxPipeline;
        private DeviceBuffer? _skyboxUniformBuffer;
        private ResourceLayout? _skyboxLayout;
        private ResourceSet? _skyboxResourceSet;
        private TextureView? _currentSkyboxTextureView; // Track for resource set invalidation

        // --- SSIL / GTAO (shared: shaders, pipelines, layouts, UBO buffers, sampler) ---
        private Veldrid.Shader? _ssilPrefilterShader;
        private Veldrid.Shader? _ssilMainShader;
        private Veldrid.Shader? _ssilDenoiseShader;
        private Pipeline? _ssilPrefilterPipeline;
        private Pipeline? _ssilMainPipeline;
        private Pipeline? _ssilDenoisePipeline;
        private ResourceLayout? _ssilPrefilterLayout;
        private ResourceLayout? _ssilMainLayout;
        private ResourceLayout? _ssilDenoiseLayout;
        private ResourceLayout? _ssilOutputLayout;  // Set 2 for ambient pass
        private DeviceBuffer? _ssilPrefilterParamsBuffer;
        private DeviceBuffer? _ssilMainParamsBuffer;
        private DeviceBuffer? _ssilDenoiseParamsBuffer;
        private DeviceBuffer? _ssilMainFlagsBuffer;
        private DeviceBuffer? _ssilDenoiseFlagsBuffer;
        private Sampler? _ssilClampSampler;
        private DeviceBuffer? _ssilOutputParamsBuffer;

        // Temporal filter (shared: shader, pipeline, layout, UBO buffer)
        private Veldrid.Shader? _ssilTemporalShader;
        private Pipeline? _ssilTemporalPipeline;
        private ResourceLayout? _ssilTemporalLayout;
        private DeviceBuffer? _ssilTemporalParamsBuffer;

        /// <summary>
        /// When set, final blit and debug overlay render to this instead of the swapchain.
        /// Used by ImGui editor to render scene to an offscreen texture for Game View.
        /// </summary>
        public Framebuffer? OverrideOutputFramebuffer { get; set; }

        // --- Material override (drag-hover preview) ---
        private int _materialOverrideObjectId;
        private Material? _materialOverride;

        public void SetMaterialOverride(int objectId, Material? material)
        {
            _materialOverrideObjectId = objectId;
            _materialOverride = material;
        }

        public void ClearMaterialOverride()
        {
            _materialOverrideObjectId = 0;
            _materialOverride = null;
        }

        private Framebuffer FinalOutputFramebuffer => OverrideOutputFramebuffer ?? _device!.SwapchainFramebuffer;

        // --- FSR Upscaler (shared: shader, pipeline, layout, UBO buffer) ---
        private bool _fsrWasEnabled;               // 런타임 토글 감지
        private FsrScaleMode _fsrLastScaleMode;    // 배율 변경 감지
        private float _fsrLastCustomScale;         // 커스텀 배율 변경 감지
        private Veldrid.Shader? _fsrUpscaleShader;
        private Pipeline? _fsrUpscalePipeline;
        private ResourceLayout? _fsrUpscaleLayout;
        private DeviceBuffer? _fsrParamsBuffer;

        // --- CAS Sharpening (shared: shader, pipeline, layout, UBO buffer) ---
        private Veldrid.Shader? _casShader;
        private Pipeline? _casPipeline;
        private ResourceLayout? _casLayout;
        private DeviceBuffer? _casParamsBuffer;

        // --- Post-processing ---
        public PostProcessStack? PostProcessing => _gameViewContext?.PostProcessStack;

        // --- Debug overlay ---
        private Veldrid.Shader[]? _debugOverlayShaders;
        private Pipeline? _debugOverlayPipeline;
        private ResourceLayout? _debugOverlayLayout;
        private DeviceBuffer? _debugOverlayParamsBuffer;
        private Sampler? _debugOverlaySampler;


        public void Initialize(GraphicsDevice device)
        {
            _device = device;
            var factory = device.ResourceFactory;
            uint width = device.SwapchainFramebuffer.Width;
            uint height = device.SwapchainFramebuffer.Height;

            // --- Compile shaders ---
            _forwardShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("vertex.glsl"),
                ShaderRegistry.Resolve("fragment.glsl"));

            _geometryShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("deferred_geometry.vert"),
                ShaderRegistry.Resolve("deferred_geometry.frag"));

            _ambientShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("deferred_lighting.vert"),
                ShaderRegistry.Resolve("deferred_ambient.frag"));

            _directionalLightShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("deferred_lighting.vert"),
                ShaderRegistry.Resolve("deferred_directlight.frag"));

            _pointLightShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("deferred_pointlight.vert"),
                ShaderRegistry.Resolve("deferred_pointlight.frag"));

            _spotLightShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("deferred_spotlight.vert"),
                ShaderRegistry.Resolve("deferred_spotlight.frag"));

            _shadowAtlasShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("shadow.vert"),
                ShaderRegistry.Resolve("shadow_atlas.frag"));

            _shadowBlurShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("fullscreen.vert"),
                ShaderRegistry.Resolve("shadow_blur.frag"));

            _skyboxShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("skybox.vert"),
                ShaderRegistry.Resolve("skybox.frag"));

            // --- SSIL compute shaders ---
            _ssilPrefilterShader = ShaderCompiler.CompileComputeGLSL(device,
                ShaderRegistry.Resolve("ssil_prefilter_depth.comp"));
            _ssilMainShader = ShaderCompiler.CompileComputeGLSL(device,
                ShaderRegistry.Resolve("ssil_main.comp"));
            _ssilDenoiseShader = ShaderCompiler.CompileComputeGLSL(device,
                ShaderRegistry.Resolve("ssil_denoise.comp"));
            _ssilTemporalShader = ShaderCompiler.CompileComputeGLSL(device,
                ShaderRegistry.Resolve("ssil_temporal.comp"));

            // --- FSR Upscaler compute shader ---
            var fsrShaderPath = ShaderRegistry.Resolve("fsr_upscale.comp");
            _fsrUpscaleShader = ShaderCompiler.CompileComputeGLSL(device, fsrShaderPath);

            // --- CAS Sharpening compute shader ---
            _casShader = ShaderCompiler.CompileComputeGLSL(device,
                ShaderRegistry.Resolve("fsr_cas.comp"));

            // --- Uniform buffers ---
            _transformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<TransformUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _materialBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<MaterialUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _lightBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<LightUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _ambientBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<AmbientUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _lightVolumeBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<LightVolumeUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _skyboxUniformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SkyboxUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _envMapBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<EnvMapUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // --- Sampler ---
            _defaultSampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Wrap, SamplerAddressMode.Wrap, SamplerAddressMode.Wrap,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, uint.MaxValue, 0, SamplerBorderColor.TransparentBlack));

            // --- Shadow sampler (clamp to border white, for outside-frustum reads) ---
            _shadowSampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Border, SamplerAddressMode.Border, SamplerAddressMode.Border,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, 0, 0, SamplerBorderColor.OpaqueWhite));

            // --- SSIL clamp sampler ---
            _ssilClampSampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, uint.MaxValue, 0, SamplerBorderColor.TransparentBlack));

            // --- SSIL uniform buffers ---
            _ssilPrefilterParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILPrefilterParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _ssilMainParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILMainParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _ssilMainFlagsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILMainFlags>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _ssilDenoiseParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILDenoiseParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _ssilDenoiseFlagsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILDenoiseFlags>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _ssilOutputParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILOutputParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _ssilTemporalParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SSILTemporalParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // --- Shadow transform buffer ---
            _shadowTransformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<ShadowTransformUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // --- White fallback texture ---
            _whiteTexture = Texture2D.CreateWhitePixel();
            _whiteTexture.UploadToGPU(device);

            // --- Default normal map (flat) ---
            _defaultNormalTexture = Texture2D.DefaultNormal;
            _defaultNormalTexture.UploadToGPU(device);
            _defaultMROTexture = Texture2D.DefaultMRO;
            _defaultMROTexture.UploadToGPU(device);

            // --- White fallback cubemap ---
            _whiteCubemap = Cubemap.CreateWhiteCubemap();
            _whiteCubemap.UploadToGPU(device);

            // --- Resource layouts ---
            // Per-object (set 0): transforms + material + texture + sampler
            _perObjectLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Transforms", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MaterialData", ResourceKind.UniformBuffer, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("NormalMap", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MROMap", ResourceKind.TextureReadOnly, ShaderStages.Fragment)));

            // Per-frame (set 1): lights (forward)
            _perFrameLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("LightData", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            // GBuffer layout (set 0, shared by all lighting passes)
            _gBufferLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("gAlbedo", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("gNormal", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("gMaterial", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("gWorldPos", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("gSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Ambient layout (set 1)
            _ambientLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("AmbientData", ResourceKind.UniformBuffer, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("EnvMap", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("EnvMapParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            // Light volume shadow layout (set 1, all light types — UBO + shadow map + sampler)
            _lightVolumeShadowLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("LightVolumeData", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment),
                new ResourceLayoutElementDescription("ShadowMap", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("ShadowSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Shadow pass layout (set 0): MVP transform + depth params
            _shadowLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ShadowTransforms", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));

            // Skybox (set 0): uniform buffer + texture + sampler
            _skyboxLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SkyboxUniforms", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SkyTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SkySampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // --- SSIL compute layouts ---
            _ssilPrefilterLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("depthInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("depthSampler", ResourceKind.Sampler, ShaderStages.Compute),
                new ResourceLayoutElementDescription("depthMipOut", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("PrefilterParams", ResourceKind.UniformBuffer, ShaderStages.Compute),
                new ResourceLayoutElementDescription("prevMipInput", ResourceKind.TextureReadOnly, ShaderStages.Compute)));

            _ssilMainLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("depthMipTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("normalTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("linearSampler", ResourceKind.Sampler, ShaderStages.Compute),
                new ResourceLayoutElementDescription("aoOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("SSILParams", ResourceKind.UniformBuffer, ShaderStages.Compute),
                new ResourceLayoutElementDescription("indirectOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("albedoTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("SSILFlags", ResourceKind.UniformBuffer, ShaderStages.Compute)));

            _ssilDenoiseLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("aoInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("depthMipTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("normalTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("linearSampler", ResourceKind.Sampler, ShaderStages.Compute),
                new ResourceLayoutElementDescription("aoOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("DenoiseParams", ResourceKind.UniformBuffer, ShaderStages.Compute),
                new ResourceLayoutElementDescription("indirectInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("indirectOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("DenoiseFlags", ResourceKind.UniformBuffer, ShaderStages.Compute)));

            // SSIL output layout (Set 2 for ambient pass)
            _ssilOutputLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("gAO", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("gIndirect", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SSILOutputParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            // SSIL temporal layout
            _ssilTemporalLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("currentAO", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("currentIndirect", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("historyAO", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("historyIndirect", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("worldPosTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("linearSampler", ResourceKind.Sampler, ShaderStages.Compute),
                new ResourceLayoutElementDescription("aoOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("indirectOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("TemporalParams", ResourceKind.UniformBuffer, ShaderStages.Compute)));

            // --- SSIL compute pipelines ---
            _ssilPrefilterPipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                _ssilPrefilterShader!, new[] { _ssilPrefilterLayout }, 16, 16, 1));
            _ssilMainPipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                _ssilMainShader!, new[] { _ssilMainLayout }, 8, 8, 1));
            _ssilDenoisePipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                _ssilDenoiseShader!, new[] { _ssilDenoiseLayout }, 8, 8, 1));
            _ssilTemporalPipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                _ssilTemporalShader!, new[] { _ssilTemporalLayout }, 8, 8, 1));

            // --- FSR Upscaler layout + pipeline ---
            _fsrUpscaleLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("colorInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("depthInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("velocityInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("linearSampler", ResourceKind.Sampler, ShaderStages.Compute),
                new ResourceLayoutElementDescription("historyInput", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("upscaledOutput", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("FsrParams", ResourceKind.UniformBuffer, ShaderStages.Compute)));

            _fsrUpscalePipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                _fsrUpscaleShader!, new[] { _fsrUpscaleLayout }, 8, 8, 1));

            _fsrParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<FsrUpscaleParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // --- CAS Sharpening layout + pipeline ---
            _casLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("inputTex", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("linearSampler", ResourceKind.Sampler, ShaderStages.Compute),
                new ResourceLayoutElementDescription("outputTex", ResourceKind.TextureReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("CasParams", ResourceKind.UniformBuffer, ShaderStages.Compute)));
            _casPipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                _casShader!, new[] { _casLayout }, 8, 8, 1));
            _casParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<CasParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // --- Forward resource sets ---
            _perFrameResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _perFrameLayout, _lightBuffer));

            _defaultResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _perObjectLayout, _transformBuffer, _materialBuffer, _whiteTexture.TextureView!, _defaultSampler, _defaultNormalTexture!.TextureView!, _defaultMROTexture!.TextureView!));

            _skyboxResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _skyboxLayout, _skyboxUniformBuffer, _whiteCubemap!.TextureView!, _defaultSampler));
            _currentSkyboxTextureView = null;

            // --- Shadow Atlas (VSM: R32_G32_Float for depth moments) ---
            _atlasTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)AtlasSize, (uint)AtlasSize, 1, 1, PixelFormat.R32_G32_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _atlasDepthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)AtlasSize, (uint)AtlasSize, 1, 1, PixelFormat.D32_Float_S8_UInt,
                TextureUsage.DepthStencil));
            _atlasView = factory.CreateTextureView(_atlasTexture);
            _atlasFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                _atlasDepthTexture, _atlasTexture));

            // --- VSM blur temp texture + framebuffers ---
            _atlasTempTexture = factory.CreateTexture(TextureDescription.Texture2D(
                (uint)AtlasSize, (uint)AtlasSize, 1, 1, PixelFormat.R32_G32_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _atlasTempView = factory.CreateTextureView(_atlasTempTexture);
            _atlasTempFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                null, _atlasTempTexture));
            _atlasBlurFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                null, _atlasTexture));

            // --- VSM blur layout + buffer + resource sets ---
            _shadowBlurLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("BlurParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            _shadowBlurBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<ShadowBlurParams>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _shadowBlurSetH = factory.CreateResourceSet(new ResourceSetDescription(
                _shadowBlurLayout, _atlasView, _shadowSampler, _shadowBlurBuffer));
            _shadowBlurSetV = factory.CreateResourceSet(new ResourceSetDescription(
                _shadowBlurLayout, _atlasTempView, _shadowSampler, _shadowBlurBuffer));

            // --- Unified atlas shadow resource set (for all light types) ---
            _atlasShadowSet = factory.CreateResourceSet(new ResourceSetDescription(
                _lightVolumeShadowLayout, _lightVolumeBuffer, _atlasView, _shadowSampler));

            // --- Light volume meshes ---
            _lightSphereMesh = PrimitiveGenerator.CreateSphere(12, 8);
            _lightSphereMesh.UploadToGPU(device);

            _lightConeMesh = PrimitiveGenerator.CreateCone(16);
            _lightConeMesh.UploadToGPU(device);

            // --- Debug Overlay (CreatePipelines보다 먼저 생성해야 파이프라인에 포함됨) ---
            _debugOverlayShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("fullscreen.vert"),
                ShaderRegistry.Resolve("debug_overlay.frag"));

            _debugOverlayParamsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<DebugOverlayParamsGPU>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            _debugOverlayLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("SourceTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("SourceSampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("DebugParams", ResourceKind.UniformBuffer, ShaderStages.Fragment)));

            _debugOverlaySampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));

            // --- Create pipelines (shared across all contexts) ---
            CreatePipelines(factory);

            // --- Create game view context ---
            _gameViewContext = new RenderContext("GameView");
            CreateContextResources(_gameViewContext, width, height);

            // --- PostProcessing Stack (render resolution) ---
            _gameViewContext.PostProcessStack = new PostProcessStack();
            _gameViewContext.PostProcessStack.Initialize(device, _gameViewContext.RenderWidth, _gameViewContext.RenderHeight, ShaderRegistry.Resolve);
            _gameViewContext.PostProcessStack.AddEffect(new BloomEffect());
            _gameViewContext.PostProcessStack.AddEffect(new TonemapEffect());

            Debug.Log("[RenderSystem] Light volume PBR pipeline initialized");
        }

        private void CreatePipelines(ResourceFactory factory)
        {
            // --- Vertex layout ---
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("UV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            // Constant OutputDescriptions (resolution-independent)
            var gBufferOutputDesc = new OutputDescription(
                new OutputAttachmentDescription(PixelFormat.D32_Float_S8_UInt),
                new OutputAttachmentDescription(PixelFormat.R8_G8_B8_A8_UNorm),
                new OutputAttachmentDescription(PixelFormat.R16_G16_B16_A16_Float),
                new OutputAttachmentDescription(PixelFormat.R8_G8_B8_A8_UNorm),
                new OutputAttachmentDescription(PixelFormat.R16_G16_B16_A16_Float),
                new OutputAttachmentDescription(PixelFormat.R16_G16_Float));

            var hdrOutputDesc = new OutputDescription(
                new OutputAttachmentDescription(PixelFormat.D32_Float_S8_UInt),
                new OutputAttachmentDescription(PixelFormat.R16_G16_B16_A16_Float));

            // --- Geometry Pipeline (→ GBuffer, 5 color + depth) ---
            // Explicitly provide 5 blend attachments for MRT
            var gBufferBlend = new BlendStateDescription
            {
                AttachmentStates = new[]
                {
                    BlendAttachmentDescription.OverrideBlend, // RT0: Albedo
                    BlendAttachmentDescription.OverrideBlend, // RT1: Normal
                    BlendAttachmentDescription.OverrideBlend, // RT2: Material
                    BlendAttachmentDescription.OverrideBlend, // RT3: WorldPos
                    BlendAttachmentDescription.OverrideBlend, // RT4: Velocity
                }
            };
            _geometryPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = gBufferBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: true, comparisonKind: ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.Back, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _perObjectLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _geometryShaders!),
                Outputs = gBufferOutputDesc,
            });

            // --- Additive blend state (shared by directional + point light passes) ---
            var additiveBlend = new BlendStateDescription(
                RgbaFloat.Black,
                new BlendAttachmentDescription(
                    blendEnabled: true,
                    sourceColorFactor: BlendFactor.One,
                    destinationColorFactor: BlendFactor.One,
                    colorFunction: BlendFunction.Add,
                    sourceAlphaFactor: BlendFactor.One,
                    destinationAlphaFactor: BlendFactor.One,
                    alphaFunction: BlendFunction.Add));

            // --- Ambient Pipeline (→ HDR, fullscreen, overwrite) ---
            _ambientPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gBufferLayout!, _ambientLayout!, _ssilOutputLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                    shaders: _ambientShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Shadow Atlas Pipelines (Back / Front / NoCull) ---
            var shadowPipelineBase = new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: true, comparisonKind: ComparisonKind.LessEqual),
                RasterizerState = default,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _shadowLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _shadowAtlasShaders!),
                Outputs = _atlasFramebuffer!.OutputDescription,
            };
            RasterizerStateDescription ShadowRasterizer(FaceCullMode cull) => new(
                cullMode: cull, fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false);

            shadowPipelineBase.RasterizerState = ShadowRasterizer(FaceCullMode.Back);
            _shadowAtlasBackCullPipeline = factory.CreateGraphicsPipeline(shadowPipelineBase);

            shadowPipelineBase.RasterizerState = ShadowRasterizer(FaceCullMode.Front);
            _shadowAtlasFrontCullPipeline = factory.CreateGraphicsPipeline(shadowPipelineBase);

            shadowPipelineBase.RasterizerState = ShadowRasterizer(FaceCullMode.None);
            _shadowAtlasNoCullPipeline = factory.CreateGraphicsPipeline(shadowPipelineBase);

            // --- VSM Shadow Blur Pipeline (fullscreen quad, no depth) ---
            _shadowBlurPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _shadowBlurLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                    shaders: _shadowBlurShaders!),
                Outputs = _atlasTempFramebuffer!.OutputDescription,
            });

            // --- Directional Light Pipeline (→ HDR, fullscreen, additive) ---
            _directionalLightPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = additiveBlend,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gBufferLayout!, _lightVolumeShadowLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                    shaders: _directionalLightShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Point Light Pipeline (→ HDR, sphere mesh, additive, cubemap shadow) ---
            // CullMode.Front + GreaterEqual: 뒷면(구 내부면)을 렌더하여
            // 구의 먼 면이 지오메트리 뒤에 있을 때 통과 → 카메라 위치와 무관하게 동작
            _pointLightPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = additiveBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.GreaterEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.Front, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gBufferLayout!, _lightVolumeShadowLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _pointLightShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Spot Light Pipeline (→ HDR, cone mesh, additive) ---
            // CullMode.Front + GreaterEqual: 뒷면(콘 내부면)을 렌더하여
            // 콘의 먼 면이 지오메트리 뒤에 있을 때 통과 → 카메라가 콘 외부에 있어도 동작
            _spotLightPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = additiveBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.GreaterEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.Front, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _gBufferLayout!, _lightVolumeShadowLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _spotLightShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Skybox Pipeline (→ HDR, depth test LessEqual, no depth write) ---
            _skyboxPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _skyboxLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                    shaders: _skyboxShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Forward Pipeline (→ HDR, for fallback solid rendering) ---
            _forwardPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: true, comparisonKind: ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.Back, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _perObjectLayout!, _perFrameLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _forwardShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Wireframe Pipeline (→ HDR) ---
            _wireframePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = BlendStateDescription.SingleOverrideBlend,
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Wireframe,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _perObjectLayout!, _perFrameLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _forwardShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Sprite Pipeline (→ HDR, alpha blend) ---
            _spritePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = new BlendStateDescription(
                    RgbaFloat.Black,
                    new BlendAttachmentDescription(
                        blendEnabled: true,
                        sourceColorFactor: BlendFactor.SourceAlpha,
                        destinationColorFactor: BlendFactor.InverseSourceAlpha,
                        colorFunction: BlendFunction.Add,
                        sourceAlphaFactor: BlendFactor.One,
                        destinationAlphaFactor: BlendFactor.InverseSourceAlpha,
                        alphaFunction: BlendFunction.Add)),
                DepthStencilState = new DepthStencilStateDescription(
                    depthTestEnabled: true, depthWriteEnabled: false, comparisonKind: ComparisonKind.LessEqual),
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _perObjectLayout!, _perFrameLayout! },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _forwardShaders!),
                Outputs = hdrOutputDesc,
            });

            // --- Debug Overlay Pipeline (→ Swapchain, overwrite) ---
            if (_debugOverlayShaders != null && _debugOverlayLayout != null)
            {
                _debugOverlayPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
                {
                    BlendState = BlendStateDescription.SingleOverrideBlend,
                    DepthStencilState = DepthStencilStateDescription.Disabled,
                    RasterizerState = new RasterizerStateDescription(
                        cullMode: FaceCullMode.None, fillMode: PolygonFillMode.Solid,
                        frontFace: FrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
                    PrimitiveTopology = PrimitiveTopology.TriangleList,
                    ResourceLayouts = new[] { _debugOverlayLayout },
                    ShaderSet = new ShaderSetDescription(
                        vertexLayouts: Array.Empty<VertexLayoutDescription>(),
                        shaders: _debugOverlayShaders),
                    Outputs = _device!.SwapchainFramebuffer.OutputDescription,
                });
            }
        }

        public void CreateContextResources(RenderContext ctx, uint width, uint height)
        {
            var factory = _device!.ResourceFactory;

            // Calculate render vs display resolution
            ctx.DisplayWidth = width;
            ctx.DisplayHeight = height;
            (ctx.RenderWidth, ctx.RenderHeight) = CalcRenderResolution(width, height);

            // Defer disposal of old size-dependent resources.
            // These may still be referenced by the current frame's CommandList (not yet submitted).
            // They will be actually disposed at the start of the next Render() call.
            ctx.DeferDispose(ctx.GBufferResourceSet);
            DeferDispose(_ambientResourceSet);
            ctx.DeferDispose(ctx.HdrView);
            ctx.DeferDispose(ctx.HdrFramebuffer);
            ctx.DeferDispose(ctx.HdrTexture);

            // FSR size-dependent
            ctx.DeferDispose(ctx.FsrUpscaleSet);
            ctx.DeferDispose(ctx.UpscaledView);
            ctx.DeferDispose(ctx.UpscaledTexture);
            ctx.DeferDispose(ctx.HistoryView);
            ctx.DeferDispose(ctx.HistoryTexture);
            // CAS size-dependent
            ctx.DeferDispose(ctx.CasSet);
            ctx.DeferDispose(ctx.CasView);
            ctx.DeferDispose(ctx.CasTexture);

            // SSIL size-dependent
            ctx.DeferDispose(ctx.SsilTemporalSet);
            ctx.DeferDispose(ctx.AoHistoryView);
            ctx.DeferDispose(ctx.AoHistoryTexture);
            ctx.DeferDispose(ctx.IndirectHistoryView);
            ctx.DeferDispose(ctx.IndirectHistoryTexture);
            ctx.DeferDispose(ctx.SsilOutputSet);
            ctx.DeferDispose(ctx.SsilMainSet);
            ctx.DeferDispose(ctx.SsilDenoiseSet);
            ctx.DeferDispose(ctx.SsilDenoiseSetV);
            for (int i = 0; i < 5; i++)
            {
                ctx.DeferDispose(ctx.SsilPrefilterSets[i]);
                ctx.DeferDispose(ctx.DepthMipLevelViews[i]);
            }
            ctx.DeferDispose(ctx.DepthMipFullView);
            ctx.DeferDispose(ctx.DepthMipTexture);
            ctx.DeferDispose(ctx.AoRawView);
            ctx.DeferDispose(ctx.AoRawTexture);
            ctx.DeferDispose(ctx.AoView);
            ctx.DeferDispose(ctx.AoTexture);
            ctx.DeferDispose(ctx.IndirectRawView);
            ctx.DeferDispose(ctx.IndirectRawTexture);
            ctx.DeferDispose(ctx.IndirectView);
            ctx.DeferDispose(ctx.IndirectTexture);

            // --- GBuffer (render resolution) ---
            ctx.GBuffer ??= new GBuffer();
            ctx.GBuffer.Initialize(_device, ctx.RenderWidth, ctx.RenderHeight);

            // --- HDR intermediate texture (render resolution) ---
            ctx.HdrTexture = factory.CreateTexture(TextureDescription.Texture2D(
                ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                PixelFormat.R16_G16_B16_A16_Float,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            ctx.HdrView = factory.CreateTextureView(ctx.HdrTexture);

            // HDR framebuffer shares depth with GBuffer (for forward pass depth testing)
            ctx.HdrFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                ctx.GBuffer.DepthTexture,
                ctx.HdrTexture));

            // --- SSIL textures (render resolution) ---
            if (_ssilPrefilterPipeline != null)
            {
                // Depth MIP chain (5 levels, R16F)
                ctx.DepthMipTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 5, 1,
                    PixelFormat.R16_Float,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.DepthMipFullView = factory.CreateTextureView(ctx.DepthMipTexture);
                for (int i = 0; i < 5; i++)
                {
                    ctx.DepthMipLevelViews[i] = factory.CreateTextureView(
                        new TextureViewDescription(ctx.DepthMipTexture, (uint)i, 1, 0, 1));
                }

                // AO textures (R8)
                ctx.AoRawTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                    PixelFormat.R8_UNorm,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.AoRawView = factory.CreateTextureView(ctx.AoRawTexture);

                ctx.AoTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                    PixelFormat.R8_UNorm,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.AoView = factory.CreateTextureView(ctx.AoTexture);

                // Indirect light textures (RGBA16F)
                ctx.IndirectRawTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                    PixelFormat.R16_G16_B16_A16_Float,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.IndirectRawView = factory.CreateTextureView(ctx.IndirectRawTexture);

                ctx.IndirectTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                    PixelFormat.R16_G16_B16_A16_Float,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.IndirectView = factory.CreateTextureView(ctx.IndirectTexture);

                // Prefilter resource sets (one per MIP level)
                for (int i = 0; i < 5; i++)
                {
                    var prevInput = i == 0 ? ctx.DepthMipLevelViews[0]! : ctx.DepthMipLevelViews[i - 1]!;
                    ctx.SsilPrefilterSets[i] = factory.CreateResourceSet(new ResourceSetDescription(
                        _ssilPrefilterLayout!,
                        ctx.GBuffer.DepthView,                // depthInput (raw depth for MIP 0)
                        _ssilClampSampler!,                // depthSampler
                        ctx.DepthMipLevelViews[i]!,           // depthMipOut (write target)
                        _ssilPrefilterParamsBuffer!,       // PrefilterParams
                        prevInput));                        // prevMipInput (previous MIP for 1+)
                }

                // Main pass resource set
                ctx.SsilMainSet = factory.CreateResourceSet(new ResourceSetDescription(
                    _ssilMainLayout!,
                    ctx.DepthMipFullView!,            // depthMipTex (all MIPs)
                    ctx.GBuffer.NormalView,            // normalTex
                    _ssilClampSampler!,             // linearSampler
                    ctx.AoRawView!,                    // aoOutput
                    _ssilMainParamsBuffer!,         // SSILParams
                    ctx.IndirectRawView!,              // indirectOutput
                    ctx.GBuffer.AlbedoView,            // albedoTex
                    _ssilMainFlagsBuffer!));        // SSILFlags

                // Denoise H-pass: Raw → Texture
                ctx.SsilDenoiseSet = factory.CreateResourceSet(new ResourceSetDescription(
                    _ssilDenoiseLayout!,
                    ctx.AoRawView!,                    // aoInput
                    ctx.DepthMipLevelViews[0]!,        // depthMipTex (MIP 0)
                    ctx.GBuffer.NormalView,             // normalTex
                    _ssilClampSampler!,             // linearSampler
                    ctx.AoView!,                       // aoOutput
                    _ssilDenoiseParamsBuffer!,      // DenoiseParams
                    ctx.IndirectRawView!,              // indirectInput
                    ctx.IndirectView!,                 // indirectOutput
                    _ssilDenoiseFlagsBuffer!));     // DenoiseFlags

                // Denoise V-pass: Texture → Raw (swapped input/output)
                ctx.SsilDenoiseSetV = factory.CreateResourceSet(new ResourceSetDescription(
                    _ssilDenoiseLayout!,
                    ctx.AoView!,                       // aoInput (H-blurred)
                    ctx.DepthMipLevelViews[0]!,        // depthMipTex (MIP 0)
                    ctx.GBuffer.NormalView,             // normalTex
                    _ssilClampSampler!,             // linearSampler
                    ctx.AoRawView!,                    // aoOutput (fully denoised)
                    _ssilDenoiseParamsBuffer!,      // DenoiseParams (reused)
                    ctx.IndirectView!,                 // indirectInput (H-blurred)
                    ctx.IndirectRawView!,              // indirectOutput (fully denoised)
                    _ssilDenoiseFlagsBuffer!));     // DenoiseFlags (reused)

                // History textures for temporal filter
                ctx.AoHistoryTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                    PixelFormat.R8_UNorm,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.AoHistoryView = factory.CreateTextureView(ctx.AoHistoryTexture);

                ctx.IndirectHistoryTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.RenderWidth, ctx.RenderHeight, 1, 1,
                    PixelFormat.R16_G16_B16_A16_Float,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.IndirectHistoryView = factory.CreateTextureView(ctx.IndirectHistoryTexture);

                // Temporal filter resource set
                // After 2-pass denoise, result is in Raw. Temporal reads Raw, writes Texture.
                ctx.SsilTemporalSet = factory.CreateResourceSet(new ResourceSetDescription(
                    _ssilTemporalLayout!,
                    ctx.AoRawView!,                    // currentAO (denoised in Raw)
                    ctx.IndirectRawView!,              // currentIndirect (denoised in Raw)
                    ctx.AoHistoryView!,                // historyAO
                    ctx.IndirectHistoryView!,          // historyIndirect
                    ctx.GBuffer.WorldPosView,          // worldPosTex
                    _ssilClampSampler!,             // linearSampler
                    ctx.AoView!,                       // aoOutput (temporal writes to Texture)
                    ctx.IndirectView!,                 // indirectOutput (temporal writes to Texture)
                    _ssilTemporalParamsBuffer!));   // TemporalParams

                // SSIL output set (Set 2 for ambient pass)
                // After temporal filter, final result is in aoTexture/indirectTexture
                ctx.SsilOutputSet = factory.CreateResourceSet(new ResourceSetDescription(
                    _ssilOutputLayout!,
                    ctx.AoView!,                       // gAO (temporal output)
                    ctx.IndirectView!,                 // gIndirect (temporal output)
                    _ssilOutputParamsBuffer!));     // SSILOutputParams
            }

            // --- GBuffer resource set (shared by all lighting passes) ---
            ctx.GBufferResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _gBufferLayout!,
                ctx.GBuffer.AlbedoView,
                ctx.GBuffer.NormalView,
                ctx.GBuffer.MaterialView,
                ctx.GBuffer.WorldPosView,
                _defaultSampler!));

            // --- Ambient resource set ---
            _ambientResourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                _ambientLayout!,
                _ambientBuffer!,
                _whiteCubemap!.TextureView!,
                _envMapBuffer!));
            _currentAmbientEnvMapView = null;

            // --- FSR Upscaler textures (display resolution) ---
            // 항상 생성 (런타임 토글 지원). fsrEnabled=false여도 텍스처만 만들어둠.
            if (_fsrUpscaleLayout != null)
            {
                ctx.UpscaledTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.DisplayWidth, ctx.DisplayHeight, 1, 1,
                    PixelFormat.R16_G16_B16_A16_Float,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.UpscaledView = factory.CreateTextureView(ctx.UpscaledTexture);

                ctx.HistoryTexture = factory.CreateTexture(TextureDescription.Texture2D(
                    ctx.DisplayWidth, ctx.DisplayHeight, 1, 1,
                    PixelFormat.R16_G16_B16_A16_Float,
                    TextureUsage.Sampled | TextureUsage.Storage));
                ctx.HistoryView = factory.CreateTextureView(ctx.HistoryTexture);

                ctx.FsrUpscaleSet = factory.CreateResourceSet(new ResourceSetDescription(
                    _fsrUpscaleLayout,
                    ctx.HdrView!,                         // colorInput (render res)
                    ctx.GBuffer.DepthCopyView,            // depthInput
                    ctx.GBuffer.VelocityView,             // velocityInput
                    _ssilClampSampler!,                // linearSampler
                    ctx.HistoryView!,                     // historyInput (display res)
                    ctx.UpscaledView!,                    // upscaledOutput (display res)
                    _fsrParamsBuffer!));               // FsrParams

                ctx.FsrFrameIndex = 0;
                _fsrWasEnabled = RenderSettings.fsrEnabled;
                _fsrLastScaleMode = RenderSettings.fsrScaleMode;
                _fsrLastCustomScale = RenderSettings.fsrCustomScale;

                // --- CAS texture (display resolution) ---
                if (_casLayout != null)
                {
                    ctx.CasTexture = factory.CreateTexture(TextureDescription.Texture2D(
                        ctx.DisplayWidth, ctx.DisplayHeight, 1, 1,
                        PixelFormat.R16_G16_B16_A16_Float,
                        TextureUsage.Sampled | TextureUsage.Storage));
                    ctx.CasView = factory.CreateTextureView(ctx.CasTexture);

                    ctx.CasSet = factory.CreateResourceSet(new ResourceSetDescription(
                        _casLayout,
                        ctx.UpscaledView!,          // inputTex (upscaled result)
                        _ssilClampSampler!,      // linearSampler
                        ctx.CasView!,               // outputTex
                        _casParamsBuffer!));      // CasParams
                }

            }
        }

        public void ResizeContext(RenderContext ctx, uint width, uint height)
        {
            if (_device == null || width == 0 || height == 0) return;
            if (width == ctx.DisplayWidth && height == ctx.DisplayHeight) return;
            CreateContextResources(ctx, width, height);
            ctx.PostProcessStack?.Resize(ctx.RenderWidth, ctx.RenderHeight);
        }

        public RenderContext CreateSceneViewContext(uint width, uint height)
        {
            if (_sceneViewContext != null) return _sceneViewContext;
            _sceneViewContext = new RenderContext("SceneView");
            CreateContextResources(_sceneViewContext, width, height);
            _sceneViewContext.PostProcessStack = new PostProcessStack();
            _sceneViewContext.PostProcessStack.Initialize(_device!, _sceneViewContext.RenderWidth, _sceneViewContext.RenderHeight, ShaderRegistry.Resolve);
            _sceneViewContext.PostProcessStack.AddEffect(new BloomEffect());
            _sceneViewContext.PostProcessStack.AddEffect(new TonemapEffect());
            return _sceneViewContext;
        }

        public void Resize(uint width, uint height)
        {
            if (_gameViewContext == null) return;
            ResizeContext(_gameViewContext, width, height);
        }

        // ==============================
        // Render
        // ==============================

        /// <summary>
        /// Call once per frame BEFORE any Render/Resize calls.
        /// Flushes deferred disposals from the PREVIOUS frame (whose commands have been submitted).
        /// </summary>
        public void BeginFrame()
        {
            FlushPendingDisposal();
        }

        public void Render(CommandList cl, Camera? camera, float aspectRatio, RenderContext ctx)
        {
            if (_device == null || ctx.GBuffer == null || camera == null)
                return;

            _activeCtx = ctx;

            // --- FSR 런타임 토글/배율 변경 감지 → 리소스 재생성 ---
            bool fsrNow = RenderSettings.fsrEnabled;
            var fsrModeNow = RenderSettings.fsrScaleMode;
            float customScaleNow = RenderSettings.fsrCustomScale;
            bool fsrStateChanged = fsrNow != _fsrWasEnabled
                || (fsrNow && fsrModeNow != _fsrLastScaleMode)
                || (fsrNow && fsrModeNow == FsrScaleMode.Custom
                    && MathF.Abs(customScaleNow - _fsrLastCustomScale) > 0.01f);
            if (fsrStateChanged)
            {
                FlushPendingDisposal();
                CreateContextResources(ctx, ctx.DisplayWidth, ctx.DisplayHeight);
                ctx.PostProcessStack?.Resize(ctx.RenderWidth, ctx.RenderHeight);
                _fsrWasEnabled = fsrNow;
                _fsrLastScaleMode = fsrModeNow;
                _fsrLastCustomScale = customScaleNow;
            }

            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspectRatio).ToNumerics();

            var unjitteredViewProj = viewMatrix * projMatrix;

            // Apply sub-pixel jitter for temporal upscaling
            System.Numerics.Vector2 jitterOffset = System.Numerics.Vector2.Zero;
            System.Numerics.Matrix4x4 jitteredProj = projMatrix;
            if (RenderSettings.fsrEnabled && ctx.RenderWidth > 0 && ctx.RenderHeight > 0)
            {
                jitterOffset = GetHaltonJitter(ctx.FsrFrameIndex) * RenderSettings.fsrJitterScale;
                jitteredProj = ApplyJitter(projMatrix, jitterOffset, ctx.RenderWidth, ctx.RenderHeight);

            }

            // Geometry pass uses jittered VP; everything else uses unjittered
            var jitteredViewProj = viewMatrix * jitteredProj;
            var viewProj = unjitteredViewProj;

            // === 1. Geometry Pass → G-Buffer ===
            cl.SetFramebuffer(ctx.GBuffer.Framebuffer);
            cl.ClearColorTarget(0, RgbaFloat.Clear);     // Albedo
            cl.ClearColorTarget(1, RgbaFloat.Clear);     // Normal
            cl.ClearColorTarget(2, RgbaFloat.Clear);     // Material
            cl.ClearColorTarget(3, RgbaFloat.Clear);     // WorldPos (alpha=0 → no geometry)
            cl.ClearColorTarget(4, RgbaFloat.Clear);     // Velocity
            cl.ClearDepthStencil(1f);
            cl.SetPipeline(_geometryPipeline);
            DrawOpaqueRenderers(cl, jitteredViewProj);

            // === 1.5 Shadow Pass ===
            RenderShadowPass(cl, camera);
            BlurShadowAtlas(cl);

            // === 1.75 SSIL Compute Pass ===
            RunSSILPass(cl, camera, viewMatrix, projMatrix, unjitteredViewProj);

            // === 2. Ambient/IBL Pass → HDR (Overwrite) ===
            cl.SetFramebuffer(ctx.HdrFramebuffer);
            if (camera.clearFlags == CameraClearFlags.SolidColor)
            {
                var bg = camera.backgroundColor;
                cl.ClearColorTarget(0, new RgbaFloat(bg.r, bg.g, bg.b, bg.a));
            }
            else
            {
                cl.ClearColorTarget(0, RgbaFloat.Clear);
            }
            // (depth is shared with GBuffer — do NOT clear it)

            UpdateEnvMapForAmbient();
            UploadAmbientData(cl, camera);
            UploadEnvMapData(cl);

            if (_ssilOutputParamsBuffer != null)
            {
                bool ssilActive = RoseEngine.RenderSettings.ssilEnabled;
                bool indirectActive = ssilActive && RoseEngine.RenderSettings.ssilIndirectEnabled;
                cl.UpdateBuffer(_ssilOutputParamsBuffer, 0, new SSILOutputParams
                {
                    IndirectBoost = indirectActive ? RoseEngine.RenderSettings.ssilIndirectBoost : 0f,
                    SaturationBoost = indirectActive ? RoseEngine.RenderSettings.ssilSaturationBoost : 0f,
                    AoIntensity = ssilActive ? RoseEngine.RenderSettings.ssilAoIntensity : 0f,
                });
            }

            cl.SetPipeline(_ambientPipeline);
            cl.SetGraphicsResourceSet(0, ctx.GBufferResourceSet);
            cl.SetGraphicsResourceSet(1, _ambientResourceSet);
            if (ctx.SsilOutputSet != null)
                cl.SetGraphicsResourceSet(2, ctx.SsilOutputSet);
            cl.Draw(3, 1, 0, 0);

            // === 3. Direct Lights → HDR (Additive) ===
            // Restore viewport to HDR size after shadow pass
            cl.SetFramebuffer(ctx.HdrFramebuffer);
            cl.SetFullViewports();

            int dirCount = 0, pointCount = 0, spotCount = 0;
            foreach (var light in Light._allLights)
            {
                if (!light.enabled || !light.gameObject.activeInHierarchy) continue;

                UploadSingleLightUniforms(cl, light, camera, viewProj);
                var lightSet = GetLightVolumeResourceSet(light);

                if (light.type == LightType.Directional)
                {
                    cl.SetPipeline(_directionalLightPipeline);
                    cl.SetGraphicsResourceSet(0, ctx.GBufferResourceSet);
                    cl.SetGraphicsResourceSet(1, lightSet);
                    cl.Draw(3, 1, 0, 0);
                    dirCount++;
                }
                else if (light.type == LightType.Point)
                {
                    cl.SetPipeline(_pointLightPipeline);
                    cl.SetGraphicsResourceSet(0, ctx.GBufferResourceSet);
                    cl.SetGraphicsResourceSet(1, lightSet);
                    cl.SetVertexBuffer(0, _lightSphereMesh!.VertexBuffer);
                    cl.SetIndexBuffer(_lightSphereMesh.IndexBuffer!, IndexFormat.UInt32);
                    cl.DrawIndexed((uint)_lightSphereMesh.indices.Length);
                    pointCount++;
                }
                else if (light.type == LightType.Spot)
                {
                    cl.SetPipeline(_spotLightPipeline);
                    cl.SetGraphicsResourceSet(0, ctx.GBufferResourceSet);
                    cl.SetGraphicsResourceSet(1, lightSet);
                    cl.SetVertexBuffer(0, _lightConeMesh!.VertexBuffer);
                    cl.SetIndexBuffer(_lightConeMesh.IndexBuffer!, IndexFormat.UInt32);
                    cl.DrawIndexed((uint)_lightConeMesh.indices.Length);
                    spotCount++;
                }
            }

            // === 4. Skybox Pass → HDR (depth test LessEqual, only empty pixels) ===
            if (camera.clearFlags == CameraClearFlags.Skybox)
            {
                RenderSkybox(cl, camera, viewProj);
            }

            // === 5. Forward Pass → HDR (sprites, text, wireframe) ===
            if (DebugOverlaySettings.wireframe && _wireframePipeline != null)
            {
                UploadForwardLightData(cl, camera);
                cl.SetPipeline(_wireframePipeline);
                DrawAllRenderers(cl, viewProj, useWireframeColor: true);
            }

            if (_spritePipeline != null && SpriteRenderer._allSpriteRenderers.Count > 0)
            {
                DrawAllSprites(cl, viewProj, camera);
            }

            if (_spritePipeline != null && TextRenderer._allTextRenderers.Count > 0)
            {
                DrawAllTexts(cl, viewProj, camera);
            }

            // === 6. Post-Processing + Upscale → Swapchain ===
            bool fsrActive = RenderSettings.fsrEnabled && _fsrUpscalePipeline != null && ctx.UpscaledView != null;

            // PostProcessManager가 비활성이면 이펙트 실행을 건너뛴다 (blit만 수행)
            bool ppActive = PostProcessManager.Instance?.IsPostProcessActive ?? false;

            if (fsrActive)
            {
                // Run post-processing effects only (no blit to swapchain)
                var postProcessResult = ppActive
                    ? (ctx.PostProcessStack?.ExecuteEffectsOnly(cl, ctx.HdrView!) ?? ctx.HdrView!)
                    : ctx.HdrView!;

                // Temporal Upscale: render res → display res
                cl.UpdateBuffer(_fsrParamsBuffer!, 0, new FsrUpscaleParams
                {
                    RenderSize = new System.Numerics.Vector2(ctx.RenderWidth, ctx.RenderHeight),
                    DisplaySize = new System.Numerics.Vector2(ctx.DisplayWidth, ctx.DisplayHeight),
                    RenderSizeRcp = new System.Numerics.Vector2(1f / ctx.RenderWidth, 1f / ctx.RenderHeight),
                    DisplaySizeRcp = new System.Numerics.Vector2(1f / ctx.DisplayWidth, 1f / ctx.DisplayHeight),
                    JitterOffset = jitterOffset,
                    FrameIndex = ctx.FsrFrameIndex,
                });

                // Re-create resource set with current post-process result if needed
                if (postProcessResult != ctx.HdrView)
                {
                    ctx.FsrUpscaleSet?.Dispose();
                    ctx.FsrUpscaleSet = _device.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                        _fsrUpscaleLayout!,
                        postProcessResult,                 // colorInput
                        ctx.GBuffer.DepthCopyView,            // depthInput
                        ctx.GBuffer.VelocityView,             // velocityInput
                        _ssilClampSampler!,                // linearSampler
                        ctx.HistoryView!,                     // historyInput
                        ctx.UpscaledView!,                    // upscaledOutput
                        _fsrParamsBuffer!));               // FsrParams
                }

                uint dispatchX = (ctx.DisplayWidth + 7) / 8;
                uint dispatchY = (ctx.DisplayHeight + 7) / 8;

                cl.SetPipeline(_fsrUpscalePipeline);
                cl.SetComputeResourceSet(0, ctx.FsrUpscaleSet!);
                cl.Dispatch(dispatchX, dispatchY, 1);

                // Copy upscaled → history for next frame
                cl.CopyTexture(
                    ctx.UpscaledTexture!, 0, 0, 0, 0, 0,
                    ctx.HistoryTexture!, 0, 0, 0, 0, 0,
                    ctx.DisplayWidth, ctx.DisplayHeight, 1, 1);

                // CAS Sharpening pass (optional)
                TextureView blitSource = ctx.UpscaledView!;
                float sharpness = RenderSettings.fsrSharpness;
                bool casReady = _casPipeline != null && ctx.CasView != null && ctx.CasSet != null;
                if (sharpness > 0.01f && casReady)
                {
                    cl.UpdateBuffer(_casParamsBuffer!, 0, new CasParams
                    {
                        ImageSize = new System.Numerics.Vector2(ctx.DisplayWidth, ctx.DisplayHeight),
                        ImageSizeRcp = new System.Numerics.Vector2(1f / ctx.DisplayWidth, 1f / ctx.DisplayHeight),
                        Sharpness = sharpness,
                    });
                    cl.SetPipeline(_casPipeline!);
                    cl.SetComputeResourceSet(0, ctx.CasSet!);
                    cl.Dispatch(dispatchX, dispatchY, 1);
                    blitSource = ctx.CasView!;
                }

                // Blit to swapchain
                ctx.PostProcessStack?.BlitToSwapchain(cl, blitSource, FinalOutputFramebuffer);

                ctx.FsrFrameIndex++;
            }
            else
            {
                // Standard path: PostProcess → Swapchain (no upscaling)
                if (ppActive)
                    ctx.PostProcessStack?.Execute(cl, ctx.HdrView!, FinalOutputFramebuffer);
                else
                    ctx.PostProcessStack?.BlitToSwapchain(cl, ctx.HdrView!, FinalOutputFramebuffer);
            }

        }

        /// <summary>
        /// Debug overlay를 스왑체인에 직접 렌더. ImGui 패스 이후에 호출해야 합니다.
        /// </summary>
        public void RenderDebugOverlayToSwapchain(CommandList cl)
        {
            if (_device == null || DebugOverlaySettings.overlay == DebugOverlay.None)
                return;
            RenderDebugOverlay(cl, _device.SwapchainFramebuffer);
        }

        // Shadow, Lighting, Draw, Debug, SSIL methods are in partial class files:
        // - RenderSystem.Shadow.cs
        // - RenderSystem.Lighting.cs
        // - RenderSystem.Draw.cs
        // - RenderSystem.Debug.cs
        // - RenderSystem.SSIL.cs

        // (Shared helpers used by partial class files)

        // ==============================
        // Draw helpers
        // ==============================

        private ResourceSet GetOrCreateResourceSet(TextureView? textureView, TextureView? normalTexView = null, TextureView? mroTexView = null)
        {
            var mainTex = textureView ?? _whiteTexture!.TextureView!;
            var normalTex = normalTexView ?? _defaultNormalTexture!.TextureView!;
            var mroTex = mroTexView ?? _defaultMROTexture!.TextureView!;
            var key = (mainTex, normalTex, mroTex);

            if (_resourceSetCache.TryGetValue(key, out var cached))
                return cached;

            var resourceSet = _device!.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                _perObjectLayout!, _transformBuffer!, _materialBuffer!, mainTex, _defaultSampler!, normalTex, mroTex));
            _resourceSetCache[key] = resourceSet;
            return resourceSet;
        }

        /// <summary>공통 draw call — Transform/Material 업로드 + ResourceSet 바인딩 + DrawIndexed.</summary>
        private void DrawMesh(CommandList cl, System.Numerics.Matrix4x4 viewProj,
            Mesh mesh, Transform t, MaterialUniforms matUniforms, TextureView? texView,
            bool bindPerFrame, TextureView? normalTexView = null, TextureView? mroTexView = null)
        {
            cl.UpdateBuffer(_transformBuffer, 0, new TransformUniforms
            {
                World = RoseEngine.Matrix4x4.TRS(t.position, t.rotation, t.lossyScale).ToNumerics(),
                ViewProjection = viewProj,
                PrevViewProjection = _activeCtx!.PrevViewProj,
            });
            cl.UpdateBuffer(_materialBuffer, 0, matUniforms);

            cl.SetGraphicsResourceSet(0, GetOrCreateResourceSet(texView, normalTexView, mroTexView));
            if (bindPerFrame)
                cl.SetGraphicsResourceSet(1, _perFrameResourceSet);

            cl.SetVertexBuffer(0, mesh.VertexBuffer);
            cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
            cl.DrawIndexed((uint)mesh.indices.Length);
        }

        private (MaterialUniforms mat, TextureView? tex, TextureView? normalTex, TextureView? mroTex) PrepareMaterial(Material? material)
        {
            var color = material?.color ?? Color.white;
            var emission = material?.emission ?? Color.black;
            TextureView? texView = null;
            float hasTexture = 0f;
            if (material?.mainTexture != null)
            {
                material.mainTexture.UploadToGPU(_device!, generateMipmaps: true);
                if (material.mainTexture.TextureView != null)
                { texView = material.mainTexture.TextureView; hasTexture = 1f; }
            }
            TextureView? normalTexView = null;
            float hasNormalMap = 0f;
            if (material?.normalMap != null && material.normalMap != Texture2D.DefaultNormal)
            {
                material.normalMap.UploadToGPU(_device!, generateMipmaps: true);
                if (material.normalMap.TextureView != null)
                { normalTexView = material.normalMap.TextureView; hasNormalMap = 1f; }
            }
            TextureView? mroTexView = null;
            float hasMROMap = 0f;
            if (material?.MROMap != null && material.MROMap != Texture2D.DefaultMRO)
            {
                material.MROMap.UploadToGPU(_device!, generateMipmaps: true);
                if (material.MROMap.TextureView != null)
                { mroTexView = material.MROMap.TextureView; hasMROMap = 1f; }
            }
            var texScale = material?.textureScale ?? RoseEngine.Vector2.one;
            var texOffset = material?.textureOffset ?? RoseEngine.Vector2.zero;
            return (new MaterialUniforms
            {
                Color = new Vector4(color.r, color.g, color.b, color.a),
                Emission = new Vector4(emission.r, emission.g, emission.b, emission.a),
                HasTexture = hasTexture,
                Metallic = material?.metallic ?? 0f,
                Roughness = material?.roughness ?? 0.5f,
                Occlusion = material?.occlusion ?? 1f,
                NormalMapStrength = material?.normalMapStrength ?? 1f,
                HasNormalMap = hasNormalMap,
                HasMROMap = hasMROMap,
                TextureOffset = new System.Numerics.Vector2(texOffset.x, texOffset.y),
                TextureScale = new System.Numerics.Vector2(texScale.x, texScale.y),
            }, texView, normalTexView, mroTexView);
        }

        // ==============================
        // Utilities
        // ==============================

        public void Dispose()
        {
            // Flush any deferred disposals before final cleanup
            FlushPendingDisposal();

            // Dispose per-viewport contexts (owns all per-context resources)
            _gameViewContext?.Dispose();
            _sceneViewContext?.Dispose();

            _geometryPipeline?.Dispose();
            _ambientPipeline?.Dispose();
            _directionalLightPipeline?.Dispose();
            _pointLightPipeline?.Dispose();
            _spotLightPipeline?.Dispose();
            _shadowAtlasBackCullPipeline?.Dispose();
            _shadowAtlasFrontCullPipeline?.Dispose();
            _shadowAtlasNoCullPipeline?.Dispose();
            _skyboxPipeline?.Dispose();
            _forwardPipeline?.Dispose();
            _wireframePipeline?.Dispose();
            _spritePipeline?.Dispose();

            // Shadow atlas
            _atlasShadowSet?.Dispose();
            _atlasView?.Dispose();
            _atlasFramebuffer?.Dispose();
            _atlasTexture?.Dispose();
            _atlasDepthTexture?.Dispose();
            _shadowSampler?.Dispose();
            _shadowTransformBuffer?.Dispose();
            _shadowLayout?.Dispose();

            // VSM blur
            _shadowBlurPipeline?.Dispose();
            _shadowBlurSetH?.Dispose();
            _shadowBlurSetV?.Dispose();
            _shadowBlurBuffer?.Dispose();
            _shadowBlurLayout?.Dispose();
            _atlasTempView?.Dispose();
            _atlasTempFramebuffer?.Dispose();
            _atlasBlurFramebuffer?.Dispose();
            _atlasTempTexture?.Dispose();

            _skyboxResourceSet?.Dispose();
            _skyboxLayout?.Dispose();
            _skyboxUniformBuffer?.Dispose();

            // SSIL (shared: pipelines, layouts, UBO buffers, shaders, sampler)
            _ssilPrefilterPipeline?.Dispose();
            _ssilMainPipeline?.Dispose();
            _ssilDenoisePipeline?.Dispose();
            _ssilPrefilterLayout?.Dispose();
            _ssilMainLayout?.Dispose();
            _ssilDenoiseLayout?.Dispose();
            _ssilOutputLayout?.Dispose();
            _ssilPrefilterParamsBuffer?.Dispose();
            _ssilMainParamsBuffer?.Dispose();
            _ssilMainFlagsBuffer?.Dispose();
            _ssilDenoiseParamsBuffer?.Dispose();
            _ssilDenoiseFlagsBuffer?.Dispose();
            _ssilClampSampler?.Dispose();
            _ssilOutputParamsBuffer?.Dispose();
            _ssilTemporalPipeline?.Dispose();
            _ssilTemporalLayout?.Dispose();
            _ssilTemporalParamsBuffer?.Dispose();
            _ssilTemporalShader?.Dispose();
            _ssilPrefilterShader?.Dispose();
            _ssilMainShader?.Dispose();
            _ssilDenoiseShader?.Dispose();

            // FSR Upscaler (shared: pipeline, layout, UBO buffer, shader)
            _fsrUpscalePipeline?.Dispose();
            _fsrUpscaleLayout?.Dispose();
            _fsrParamsBuffer?.Dispose();
            _fsrUpscaleShader?.Dispose();
            // CAS (shared: pipeline, layout, UBO buffer, shader)
            _casPipeline?.Dispose();
            _casLayout?.Dispose();
            _casParamsBuffer?.Dispose();
            _casShader?.Dispose();

            _ambientResourceSet?.Dispose();

            _gBufferLayout?.Dispose();
            _ambientLayout?.Dispose();
            _lightVolumeShadowLayout?.Dispose();

            _ambientBuffer?.Dispose();
            _lightVolumeBuffer?.Dispose();
            _envMapBuffer?.Dispose();

            _transformBuffer?.Dispose();
            _materialBuffer?.Dispose();
            _lightBuffer?.Dispose();
            _defaultSampler?.Dispose();
            _whiteTexture?.Dispose();
            // _defaultNormalTexture is Texture2D.DefaultNormal — shared, not owned
            _whiteCubemap?.Dispose();
            _defaultResourceSet?.Dispose();
            _perFrameResourceSet?.Dispose();
            _perObjectLayout?.Dispose();
            _perFrameLayout?.Dispose();

            foreach (var rs in _resourceSetCache.Values)
                rs.Dispose();
            _resourceSetCache.Clear();

            _debugOverlayPipeline?.Dispose();
            _debugOverlayParamsBuffer?.Dispose();
            _debugOverlayLayout?.Dispose();
            _debugOverlaySampler?.Dispose();
            if (_debugOverlayShaders != null)
                foreach (var s in _debugOverlayShaders) s.Dispose();

            if (_forwardShaders != null)
                foreach (var s in _forwardShaders) s.Dispose();
            if (_geometryShaders != null)
                foreach (var s in _geometryShaders) s.Dispose();
            if (_ambientShaders != null)
                foreach (var s in _ambientShaders) s.Dispose();
            if (_directionalLightShaders != null)
                foreach (var s in _directionalLightShaders) s.Dispose();
            if (_pointLightShaders != null)
                foreach (var s in _pointLightShaders) s.Dispose();
            if (_spotLightShaders != null)
                foreach (var s in _spotLightShaders) s.Dispose();
            if (_shadowAtlasShaders != null)
                foreach (var s in _shadowAtlasShaders) s.Dispose();
            if (_shadowBlurShaders != null)
                foreach (var s in _shadowBlurShaders) s.Dispose();
            if (_skyboxShaders != null)
                foreach (var s in _skyboxShaders) s.Dispose();

            Debug.Log("[RenderSystem] Disposed");
        }

        // === FSR Upscaler helpers ===

        private static float HaltonSequence(int index, int @base)
        {
            float result = 0f;
            float f = 1f / @base;
            int i = index;
            while (i > 0)
            {
                result += f * (i % @base);
                i /= @base;
                f /= @base;
            }
            return result;
        }

        private static System.Numerics.Vector2 GetHaltonJitter(int frameIndex)
        {
            int idx = (frameIndex % 16) + 1;
            return new System.Numerics.Vector2(
                HaltonSequence(idx, 2) - 0.5f,
                HaltonSequence(idx, 3) - 0.5f);
        }

        private static (uint rw, uint rh) CalcRenderResolution(uint dw, uint dh)
        {
            if (!RenderSettings.fsrEnabled) return (dw, dh);

            float scale = RenderSettings.fsrScaleMode switch
            {
                FsrScaleMode.NativeAA => 1.0f,
                FsrScaleMode.Quality => 1.5f,
                FsrScaleMode.Balanced => 1.7f,
                FsrScaleMode.Performance => 2.0f,
                FsrScaleMode.UltraPerformance => 3.0f,
                _ => RenderSettings.fsrCustomScale
            };

            uint rw = Math.Max((uint)(dw / scale), 1);
            uint rh = Math.Max((uint)(dh / scale), 1);
            return (rw, rh);
        }

        private static System.Numerics.Matrix4x4 ApplyJitter(
            System.Numerics.Matrix4x4 projection, System.Numerics.Vector2 jitter, uint renderW, uint renderH)
        {
            var j = projection;
            j.M31 += jitter.X * 2.0f / renderW;
            j.M32 += jitter.Y * 2.0f / renderH;
            return j;
        }
    }
}
