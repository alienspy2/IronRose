using System;
using System.IO;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;

namespace RoseEngine
{
    public class Texture2D : IDisposable
    {
        public string name { get; set; } = "";
        public int width { get; private set; }
        public int height { get; private set; }

        internal byte[]? _pixelData;
        internal float[]? _hdrPixelData;   // RGBA float32, 4 floats per pixel (null for LDR)
        public bool isHDR => _hdrPixelData != null;
        internal PixelFormat _gpuFormat = PixelFormat.R8_G8_B8_A8_UNorm;
        internal byte[][]? _mipData;  // per-mip BC block data (null for uncompressed)
        internal Veldrid.Texture? VeldridTexture { get; private set; }
        internal TextureView? TextureView { get; private set; }
        private bool _isDirty = true;
        private bool _hasMipmaps;

        public Texture2D(int width, int height)
        {
            this.width = width;
            this.height = height;
            _pixelData = new byte[width * height * 4];
        }

        private Texture2D(int width, int height, byte[] data)
        {
            this.width = width;
            this.height = height;
            _pixelData = data;
        }

        internal Texture2D(int width, int height, float[] hdrData)
        {
            this.width = width;
            this.height = height;
            _hdrPixelData = hdrData;
            _pixelData = null;
            _gpuFormat = PixelFormat.R16_G16_B16_A16_Float;
        }

        public static Texture2D LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Texture file not found: {path}");

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".hdr") return LoadHdrFromFile(path);
            if (ext == ".exr") return LoadExrFromFile(path);

