// ------------------------------------------------------------
// @file    GpuTextureCompressor.cs
// @brief   Vulkan compute 셰이더 기반 GPU BC7/BC5 텍스처 압축기. 에셋 warm-up 시 사용.
// @deps    IronRose.Rendering/ShaderCompiler, RoseEngine/EditorDebug, RoseEngine/ThreadGuard, Veldrid
// @exports
//   sealed class GpuTextureCompressor : IDisposable
//     ctor(GraphicsDevice)                                                 — 장치 참조 저장 (초기화는 Initialize)
//     Initialize(string shaderDir): void                                   — compute 파이프라인/버퍼 생성 (1회)
//     CompressBC7(byte[] rgba, int w, int h): byte[]                       — RGBA→BC7 블록 배열 반환
//     CompressBC5(byte[] rgba, int w, int h): byte[]                       — RGBA→BC5 블록 배열 반환
//     GenerateMipmapsGPU(byte[] rgba, int w, int h): byte[][]              — GPU mipmap 체인 생성 후 RGBA 각 레벨 반환
//     Dispose(): void                                                      — GPU 리소스 해제
// @note    내부 _lock으로 동시 호출을 직렬화. 2048×2048까지는 사전 할당 버퍼 재사용.
//          (Phase B-5) 모든 GPU 진입 지점(Initialize/CompressBC7/CompressBC5/GenerateMipmapsGPU/Dispose)에
//          ThreadGuard.CheckMainThread 가드를 두어 메인 외 스레드 진입을 차단하고 null/빈 결과로 스킵한다
//          (throw 금지). 위반 시 호출자는 CPU 폴백 경로로 안전하게 이어져야 한다.
// ------------------------------------------------------------
using System;
using System.IO;
using System.Runtime.InteropServices;
using IronRose.Rendering;
using RoseEngine;
using Veldrid;
using Veldrid.SPIRV;
using Debug = RoseEngine.EditorDebug;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// GPU-accelerated BC7/BC5 texture compressor using Vulkan compute shaders.
    /// Thread-safe via lock; designed for use during asset warm-up.
    /// </summary>
    public sealed class GpuTextureCompressor : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly object _lock = new();

        // Compute pipelines
        private Pipeline? _bc7Pipeline;
        private Pipeline? _bc5Pipeline;
        private ResourceLayout? _layout;

        // Pre-allocated buffers for max 2048×2048
        private const int MaxPixels = 2048 * 2048;
        private const int MaxBlocks = (2048 / 4) * (2048 / 4); // 262144
        private const uint MaxInputBytes = MaxPixels * 4;       // 16 MB
        private const uint MaxOutputBytes = MaxBlocks * 16;     // 4 MB

        private DeviceBuffer? _stagingInput;
        private DeviceBuffer? _gpuInput;
        private DeviceBuffer? _gpuOutput;
        private DeviceBuffer? _stagingOutput;
        private DeviceBuffer? _paramsBuffer;
        private CommandList? _cl;

        private bool _initialized;
        private bool _disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct CompressParams
        {
            public uint TexWidth;
            public uint TexHeight;
            public uint BlocksX;
            public uint BlocksY;
        }

        public GpuTextureCompressor(GraphicsDevice device)
        {
            _device = device;
        }

        public void Initialize(string shaderDir)
        {
            if (_initialized) return;
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.Initialize"))
                return; // 메인이 아니면 초기화를 건너뛴다 (드라이버 충돌 회피)
            var factory = _device.ResourceFactory;

            // Compile compute shaders
            var bc7Shader = ShaderCompiler.CompileComputeGLSL(_device,
                Path.Combine(shaderDir, "compress_bc7.comp"));
            var bc5Shader = ShaderCompiler.CompileComputeGLSL(_device,
                Path.Combine(shaderDir, "compress_bc5.comp"));

            // Resource layout: input SSBO + output SSBO + params UBO
            _layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("InputPixels",
                    ResourceKind.StructuredBufferReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("OutputBlocks",
                    ResourceKind.StructuredBufferReadWrite, ShaderStages.Compute),
                new ResourceLayoutElementDescription("Params",
                    ResourceKind.UniformBuffer, ShaderStages.Compute)));

            // Compute pipelines (workgroup 8×8×1)
            _bc7Pipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                bc7Shader, new[] { _layout }, 8, 8, 1));
            _bc5Pipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                bc5Shader, new[] { _layout }, 8, 8, 1));

            // Pre-allocate buffers for max size
            _stagingInput = factory.CreateBuffer(new BufferDescription(
                MaxInputBytes, BufferUsage.Staging));

            _gpuInput = factory.CreateBuffer(new BufferDescription(
                MaxInputBytes, BufferUsage.StructuredBufferReadOnly, 4));

            _gpuOutput = factory.CreateBuffer(new BufferDescription(
                MaxOutputBytes, BufferUsage.StructuredBufferReadWrite, 4));

            _stagingOutput = factory.CreateBuffer(new BufferDescription(
                MaxOutputBytes, BufferUsage.Staging));

            _paramsBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<CompressParams>(),
                BufferUsage.UniformBuffer));

            _cl = factory.CreateCommandList();

            _initialized = true;
            Debug.Log("[GpuTextureCompressor] Initialized with BC7 + BC5 compute pipelines");
        }

        public byte[] CompressBC7(byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.CompressBC7"))
                return Array.Empty<byte>();
            return CompressInternal(rgbaData, width, height, _bc7Pipeline!);
        }

        public byte[] CompressBC5(byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.CompressBC5"))
                return Array.Empty<byte>();
            return CompressInternal(rgbaData, width, height, _bc5Pipeline!);
        }

        private byte[] CompressInternal(byte[] rgbaData, int width, int height, Pipeline pipeline)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            int totalBlocks = blocksX * blocksY;
            uint outputBytes = (uint)(totalBlocks * 16);
            uint inputBytes = (uint)(width * height * 4);

            lock (_lock)
            {
                var factory = _device.ResourceFactory;
                bool useTemp = width * height > MaxPixels;

                DeviceBuffer stagingIn, gpuIn, gpuOut, stagingOut;
                if (useTemp)
                {
                    stagingIn = factory.CreateBuffer(new BufferDescription(inputBytes, BufferUsage.Staging));
                    gpuIn = factory.CreateBuffer(new BufferDescription(inputBytes, BufferUsage.StructuredBufferReadOnly, 4));
                    gpuOut = factory.CreateBuffer(new BufferDescription(outputBytes, BufferUsage.StructuredBufferReadWrite, 4));
                    stagingOut = factory.CreateBuffer(new BufferDescription(outputBytes, BufferUsage.Staging));
                }
                else
                {
                    stagingIn = _stagingInput!;
                    gpuIn = _gpuInput!;
                    gpuOut = _gpuOutput!;
                    stagingOut = _stagingOutput!;
                }

                try
                {
                    // Upload pixel data
                    _device.UpdateBuffer(stagingIn, 0, rgbaData);

                    // Update params
                    var p = new CompressParams
                    {
                        TexWidth = (uint)width,
                        TexHeight = (uint)height,
                        BlocksX = (uint)blocksX,
                        BlocksY = (uint)blocksY
                    };
                    _device.UpdateBuffer(_paramsBuffer!, 0, ref p);

                    // Create resource set
                    var resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
                        _layout!, gpuIn, gpuOut, _paramsBuffer!));

                    // Submit 1: Upload input + compute dispatch
                    // (split into 2 submissions to enforce a full pipeline barrier
                    //  between compute writes and the output CopyBuffer readback)
                    _cl!.Begin();
                    _cl.CopyBuffer(stagingIn, 0, gpuIn, 0, inputBytes);
                    _cl.SetPipeline(pipeline);
                    _cl.SetComputeResourceSet(0, resourceSet);
                    _cl.Dispatch((uint)((blocksX + 7) / 8), (uint)((blocksY + 7) / 8), 1);
                    _cl.End();
                    _device.SubmitCommands(_cl);
                    _device.WaitForIdle();

                    // Submit 2: Read back compute output (after full fence)
                    _cl.Begin();
                    _cl.CopyBuffer(gpuOut, 0, stagingOut, 0, outputBytes);
                    _cl.End();
                    _device.SubmitCommands(_cl);
                    _device.WaitForIdle();

                    // Read back result
                    var result = new byte[outputBytes];
                    var map = _device.Map(stagingOut, MapMode.Read);
                    Marshal.Copy(map.Data, result, 0, (int)outputBytes);
                    _device.Unmap(stagingOut);

                    resourceSet.Dispose();
                    return result;
                }
                finally
                {
                    if (useTemp)
                    {
                        stagingIn.Dispose();
                        gpuIn.Dispose();
                        gpuOut.Dispose();
                        stagingOut.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Generates mipmaps on GPU via hardware filtering, then reads back each mip level as RGBA.
        /// Returns byte[][] where [0] = mip0 (original size), [1] = mip1 (w/2, h/2), etc.
        /// </summary>
        public byte[][] GenerateMipmapsGPU(byte[] rgbaData, int width, int height)
        {
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.GenerateMipmapsGPU"))
                return Array.Empty<byte[]>();
            lock (_lock)
            {
                var factory = _device.ResourceFactory;
                uint mipLevels = (uint)(Math.Floor(Math.Log2(Math.Max(width, height))) + 1);

                // Create source texture with GenerateMipmaps capability
                var srcTex = factory.CreateTexture(new TextureDescription(
                    (uint)width, (uint)height, 1, mipLevels, 1,
                    PixelFormat.R8_G8_B8_A8_UNorm,
                    TextureUsage.Sampled | TextureUsage.GenerateMipmaps,
                    TextureType.Texture2D));

                // Upload mip 0
                _device.UpdateTexture(srcTex, rgbaData, 0, 0, 0,
                    (uint)width, (uint)height, 1, 0, 0);

                // GPU hardware mipmap generation (linear-space box filter with proper downsampling)
                _cl!.Begin();
                _cl.GenerateMipmaps(srcTex);
                _cl.End();
                _device.SubmitCommands(_cl);
                _device.WaitForIdle();

                // Staging texture for readback
                var stagingTex = factory.CreateTexture(new TextureDescription(
                    (uint)width, (uint)height, 1, mipLevels, 1,
                    PixelFormat.R8_G8_B8_A8_UNorm,
                    TextureUsage.Staging,
                    TextureType.Texture2D));

                // Copy all mip levels to staging
                _cl.Begin();
                uint mw = (uint)width, mh = (uint)height;
                for (uint mip = 0; mip < mipLevels; mip++)
                {
                    _cl.CopyTexture(srcTex, 0, 0, 0, mip, 0,
                        stagingTex, 0, 0, 0, mip, 0, mw, mh, 1, 1);
                    mw = Math.Max(1, mw / 2);
                    mh = Math.Max(1, mh / 2);
                }
                _cl.End();
                _device.SubmitCommands(_cl);
                _device.WaitForIdle();

                // Read back each mip level
                var result = new byte[mipLevels][];
                mw = (uint)width;
                mh = (uint)height;
                for (uint mip = 0; mip < mipLevels; mip++)
                {
                    uint rowBytes = mw * 4;
                    result[mip] = new byte[mw * mh * 4];

                    var mapped = _device.Map(stagingTex, MapMode.Read, mip);
                    if (mapped.RowPitch == rowBytes)
                    {
                        Marshal.Copy(mapped.Data, result[mip], 0, result[mip].Length);
                    }
                    else
                    {
                        // Copy row by row to handle pitch padding
                        for (uint y = 0; y < mh; y++)
                        {
                            var srcPtr = IntPtr.Add(mapped.Data, (int)(y * mapped.RowPitch));
                            Marshal.Copy(srcPtr, result[mip], (int)(y * rowBytes), (int)rowBytes);
                        }
                    }
                    _device.Unmap(stagingTex);

                    mw = Math.Max(1, mw / 2);
                    mh = Math.Max(1, mh / 2);
                }

                srcTex.Dispose();
                stagingTex.Dispose();

                return result;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!ThreadGuard.CheckMainThread("GpuTextureCompressor.Dispose"))
                return; // 메인이 아니면 GPU 리소스 해제를 스킵 (드라이버 충돌 회피. 프로세스 종료 시 자동 정리됨)
            _cl?.Dispose();
            _stagingInput?.Dispose();
            _gpuInput?.Dispose();
            _gpuOutput?.Dispose();
            _stagingOutput?.Dispose();
            _paramsBuffer?.Dispose();
            _bc7Pipeline?.Dispose();
            _bc5Pipeline?.Dispose();
            _layout?.Dispose();
        }
    }
}
