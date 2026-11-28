using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Xna.Framework;
using Synthapixel.MonoGame.AgentTools.Imaging;

namespace Synthapixel.MonoGame.AgentTools.Tests;

public class ImagingTests
{
    [Theory]
    [InlineData(5, 3)]      // narrower than one vector step: scalar filter only
    [InlineData(37, 4)]     // vector steps plus a scalar tail
    public void Png_round_trips_pixels_and_has_valid_chunk_crcs(int width, int height)
    {
        var random = new Random(width);
        var pixels = Enumerable.Range(0, width * height).Select(_ => new Color(random.Next(256), random.Next(256), random.Next(256), random.Next(256))).ToArray();
        using var stream = new MemoryStream();

        PngEncoder.Write(stream, width, height, pixels);

        var (w, h, rgb) = Decode(stream.ToArray());
        Assert.Equal((width, height), (w, h));
        for (int i = 0; i < pixels.Length; ++i)
            Assert.Equal((pixels[i].R, pixels[i].G, pixels[i].B), (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(65)]
    [InlineData(5552)]
    [InlineData(5553)]
    [InlineData(100_003)]
    public void Checksums_match_reference_implementations(int length)
    {
        var random = new Random(length);
        var data = new byte[length];
        random.NextBytes(data);
        // All 255s is the worst case for the sums overflowing between reductions.
        var saturated = Enumerable.Repeat((byte)255, length).ToArray();

        foreach (var input in new[] { data, saturated })
        {
            Assert.Equal(ReferenceAdler32(input), Checksums.Adler32(input));
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(input), Checksums.Crc32(input));
        }
    }

    private static uint ReferenceAdler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return b << 16 | a;
    }

    [Theory]
    [InlineData("random")]
    [InlineData("runs")]
    [InlineData("window")]
    [InlineData("empty")]
    public void Zlib_encoder_output_inflates_to_the_input(string kind)
    {
        var random = new Random(7);
        var input = kind switch
        {
            "random" => Enumerable.Range(0, 70_000).Select(_ => (byte)random.Next(256)).ToArray(),
            // Runs longer than the 258-byte maximum match, and short ones at every length.
            "runs" => Enumerable.Range(0, 400).SelectMany(i => Enumerable.Repeat((byte)(i % 3), i % 2 == 0 ? 1000 : i % 9)).ToArray(),
            // A block repeated exactly one window (32768 bytes) later, and again just beyond it.
            "window" => Repeat(Enumerable.Range(0, 32768).Select(_ => (byte)random.Next(4)).ToArray(), 3, extra: 1),
            _ => [],
        };
        var encoder = new ZLibFastEncoder();
        var output = new byte[ZLibFastEncoder.MaxCompressedLength(Math.Max(input.Length, 5000))];

        // Twice, to check that the reused match table doesn't carry over between inputs.
        encoder.Compress(new byte[5000], output);
        var length = encoder.Compress(input, output);

        using var zlib = new ZLibStream(new MemoryStream(output, 0, length), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        Assert.Equal(input, inflated.ToArray());
    }

    [Fact]
    public void Encoding_a_frame_again_allocates_nothing()
    {
        const int width = 320, height = 180;
        var pixels = Enumerable.Range(0, width * height).Select(i => new Color(i % 256, i / width % 256, 40)).ToArray();
        var encoder = new PngEncoder();
        encoder.Encode(width, height, pixels);

        var before = GC.GetAllocatedBytesForCurrentThread();
        encoder.Encode(width, height, pixels);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Writing_a_file_natively_allocates_nothing()
    {
        var path = NativeFile.Prepare(Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.bin"));
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        try
        {
            NativeFile.Write(path, data);

            var before = GC.GetAllocatedBytesForCurrentThread();
            NativeFile.Write(path, data);

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(data, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Repeat(byte[] block, int times, int extra) =>
        [.. Enumerable.Repeat(block, times).SelectMany(b => b), .. block.Take(extra)];

    [Fact]
    public void Contact_sheet_picks_evenly_spaced_frames_including_ends()
    {
        Assert.Equal([1, 4, 7, 10], ContactSheet.PickFrames(10, 4));
        Assert.Equal([1, 2, 3], ContactSheet.PickFrames(3, 12));
        Assert.Equal([1], ContactSheet.PickFrames(1, 12));
    }

    [Fact]
    public void Contact_sheet_lays_out_downscaled_tiles_with_gaps()
    {
        var sheet = new ContactSheet(frameWidth: 8, frameHeight: 4, tiles: 3, columns: 2, maxTileWidth: 4);

        Assert.Equal((4 + 2 + 4, 2 + 2 + 2), (sheet.Width, sheet.Height));
    }

    private static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        int offset = 8, width = 0, height = 0;
        using var idat = new MemoryStream();
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var typeAndData = png.AsSpan(offset + 4, 4 + length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(crc, System.IO.Hashing.Crc32.HashToUInt32(typeAndData));
            var type = System.Text.Encoding.ASCII.GetString(typeAndData[..4]);
            var data = typeAndData[4..];
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            offset += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        var stride = width * 3;
        var rgb = new byte[stride * height];
        for (int y = 0; y < height; ++y)
        {
            Assert.Equal(1, bytes[y * (stride + 1)]);   // Sub filter
            for (int x = 0; x < stride; ++x)
            {
                var left = x >= 3 ? rgb[y * stride + x - 3] : 0;
                rgb[y * stride + x] = (byte)(bytes[y * (stride + 1) + 1 + x] + left);
            }
        }
        return (width, height, rgb);
    }
}
