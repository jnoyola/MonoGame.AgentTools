using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Microsoft.Xna.Framework;

namespace Synthapixel.MonoGame.AgentTools.Imaging;

/// <summary>
/// Minimal PNG writer for 8-bit RGB (alpha is dropped: a backbuffer's alpha isn't meaningful). Needs no
/// graphics device, so frames can be written without round-tripping through a texture.
/// </summary>
/// <remarks>
/// An instance keeps its buffers (filtered rows and the whole file) between images, compresses with
/// <see cref="ZLibFastEncoder"/> and writes with <see cref="NativeFile"/>, so saving a stream of frames
/// allocates nothing once the buffers have grown to the frame size. One-off images can use the static
/// <see cref="Write"/>.
/// </remarks>
internal sealed class PngEncoder
{
    private const int HeaderLength = 8 + 25 + 8;   // signature, IHDR chunk, IDAT length and type
    private const int TrailerLength = 4 + 12;      // IDAT CRC, IEND chunk

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> End => [0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];

    private readonly ZLibFastEncoder _zlib = new();
    private byte[] _filtered = [];
    private byte[] _file = [];

    /// <summary>Writes the image to <paramref name="path"/>, best prepared with <see cref="NativeFile.Prepare"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Save(string path, int width, int height, ReadOnlySpan<Color> pixels) =>
        NativeFile.Write(path, Encode(width, height, pixels));

    public static void Write(Stream output, int width, int height, ReadOnlySpan<Color> pixels) =>
        output.Write(new PngEncoder().Encode(width, height, pixels));

    /// <summary>The encoded file, in a buffer reused by the next call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ReadOnlySpan<byte> Encode(int width, int height, ReadOnlySpan<Color> pixels)
    {
        if (pixels.Length < width * height)
            throw new ArgumentException("Not enough pixels for the image size.", nameof(pixels));

        var filtered = Filter(width, height, pixels);
        var capacity = HeaderLength + ZLibFastEncoder.MaxCompressedLength(filtered.Length) + TrailerLength;
        if (_file.Length < capacity)
            _file = new byte[capacity];
        var file = _file.AsSpan();

        Signature.CopyTo(file);
        var ihdr = file.Slice(8, 25);
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 13);
        "IHDR"u8.CopyTo(ihdr[4..]);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[8..], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[12..], height);
        ihdr[16] = 8;   // bit depth
        ihdr[17] = 2;   // color type: RGB
        ihdr[18] = 0;   // compression
        ihdr[19] = 0;   // filter method
        ihdr[20] = 0;   // no interlace
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[21..], Checksums.Crc32(ihdr.Slice(4, 17)));

        var length = _zlib.Compress(filtered, file[HeaderLength..]);
        BinaryPrimitives.WriteInt32BigEndian(file[33..], length);
        "IDAT"u8.CopyTo(file[37..]);
        var end = HeaderLength + length;
        BinaryPrimitives.WriteUInt32BigEndian(file[end..], Checksums.Crc32(file[37..end]));
        End.CopyTo(file[(end + 4)..]);
        return file[..(end + TrailerLength)];
    }

    /// <summary>
    /// Filter type 1 (Sub) on every row: each byte minus the same channel of the pixel to its left, so flat
    /// backgrounds and gradients become runs the match finder turns into almost nothing. Four pixels at a
    /// time: subtract the pixels one to the left, then drop alpha with a byte shuffle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ReadOnlySpan<byte> Filter(int width, int height, ReadOnlySpan<Color> pixels)
    {
        var stride = 1 + width * 3;
        if (_filtered.Length != stride * height)
            _filtered = new byte[stride * height];
        var output = _filtered.AsSpan();
        var dropAlpha = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0xFF, 0xFF, 0xFF, 0xFF);
        var vector = Vector128.IsHardwareAccelerated;
        for (int y = 0; y < height; ++y)
        {
            var row = output.Slice(y * stride, stride);
            var source = MemoryMarshal.AsBytes(pixels.Slice(y * width, width));
            row[0] = 1;
            row[1] = source[0];
            row[2] = source[1];
            row[3] = source[2];
            var x = 1;
            // Each step stores 16 bytes for 12, so stop while the spare 4 still land inside the row.
            if (vector)
            {
                for (; x + 6 <= width; x += 4)
                {
                    var current = Vector128.Create(source.Slice(x * 4, 16));
                    var left = Vector128.Create(source.Slice(x * 4 - 4, 16));
                    Vector128.Shuffle(current - left, dropAlpha).CopyTo(row[(1 + x * 3)..]);
                }
            }
            for (; x < width; ++x)
            {
                var o = 1 + x * 3;
                var s = x * 4;
                row[o] = (byte)(source[s] - source[s - 4]);
                row[o + 1] = (byte)(source[s + 1] - source[s - 3]);
                row[o + 2] = (byte)(source[s + 2] - source[s - 2]);
            }
        }
        return output;
    }
}
