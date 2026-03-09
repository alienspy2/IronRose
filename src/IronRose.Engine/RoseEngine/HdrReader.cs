using System;
using System.IO;
using System.Text;

namespace RoseEngine
{
    /// <summary>
    /// Minimal Radiance HDR (.hdr / .pic) reader.
    /// Supports: RGBE and XYZE format, new-style adaptive RLE and uncompressed scanlines.
    /// </summary>
    internal static class HdrReader
    {
        /// <returns>(width, height, float[] RGBA data)</returns>
        public static (int w, int h, float[] data) Read(string path)
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }

        public static (int w, int h, float[] data) Read(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            // ── Header ──
            ReadHeader(reader);

            // ── Resolution string: e.g. "-Y 1024 +X 2048"
            var resLine = ReadLine(reader);
            if (!TryParseResolution(resLine, out int width, out int height))
                throw new InvalidDataException($"Invalid HDR resolution line: {resLine}");

            // ── Pixel data (RGBE) ──
            var rgbe = new byte[width * height * 4];
            ReadPixels(reader, rgbe, width, height);

            // ── Convert RGBE → linear float RGBA ──
            var data = new float[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int s = i * 4;
                byte r = rgbe[s + 0];
                byte g = rgbe[s + 1];
                byte b = rgbe[s + 2];
                byte e = rgbe[s + 3];

                if (e == 0)
                {
                    // data is already zeroed
                }
                else
                {
                    // ldexp(1.0, e - (128 + 8)) = 2^(e-136)
                    float scale = MathF.Pow(2f, e - (128 + 8));
                    data[s + 0] = (r + 0.5f) * scale;
                    data[s + 1] = (g + 0.5f) * scale;
                    data[s + 2] = (b + 0.5f) * scale;
                }
                data[s + 3] = 1f;
            }

            return (width, height, data);
        }

        private static void ReadHeader(BinaryReader reader)
        {
            // First line must be #?RADIANCE or #?RGBE (or similar magic)
            var magic = ReadLine(reader);
            if (!magic.StartsWith("#?"))
                throw new InvalidDataException("Not a Radiance HDR file (missing #? magic).");

            // Read header key=value lines until empty line
            while (true)
            {
                var line = ReadLine(reader);
                if (string.IsNullOrEmpty(line))
                    break;
                // We could parse FORMAT= here, but we only support RGBE/XYZE anyway
            }
        }

        private static bool TryParseResolution(string line, out int width, out int height)
        {
            width = height = 0;
            // Standard format: "-Y <height> +X <width>"
            // Also handle "+Y ... +X ...", "-Y ... -X ...", etc.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;

            // parts[0] = "-Y" or "+Y", parts[1] = height
            // parts[2] = "+X" or "-X", parts[3] = width
            if ((parts[0] == "-Y" || parts[0] == "+Y") &&
                (parts[2] == "+X" || parts[2] == "-X"))
            {
                return int.TryParse(parts[1], out height) &&
                       int.TryParse(parts[3], out width);
            }

            // Transposed: "+X <width> -Y <height>"
            if ((parts[0] == "+X" || parts[0] == "-X") &&
                (parts[2] == "-Y" || parts[2] == "+Y"))
            {
                return int.TryParse(parts[1], out width) &&
                       int.TryParse(parts[3], out height);
            }

            return false;
        }

        private static void ReadPixels(BinaryReader reader, byte[] rgbe, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width * 4;

                if (width < 8 || width > 0x7fff)
                {
                    // Old format / uncompressed: just read raw RGBE
                    ReadFlatScanline(reader, rgbe, rowOffset, width);
                    continue;
                }

                // Peek at first 4 bytes to determine encoding
                byte b0 = reader.ReadByte();
                byte b1 = reader.ReadByte();
                byte b2 = reader.ReadByte();
                byte b3 = reader.ReadByte();

                if (b0 == 2 && b1 == 2)
                {
                    // New-style adaptive RLE
                    int encodedWidth = (b2 << 8) | b3;
                    if (encodedWidth != width)
                        throw new InvalidDataException("HDR scanline width mismatch.");

                    ReadRleScanline(reader, rgbe, rowOffset, width);
                }
                else
                {
                    // Old format: first 4 bytes are RGBE of first pixel
                    rgbe[rowOffset + 0] = b0;
                    rgbe[rowOffset + 1] = b1;
                    rgbe[rowOffset + 2] = b2;
                    rgbe[rowOffset + 3] = b3;
                    ReadFlatScanline(reader, rgbe, rowOffset + 4, width - 1);
                }
            }
        }

        private static void ReadFlatScanline(BinaryReader reader, byte[] rgbe, int offset, int pixelCount)
        {
            var bytes = reader.ReadBytes(pixelCount * 4);
            Buffer.BlockCopy(bytes, 0, rgbe, offset, bytes.Length);
        }

        /// <summary>
        /// New-style adaptive RLE: 4 separate channel planes, each run-length encoded.
        /// </summary>
        private static void ReadRleScanline(BinaryReader reader, byte[] rgbe, int rowOffset, int width)
        {
            // Decode each channel (R, G, B, E) separately
            for (int ch = 0; ch < 4; ch++)
            {
                int ptr = 0;
                while (ptr < width)
                {
                    byte code = reader.ReadByte();
                    if (code > 128)
                    {
                        // RLE run: repeat next byte (code - 128) times
                        int count = code - 128;
                        byte val = reader.ReadByte();
                        for (int i = 0; i < count && ptr < width; i++, ptr++)
                            rgbe[rowOffset + ptr * 4 + ch] = val;
                    }
                    else
                    {
                        // Literal run: read 'code' bytes
                        int count = code;
                        for (int i = 0; i < count && ptr < width; i++, ptr++)
                            rgbe[rowOffset + ptr * 4 + ch] = reader.ReadByte();
                    }
                }
            }
        }

        private static string ReadLine(BinaryReader reader)
        {
            var sb = new StringBuilder(128);
            while (true)
            {
                int b = reader.BaseStream.ReadByte();
                if (b < 0 || b == '\n') break;
                if (b == '\r') continue;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
