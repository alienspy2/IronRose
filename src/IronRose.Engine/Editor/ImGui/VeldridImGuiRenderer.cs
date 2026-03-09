using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImGuiNET;
using IronRose.Rendering;
using Veldrid;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// Renders ImGui draw data using Veldrid.
    /// Manages pipeline, font atlas texture, and per-frame vertex/index buffers.
    /// </summary>
    public sealed class VeldridImGuiRenderer : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly ResourceFactory _factory;

        // Pipeline resources
        private Pipeline _pipeline = null!;
        private ResourceLayout _projectionLayout = null!;
        private ResourceLayout _textureLayout = null!;
        private DeviceBuffer _projectionBuffer = null!;
        private ResourceSet _projectionSet = null!;
        private Shader[] _shaders = null!;

        // Font atlas
        private Texture _fontTexture = null!;
        private TextureView _fontTextureView = null!;
        private ResourceSet _fontResourceSet = null!;
        private Sampler _linearSampler = null!;

        // Dynamic buffers
        private DeviceBuffer _vertexBuffer = null!;
        private DeviceBuffer _indexBuffer = null!;
        private uint _vertexBufferSize;
        private uint _indexBufferSize;

        // Texture bindings cache
        private readonly Dictionary<IntPtr, ResourceSet> _textureBindings = new();
        private int _nextTextureId = 1;

        public VeldridImGuiRenderer(GraphicsDevice device, string shaderDirectory, OutputDescription outputDescription)
        {
            _device = device;
            _factory = device.ResourceFactory;
            CreateResources(shaderDirectory, outputDescription);
        }

        private void CreateResources(string shaderDir, OutputDescription outputDesc)
        {
            // Projection uniform buffer (mat4)
            _projectionBuffer = _factory.CreateBuffer(new BufferDescription(64, BufferUsage.UniformBuffer | BufferUsage.Dynamic));

            // Resource layouts
            _projectionLayout = _factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("ProjectionBuffer", ResourceKind.UniformBuffer, ShaderStages.Vertex)));

            _textureLayout = _factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("FontTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("FontSampler", ResourceKind.Sampler, ShaderStages.Fragment)));

            // Compile shaders
            _shaders = ShaderCompiler.CompileGLSL(
                _device,
                System.IO.Path.Combine(shaderDir, "imgui.vert"),
                System.IO.Path.Combine(shaderDir, "imgui.frag"));

            // Vertex layout matching ImDrawVert: pos(Float2) + uv(Float2) + col(Byte4_Norm)
            var vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Byte4_Norm));

            // Alpha blend pipeline
            var blendState = new BlendStateDescription(RgbaFloat.Black,
                new BlendAttachmentDescription(
                    blendEnabled: true,
                    sourceColorFactor: BlendFactor.SourceAlpha,
                    destinationColorFactor: BlendFactor.InverseSourceAlpha,
                    colorFunction: BlendFunction.Add,
                    sourceAlphaFactor: BlendFactor.One,
                    destinationAlphaFactor: BlendFactor.InverseSourceAlpha,
                    alphaFunction: BlendFunction.Add));

            _pipeline = _factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = blendState,
                DepthStencilState = DepthStencilStateDescription.Disabled,
                RasterizerState = new RasterizerStateDescription(
                    cullMode: FaceCullMode.None,
                    fillMode: PolygonFillMode.Solid,
                    frontFace: FrontFace.Clockwise,
                    depthClipEnabled: false,
                    scissorTestEnabled: true),
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _projectionLayout, _textureLayout },
                ShaderSet = new ShaderSetDescription(
                    vertexLayouts: new[] { vertexLayout },
                    shaders: _shaders),
                Outputs = outputDesc,
            });

            // Linear sampler for font atlas
            _linearSampler = _factory.CreateSampler(new SamplerDescription(
                SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                SamplerFilter.MinLinear_MagLinear_MipLinear,
                null, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));

            // Projection resource set
            _projectionSet = _factory.CreateResourceSet(new ResourceSetDescription(_projectionLayout, _projectionBuffer));

            // Initial dynamic buffers (will grow as needed)
            _vertexBufferSize = 10000 * (uint)Unsafe.SizeOf<ImDrawVert>();
            _indexBufferSize = 30000 * sizeof(ushort);
            _vertexBuffer = _factory.CreateBuffer(new BufferDescription(_vertexBufferSize, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            _indexBuffer = _factory.CreateBuffer(new BufferDescription(_indexBufferSize, BufferUsage.IndexBuffer | BufferUsage.Dynamic));

            // Build font atlas
            RecreateFontAtlas();
        }

        public void RecreateFontAtlas()
        {
            var io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

            // Dispose old font resources
            _fontResourceSet?.Dispose();
            _fontTextureView?.Dispose();
            _fontTexture?.Dispose();

            _fontTexture = _factory.CreateTexture(new TextureDescription(
                (uint)width, (uint)height, 1, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled,
                TextureType.Texture2D));

            _device.UpdateTexture(_fontTexture, pixels, (uint)(width * height * bytesPerPixel), 0, 0, 0, (uint)width, (uint)height, 1, 0, 0);

            _fontTextureView = _factory.CreateTextureView(_fontTexture);
            _fontResourceSet = _factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _fontTextureView, _linearSampler));

            io.Fonts.SetTexID(GetOrRegisterTexture(_fontResourceSet));
            io.Fonts.ClearTexData();
        }

        /// <summary>Register a ResourceSet and return an IntPtr ID for ImGui.Image().</summary>
        public IntPtr GetOrRegisterTexture(ResourceSet resourceSet)
        {
            var id = new IntPtr(_nextTextureId++);
            _textureBindings[id] = resourceSet;
            return id;
        }

        /// <summary>Create and register a texture binding for use with ImGui.Image().</summary>
        public IntPtr GetOrCreateImGuiBinding(TextureView textureView)
        {
            var rs = _factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, textureView, _linearSampler));
            return GetOrRegisterTexture(rs);
        }

        /// <summary>Update an existing texture binding with a new TextureView (e.g. after resize).</summary>
        public void UpdateImGuiBinding(IntPtr id, TextureView newView)
        {
            if (_textureBindings.TryGetValue(id, out var old))
                old.Dispose();
            var rs = _factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, newView, _linearSampler));
            _textureBindings[id] = rs;
        }

        public void Render(CommandList cl, ImDrawDataPtr drawData)
        {
            if (drawData.CmdListsCount == 0) return;

            // Calculate total vertex/index counts
            uint totalVertexBytes = (uint)(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>());
            uint totalIndexBytes = (uint)(drawData.TotalIdxCount * sizeof(ushort));

            // Grow buffers if needed
            if (totalVertexBytes > _vertexBufferSize)
            {
                _vertexBuffer.Dispose();
                _vertexBufferSize = (uint)(totalVertexBytes * 1.5f);
                _vertexBuffer = _factory.CreateBuffer(new BufferDescription(_vertexBufferSize, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
            }
            if (totalIndexBytes > _indexBufferSize)
            {
                _indexBuffer.Dispose();
                _indexBufferSize = (uint)(totalIndexBytes * 1.5f);
                _indexBuffer = _factory.CreateBuffer(new BufferDescription(_indexBufferSize, BufferUsage.IndexBuffer | BufferUsage.Dynamic));
            }

            // Upload vertex/index data
            uint vtxOffset = 0;
            uint idxOffset = 0;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdLists[n];
                uint vtxSize = (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>());
                uint idxSize = (uint)(cmdList.IdxBuffer.Size * sizeof(ushort));

                cl.UpdateBuffer(_vertexBuffer, vtxOffset, cmdList.VtxBuffer.Data, vtxSize);
                cl.UpdateBuffer(_indexBuffer, idxOffset, cmdList.IdxBuffer.Data, idxSize);

                vtxOffset += vtxSize;
                idxOffset += idxSize;
            }

            // Update projection matrix
            var displayPos = drawData.DisplayPos;
            var displaySize = drawData.DisplaySize;
            float L = displayPos.X;
            float R = displayPos.X + displaySize.X;
            float T = displayPos.Y;
            float B = displayPos.Y + displaySize.Y;

            var mvp = new Matrix4x4(
                2f / (R - L),       0f,                  0f, 0f,
                0f,                  2f / (T - B),        0f, 0f,
                0f,                  0f,                 -1f, 0f,
                (R + L) / (L - R),  (T + B) / (B - T),   0f, 1f);

            cl.UpdateBuffer(_projectionBuffer, 0, ref mvp);

            // Set pipeline state
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _projectionSet);
            cl.SetVertexBuffer(0, _vertexBuffer);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);

            // Draw commands
            var clipOff = drawData.DisplayPos;
            var clipScale = drawData.FramebufferScale;

            uint globalVtxOffset = 0;
            uint globalIdxOffset = 0;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var cmdList = drawData.CmdLists[n];
                for (int cmd = 0; cmd < cmdList.CmdBuffer.Size; cmd++)
                {
                    var pcmd = cmdList.CmdBuffer[cmd];
                    if (pcmd.UserCallback != IntPtr.Zero)
                        continue;

                    // Clip rect
                    var clipRect = new Vector4(
                        (pcmd.ClipRect.X - clipOff.X) * clipScale.X,
                        (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y,
                        (pcmd.ClipRect.Z - clipOff.X) * clipScale.X,
                        (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                    if (clipRect.X >= displaySize.X || clipRect.Y >= displaySize.Y ||
                        clipRect.Z < 0f || clipRect.W < 0f)
                        continue;

                    cl.SetScissorRect(0,
                        (uint)Math.Max(clipRect.X, 0),
                        (uint)Math.Max(clipRect.Y, 0),
                        (uint)(clipRect.Z - Math.Max(clipRect.X, 0)),
                        (uint)(clipRect.W - Math.Max(clipRect.Y, 0)));

                    // Bind texture
                    if (_textureBindings.TryGetValue(pcmd.TextureId, out var rs))
                        cl.SetGraphicsResourceSet(1, rs);

                    cl.DrawIndexed(pcmd.ElemCount,
                        1,
                        pcmd.IdxOffset + globalIdxOffset,
                        (int)(pcmd.VtxOffset + globalVtxOffset),
                        0);
                }

                globalVtxOffset += (uint)cmdList.VtxBuffer.Size;
                globalIdxOffset += (uint)cmdList.IdxBuffer.Size;
            }
        }

        public void Dispose()
        {
            foreach (var rs in _textureBindings.Values)
                rs.Dispose();
            _textureBindings.Clear();

            _fontResourceSet?.Dispose();
            _fontTextureView?.Dispose();
            _fontTexture?.Dispose();
            _linearSampler?.Dispose();

            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();

            _projectionSet?.Dispose();
            _projectionBuffer?.Dispose();

            _pipeline?.Dispose();
            _projectionLayout?.Dispose();
            _textureLayout?.Dispose();

            foreach (var shader in _shaders)
                shader.Dispose();
        }
    }
}
