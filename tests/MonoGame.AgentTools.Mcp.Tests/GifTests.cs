using MonoGame.AgentTools.Mcp.Imaging;
using MonoGame.AgentTools.Mcp.Tools;

namespace MonoGame.AgentTools.Mcp.Tests;

public class GifTests
{
    private static readonly byte[][] Colors = [[0, 0, 0], [255, 255, 255], [255, 0, 0], [0, 64, 255]];

    [Fact]
    public void Frames_decode_to_the_input_and_unchanged_frames_extend_the_previous_delay()
    {
        const int width = 300, height = 200;
        var random = new Random(5);
        // Noise over four colors fills the 4096-code table several times, so clear codes are exercised.
        var noise = Image(width, height, (_, _) => random.Next(4));
        var changed = Image(width, height, (x, y) => x is >= 40 and < 90 && y is >= 30 and < 60 ? 2 : Index(noise, x, y));
        var histogram = new GifHistogram();
        histogram.Add(noise);
        histogram.Add(changed);
        using var stream = new MemoryStream();

        var gif = new GifWriter(stream, width, height, GifPalette.Build(histogram));
        gif.Add(noise, 3);
        gif.Add(noise, 3);
        gif.Add(changed, 4);
        gif.Finish();

        var frames = Decode(stream.ToArray(), out var w, out var h);
        Assert.Equal((width, height), (w, h));
        Assert.Equal([6, 4], frames.Select(f => f.Delay));
        Assert.Equal(noise.Pixels, frames[0].Pixels);
        Assert.Equal(changed.Pixels, frames[1].Pixels);
        Assert.Equal((40, 30, 50, 30), frames[1].Rect);
    }

