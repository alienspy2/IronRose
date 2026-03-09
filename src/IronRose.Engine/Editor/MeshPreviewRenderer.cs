using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using IronRose.Rendering;
using RoseEngine;
using Veldrid;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Shader = Veldrid.Shader;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 메시/머티리얼 에셋 미리보기용 경량 오프스크린 렌더러.
    /// Mesh 모드: MatCap 솔리드 + 와이어프레임 오버레이 (카메라 궤도 회전)
    /// Material 모드: PBR 셰이더 + 고정 조명/카메라 + 오브젝트 회전
    /// </summary>
    internal sealed class MeshPreviewRenderer : IDisposable
    {
        private const uint PreviewSize = 256;
        private static readonly RgbaFloat ClearColor = new(0.18f, 0.18f, 0.18f, 1f);
        private static readonly Vector4 WireColor = new(0.05f, 0.05f, 0.05f, 1f);

        private readonly GraphicsDevice _device;

        // Framebuffer
        private Texture? _colorTexture;
        private TextureView? _colorTextureView;
        private Texture? _depthTexture;
        private Framebuffer? _framebuffer;

        // Pipelines — mesh mode (matcap)
        private Shader[]? _matcapShaders;
        private Pipeline? _solidPipeline;
        private Pipeline? _wirePipeline;

        // Pipelines — material mode (PBR)
        private Shader[]? _materialShaders;
        private Pipeline? _materialPipeline;

        // Uniform buffers
        private DeviceBuffer? _transformBuffer;
        private DeviceBuffer? _materialBuffer;
        private DeviceBuffer? _lightBuffer;

        // Resource layouts/sets
        private ResourceLayout? _perObjectLayout;
        private ResourceLayout? _perFrameLayout;
        private ResourceSet? _perObjectSet;
        private ResourceSet? _perFrameSet;

        // Default texture
        private Texture2D? _whiteTexture;
        private Sampler? _sampler;

        // State
        private Mesh? _mesh;
        private bool _dirty;
        private float _yaw = 30f;
        private float _pitch = 20f;
        private float _distance = 3f;
        private Vector3 _center;

        // Material override
        private Vector4 _solidColor = new(0.85f, 0.85f, 0.85f, 1f);
        private float _hasTexture;
        private float _metallic;
        private float _roughness = 0.5f;
        private bool _showWireframe = true;
        private bool _materialMode;
        private TextureView? _boundTextureView;

        public TextureView? ColorTextureView => _colorTextureView;
        public bool IsInitialized => _framebuffer != null;

        public MeshPreviewRenderer(GraphicsDevice device)
        {
            _device = device;
            Initialize();
        }

        private void Initialize()
        {
            var factory = _device.ResourceFactory;

            // Framebuffer
            var colorFormat = _device.SwapchainFramebuffer.OutputDescription.ColorAttachments[0].Format;
            var depthFormat = _device.SwapchainFramebuffer.OutputDescription.DepthAttachment?.Format
                              ?? PixelFormat.D32_Float_S8_UInt;

            _colorTexture = factory.CreateTexture(TextureDescription.Texture2D(
                PreviewSize, PreviewSize, 1, 1, colorFormat,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _colorTextureView = factory.CreateTextureView(_colorTexture);

            _depthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                PreviewSize, PreviewSize, 1, 1, depthFormat,
                TextureUsage.DepthStencil));

            _framebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                _depthTexture, _colorTexture));

            // Uniform buffers
            _transformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SceneViewTransformUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _materialBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SceneViewMaterialUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _lightBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<SceneViewLightUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Sampler + white fallback texture
            _sampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Wrap, SamplerAddressMode.Wrap, SamplerAddressMode.Wrap,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));

            _whiteTexture = Texture2D.CreateWhitePixel();
            _whiteTexture.UploadToGPU(_device);

            // Resource layouts
            _perObjectLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Transforms", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MaterialData", ResourceKind.UniformBuffer, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            _perFrameLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("LightData", ResourceKind.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment)));

            _perObjectSet = factory.CreateResourceSet(new ResourceSetDescription(
                _perObjectLayout, _transformBuffer, _materialBuffer, _whiteTexture.TextureView!, _sampler));
            _perFrameSet = factory.CreateResourceSet(new ResourceSetDescription(_perFrameLayout, _lightBuffer));

            // Vertex layout
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("UV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            var layouts = new[] { _perObjectLayout, _perFrameLayout };

            // Compile matcap shaders (mesh preview)
            var shaderDir = FindShaderDirectory();
            _matcapShaders = ShaderCompiler.CompileGLSL(_device,
                Path.Combine(shaderDir, "sceneview_matcap.vert"),
                Path.Combine(shaderDir, "sceneview_matcap.frag"));

            // Solid pipeline (matcap)
            _solidPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _matcapShaders),
                layouts,
                _framebuffer.OutputDescription));

            // Wireframe overlay pipeline
            _wirePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Wireframe, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _matcapShaders),
                layouts,
                _framebuffer.OutputDescription));

            // Compile material preview shaders (PBR)
            _materialShaders = ShaderCompiler.CompileGLSL(_device,
                Path.Combine(shaderDir, "sceneview_diffuse.vert"),
                Path.Combine(shaderDir, "preview_material.frag"));

            _materialPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _materialShaders),
                layouts,
                _framebuffer.OutputDescription));
        }

        public void SetMesh(Mesh mesh)
        {
            _mesh = mesh;
            mesh.RecalculateBounds();
            var b = mesh.bounds;
            _center = new Vector3(b.center.x, b.center.y, b.center.z);
            var size = new Vector3(b.size.x, b.size.y, b.size.z);
            _distance = size.Length() * 1.5f;
            if (_distance < 0.1f) _distance = 2f;
            _yaw = 30f;
            _pitch = 20f;
            _dirty = true;
        }

        public void UpdateOrbit(float deltaYaw, float deltaPitch)
        {
            _yaw += deltaYaw;
            _pitch = Math.Clamp(_pitch + deltaPitch, -89f, 89f);
            _dirty = true;
        }

        public void SetShowWireframe(bool show)
        {
            if (_showWireframe != show)
            {
                _showWireframe = show;
                _dirty = true;
            }
        }

        /// <summary>머티리얼 모드: PBR 셰이더, 고정 카메라/조명, 오브젝트 회전.</summary>
        public void SetMaterialOverride(Vector4 color, float metallic, float roughness, TextureView? textureView)
        {
            _materialMode = true;
            _solidColor = color;
            _metallic = metallic;
            _roughness = roughness;
            _hasTexture = textureView != null ? 1f : 0f;
            _showWireframe = false;

            if (_boundTextureView != textureView)
            {
                _boundTextureView = textureView;
                RebuildPerObjectSet(textureView);
            }

            _dirty = true;
        }

        /// <summary>기본 메시 모드(MatCap + 와이어프레임)으로 리셋.</summary>
        public void ClearMaterialOverride()
        {
            _materialMode = false;
            _solidColor = new Vector4(0.85f, 0.85f, 0.85f, 1f);
            _hasTexture = 0f;
            _metallic = 0f;
            _roughness = 0.5f;
            _showWireframe = true;

            if (_boundTextureView != null)
            {
                _boundTextureView = null;
                RebuildPerObjectSet(null);
            }

            _dirty = true;
        }

        private void RebuildPerObjectSet(TextureView? textureView)
        {
            _perObjectSet?.Dispose();
            var tv = textureView ?? _whiteTexture!.TextureView!;
            _perObjectSet = _device.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                _perObjectLayout!, _transformBuffer!, _materialBuffer!, tv, _sampler!));
        }

        /// <summary>더티 플래그가 세워져 있으면 오프스크린 RT에 렌더링.</summary>
        public void RenderIfDirty()
        {
            if (!_dirty || _mesh == null || _framebuffer == null) return;
            _dirty = false;

            // Ensure mesh GPU buffers
            if (_mesh.VertexBuffer == null || _mesh.IndexBuffer == null)
                _mesh.UploadToGPU(_device);
            if (_mesh.VertexBuffer == null) return;

            if (_materialMode)
                RenderMaterialMode();
            else
                RenderMeshMode();
        }

        /// <summary>Mesh 모드: 카메라 궤도 회전, MatCap + 와이어프레임.</summary>
        private void RenderMeshMode()
        {
            // Camera orbits around the object
            float yawRad = _yaw * MathF.PI / 180f;
            float pitchRad = _pitch * MathF.PI / 180f;
            float cosPitch = MathF.Cos(pitchRad);
            var eyeOffset = new Vector3(
                cosPitch * MathF.Sin(yawRad),
                MathF.Sin(pitchRad),
                cosPitch * MathF.Cos(yawRad)) * _distance;
            var eye = _center + eyeOffset;

            var view = Matrix4x4.CreateLookAt(eye, _center, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 6f, 1f, _distance * 0.01f, _distance * 10f);
            var viewProj = view * proj;

            var cl = _device.ResourceFactory.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(_framebuffer!);
            cl.ClearColorTarget(0, ClearColor);
            cl.ClearDepthStencil(1f, 0);

            // Light follows camera
            cl.UpdateBuffer(_lightBuffer!, 0, new SceneViewLightUniforms
            {
                CameraPos = new Vector4(eye, 1f),
                LightDir = new Vector4(Vector3.Normalize(eye - _center), 0f),
                LightColor = new Vector4(1f, 1f, 1f, 1f),
            });

            var transforms = new SceneViewTransformUniforms
            {
                World = Matrix4x4.Identity,
                ViewProjection = viewProj,
                ViewMatrix = view,
            };

            // Pass 1: Solid MatCap
            cl.SetPipeline(_solidPipeline);
            cl.UpdateBuffer(_transformBuffer!, 0, transforms);
            cl.UpdateBuffer(_materialBuffer!, 0, new SceneViewMaterialUniforms
            {
                Color = _solidColor,
                HasTexture = _hasTexture,
            });
            cl.SetGraphicsResourceSet(0, _perObjectSet);
            cl.SetGraphicsResourceSet(1, _perFrameSet);
            cl.SetVertexBuffer(0, _mesh!.VertexBuffer);
            cl.SetIndexBuffer(_mesh.IndexBuffer!, IndexFormat.UInt32);
            cl.DrawIndexed((uint)_mesh.indices.Length);

            // Pass 2: Wireframe overlay
            if (_showWireframe)
            {
                cl.SetPipeline(_wirePipeline);
                cl.UpdateBuffer(_materialBuffer!, 0, new SceneViewMaterialUniforms
                {
                    Color = WireColor,
                    HasTexture = 0f,
                });
                cl.DrawIndexed((uint)_mesh.indices.Length);
            }

            cl.End();
            _device.SubmitCommands(cl);
            cl.Dispose();
        }

        /// <summary>Material 모드: 고정 카메라/조명, 오브젝트(sphere) 회전, PBR 셰이더.</summary>
        private void RenderMaterialMode()
        {
            // Fixed camera position (약간 위에서 바라봄)
            var eye = new Vector3(0f, 0.15f, _distance);
            var target = Vector3.Zero;

            var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 6f, 1f, _distance * 0.01f, _distance * 10f);
            var viewProj = view * proj;

            // World rotation from yaw/pitch (오브젝트가 회전)
            float yawRad = _yaw * MathF.PI / 180f;
            float pitchRad = _pitch * MathF.PI / 180f;
            var world = Matrix4x4.CreateRotationX(pitchRad) * Matrix4x4.CreateRotationY(yawRad);

            // Fixed key light direction (우상단에서 비춤)
            var keyLightDir = Vector3.Normalize(new Vector3(0.5f, -0.7f, -0.5f));

            var cl = _device.ResourceFactory.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(_framebuffer!);
            cl.ClearColorTarget(0, ClearColor);
            cl.ClearDepthStencil(1f, 0);

            cl.UpdateBuffer(_lightBuffer!, 0, new SceneViewLightUniforms
            {
                CameraPos = new Vector4(eye, 1f),
                LightDir = new Vector4(keyLightDir, 0f),
                LightColor = new Vector4(1f, 0.98f, 0.95f, 1f),
            });

            cl.UpdateBuffer(_transformBuffer!, 0, new SceneViewTransformUniforms
            {
                World = world,
                ViewProjection = viewProj,
                ViewMatrix = view,
            });

            cl.UpdateBuffer(_materialBuffer!, 0, new SceneViewMaterialUniforms
            {
                Color = _solidColor,
                HasTexture = _hasTexture,
                Metallic = _metallic,
                Roughness = _roughness,
            });

            cl.SetPipeline(_materialPipeline);
            cl.SetGraphicsResourceSet(0, _perObjectSet);
            cl.SetGraphicsResourceSet(1, _perFrameSet);
            cl.SetVertexBuffer(0, _mesh!.VertexBuffer);
            cl.SetIndexBuffer(_mesh.IndexBuffer!, IndexFormat.UInt32);
            cl.DrawIndexed((uint)_mesh.indices.Length);

            cl.End();
            _device.SubmitCommands(cl);
            cl.Dispose();
        }

        public void Dispose()
        {
            _solidPipeline?.Dispose();
            _wirePipeline?.Dispose();
            _materialPipeline?.Dispose();
            if (_matcapShaders != null)
                foreach (var s in _matcapShaders) s.Dispose();
            if (_materialShaders != null)
                foreach (var s in _materialShaders) s.Dispose();
            _perObjectSet?.Dispose();
            _perFrameSet?.Dispose();
            _perObjectLayout?.Dispose();
            _perFrameLayout?.Dispose();
            _transformBuffer?.Dispose();
            _materialBuffer?.Dispose();
            _lightBuffer?.Dispose();
            _sampler?.Dispose();
            _whiteTexture?.Dispose();
            _framebuffer?.Dispose();
            _depthTexture?.Dispose();
            _colorTextureView?.Dispose();
            _colorTexture?.Dispose();
        }

        private static string FindShaderDirectory()
        {
            string[] candidates = { "Shaders", "../Shaders", "../../Shaders" };
            foreach (var candidate in candidates)
            {
                string fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath) &&
                    File.Exists(Path.Combine(fullPath, "sceneview_matcap.vert")))
                    return fullPath;
            }
            return "Shaders";
        }
    }
}
