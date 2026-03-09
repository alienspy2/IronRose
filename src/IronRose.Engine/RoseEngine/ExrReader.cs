using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RoseEngine
{
    /// <summary>
    /// Minimal OpenEXR scanline reader.
    /// Supports: single-part, scanline order, HALF/FLOAT channels, NO/ZIP/ZIPS compression.
    /// </summary>
    internal static class ExrReader
    {
        private const uint Magic = 20000630; // 0x01312F76
        private const int COMPRESSION_NONE = 0;
        private const int COMPRESSION_ZIPS = 2; // ZIP single scanline
        private const int COMPRESSION_ZIP = 3;  // ZIP multi scanline (16 lines)

        private const int PIXEL_TYPE_UINT = 0;
        private const int PIXEL_TYPE_HALF = 1;
        private const int PIXEL_TYPE_FLOAT = 2;

        private struct ChannelInfo
        {
            public string Name;
            public int PixelType; // 1=HALF, 2=FLOAT
            public int BytesPerPixel;
        }

        public static (int width, int height, float[] rgbaData) Read(string path)
        {
            using var fs = File.OpenRead(path);
            return Read(fs);
        }

        public static (int width, int height, float[] rgbaData) Read(Stream stream)
        {
            using var reader = new BinaryReader(stream);

            // Magic number
            uint magic = reader.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException($"Not a valid EXR file (magic: 0x{magic:X8})");

            // Version field
            int version = reader.ReadInt32();
            bool isTiled = (version & 0x200) != 0;
            if (isTiled)
                throw new NotSupportedException("Tiled EXR files are not supported");

            // Parse header attributes
            var channels = new List<ChannelInfo>();
            int compression = COMPRESSION_NONE;
            int dataXMin = 0, dataYMin = 0, dataXMax = 0, dataYMax = 0;

            while (true)
            {
                string attrName = ReadNullTerminatedString(reader);
                if (string.IsNullOrEmpty(attrName))
                    break; // end of header

                string attrType = ReadNullTerminatedString(reader);
                int attrSize = reader.ReadInt32();
                long attrStart = stream.Position;

                if (attrName == "channels" && attrType == "chlist")
                {
                    channels = ReadChannelList(reader, attrSize);
                }
                else if (attrName == "compression" && attrType == "compression")
                {
                    compression = reader.ReadByte();
                }
                else if (attrName == "dataWindow" && attrType == "box2i")
                {
                    dataXMin = reader.ReadInt32();
                    dataYMin = reader.ReadInt32();
                    dataXMax = reader.ReadInt32();
                    dataYMax = reader.ReadInt32();
                }

                // Skip to end of attribute data
                stream.Position = attrStart + attrSize;
            }

            if (channels.Count == 0)
                throw new InvalidDataException("EXR file has no channels");

            int width = dataXMax - dataXMin + 1;
            int height = dataYMax - dataYMin + 1;
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"Invalid EXR dimensions: {width}x{height}");

            // Validate compression
            if (compression != COMPRESSION_NONE && compression != COMPRESSION_ZIP && compression != COMPRESSION_ZIPS)
                throw new NotSupportedException($"EXR compression type {compression} is not supported (only NONE/ZIP/ZIPS)");

            // Calculate bytes per scanline (all channels interleaved)
            int bytesPerPixelRow = 0;
            foreach (var ch in channels)
                bytesPerPixelRow += ch.BytesPerPixel;
            int bytesPerScanline = bytesPerPixelRow * width;

            // Determine scanlines per chunk
            int scanlinesPerChunk = compression switch
            {
                COMPRESSION_ZIP => 16,
                COMPRESSION_ZIPS => 1,
                _ => 1, // NONE
            };

            // Read offset table
            int chunkCount = (height + scanlinesPerChunk - 1) / scanlinesPerChunk;
            var offsets = new long[chunkCount];
            for (int i = 0; i < chunkCount; i++)
                offsets[i] = reader.ReadInt64();

            // Allocate output RGBA float array
            var rgbaData = new float[width * height * 4];
            // Default alpha to 1.0 (if no A channel)
            for (int i = 3; i < rgbaData.Length; i += 4)
                rgbaData[i] = 1.0f;

            // Find channel indices for R, G, B, A
            int rIdx = -1, gIdx = -1, bIdx = -1, aIdx = -1;
            for (int i = 0; i < channels.Count; i++)
            {
                switch (channels[i].Name)
                {
                    case "R": rIdx = i; break;
                    case "G": gIdx = i; break;
                    case "B": bIdx = i; break;
                    case "A": aIdx = i; break;
                }
            }

            // Read chunks
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                stream.Position = offsets[chunk];
                int yStart = reader.ReadInt32(); // scanline Y coordinate
                int dataSize = reader.ReadInt32();

                int linesInChunk = Math.Min(scanlinesPerChunk, dataYMin + height - yStart);
                int uncompressedSize = bytesPerScanline * linesInChunk;

                byte[] pixelData;
                if (compression == COMPRESSION_NONE)
                {
                    pixelData = reader.ReadBytes(dataSize);
                }
                else
                {
                    // ZIP/ZIPS: deflate-compressed
                    var compressedBytes = reader.ReadBytes(dataSize);
                    pixelData = DecompressZip(compressedBytes, uncompressedSize);
                }

                // Parse scanlines within the chunk
                // EXR channel data is stored channel-by-channel within each scanline
                // (not interleaved pixel-by-pixel)
                for (int line = 0; line < linesInChunk; line++)
                {
                    int y = yStart - dataYMin + line;
                    if (y < 0 || y >= height) continue;

                    int lineOffset = line * bytesPerScanline;

                    // Channels are stored in alphabetical order, each channel's full scanline data contiguous
                    int channelByteOffset = 0;
                    for (int ci = 0; ci < channels.Count; ci++)
                    {
                        var ch = channels[ci];
                        int targetComponent = -1;
                        if (ci == rIdx) targetComponent = 0;
                        else if (ci == gIdx) targetComponent = 1;
                        else if (ci == bIdx) targetComponent = 2;
                        else if (ci == aIdx) targetComponent = 3;

                        for (int x = 0; x < width; x++)
                        {
                            int srcOffset = lineOffset + channelByteOffset + x * ch.BytesPerPixel;
                            float value;

                            if (ch.PixelType == PIXEL_TYPE_HALF)
                            {
                                var half = BinaryPrimitives.ReadHalfLittleEndian(
                                    pixelData.AsSpan(srcOffset, 2));
                                value = (float)half;
                            }
                            else if (ch.PixelType == PIXEL_TYPE_FLOAT)
                            {
                                value = BinaryPrimitives.ReadSingleLittleEndian(
                                    pixelData.AsSpan(srcOffset, 4));
                            }
                            else
                            {
                                // UINT — normalize to float
                                value = BinaryPrimitives.ReadUInt32LittleEndian(
                                    pixelData.AsSpan(srcOffset, 4));
                            }

                            if (targetComponent >= 0)
                            {
                                int dstIdx = (y * width + x) * 4 + targetComponent;
                                rgbaData[dstIdx] = value;
                            }
                        }

                        channelByteOffset += ch.BytesPerPixel * width;
                    }
                }
            }

            return (width, height, rgbaData);
        }

        private static List<ChannelInfo> ReadChannelList(BinaryReader reader, int attrSize)
        {
            var channels = new List<ChannelInfo>();
            long endPos = reader.BaseStream.Position + attrSize;

            while (reader.BaseStream.Position < endPos - 1)
            {
                string name = ReadNullTerminatedString(reader);
                if (string.IsNullOrEmpty(name))
                    break;

                int pixelType = reader.ReadInt32();
                reader.ReadByte(); // pLinear (unused)
                reader.ReadBytes(3); // reserved
                reader.ReadInt32(); // xSampling
                reader.ReadInt32(); // ySampling

                int bytesPerPixel = pixelType switch
                {
                    PIXEL_TYPE_HALF => 2,
                    PIXEL_TYPE_FLOAT => 4,
                    PIXEL_TYPE_UINT => 4,
                    _ => throw new NotSupportedException($"Unsupported EXR pixel type: {pixelType}")
                };

                channels.Add(new ChannelInfo
                {
                    Name = name,
                    PixelType = pixelType,
                    BytesPerPixel = bytesPerPixel,
                });
            }

            return channels;
        }

        private static byte[] DecompressZip(byte[] compressedData, int expectedSize)
        {
            // EXR ZIP uses raw deflate (no zlib header) — but some files use zlib (2-byte header).
            // Try raw deflate first, fall back to zlib-wrapped.
            byte[]? result = TryDeflate(compressedData, expectedSize, raw: true);
            result ??= TryDeflate(compressedData, expectedSize, raw: false);
            if (result == null)
                throw new InvalidDataException("Failed to decompress EXR ZIP data");

            // EXR ZIP uses a predictor: reconstruct the original data
            ReconstructPredictor(result);

            // EXR ZIP interleaves bytes: deinterleave
            Deinterleave(result);

            return result;
        }

        private static byte[]? TryDeflate(byte[] data, int expectedSize, bool raw)
        {
            try
            {
                using var input = new MemoryStream(data);
                if (!raw)
                {
                    // Skip 2-byte zlib header
                    if (data.Length < 2) return null;
                    input.Position = 2;
                }
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                var output = new byte[expectedSize];
                int totalRead = 0;
                while (totalRead < expectedSize)
                {
                    int read = deflate.Read(output, totalRead, expectedSize - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                return totalRead == expectedSize ? output : null;
            }
            catch
            {
                return null;
            }
        }

        private static void ReconstructPredictor(byte[] data)
        {
            // EXR uses a simple byte-level delta predictor
            for (int i = 1; i < data.Length; i++)
                data[i] = (byte)(data[i] + data[i - 1]);
        }

        private static void Deinterleave(byte[] data)
        {
            int len = data.Length;
            var tmp = new byte[len];
            int half = (len + 1) / 2;

            // EXR interleaves: even bytes first, then odd bytes
            int j = 0;
            for (int i = 0; i < half; i++)
            {
                tmp[j++] = data[i];
                if (half + i < len)
                    tmp[j++] = data[half + i];
            }

            Buffer.BlockCopy(tmp, 0, data, 0, len);
        }

        private static string ReadNullTerminatedString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            while (true)
            {
                byte b = reader.ReadByte();
                if (b == 0) break;
                bytes.Add(b);
            }
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
