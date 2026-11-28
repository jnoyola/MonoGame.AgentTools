using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Synthapixel.MonoGame.AgentTools.Imaging;

/// <summary>
/// zlib (RFC 1950) encoder that writes one deflate (RFC 1951) block with the fixed Huffman codes and a
/// single-probe hash match finder, the approach of stb_image_write's encoder. It allocates nothing per
/// call, which <see cref="System.IO.Compression.ZLibStream"/> can't (a new stream per image).
/// </summary>
/// <remarks>
/// Replace with System.IO.Compression.ZLibEncoder (.NET 11, reusable via <c>Reset</c>) once the package's
/// minimum framework includes it.
/// </remarks>
internal sealed class ZLibFastEncoder
{
    private const int HashBits = 15;
    private const int WindowSize = 32768;
    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int MaxIndexedMatch = 8;

    // Arrays, not ReadOnlySpan properties: only single-byte element spans are free static data.
    private static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static ReadOnlySpan<byte> LengthExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static ReadOnlySpan<byte> DistanceExtra => [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    // Fixed Huffman codes, bit-reversed because deflate packs Huffman codes most significant bit first.
    private static readonly ushort[] LiteralCode = new ushort[288];
    private static readonly byte[] LiteralBits = new byte[288];
    private static readonly byte[] LengthIndex = new byte[MaxMatch + 1];
    private static readonly byte[] DistanceIndex = new byte[WindowSize + 1];

    private readonly int[] _head = new int[1 << HashBits];

    static ZLibFastEncoder()
    {
        for (int v = 0; v < 288; ++v)
        {
            var (code, bits) = v switch
            {
                < 144 => (0x30 + v, 8),
                < 256 => (0x190 + v - 144, 9),
                < 280 => (v - 256, 7),
                _ => (0xC0 + v - 280, 8),
            };
            LiteralCode[v] = (ushort)Reverse(code, bits);
            LiteralBits[v] = (byte)bits;
        }
        for (int i = 0; i < 28; ++i)
            for (int length = LengthBase[i]; length < LengthBase[i] + (1 << LengthExtra[i]) && length < MaxMatch; ++length)
                LengthIndex[length] = (byte)i;
        LengthIndex[MaxMatch] = 28;
        for (int i = 0; i < 30; ++i)
            for (int d = DistanceBase[i]; d < DistanceBase[i] + (1 << DistanceExtra[i]) && d <= WindowSize; ++d)
                DistanceIndex[d] = (byte)i;
    }

    /// <summary>Upper bound on the output size: fixed codes spend at most 9 bits per input byte.</summary>
    public static int MaxCompressedLength(int inputLength) => inputLength + inputLength / 8 + 16;

    /// <summary>Compresses <paramref name="input"/> into <paramref name="output"/> and returns the bytes written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Compress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < MaxCompressedLength(input.Length))
            throw new ArgumentException("Output is smaller than MaxCompressedLength.", nameof(output));

        output[0] = 0x78;   // deflate, 32 KB window
        output[1] = 0x01;   // fastest; (0x78 * 256 + 0x01) % 31 == 0
        var bits = new BitWriter(output, 2);
        bits.Write(0b011, 3);   // final block, fixed Huffman codes

        Array.Fill(_head, -WindowSize - 1);
        var n = input.Length;
        var pos = 0;
        while (pos < n)
        {
            var length = 0;
            var distance = 0;
            if (pos + MinMatch <= n)
            {
                var h = Hash(input, pos);
                var candidate = _head[h];
                _head[h] = pos;
                if (pos - candidate <= WindowSize)
                {
                    var max = Math.Min(MaxMatch, n - pos);
                    length = input.Slice(candidate, max).CommonPrefixLength(input.Slice(pos, max));
                    distance = pos - candidate;
                }
            }

            if (length >= MinMatch)
            {
                var li = LengthIndex[length];
                bits.Write(LiteralCode[257 + li], LiteralBits[257 + li]);
                bits.Write((uint)(length - LengthBase[li]), LengthExtra[li]);
                var di = DistanceIndex[distance];
                bits.Write((uint)Reverse(di, 5), 5);
                bits.Write((uint)(distance - DistanceBase[di]), DistanceExtra[di]);

                // Like zlib's fastest level, index the positions inside short matches only: long matches are
                // runs, and indexing every byte of them costs more time than it saves space.
                var end = pos + length;
                if (length <= MaxIndexedMatch)
                    for (int p = pos + 1; p < end && p + MinMatch <= n; ++p)
                        _head[Hash(input, p)] = p;
                pos = end;
            }
            else
            {
                var literal = input[pos++];
                bits.Write(LiteralCode[literal], LiteralBits[literal]);
            }
        }
        bits.Write(LiteralCode[256], LiteralBits[256]);   // end of block

        var written = bits.Finish();
        BinaryPrimitives.WriteUInt32BigEndian(output[written..], Checksums.Adler32(input));
        return written + 4;
    }

    private static uint Hash(ReadOnlySpan<byte> data, int pos) =>
        ((uint)(data[pos] | data[pos + 1] << 8 | data[pos + 2] << 16) * 0x9E3779B1u) >> (32 - HashBits);

    private static int Reverse(int code, int bits)
    {
        var reversed = 0;
        for (int i = 0; i < bits; ++i, code >>= 1)
            reversed = reversed << 1 | (code & 1);
        return reversed;
    }

    /// <summary>Packs bits least significant first, as deflate requires.</summary>
    private ref struct BitWriter(Span<byte> output, int position)
    {
        private readonly Span<byte> _output = output;
        private int _position = position;
        private ulong _buffer;
        private int _count;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Write(uint value, int bits)
        {
            _buffer |= (ulong)value << _count;
            _count += bits;
            if (_count >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_output[_position..], (uint)_buffer);
                _position += 4;
                _buffer >>= 32;
                _count -= 32;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int Finish()
        {
            for (; _count > 0; _count -= 8, _buffer >>= 8)
                _output[_position++] = (byte)_buffer;
            return _position;
        }
    }
}
