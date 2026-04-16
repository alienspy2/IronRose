using System;
using System.Collections.Generic;
using System.Numerics;
using Veldrid;
using RoseEngine;
using IronRose.Engine;
using IronRose.Engine.Editor.SceneView;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Shader = Veldrid.Shader;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Rendering
{
    /// <summary>
    /// Scene View 전용 간소화된 Forward 렌더러.
    /// Wireframe, MatCap, DiffuseOnly 모드를 독립 파이프라인으로 렌더링.
    /// RenderSystem과 리소스를 공유하지 않음.
    /// </summary>
    public class SceneViewRenderer : IDisposable
    {
        private GraphicsDevice? _device;

        // --- Framebuffer ---
        private Texture? _colorTexture;
        private TextureView? _colorTextureView;
        private Texture? _depthTexture;
        private Framebuffer? _framebuffer;
        private uint _width, _height;
        private PixelFormat _colorFormat;
        private PixelFormat _depthFormat;

        // --- Wireframe pipeline ---
        private Shader[]? _wireframeShaders;
        private Pipeline? _wireframePipeline;

        // --- MatCap pipeline ---
        private Shader[]? _matcapShaders;
        private Pipeline? _matcapPipeline;

        // --- DiffuseOnly pipeline ---
        private Shader[]? _diffuseShaders;
        private Pipeline? _diffusePipeline;

        // --- Pick pipeline ---
        private Shader[]? _pickShaders;
        private Pipeline? _pickPipeline;
        private Texture? _pickTexture;
        private Texture? _pickStaging;
        private Framebuffer? _pickFramebuffer;

        // --- Outline pipeline ---
        private Shader[]? _outlineStencilShaders;
        private Shader[]? _outlineExpandShaders;
        private Pipeline? _outlineStencilPipeline;
        private Pipeline? _outlineExpandPipeline;

        // --- Gizmo pipeline (depth off, always on top) ---
        private Pipeline? _gizmoPipeline;

        // --- Gizmo line pipeline (depth off, LineList topology) ---
        private Shader[]? _gizmoLineShaders;
        private Pipeline? _gizmoLinePipeline;

        // --- Gizmo line pick pipeline (LineList, depth off, pick output) ---
        private Pipeline? _gizmoLinePickPipeline;

        // --- Shared resources ---
        private DeviceBuffer? _transformBuffer;
        private DeviceBuffer? _materialBuffer;
        private DeviceBuffer? _lightBuffer;
        private DeviceBuffer? _pickIdBuffer;
        private DeviceBuffer? _outlineBuffer;
        private ResourceLayout? _perObjectLayout;
        private ResourceLayout? _perFrameLayout;
        private ResourceLayout? _pickLayout;
        private ResourceLayout? _outlineLayout;
        private ResourceSet? _perFrameSet;
        private ResourceSet? _outlineSet;
        private ResourceSet? _pickIdSet;
        private Sampler? _defaultSampler;
        private Texture2D? _whiteTexture;
        private ResourceSet? _defaultObjSet;
        private readonly Dictionary<TextureView, ResourceSet> _resourceSetCache = new();

        // --- Material override (drag-hover preview) ---
        private int _materialOverrideObjectId;
        private Material? _materialOverride;

        public int MaterialOverrideObjectId => _materialOverrideObjectId;
        public Material? MaterialOverrideRef => _materialOverride;

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

        // --- Pick state ---
        private bool _pickRequested;
        private uint _pickX, _pickY;
        private Action<uint>? _pickCallback;
        // Optional exclusion set for cycle-pick: renderers whose GameObject ID is in this set
        // will be skipped during the pick pass (treated as if not present).
        private HashSet<int>? _pickExcludeIds;

        public Framebuffer? Framebuffer => _framebuffer;
        public TextureView? ColorTextureView => _colorTextureView;

        public void Initialize(GraphicsDevice device)
        {
            _device = device;
            // Match swapchain formats so RenderSystem can render to our FB in Rendered mode
            _colorFormat = device.SwapchainFramebuffer.OutputDescription.ColorAttachments[0].Format;
            _depthFormat = device.SwapchainFramebuffer.OutputDescription.DepthAttachment?.Format
                           ?? PixelFormat.D32_Float_S8_UInt;
            var factory = device.ResourceFactory;

            // --- Uniform buffers ---
            _transformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SceneViewTransformUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _materialBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SceneViewMaterialUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _lightBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SceneViewLightUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _pickIdBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<PickIdUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));
            _outlineBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<OutlineUniforms>(),
                BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // --- Sampler ---
            _defaultSampler = factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Wrap, SamplerAddressMode.Wrap, SamplerAddressMode.Wrap,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));

            // --- White texture fallback ---
            _whiteTexture = Texture2D.CreateWhitePixel();
            _whiteTexture.UploadToGPU(device);

            // --- Vertex layout ---
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("UV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            // --- Per-object layout: Transform + Material + Texture + Sampler ---
            _perObjectLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Transforms", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("MaterialData", ResourceKind.UniformBuffer, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MainTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("MainSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // --- Per-frame layout: Light data ---
            _perFrameLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("LightData", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));

            _perFrameSet = factory.CreateResourceSet(new ResourceSetDescription(_perFrameLayout, _lightBuffer));

            // --- Default obj resource set (white tex) ---
            _defaultObjSet = factory.CreateResourceSet(new ResourceSetDescription(
                _perObjectLayout, _transformBuffer, _materialBuffer, _whiteTexture.TextureView!, _defaultSampler));

            // --- Compile shaders ---
            CompileShaders(device);

            // --- Pick layout ---
            _pickLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("PickData", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));

            // --- Outline layout ---
            _outlineLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("OutlineData", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));

            // --- Cached resource sets for outline and pick ---
            _outlineSet = factory.CreateResourceSet(new ResourceSetDescription(_outlineLayout, _outlineBuffer));
            _pickIdSet = factory.CreateResourceSet(new ResourceSetDescription(_pickLayout, _pickIdBuffer));

            // --- Create initial framebuffer ---
            uint w = device.SwapchainFramebuffer.Width;
            uint h = device.SwapchainFramebuffer.Height;
            CreateFramebuffer(w, h);
            CreatePipelines(factory, vertexLayout);
        }

        private void CompileShaders(GraphicsDevice device)
        {
            // MatCap
            _matcapShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("sceneview_matcap.vert"),
                ShaderRegistry.Resolve("sceneview_matcap.frag"));

            // DiffuseOnly — reuse existing forward shaders (vertex.glsl + fragment.glsl)
            _diffuseShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("sceneview_diffuse.vert"),
                ShaderRegistry.Resolve("sceneview_diffuse.frag"));

            // Wireframe — reuse existing forward vertex shader, simple flat color fragment
            _wireframeShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("sceneview_matcap.vert"),
                ShaderRegistry.Resolve("sceneview_matcap.frag"));

            // Pick
            _pickShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("pick_object.vert"),
                ShaderRegistry.Resolve("pick_object.frag"));

            // Outline
            _outlineStencilShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("outline.vert"),
                ShaderRegistry.Resolve("outline.frag"));
            _outlineExpandShaders = _outlineStencilShaders; // Same shaders, different pipeline state

            // Gizmo lines
            _gizmoLineShaders = ShaderCompiler.CompileGLSL(device,
                ShaderRegistry.Resolve("gizmo_line.vert"),
                ShaderRegistry.Resolve("gizmo_line.frag"));
        }

        private void CreateFramebuffer(uint width, uint height)
        {
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            _width = width;
            _height = height;

            var factory = _device!.ResourceFactory;

            _colorTexture?.Dispose();
            _colorTextureView?.Dispose();
            _depthTexture?.Dispose();
            _framebuffer?.Dispose();
            _pickTexture?.Dispose();
            _pickStaging?.Dispose();
            _pickFramebuffer?.Dispose();

            _colorTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1, _colorFormat,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            _colorTextureView = factory.CreateTextureView(_colorTexture);

            _depthTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1, _depthFormat,
                TextureUsage.DepthStencil));

            _framebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                _depthTexture, _colorTexture));

            // Pick framebuffer (R32_UInt)
            _pickTexture = factory.CreateTexture(TextureDescription.Texture2D(
                width, height, 1, 1, PixelFormat.R32_UInt,
                TextureUsage.RenderTarget));

            _pickStaging = factory.CreateTexture(TextureDescription.Texture2D(
                1, 1, 1, 1, PixelFormat.R32_UInt,
                TextureUsage.Staging));

            _pickFramebuffer = factory.CreateFramebuffer(new FramebufferDescription(
                _depthTexture, _pickTexture));
        }

        private void CreatePipelines(ResourceFactory factory, VertexLayoutDescription vertexLayout)
        {
            // Wireframe pipeline
            _wireframePipeline?.Dispose();
            _wireframePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Wireframe, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _wireframeShaders),
                new[] { _perObjectLayout, _perFrameLayout },
                _framebuffer!.OutputDescription));

            // MatCap pipeline
            _matcapPipeline?.Dispose();
            _matcapPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _matcapShaders),
                new[] { _perObjectLayout, _perFrameLayout },
                _framebuffer!.OutputDescription));

            // DiffuseOnly pipeline
            _diffusePipeline?.Dispose();
            _diffusePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _diffuseShaders),
                new[] { _perObjectLayout, _perFrameLayout },
                _framebuffer!.OutputDescription));

            // Pick pipeline
            _pickPipeline?.Dispose();
            _pickPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleDisabled,
                new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _pickShaders),
                new[] { _pickLayout },
                _pickFramebuffer!.OutputDescription));

            // Outline stencil write pipeline
            var stencilWrite = new DepthStencilStateDescription(
                true, false, ComparisonKind.LessEqual,
                true, // stencil test enabled
                new StencilBehaviorDescription(StencilOperation.Replace, StencilOperation.Replace, StencilOperation.Replace, ComparisonKind.Always),
                new StencilBehaviorDescription(StencilOperation.Replace, StencilOperation.Replace, StencilOperation.Replace, ComparisonKind.Always),
                0xFF, 0xFF, 1);

            // Stencil write — no color output
            var noColorWrite = new BlendStateDescription
            {
                AttachmentStates = new[]
                {
                    new BlendAttachmentDescription
                    {
                        BlendEnabled = false,
                        ColorWriteMask = ColorWriteMask.None,
                    }
                }
            };

            _outlineStencilPipeline?.Dispose();
            _outlineStencilPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                noColorWrite,
                stencilWrite,
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _outlineStencilShaders),
                new[] { _outlineLayout },
                _framebuffer!.OutputDescription));

            // Outline expand pipeline
            var stencilTest = new DepthStencilStateDescription(
                false, false, ComparisonKind.Always,
                true,
                new StencilBehaviorDescription(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, ComparisonKind.NotEqual),
                new StencilBehaviorDescription(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, ComparisonKind.NotEqual),
                0xFF, 0x00, 1);

            _outlineExpandPipeline?.Dispose();
            _outlineExpandPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                stencilTest,
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _outlineExpandShaders),
                new[] { _outlineLayout },
                _framebuffer!.OutputDescription));

            // Gizmo pipeline (depth test OFF — always on top, same shaders as matcap)
            _gizmoPipeline?.Dispose();
            _gizmoPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(new[] { vertexLayout }, _matcapShaders),
                new[] { _perObjectLayout, _perFrameLayout },
                _framebuffer!.OutputDescription));

            // Gizmo line pipeline (depth test OFF, LineList topology, always on top)
            _gizmoLinePipeline?.Dispose();
            _gizmoLinePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, false),
                PrimitiveTopology.LineList,
                new ShaderSetDescription(new[] { vertexLayout }, _gizmoLineShaders),
                new[] { _perObjectLayout, _perFrameLayout },
                _framebuffer!.OutputDescription));

            // Gizmo line pick pipeline (LineList, depth OFF, pick framebuffer output)
            _gizmoLinePickPipeline?.Dispose();
            _gizmoLinePickPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleDisabled,
                new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, false),
                PrimitiveTopology.LineList,
                new ShaderSetDescription(new[] { vertexLayout }, _pickShaders),
                new[] { _pickLayout },
                _pickFramebuffer!.OutputDescription));
        }

        public void Resize(uint width, uint height)
        {
            if (width < 1 || height < 1) return;
            if (width == _width && height == _height) return;

            _device!.WaitForIdle();
            _resourceSetCache.Clear();

            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
                new VertexElementDescription("UV", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

            CreateFramebuffer(width, height);
            CreatePipelines(_device.ResourceFactory, vertexLayout);
        }

        /// <summary>
        /// Scene View 렌더링 (Wireframe / MatCap / DiffuseOnly).
        /// WYSIWYG는 RenderSystem을 통해 별도 처리.
        /// </summary>
        public void Render(CommandList cl, EditorCamera camera,
            SceneViewRenderMode mode, TextureView? matcapTexture,
            IReadOnlyList<int>? selectedObjectIds)
        {
            if (_device == null || _framebuffer == null) return;

            float aspect = (float)_width / _height;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();
            var viewProj = viewMatrix * projMatrix;

            cl.SetFramebuffer(_framebuffer);

            // Clear
            var clearColor = mode switch
            {
                SceneViewRenderMode.Wireframe => new RgbaFloat(0.15f, 0.15f, 0.15f, 1f),
                SceneViewRenderMode.MatCap => new RgbaFloat(0.3f, 0.3f, 0.3f, 1f),
                SceneViewRenderMode.DiffuseOnly => new RgbaFloat(0.4f, 0.5f, 0.6f, 1f),
                _ => new RgbaFloat(0.2f, 0.2f, 0.2f, 1f),
            };
            cl.ClearColorTarget(0, clearColor);
            cl.ClearDepthStencil(1f, 0);

            // Setup light data
            var camPos = new Vector4(camera.Position.x, camera.Position.y, camera.Position.z, 0);
            cl.UpdateBuffer(_lightBuffer, 0, new SceneViewLightUniforms
            {
                CameraPos = camPos,
                LightDir = new Vector4(0.3f, -0.8f, 0.5f, 0), // default directional light
                LightColor = new Vector4(1f, 1f, 1f, 1f),
            });

            // Select pipeline based on mode
            Pipeline? activePipeline = mode switch
            {
                SceneViewRenderMode.Wireframe => _wireframePipeline,
                SceneViewRenderMode.MatCap => _matcapPipeline,
                SceneViewRenderMode.DiffuseOnly => _diffusePipeline,
                _ => _matcapPipeline,
            };
            cl.SetPipeline(activePipeline);

            // Draw all mesh renderers
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;
                var mesh = filter.mesh;
                mesh.UploadToGPU(_device);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                // Prepare material
                SceneViewMaterialUniforms matUniforms;
                TextureView? texView = null;

                if (mode == SceneViewRenderMode.DiffuseOnly)
                {
                    // Material override: drag-hover preview용 임시 Material
                    var mat = (_materialOverride != null &&
                               renderer.gameObject.GetInstanceID() == _materialOverrideObjectId)
                        ? _materialOverride
                        : renderer.material;
                    var (mu, tv) = PrepareMaterial(mat);
                    matUniforms = mu;
                    texView = tv;
                }
                else if (mode == SceneViewRenderMode.MatCap)
                {
                    matUniforms = new SceneViewMaterialUniforms
                    {
                        Color = new Vector4(1, 1, 1, 1),
                        HasTexture = matcapTexture != null ? 1f : 0f,
                    };
                    texView = matcapTexture;
                }
                else // Wireframe
                {
                    matUniforms = new SceneViewMaterialUniforms
                    {
                        Color = new Vector4(0.8f, 0.8f, 0.8f, 1f),
                        HasTexture = 0f,
                    };
                }

                DrawMesh(cl, viewProj, viewMatrix, mesh, renderer.transform, matUniforms, texView);
            }

            // Selection outline
            if (selectedObjectIds != null)
            {
                foreach (int selId in selectedObjectIds)
                    DrawSelectionOutline(cl, viewProj, viewMatrix, selId, camPos);
            }

            // Hover outline (cyan) for material drag preview
            if (_materialOverrideObjectId != 0)
            {
                DrawSelectionOutline(cl, viewProj, viewMatrix, _materialOverrideObjectId, camPos,
                    new Vector4(0.3f, 0.7f, 1.0f, 1f));
            }
        }

        /// <summary>
        /// Rendered 모드에서 RenderSystem.Render() 이후, 그리드+아웃라인+기즈모만 오버레이 렌더링.
        /// </summary>
        public void RenderOverlays(CommandList cl, Framebuffer targetFB,
            EditorCamera camera, IReadOnlyList<int>? selectedObjectIds)
        {
            if (_device == null) return;

            float aspect = (float)targetFB.Width / targetFB.Height;
            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();
            var viewProj = viewMatrix * projMatrix;

            cl.SetFramebuffer(targetFB);
            // Clear stencil for outline pass (color/depth preserved from RenderSystem)
            cl.ClearDepthStencil(1f, 0);

            var camPos = new Vector4(camera.Position.x, camera.Position.y, camera.Position.z, 0);

            if (selectedObjectIds != null)
            {
                foreach (int selId in selectedObjectIds)
                    DrawSelectionOutline(cl, viewProj, viewMatrix, selId, camPos);
            }

            // Hover outline (cyan) for material drag preview
            if (_materialOverrideObjectId != 0)
            {
                DrawSelectionOutline(cl, viewProj, viewMatrix, _materialOverrideObjectId, camPos,
                    new Vector4(0.3f, 0.7f, 1.0f, 1f));
            }
        }

        public void RequestPick(uint x, uint y, Action<uint> callback)
        {
            RequestPick(x, y, callback, null);
        }

        /// <summary>
        /// 피킹 요청. <paramref name="excludeIds"/>에 포함된 GameObject ID는 피킹 패스에서 스킵되어
        /// 같은 픽셀 위치에서 다음 후보를 골라낼 수 있다 (cycle picking).
        /// </summary>
        public void RequestPick(uint x, uint y, Action<uint> callback, IReadOnlyCollection<int>? excludeIds)
        {
            _pickRequested = true;
            _pickX = x;
            _pickY = y;
            _pickCallback = callback;
            if (excludeIds != null && excludeIds.Count > 0)
            {
                if (_pickExcludeIds == null) _pickExcludeIds = new HashSet<int>();
                else _pickExcludeIds.Clear();
                foreach (var id in excludeIds) _pickExcludeIds.Add(id);
            }
            else
            {
                _pickExcludeIds = null;
            }
        }

        /// <summary>
        /// Execute the pending pick pass after all gizmo geometry has been collected.
        /// Renders meshes first (with depth), then gizmo lines on top (depth OFF).
        /// </summary>
        public void ExecutePendingPick(EditorCamera camera, float aspect, GizmoRenderer? gizmoRenderer)
        {
            if (!_pickRequested || _device == null) return;
            _pickRequested = false;

            var viewMatrix = camera.GetViewMatrix().ToNumerics();
            var projMatrix = camera.GetProjectionMatrix(aspect).ToNumerics();
            var viewProj = viewMatrix * projMatrix;

            RenderPickPass(viewProj, gizmoRenderer);
        }

        /// <summary>
        /// Draw a gizmo mesh with depth test OFF (always on top).
        /// Uses the matcap shader with a solid color (no texture).
        /// </summary>
        public void DrawGizmoMesh(CommandList cl, Mesh mesh,
            Matrix4x4 world, Matrix4x4 viewMatrix, Matrix4x4 projMatrix, Vector4 color)
        {
            if (_gizmoPipeline == null || mesh.VertexBuffer == null || mesh.IndexBuffer == null) return;

            var viewProj = viewMatrix * projMatrix;

            cl.UpdateBuffer(_transformBuffer, 0, new SceneViewTransformUniforms
            {
                World = world,
                ViewProjection = viewProj,
                ViewMatrix = viewMatrix,
            });
            cl.UpdateBuffer(_materialBuffer, 0, new SceneViewMaterialUniforms
            {
                Color = color,
                HasTexture = 0f,
            });

            cl.SetPipeline(_gizmoPipeline);
            cl.SetGraphicsResourceSet(0, _defaultObjSet);
            cl.SetGraphicsResourceSet(1, _perFrameSet);

            cl.SetVertexBuffer(0, mesh.VertexBuffer);
            cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
            cl.DrawIndexed((uint)mesh.indices.Length);
        }

        /// <summary>
        /// Draw wireframe gizmo lines with depth test OFF (always on top).
        /// The mesh should contain LineList vertex pairs (each consecutive pair = one line segment).
        /// Uses solid color output (no matcap shading).
        /// </summary>
        public void DrawGizmoLines(CommandList cl, Mesh mesh,
            Matrix4x4 world, Matrix4x4 viewMatrix, Matrix4x4 projMatrix, Vector4 color)
        {
            DrawGizmoLines(cl, mesh, world, viewMatrix, projMatrix, color,
                0, (uint)mesh.indices.Length);
        }

        public void DrawGizmoLines(CommandList cl, Mesh mesh,
            Matrix4x4 world, Matrix4x4 viewMatrix, Matrix4x4 projMatrix, Vector4 color,
            uint indexStart, uint indexCount)
        {
            if (_gizmoLinePipeline == null || mesh.VertexBuffer == null || mesh.IndexBuffer == null) return;

            var viewProj = viewMatrix * projMatrix;

            cl.UpdateBuffer(_transformBuffer, 0, new SceneViewTransformUniforms
            {
                World = world,
                ViewProjection = viewProj,
                ViewMatrix = viewMatrix,
            });
            cl.UpdateBuffer(_materialBuffer, 0, new SceneViewMaterialUniforms
            {
                Color = color,
                HasTexture = 0f,
            });

            cl.SetPipeline(_gizmoLinePipeline);
            cl.SetGraphicsResourceSet(0, _defaultObjSet);
            cl.SetGraphicsResourceSet(1, _perFrameSet);

            cl.SetVertexBuffer(0, mesh.VertexBuffer);
            cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
            cl.DrawIndexed(indexCount, 1, indexStart, 0, 0);
        }

        private void DrawMesh(CommandList cl, Matrix4x4 viewProj, Matrix4x4 viewMatrix,
            Mesh mesh, Transform t, SceneViewMaterialUniforms matUniforms, TextureView? texView)
        {
            cl.UpdateBuffer(_transformBuffer, 0, new SceneViewTransformUniforms
            {
                World = RoseEngine.Matrix4x4.TRS(t.position, t.rotation, t.lossyScale).ToNumerics(),
                ViewProjection = viewProj,
                ViewMatrix = viewMatrix,
            });
            cl.UpdateBuffer(_materialBuffer, 0, matUniforms);

            cl.SetGraphicsResourceSet(0, GetOrCreateResourceSet(texView));
            cl.SetGraphicsResourceSet(1, _perFrameSet);

            cl.SetVertexBuffer(0, mesh.VertexBuffer);
            cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
            cl.DrawIndexed((uint)mesh.indices.Length);
        }

        private ResourceSet GetOrCreateResourceSet(TextureView? textureView)
        {
            if (textureView == null)
                return _defaultObjSet!;

            if (_resourceSetCache.TryGetValue(textureView, out var cached))
                return cached;

            var resourceSet = _device!.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                _perObjectLayout!, _transformBuffer!, _materialBuffer!, textureView, _defaultSampler!));
            _resourceSetCache[textureView] = resourceSet;
            return resourceSet;
        }

        private (SceneViewMaterialUniforms mat, TextureView? tex) PrepareMaterial(Material? material)
        {
            var color = material?.color ?? Color.white;
            TextureView? texView = null;
            float hasTexture = 0f;
            if (material?.mainTexture != null)
            {
                material.mainTexture.UploadToGPU(_device!, generateMipmaps: true);
                if (material.mainTexture.TextureView != null)
                { texView = material.mainTexture.TextureView; hasTexture = 1f; }
            }
            return (new SceneViewMaterialUniforms
            {
                Color = new Vector4(color.r, color.g, color.b, color.a),
                HasTexture = hasTexture,
            }, texView);
        }

        private void DrawSelectionOutline(CommandList cl, Matrix4x4 viewProj, Matrix4x4 viewMatrix,
            int selectedId, Vector4 cameraPos, Vector4? outlineColor = null)
        {
            var color = outlineColor ?? new Vector4(1f, 0.6f, 0f, 1f); // default: orange

            // Find selected renderer
            MeshRenderer? selectedRenderer = null;
            foreach (var r in MeshRenderer._allRenderers)
            {
                if (r.gameObject.GetInstanceID() == selectedId && r.enabled && r.gameObject.activeInHierarchy)
                { selectedRenderer = r; break; }
            }
            if (selectedRenderer == null) return;
            var filter = selectedRenderer.GetComponent<MeshFilter>();
            if (filter?.mesh == null) return;
            var mesh = filter.mesh;
            mesh.UploadToGPU(_device!);
            if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) return;

            var world = RoseEngine.Matrix4x4.TRS(
                selectedRenderer.transform.position,
                selectedRenderer.transform.rotation,
                selectedRenderer.transform.lossyScale).ToNumerics();

            // Pass 1: Stencil write
            cl.UpdateBuffer(_outlineBuffer, 0, new OutlineUniforms
            {
                World = world,
                ViewProjection = viewProj,
                OutlineColor = color,
                CameraPos = cameraPos,
                OutlineWidth = 0f, // no expansion for stencil write
            });
            cl.SetPipeline(_outlineStencilPipeline);
            cl.SetGraphicsResourceSet(0, _outlineSet);
            cl.SetVertexBuffer(0, mesh.VertexBuffer);
            cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
            cl.DrawIndexed((uint)mesh.indices.Length);

            // Pass 2: Outline expand
            cl.UpdateBuffer(_outlineBuffer, 0, new OutlineUniforms
            {
                World = world,
                ViewProjection = viewProj,
                OutlineColor = color,
                CameraPos = cameraPos,
                OutlineWidth = 0.005f, // NDC units (~5px at 1080p)
            });
            cl.SetPipeline(_outlineExpandPipeline);
            cl.SetGraphicsResourceSet(0, _outlineSet);
            cl.SetVertexBuffer(0, mesh.VertexBuffer);
            cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
            cl.DrawIndexed((uint)mesh.indices.Length);
        }

        private void RenderPickPass(Matrix4x4 viewProj, GizmoRenderer? gizmoRenderer)
        {
            // Use a separate command list to avoid interrupting the main frame's CL
            var pickCl = _device!.ResourceFactory.CreateCommandList();
            pickCl.Begin();

            pickCl.SetFramebuffer(_pickFramebuffer);
            pickCl.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 0));
            pickCl.ClearDepthStencil(1f);

            // --- Phase 1: Mesh picking (with depth test) ---
            pickCl.SetPipeline(_pickPipeline);

            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                int rawId = renderer.gameObject.GetInstanceID();
                if (_pickExcludeIds != null && _pickExcludeIds.Contains(rawId)) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;
                var mesh = filter.mesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                uint objId = (uint)rawId;
                pickCl.UpdateBuffer(_pickIdBuffer, 0, new PickIdUniforms
                {
                    World = RoseEngine.Matrix4x4.TRS(
                        renderer.transform.position,
                        renderer.transform.rotation,
                        renderer.transform.lossyScale).ToNumerics(),
                    ViewProjection = viewProj,
                    ObjectId = objId,
                });

                pickCl.SetGraphicsResourceSet(0, _pickIdSet);
                pickCl.SetVertexBuffer(0, mesh.VertexBuffer);
                pickCl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
                pickCl.DrawIndexed((uint)mesh.indices.Length);
            }

            // --- Phase 2: Gizmo line picking (depth OFF — always on top) ---
            if (gizmoRenderer != null && _gizmoLinePickPipeline != null)
            {
                gizmoRenderer.RenderPick(pickCl, _gizmoLinePickPipeline,
                    _pickIdBuffer!, _pickIdSet!, viewProj);
            }

            // Read back the pixel
            if (_pickX < _width && _pickY < _height)
            {
                pickCl.CopyTexture(_pickTexture, _pickX, _pickY, 0, 0, 0,
                    _pickStaging, 0, 0, 0, 0, 0, 1, 1, 1, 1);
            }

            pickCl.End();
            _device!.SubmitCommands(pickCl);
            _device.WaitForIdle();
            pickCl.Dispose();

            var map = _device.Map(_pickStaging, MapMode.Read);
            uint pickedId = 0;
            unsafe { pickedId = *(uint*)map.Data; }
            _device.Unmap(_pickStaging);

            _pickCallback?.Invoke(pickedId);
            _pickCallback = null;
        }

        public void Dispose()
        {
            foreach (var rs in _resourceSetCache.Values) rs.Dispose();
            _resourceSetCache.Clear();

            _wireframePipeline?.Dispose();
            _matcapPipeline?.Dispose();
            _diffusePipeline?.Dispose();
            _pickPipeline?.Dispose();
            _outlineStencilPipeline?.Dispose();
            _outlineExpandPipeline?.Dispose();
            _gizmoPipeline?.Dispose();
            _gizmoLinePipeline?.Dispose();
            _gizmoLinePickPipeline?.Dispose();

            _framebuffer?.Dispose();
            _colorTextureView?.Dispose();
            _colorTexture?.Dispose();
            _depthTexture?.Dispose();
            _pickFramebuffer?.Dispose();
            _pickTexture?.Dispose();
            _pickStaging?.Dispose();

            _transformBuffer?.Dispose();
            _materialBuffer?.Dispose();
            _lightBuffer?.Dispose();
            _pickIdBuffer?.Dispose();
            _outlineBuffer?.Dispose();

            _defaultObjSet?.Dispose();
            _perFrameSet?.Dispose();
            _outlineSet?.Dispose();
            _pickIdSet?.Dispose();

            _perObjectLayout?.Dispose();
            _perFrameLayout?.Dispose();
            _pickLayout?.Dispose();
            _outlineLayout?.Dispose();

            _defaultSampler?.Dispose();
            _whiteTexture?.Dispose();

            if (_wireframeShaders != null)
                foreach (var s in _wireframeShaders) s.Dispose();
            if (_matcapShaders != null)
                foreach (var s in _matcapShaders) s.Dispose();
            if (_diffuseShaders != null)
                foreach (var s in _diffuseShaders) s.Dispose();
            if (_pickShaders != null)
                foreach (var s in _pickShaders) s.Dispose();
            if (_outlineStencilShaders != null)
                foreach (var s in _outlineStencilShaders) s.Dispose();
            if (_gizmoLineShaders != null)
                foreach (var s in _gizmoLineShaders) s.Dispose();
        }
    }
}
