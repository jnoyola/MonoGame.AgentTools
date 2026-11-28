using System.Numerics;
using System.Runtime.CompilerServices;

namespace Synthapixel.MonoGame.AgentTools.Imaging;

/// <summary>The two checksums a PNG needs: zlib's Adler-32 and the chunks' CRC-32.</summary>
internal static class Checksums
{
    private const uint AdlerModulus = 65521;

    /// <summary>The most bytes the scalar Adler-32 sums can take before they could overflow 32 bits.</summary>
    private const int AdlerChunk = 5552;

    // Weights of each byte in a vector block for Adler-32's second sum (width, width - 1, ..., 1), split
    // into the four uint vectors the bytes widen into.
    private static readonly Vector<uint>[] AdlerWeights = CreateAdlerWeights();

    // Slice-by-8 tables: table k advances the CRC over a byte followed by k zero bytes.
    private static readonly uint[] CrcTables = CreateCrcTables();

    /// <summary>
    /// Adler-32 (RFC 1950), a vector block at a time where SIMD is available, as zlib-ng and Chromium do:
    /// per block, the first sum adds the bytes and the second adds the first sum before the block times the
    /// block size plus the bytes weighted by their distance from the block's end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var width = Vector<byte>.Count;
            var blocksPerChunk = AdlerChunk / width;
            var (w0, w1, w2, w3) = (AdlerWeights[0], AdlerWeights[1], AdlerWeights[2], AdlerWeights[3]);
            while (data.Length - i >= width)
            {
                var blocks = Math.Min(blocksPerChunk, (data.Length - i) / width);
                Vector<uint> sum = default, weighted = default, prefix = default;
                for (int k = 0; k < blocks; ++k, i += width)
                {
                    Vector.Widen(new Vector<byte>(data[i..]), out var low, out var high);
                    Vector.Widen(low, out var p0, out var p1);
                    Vector.Widen(high, out var p2, out var p3);
                    prefix += sum;
                    sum += p0 + p1 + p2 + p3;
                    weighted += p0 * w0 + p1 * w1 + p2 * w2 + p3 * w3;
                }
                // Lane sums stay well inside 32 bits for a chunk of 5552 bytes; combine in 64.
                b = (uint)((b + (ulong)blocks * (ulong)width * a + (ulong)width * Vector.Sum(prefix) + Vector.Sum(weighted)) % AdlerModulus);
                a = (uint)((a + (ulong)Vector.Sum(sum)) % AdlerModulus);
            }
        }
        while (i < data.Length)
        {
            var end = Math.Min(data.Length, i + AdlerChunk);
            for (; i < end; ++i)
            {
                a += data[i];
                b += a;
            }
            a %= AdlerModulus;
            b %= AdlerModulus;
        }
        return b << 16 | a;
    }

    /// <summary>
    /// CRC-32 (ISO 3309, as PNG uses), eight bytes per step with slice-by-8 tables, as zlib does without
    /// carry-less multiply. System.IO.Hashing's accelerated Crc32 would be faster still, but is a package
    /// that would ship in every game's output.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var t = CrcTables;
        var crc = 0xFFFFFFFFu;
        var i = 0;
        for (; data.Length - i >= 8; i += 8)
        {
            var low = crc ^ (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
            var high = (uint)(data[i + 4] | data[i + 5] << 8 | data[i + 6] << 16 | data[i + 7] << 24);
            crc = t[7 * 256 + (low & 0xFF)] ^ t[6 * 256 + (low >> 8 & 0xFF)] ^ t[5 * 256 + (low >> 16 & 0xFF)] ^ t[4 * 256 + (low >> 24)]
                ^ t[3 * 256 + (high & 0xFF)] ^ t[2 * 256 + (high >> 8 & 0xFF)] ^ t[256 + (high >> 16 & 0xFF)] ^ t[high >> 24];
        }
        for (; i < data.Length; ++i)
            crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static Vector<uint>[] CreateAdlerWeights()
    {
        var width = Vector<byte>.Count;
        var quarter = width / 4;
        var weights = new Vector<uint>[4];
        var lane = new uint[quarter];
        for (int q = 0; q < 4; ++q)
        {
            for (int j = 0; j < quarter; ++j)
                lane[j] = (uint)(width - (q * quarter + j));
            weights[q] = new Vector<uint>(lane);
        }
        return weights;
    }

    private static uint[] CreateCrcTables()
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; ++n)
        {
            var c = n;
            for (int k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        for (int n = 0; n < 256; ++n)
            for (int k = 1; k < 8; ++k)
                t[k * 256 + n] = t[(k - 1) * 256 + n] >> 8 ^ t[t[(k - 1) * 256 + n] & 0xFF];
        return t;
    }
}
