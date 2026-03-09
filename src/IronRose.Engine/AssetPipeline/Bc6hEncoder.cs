using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// CPU-based BC6H unsigned float encoder/decoder.
    /// Uses Mode 10 (one subset, 10-bit direct endpoints) for encoding.
    /// Produces valid BC6H_UFloat blocks compatible with GPU hardware decoders.
    /// </summary>
    internal static class Bc6hEncoder
    {
        // BC6H interpolation weights for 4-bit indices (16 entries)
        private static readonly int[] Weights4 =
            { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        // Precomputed: decoded float value for each 10-bit quantized value (unsigned mode)
        private static readonly float[] Lut10 = BuildLut10();

        private static float[] BuildLut10()
        {
            var lut = new float[1024];
            for (int i = 0; i < 1024; i++)
            {
                int uq = Unquantize10(i);
                int fin = (uq * 31) >> 6;
                ushort halfBits = (ushort)Math.Clamp(fin, 0, 0x7BFF);
                lut[i] = (float)BitConverter.UInt16BitsToHalf(halfBits);
            }
            return lut;
        }

        private static int Unquantize10(int comp)
        {
            if (comp == 0) return 0;
            if (comp == 1023) return 0xFFFF;
            return ((comp << 15) + 0x4000) >> 9;
        }

        /// <summary>
        /// Encode float32 RGBA HDR data to BC6H unsigned blocks.
        /// Returns raw BC6H block data (16 bytes per 4x4 block).
        /// </summary>
        public static byte[] Encode(float[] hdrData, int width, int height)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var output = new byte[blocksX * blocksY * 16];

            Span<float> blockR = stackalloc float[16];
            Span<float> blockG = stackalloc float[16];
            Span<float> blockB = stackalloc float[16];

            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                blockR.Clear(); blockG.Clear(); blockB.Clear();

                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    int y = by * 4 + py;
                    int idx = py * 4 + px;
                    if (x < width && y < height)
                    {
                        int srcIdx = (y * width + x) * 4;
                        blockR[idx] = Math.Clamp(hdrData[srcIdx], 0f, 65504f);
                        blockG[idx] = Math.Clamp(hdrData[srcIdx + 1], 0f, 65504f);
                        blockB[idx] = Math.Clamp(hdrData[srcIdx + 2], 0f, 65504f);
                    }
                }

                EncodeBlock(blockR, blockG, blockB,
                    output.AsSpan((by * blocksX + bx) * 16, 16));
            }

            return output;
        }

        /// <summary>
        /// Decode BC6H unsigned blocks to float32 RGBA data.
        /// Only decodes Mode 10 blocks (other modes output black).
        /// </summary>
        public static float[] Decode(byte[] bc6hData, int width, int height)
        {
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            var output = new float[width * height * 4];
            Span<int> indices = stackalloc int[16];

            for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                int blockOffset = (by * blocksX + bx) * 16;
                ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(bc6hData.AsSpan(blockOffset));
                ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(bc6hData.AsSpan(blockOffset + 8));

                int bit = 0;
                int mode = (int)ReadBits(lo, hi, ref bit, 5);
                if (mode != 0b00011) // Only Mode 10 supported
                {
                    FillBlackBlock(output, bx, by, width, height);
                    continue;
                }

                int rw = (int)ReadBits(lo, hi, ref bit, 10);
                int gw = (int)ReadBits(lo, hi, ref bit, 10);
                int bw = (int)ReadBits(lo, hi, ref bit, 10);
                int rx = (int)ReadBits(lo, hi, ref bit, 10);
                int gx = (int)ReadBits(lo, hi, ref bit, 10);
                int bx2 = (int)ReadBits(lo, hi, ref bit, 10);

                // Decode indices
                indices[0] = (int)ReadBits(lo, hi, ref bit, 3); // anchor: 3 bits
                for (int i = 1; i < 16; i++)
                    indices[i] = (int)ReadBits(lo, hi, ref bit, 4);

                // Unquantize endpoints
                int e0r = Unquantize10(rw), e0g = Unquantize10(gw), e0b = Unquantize10(bw);
                int e1r = Unquantize10(rx), e1g = Unquantize10(gx), e1b = Unquantize10(bx2);

                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    int y = by * 4 + py;
                    if (x >= width || y >= height) continue;

                    int idx = py * 4 + px;
                    int w = Weights4[indices[idx]];

                    int cr = (e0r * (64 - w) + e1r * w + 32) >> 6;
                    int cg = (e0g * (64 - w) + e1g * w + 32) >> 6;
                    int cb = (e0b * (64 - w) + e1b * w + 32) >> 6;

                    int pixelOffset = (y * width + x) * 4;
                    output[pixelOffset]     = HalfBitsToFloat((ushort)Math.Clamp((cr * 31) >> 6, 0, 0x7BFF));
                    output[pixelOffset + 1] = HalfBitsToFloat((ushort)Math.Clamp((cg * 31) >> 6, 0, 0x7BFF));
                    output[pixelOffset + 2] = HalfBitsToFloat((ushort)Math.Clamp((cb * 31) >> 6, 0, 0x7BFF));
                    output[pixelOffset + 3] = 1.0f;
                }
            }

            return output;
        }

        // ─── Block Encoder ──────────────────────────────────────

        private static void EncodeBlock(Span<float> r, Span<float> g, Span<float> b, Span<byte> output)
        {
            // Find min/max per channel
            float rMin = r[0], rMax = r[0];
            float gMin = g[0], gMax = g[0];
            float bMin = b[0], bMax = b[0];

            for (int i = 1; i < 16; i++)
            {
                if (r[i] < rMin) rMin = r[i]; if (r[i] > rMax) rMax = r[i];
                if (g[i] < gMin) gMin = g[i]; if (g[i] > gMax) gMax = g[i];
                if (b[i] < bMin) bMin = b[i]; if (b[i] > bMax) bMax = b[i];
            }

            // Quantize endpoints to 10 bits
            int rw = QuantizeFloat(rMin), rx = QuantizeFloat(rMax);
            int gw = QuantizeFloat(gMin), gx = QuantizeFloat(gMax);
            int bw = QuantizeFloat(bMin), bx = QuantizeFloat(bMax);

            // Precompute interpolation palette (16 entries per channel)
            Span<float> rPal = stackalloc float[16];
            Span<float> gPal = stackalloc float[16];
            Span<float> bPal = stackalloc float[16];
            for (int i = 0; i < 16; i++)
            {
                rPal[i] = InterpolateChannel(rw, rx, i);
                gPal[i] = InterpolateChannel(gw, gx, i);
                bPal[i] = InterpolateChannel(bw, bx, i);
            }

            // For each texel, find best index
            Span<int> indices = stackalloc int[16];
            for (int i = 0; i < 16; i++)
            {
                float bestError = float.MaxValue;
                int bestIdx = 0;
                for (int j = 0; j < 16; j++)
                {
                    float dr = rPal[j] - r[i];
                    float dg = gPal[j] - g[i];
                    float db = bPal[j] - b[i];
                    float err = dr * dr + dg * dg + db * db;
                    if (err < bestError) { bestError = err; bestIdx = j; }
                }
                indices[i] = bestIdx;
            }

            // Anchor fix-up: if anchor index (texel 0) MSB=1, swap endpoints & invert indices
            if (indices[0] >= 8)
            {
                (rw, rx) = (rx, rw);
                (gw, gx) = (gx, gw);
                (bw, bx) = (bx, bw);
                for (int i = 0; i < 16; i++)
                    indices[i] = 15 - indices[i];
            }

            // Pack into 128-bit block (Mode 10)
            ulong lo = 0, hi = 0;
            int bit = 0;

            WriteBits(ref lo, ref hi, ref bit, 0b00011, 5); // Mode 10
            WriteBits(ref lo, ref hi, ref bit, (ulong)rw, 10);
            WriteBits(ref lo, ref hi, ref bit, (ulong)gw, 10);
            WriteBits(ref lo, ref hi, ref bit, (ulong)bw, 10);
            WriteBits(ref lo, ref hi, ref bit, (ulong)rx, 10);
            WriteBits(ref lo, ref hi, ref bit, (ulong)gx, 10);
            WriteBits(ref lo, ref hi, ref bit, (ulong)bx, 10);

            WriteBits(ref lo, ref hi, ref bit, (ulong)(indices[0] & 0x7), 3); // anchor: 3 bits
            for (int i = 1; i < 16; i++)
                WriteBits(ref lo, ref hi, ref bit, (ulong)indices[i], 4);

            BinaryPrimitives.WriteUInt64LittleEndian(output, lo);
            BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(8), hi);
        }

        // ─── Quantization ───────────────────────────────────────

        /// <summary>Find the 10-bit quantized value whose decoded float is closest to f.</summary>
        private static int QuantizeFloat(float f)
        {
            if (f <= 0) return 0;
            if (f >= Lut10[1023]) return 1023;

            // Binary search in monotonic LUT
            int lo = 0, hi = 1023;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (Lut10[mid] <= f) lo = mid; else hi = mid;
            }
            return Math.Abs(Lut10[lo] - f) <= Math.Abs(Lut10[hi] - f) ? lo : hi;
        }

        /// <summary>Decode a single channel value given quantized endpoints and weight index.</summary>
        private static float InterpolateChannel(int e0q, int e1q, int weightIdx)
        {
            int e0 = Unquantize10(e0q);
            int e1 = Unquantize10(e1q);
            int w = Weights4[weightIdx];
            int interp = (e0 * (64 - w) + e1 * w + 32) >> 6;
            int fin = (interp * 31) >> 6;
            return HalfBitsToFloat((ushort)Math.Clamp(fin, 0, 0x7BFF));
        }

        // ─── Bit I/O ────────────────────────────────────────────

        private static void WriteBits(ref ulong lo, ref ulong hi, ref int bitPos, ulong value, int count)
        {
            ulong mask = (count < 64) ? ((1UL << count) - 1) : ~0UL;
            value &= mask;

            if (bitPos < 64)
            {
                lo |= value << bitPos;
                if (bitPos + count > 64)
                    hi |= value >> (64 - bitPos);
            }
            else
            {
                hi |= value << (bitPos - 64);
            }
            bitPos += count;
        }

        private static ulong ReadBits(ulong lo, ulong hi, ref int bitPos, int count)
        {
            ulong mask = (count < 64) ? ((1UL << count) - 1) : ~0UL;
            ulong value;

            if (bitPos < 64)
            {
                value = lo >> bitPos;
                if (bitPos + count > 64)
                    value |= hi << (64 - bitPos);
            }
            else
            {
                value = hi >> (bitPos - 64);
            }

            bitPos += count;
            return value & mask;
        }

        // ─── Helpers ─────────────────────────────────────────────

        private static float HalfBitsToFloat(ushort bits)
        {
            return (float)BitConverter.UInt16BitsToHalf(bits);
        }

        private static void FillBlackBlock(float[] output, int bx, int by, int width, int height)
        {
            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px;
                int y = by * 4 + py;
                if (x >= width || y >= height) continue;
                int offset = (y * width + x) * 4;
                output[offset] = 0f;
                output[offset + 1] = 0f;
                output[offset + 2] = 0f;
                output[offset + 3] = 1f;
            }
        }
    }
}