    [Fact]
    public void A_comparison_with_diff_plays_a_third_panel_of_changed_pixels()
    {
        var root = Directory.CreateTempSubdirectory("agenttools-gifdiff-").FullName;
        try
        {
            var workspace = new Workspace(root);
            void Capture(string name, bool changed)
            {
                var folder = workspace.PrepareCapture(name);
                File.WriteAllText(Path.Combine(folder, "summary.json"), """{ "fps": 30 }""");
                Directory.CreateDirectory(Path.Combine(folder, "frames"));
                for (var f = 1; f <= 2; f++)
                {
                    var image = new RgbImage(64, 36);
                    Array.Fill(image.Pixels, (byte)40);
                    if (changed && f == 2)
                        image.Pixels.AsSpan((25 * 64 + 20) * 3, 3).Fill(255);
                    File.WriteAllBytes(Path.Combine(folder, "frames", $"f{f:0000}.png"), Png.Encode(image));
                }
            }
            Capture("before", changed: false);
            Capture("after", changed: true);
            var tools = new GifTools(workspace);

            var result = tools.Gif("after", baseline: "before", diff: true);
            Assert.Throws<ToolException>(() => tools.Gif("after", diff: true));

            Assert.Contains("(196x36, 2 frames", result, StringComparison.Ordinal);   // three 64 px panels, 2 px gaps
            Assert.Contains("then the changed pixels in red", result, StringComparison.Ordinal);
            var path = Path.Combine(root, result.Split(' ')[1]);
            var frames = Decode(File.ReadAllBytes(path), out var width, out _);
            var red = (25 * width + 2 * (64 + 2) + 20) * 3;
            Assert.Equal([255, 0, 0], frames[1].Pixels[red..(red + 3)]);
            Assert.NotEqual(255, frames[0].Pixels[red]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Palette_colors_are_exact_means_not_bin_centers()
    {
        var histogram = new GifHistogram();
        histogram.Add(Image(2, 1, (x, _) => x));

        var palette = GifPalette.Build(histogram);

        Assert.Equal(2, palette.Count);
        Assert.Contains("255,255,255", Enumerable.Range(0, 2).Select(i => string.Join(',', palette.Colors[(i * 3)..(i * 3 + 3)])));
    }

    [Fact]
    public void Picks_frames_at_the_playback_rate_and_keeps_game_time()
    {
        var saved = Enumerable.Range(1, 60).Select(f => new SheetTile(f, f / 60.0, "")).ToList();

        var frames = GifTools.Pick(saved, 30);
        var delays = GifTools.Delays(frames, 30, speed: 1);
        var slow = GifTools.Delays(frames, 30, speed: 0.25);

        Assert.Equal(Enumerable.Range(0, 30).Select(i => 1 + 2 * i), frames.Select(f => f.Frame));
        Assert.Equal([3, 4, 3, 3, 4, 3], delays.Take(6));
        Assert.Equal(100, delays.Sum());
        Assert.Equal(400, slow.Sum());
    }

    [Fact]
    public void Picks_every_saved_frame_when_frames_were_saved_more_sparsely()
    {
        var saved = Enumerable.Range(1, 10).Select(f => new SheetTile(f * 6, f / 10.0, "")).ToList();

        Assert.Equal(saved.Select(t => t.Frame), GifTools.Pick(saved, 30).Select(f => f.Frame));
    }

    private static RgbImage Image(int width, int height, Func<int, int, int> color)
    {
        var image = new RgbImage(width, height);
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
                Colors[color(x, y)].CopyTo(image.Pixels, (y * width + x) * 3);
        return image;
    }

    private static int Index(RgbImage image, int x, int y)
    {
        var o = (y * image.Width + x) * 3;
        return Array.FindIndex(Colors, c => c[0] == image.Pixels[o] && c[1] == image.Pixels[o + 1] && c[2] == image.Pixels[o + 2]);
    }

    private sealed record Frame(byte[] Pixels, int Delay, (int X, int Y, int Width, int Height) Rect);

    /// <summary>A decoder written from the GIF89a spec, composing frames that keep the previous image (disposal 1).</summary>
    private static List<Frame> Decode(byte[] gif, out int width, out int height)
    {
        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        width = gif[6] | gif[7] << 8;
        height = gif[8] | gif[9] << 8;
        var flags = gif[10];
        var o = 13;
        var table = Array.Empty<byte>();
        if ((flags & 0x80) != 0)
        {
            var size = 3 << ((flags & 7) + 1);
            table = gif[o..(o + size)];
            o += size;
        }
        var canvas = new byte[width * height * 3];
        var frames = new List<Frame>();
        int delay = 0, transparent = -1;
        while (true)
        {
            var block = gif[o++];
            if (block == 0x3B)
                return frames;
            if (block == 0x21)
            {
                var label = gif[o++];
                if (label == 0xF9)
                {
                    Assert.Equal(1, (gif[o + 1] >> 2) & 7);   // disposal: keep
                    delay = gif[o + 2] | gif[o + 3] << 8;
                    transparent = (gif[o + 1] & 1) != 0 ? gif[o + 4] : -1;
                }
                for (int n = gif[o]; n != 0; n = gif[o])
                    o += n + 1;
                ++o;
                continue;
            }
            Assert.Equal(0x2C, block);
            int left = gif[o] | gif[o + 1] << 8, top = gif[o + 2] | gif[o + 3] << 8;
            int w = gif[o + 4] | gif[o + 5] << 8, h = gif[o + 6] | gif[o + 7] << 8;
            Assert.Equal(0, gif[o + 8]);
            o += 9;
            var minCodeSize = gif[o++];
            var data = new List<byte>();
            for (int n = gif[o]; n != 0; n = gif[o])
            {
                data.AddRange(gif[(o + 1)..(o + 1 + n)]);
                o += n + 1;
            }
            ++o;
            var indices = Lzw(data.ToArray(), minCodeSize, w * h);
            for (int i = 0; i < indices.Count; ++i)
            {
                if (indices[i] == transparent)
                    continue;
                var c = ((top + i / w) * width + left + i % w) * 3;
                table.AsSpan(indices[i] * 3, 3).CopyTo(canvas.AsSpan(c));
            }
            frames.Add(new Frame((byte[])canvas.Clone(), delay, (left, top, w, h)));
        }
    }

    private static List<int> Lzw(byte[] data, int minCodeSize, int pixels)
    {
        int clear = 1 << minCodeSize, end = clear + 1;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var output = new List<int>();
        int codeSize = minCodeSize + 1, next = end + 1, previous = -1, position = 0;

        IEnumerable<byte> Expand(int code)
        {
            var stack = new Stack<byte>();
            for (; code >= clear; code = prefix[code])
                stack.Push(suffix[code]);
            stack.Push((byte)code);
            return stack;
        }

        while (true)
        {
            var code = 0;
            for (int b = 0; b < codeSize; ++b, ++position)
                code |= ((data[position >> 3] >> (position & 7)) & 1) << b;
            if (code == clear)
            {
                (codeSize, next, previous) = (minCodeSize + 1, end + 1, -1);
                continue;
            }
            if (code == end)
                break;
            if (previous < 0)
            {
                output.Add(code);
                previous = code;
                continue;
            }
            var entry = (code < next ? Expand(code) : [.. Expand(previous), Expand(previous).First()]).ToArray();
            output.AddRange(entry.Select(e => (int)e));
            if (next < 4096)
            {
                prefix[next] = previous;
                suffix[next] = entry[0];
                ++next;
                if (next == 1 << codeSize && codeSize < 12)
                    ++codeSize;
            }
            previous = code;
        }
        Assert.Equal(pixels, output.Count);
        return output;
    }
}
