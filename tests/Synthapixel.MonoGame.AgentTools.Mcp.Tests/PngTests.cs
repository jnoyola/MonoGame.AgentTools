extern alias Library;

using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Xna.Framework;
using Synthapixel.MonoGame.AgentTools.Mcp.Imaging;
using LibraryPng = Library::Synthapixel.MonoGame.AgentTools.Imaging.PngEncoder;

namespace Synthapixel.MonoGame.AgentTools.Mcp.Tests;

public class PngTests
{
    [Fact]
    public void Reads_the_frames_the_library_writes()
    {
        var pixels = new Color[5 * 3];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(i * 10, 255 - i * 7, i * i);
        using var file = new MemoryStream();
        LibraryPng.Write(file, 5, 3, pixels);

        var image = Png.Decode(file.ToArray());

        Assert.Equal((5, 3), (image.Width, image.Height));
        for (var i = 0; i < pixels.Length; i++)
            Assert.Equal([pixels[i].R, pixels[i].G, pixels[i].B], image.Pixels[(i * 3)..(i * 3 + 3)]);
    }

    [Fact]
    public void Round_trips_its_own_output()
    {
        var image = new RgbImage(4, 4);
        Random.Shared.NextBytes(image.Pixels);

        Assert.Equal(image.Pixels, Png.Decode(Png.Encode(image)).Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Undoes_every_filter_type_with_alpha(byte filter)
    {
        // 3x2 RGBA, with the same filter on every row.
        byte[][] rows = [[10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 0], [15, 25, 35, 255, 200, 210, 220, 128, 5, 6, 7, 255]];

        var image = Png.Decode(EncodeRgba(3, rows, filter));

        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 15, 25, 35, 200, 210, 220, 5, 6, 7 }, image.Pixels);
    }

    [Fact]
    public void Rejects_unsupported_formats_with_the_reason()
    {
        var png = EncodeRgba(1, [[1, 2, 3, 4]], 0);
        png[8 + 8 + 8] = 16; // bit depth

        var e = Assert.Throws<InvalidDataException>(() => Png.Decode(png));
        Assert.Contains("bit depth 16", e.Message, StringComparison.Ordinal);
    }

    private static byte[] EncodeRgba(int width, byte[][] rows, byte filter)
    {
        const int bpp = 4;
        using var raw = new MemoryStream();
        var previous = new byte[width * bpp];
        foreach (var row in rows)
        {
            raw.WriteByte(filter);
            for (var i = 0; i < row.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                var predictor = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                raw.WriteByte((byte)(row[i] - predictor));
            }
            previous = row;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw.ToArray());

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), rows.Length);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, compressed.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    // The reader doesn't check CRCs, so these are left zero.
    private static void Chunk(Stream png, ReadOnlySpan<byte> type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(type);
        png.Write(data);
        png.Write(new byte[4]);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