            using var image = Image.Load<Rgba32>(path);
            int w = image.Width;
            int h = image.Height;
            var data = new byte[w * h * 4];

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        int offset = (y * w + x) * 4;
                        data[offset + 0] = row[x].R;
                        data[offset + 1] = row[x].G;
                        data[offset + 2] = row[x].B;
                        data[offset + 3] = row[x].A;
                    }
                }
            });

            Debug.Log($"[Texture2D] Loaded: {path} ({w}x{h})");
            return new Texture2D(w, h, data) { name = Path.GetFileNameWithoutExtension(path) };
        }

        public static Texture2D LoadHdrFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Texture file not found: {path}");

            var (w, h, data) = HdrReader.Read(path);
            Debug.Log($"[Texture2D] Loaded HDR: {path} ({w}x{h})");
            return new Texture2D(w, h, data) { name = Path.GetFileNameWithoutExtension(path) };
        }

        public static Texture2D LoadExrFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Texture file not found: {path}");

            var (w, h, data) = ExrReader.Read(path);
            Debug.Log($"[Texture2D] Loaded EXR: {path} ({w}x{h})");
            return new Texture2D(w, h, data) { name = Path.GetFileNameWithoutExtension(path) };
        }

        public static Texture2D LoadFromMemory(byte[] compressedData)
        {
            using var image = Image.Load<Rgba32>(compressedData);
            int w = image.Width;
            int h = image.Height;
            var data = new byte[w * h * 4];

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < w; x++)
                    {
                        int offset = (y * w + x) * 4;
                        data[offset + 0] = row[x].R;
                        data[offset + 1] = row[x].G;
                        data[offset + 2] = row[x].B;
                        data[offset + 3] = row[x].A;
                    }
                }
            });

            Debug.Log($"[Texture2D] Loaded from memory ({w}x{h})");
            return new Texture2D(w, h, data);
        }

        internal static Texture2D CreateFromCompressed(int width, int height, PixelFormat gpuFormat, byte[][] mipData)
        {
            return new Texture2D(width, height, mipData[0])
            {
                _gpuFormat = gpuFormat,
                _mipData = mipData,
                _pixelData = null,
            };
        }

        public static Texture2D CreateWhitePixel()
        {
            var data = new byte[] { 255, 255, 255, 255 };
            return new Texture2D(1, 1, data);
        }

        /// <summary>Flat normal (0,0,1) in tangent space → RGBA(128, 128, 255, 255).</summary>
        public static Texture2D CreateDefaultNormal()
        {
            var data = new byte[] { 128, 128, 255, 255 };
            return new Texture2D(1, 1, data);
        }

        /// <summary>Default MRO: Metallic=0, Roughness=0.5, Occlusion=1 → RGBA(0, 128, 255, 255).</summary>
        public static Texture2D CreateDefaultMRO()
        {
            var data = new byte[] { 0, 128, 255, 255 };
            return new Texture2D(1, 1, data);
        }

        // Shared default texture instances (lazy-initialized)
        private static Texture2D? _defaultNormal;
        private static Texture2D? _defaultMRO;

        /// <summary>Shared flat normal map (1x1). Thread-safe lazy init.</summary>
        public static Texture2D DefaultNormal => _defaultNormal ??= CreateDefaultNormal();

        /// <summary>Shared default MRO map (M=0, R=0.5, O=1). Thread-safe lazy init.</summary>
        public static Texture2D DefaultMRO => _defaultMRO ??= CreateDefaultMRO();

        public void SetPixels(byte[] rgbaData)
        {
            _pixelData = rgbaData;
            _isDirty = true;
        }

        public void UploadToGPU(GraphicsDevice device, bool generateMipmaps = false)
        {
            bool isCompressed = _mipData != null && _gpuFormat != PixelFormat.R8_G8_B8_A8_UNorm;
            if (isCompressed)
            {
                if (!_isDirty || _mipData == null || _mipData.Length == 0) return;

                var factory = device.ResourceFactory;
                TextureView?.Dispose();
                VeldridTexture?.Dispose();

                uint mipLevels = (uint)_mipData.Length;
                VeldridTexture = factory.CreateTexture(new TextureDescription(
                    (uint)width, (uint)height, 1, mipLevels, 1,
                    _gpuFormat,
                    TextureUsage.Sampled,
                    TextureType.Texture2D));

                uint mipW = (uint)width, mipH = (uint)height;
                for (uint mip = 0; mip < mipLevels; mip++)
                {
                    device.UpdateTexture(VeldridTexture, _mipData[mip],
                        0, 0, 0,
                        mipW, mipH, 1,
                        mip, 0);
                    mipW = Math.Max(1, mipW / 2);
                    mipH = Math.Max(1, mipH / 2);
                }

                TextureView = factory.CreateTextureView(VeldridTexture);
                _isDirty = false;
                _hasMipmaps = mipLevels > 1;
            }
            else if (_hdrPixelData != null)
            {
                // HDR path: float32 → System.Half (float16) GPU upload
                bool needsMipmapUpgrade = generateMipmaps && !_hasMipmaps && VeldridTexture != null;
                if (!_isDirty && !needsMipmapUpgrade)
                    return;

                var factory = device.ResourceFactory;

                TextureView?.Dispose();
                VeldridTexture?.Dispose();

                uint mipLevels = 1;
                var usage = TextureUsage.Sampled;
                if (generateMipmaps)
                {
                    mipLevels = (uint)Math.Floor(Math.Log2(Math.Max(width, height))) + 1;
                    usage = TextureUsage.Sampled | TextureUsage.GenerateMipmaps;
                }

                VeldridTexture = factory.CreateTexture(new TextureDescription(
                    (uint)width, (uint)height, 1, mipLevels, 1,
                    PixelFormat.R16_G16_B16_A16_Float,
                    usage,
                    TextureType.Texture2D));

                var halfData = ConvertFloatToHalfBytes(_hdrPixelData);
                device.UpdateTexture(VeldridTexture, halfData,
                    0, 0, 0,
                    (uint)width, (uint)height, 1,
                    0, 0);

                if (generateMipmaps && mipLevels > 1)
                {
                    using var cl = factory.CreateCommandList();
                    cl.Begin();
                    cl.GenerateMipmaps(VeldridTexture);
                    cl.End();
                    device.SubmitCommands(cl);
                    device.WaitForIdle();
                    Debug.Log($"[Texture2D] GenerateMipmaps (HDR): {width}x{height}, {mipLevels} mips");
                }

                TextureView = factory.CreateTextureView(VeldridTexture);
                _isDirty = false;
                _hasMipmaps = generateMipmaps && mipLevels > 1;
            }
            else
            {
                // LDR path: byte RGBA8 GPU upload
                bool needsMipmapUpgrade = generateMipmaps && !_hasMipmaps && VeldridTexture != null;
                if ((!_isDirty && !needsMipmapUpgrade) || _pixelData == null)
                    return;

                var factory = device.ResourceFactory;

                TextureView?.Dispose();
                VeldridTexture?.Dispose();

                uint mipLevels = 1;
                var usage = TextureUsage.Sampled;
                if (generateMipmaps)
                {
                    mipLevels = (uint)Math.Floor(Math.Log2(Math.Max(width, height))) + 1;
                    usage = TextureUsage.Sampled | TextureUsage.GenerateMipmaps;
                }

                VeldridTexture = factory.CreateTexture(new TextureDescription(
                    (uint)width, (uint)height, 1, mipLevels, 1,
                    PixelFormat.R8_G8_B8_A8_UNorm,
                    usage,
                    TextureType.Texture2D));

                device.UpdateTexture(VeldridTexture, _pixelData,
                    0, 0, 0,
                    (uint)width, (uint)height, 1,
                    0, 0);

                if (generateMipmaps && mipLevels > 1)
                {
                    using var cl = factory.CreateCommandList();
                    cl.Begin();
                    cl.GenerateMipmaps(VeldridTexture);
                    cl.End();
                    device.SubmitCommands(cl);
                    device.WaitForIdle();
                    Debug.Log($"[Texture2D] GenerateMipmaps: {width}x{height}, {mipLevels} mips");
                }

                TextureView = factory.CreateTextureView(VeldridTexture);
                _isDirty = false;
                _hasMipmaps = generateMipmaps && mipLevels > 1;
            }
        }

        /// <summary>
        /// Computes the average color of the texture (downsampled).
        /// Useful for IBL ambient approximation from environment maps.
        /// </summary>
        public Color GetAverageColor()
        {
            if (_hdrPixelData != null && _hdrPixelData.Length >= 4)
            {
                int pixelCount = _hdrPixelData.Length / 4;
                int step = Math.Max(1, pixelCount / 1024);
                double r = 0, g = 0, b = 0;
                int samples = 0;

                for (int i = 0; i < pixelCount; i += step)
                {
                    int offset = i * 4;
                    r += _hdrPixelData[offset];
                    g += _hdrPixelData[offset + 1];
                    b += _hdrPixelData[offset + 2];
                    samples++;
                }

                return new Color((float)(r / samples), (float)(g / samples), (float)(b / samples), 1f);
            }

            if (_pixelData == null || _pixelData.Length < 4)
                return Color.gray;

            int pxCount = _pixelData.Length / 4;
            int pxStep = Math.Max(1, pxCount / 1024);
            double lr = 0, lg = 0, lb = 0;
            int ldrSamples = 0;

            for (int i = 0; i < pxCount; i += pxStep)
            {
                int offset = i * 4;
                lr += _pixelData[offset] / 255.0;
                lg += _pixelData[offset + 1] / 255.0;
                lb += _pixelData[offset + 2] / 255.0;
                ldrSamples++;
            }

            return new Color((float)(lr / ldrSamples), (float)(lg / ldrSamples), (float)(lb / ldrSamples), 1f);
        }

        public void DebugSaveToPng(string path)
        {
            byte[]? rgba = _pixelData;

            // HDR → LDR tonemap for debug visualization
            if (rgba == null && _hdrPixelData != null)
            {
                rgba = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++)
                {
                    int si = i * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        float v = _hdrPixelData[si + c];
                        v = v / (1f + v); // Reinhard tonemap
                        rgba[si + c] = (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);
                    }
                    rgba[si + 3] = (byte)Math.Clamp(_hdrPixelData[si + 3] * 255f + 0.5f, 0, 255);
                }
            }

            if (rgba == null && _mipData != null && _mipData.Length > 0)
            {
                // Decode BC-compressed mip0 back to RGBA
                var bcFormat = _gpuFormat switch
                {
                    PixelFormat.BC7_UNorm => CompressionFormat.Bc7,
                    PixelFormat.BC5_UNorm => CompressionFormat.Bc5,
                    _ => CompressionFormat.Bc7,
                };
                var decoder = new BcDecoder();
                var decoded = decoder.DecodeRaw(_mipData[0], width, height, bcFormat);
                rgba = new byte[width * height * 4];
                for (int i = 0; i < decoded.Length; i++)
                {
                    rgba[i * 4 + 0] = decoded[i].r;
                    rgba[i * 4 + 1] = decoded[i].g;
                    rgba[i * 4 + 2] = decoded[i].b;
                    rgba[i * 4 + 3] = decoded[i].a;
                }
            }

            if (rgba == null || rgba.Length < 4) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = new Image<Rgba32>(width, height);
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < width; x++)
                    {
                        int offset = (y * width + x) * 4;
                        row[x] = new Rgba32(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
                    }
                }
            });
            image.SaveAsPng(path);
            Debug.Log($"[Texture2D] Debug saved: {path} ({width}x{height}, {_gpuFormat})");
        }

        public void Dispose()
        {
            TextureView?.Dispose();
            VeldridTexture?.Dispose();
            TextureView = null;
            VeldridTexture = null;
            _pixelData = null;
            _hdrPixelData = null;
        }

        internal static byte[] ConvertFloatToHalfBytes(float[] src)
        {
            var halves = new System.Half[src.Length];
            for (int i = 0; i < src.Length; i++)
                halves[i] = (System.Half)src[i];
            return MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
        }

        internal static float[] ConvertHalfBytesToFloat(byte[] src)
        {
            var halves = MemoryMarshal.Cast<byte, System.Half>(src.AsSpan());
            var result = new float[halves.Length];
            for (int i = 0; i < halves.Length; i++)
                result[i] = (float)halves[i];
            return result;
        }
    }
}
