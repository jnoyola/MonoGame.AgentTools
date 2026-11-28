using System.Buffers.Binary;
using System.IO.Compression;

namespace Synthapixel.MonoGame.AgentTools.Mcp.Imaging;

/// <summary>An 8-bit RGB image, row-major, 3 bytes per pixel.</summary>
internal sealed class RgbImage
{
    public RgbImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 3];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

/// <summary>
/// Minimal PNG reader and writer for the frames the library writes: 8-bit RGB or RGBA, not interlaced
/// (alpha is dropped). Avoids an imaging dependency for one file format we control.
/// </summary>
internal static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static RgbImage Load(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public static RgbImage Decode(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(Signature))
            throw new InvalidDataException("not a PNG file.");

        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();
        var position = Signature.Length;
        while (position + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data[position..]);
            var type = data.Slice(position + 4, 4);
            if (length < 0 || position + 12 + length > data.Length)
                throw new InvalidDataException("truncated chunk.");
            var chunk = data.Slice(position + 8, length);
            position += 12 + length;

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                var (bitDepth, colorType, interlace) = (chunk[8], chunk[9], chunk[12]);
                channels = colorType switch { 2 => 3, 6 => 4, _ => 0 };
                if (bitDepth != 8 || channels == 0 || interlace != 0)
                    throw new InvalidDataException($"unsupported PNG (bit depth {bitDepth}, color type {colorType}, interlace {interlace}); expected 8-bit RGB or RGBA.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(chunk);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }
        if (channels == 0)
            throw new InvalidDataException("missing IHDR.");

        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            zlib.ReadExactly(raw);

        var image = new RgbImage(width, height);
        var previous = new byte[stride];
        var current = new byte[stride];
        for (int y = 0; y < height; ++y)
        {
            var row = raw.AsSpan(y * (stride + 1), stride + 1);
            Unfilter(row[0], row[1..], previous, current, channels);
            var output = image.Pixels.AsSpan(y * width * 3, width * 3);
            for (int x = 0; x < width; ++x)
            {
                output[x * 3] = current[x * channels];
                output[x * 3 + 1] = current[x * channels + 1];
                output[x * 3 + 2] = current[x * channels + 2];
            }
            (previous, current) = (current, previous);
        }
        return image;
    }

    private static void Unfilter(byte filter, ReadOnlySpan<byte> source, ReadOnlySpan<byte> above, Span<byte> output, int bpp)
    {
        for (int i = 0; i < source.Length; ++i)
        {
            int a = i >= bpp ? output[i - bpp] : 0;
            int b = above[i];
            int c = i >= bpp ? above[i - bpp] : 0;
            output[i] = (byte)(source[i] + filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException($"unknown filter type {filter}."),
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] Encode(RgbImage image)
    {
        var stride = image.Width * 3;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + stride];
            for (int y = 0; y < image.Height; ++y)
            {
                // Sub filter: flat backgrounds and gradients compress to almost nothing.
                row[0] = 1;
                var source = image.Pixels.AsSpan(y * stride, stride);
                for (int i = 0; i < stride; ++i)
                    row[1 + i] = (byte)(source[i] - (i >= 3 ? source[i - 3] : 0));
                zlib.Write(row);
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;
        header[9] = 2;
        header[10] = header[11] = header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        output.Write(type);
        output.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc ^ 0xFFFFFFFFu);
        output.Write(buffer);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; ++n)
        {
            var c = n;
            for (int k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
