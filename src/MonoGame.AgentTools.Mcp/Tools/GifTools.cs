using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using MonoGame.AgentTools.Hud;
using MonoGame.AgentTools.Mcp.Imaging;

namespace MonoGame.AgentTools.Mcp.Tools;

[McpServerToolType]
internal sealed class GifTools(Workspace workspace)
{
    /// <summary>Browsers play delays under 2 centiseconds at 10, so 50 fps is the fastest a GIF reliably plays.</summary>
    private const int MaxFps = 50;
    private const int Gap = 2;
    private static readonly Lazy<PixelFont> Font = new(PixelFont.LoadDefault);

    [McpServerTool(Name = "gif", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Makes an animated GIF from a capture's saved frames, for the user to watch: it plays on a
        double-click in Windows, in browsers and in chat apps. You can't see the animation yourself (use
        contact_sheet), so this returns only the file's path; offer it when the user should judge motion,
        such as an A/B choice (against). Plays at game speed by default; speed below 1 is slow motion.
        Frames are labeled with frame number and game time.
        """)]
    public string Gif(
        [Description("The capture: its name under .agenttools/captures (as returned by capture), or a folder path.")]
        string capture,
        [Description("Start of the time range in game seconds. Default: the first saved frame.")]
        double? from = null,
        [Description("End of the time range in game seconds, inclusive. Default: the last saved frame.")]
        double? to = null,
        [Description("Frames per second of game time to include, at most 50. Default 30.")]
        double fps = 30,
        [Description("Playback speed: 1 is game speed, 0.25 is quarter-speed slow motion. Default 1.")]
        double speed = 1,
        [Description("Width of each frame in the GIF, in pixels. Default 640, or the frame or crop width if smaller.")]
        int width = 640,
        [Description("Region of each frame to show, 'x,y,width,height' in frame pixels.")]
        string? crop = null,
        [Description("A baseline capture to play beside this one (its name or folder): A on the left, this capture as B on the right. Both need the same fps and size.")]
        string? against = null)
    {
        if (fps <= 0 || fps > MaxFps)
            throw new ToolException($"fps must be above 0 and at most {MaxFps}: GIF delays are in hundredths of a second and browsers slow down shorter ones.");
        if (speed <= 0)
            throw new ToolException("speed must be above 0.");
        if (width < 16)
            throw new ToolException("width must be at least 16.");

        var folder = workspace.ResolveCapture(capture);
        var captureFps = SheetTools.ReadFps(folder);
        var saved = SheetTools.InRange(workspace.DisplayCapture(folder), SheetTools.SavedFrames(Path.Combine(folder, "frames"), captureFps),
            new SheetOptions { From = from, To = to });

        string? baseline = null;
        Dictionary<int, SheetTile>? baselineFrames = null;
        if (against is not null)
        {
            baseline = workspace.ResolveCapture(against);
            if (SheetTools.ReadFps(baseline) != captureFps)
                throw new ToolException("The captures ran at different fps, so the same frame number isn't the same moment.");
            baselineFrames = SheetTools.SavedFrames(Path.Combine(baseline, "frames"), captureFps).ToDictionary(t => t.Frame);
            saved = saved.Where(t => baselineFrames.ContainsKey(t.Frame)).ToList();
            if (saved.Count == 0)
                throw new ToolException("The captures have no saved frames in common in that range; capture both with the same save option.");
        }

        var frames = Pick(saved, fps);
        var delays = Delays(frames, fps, speed);
        var first = Png.Load(frames[0].Path);
        var region = crop is null ? new Crop(0, 0, first.Width, first.Height) : Crop.Parse(crop);
        if (region.X < 0 || region.Y < 0 || region.X + region.Width > first.Width || region.Y + region.Height > first.Height)
            throw new ToolException($"crop {region.X},{region.Y},{region.Width},{region.Height} is outside the {first.Width}x{first.Height} frame.");
        var panelWidth = Math.Min(width, region.Width);
        var panelHeight = Math.Max(1, (int)Math.Round(region.Height * (double)panelWidth / region.Width));
        var panels = baselineFrames is null ? 1 : 2;
        var output = new RgbImage(panels * panelWidth + (panels - 1) * Gap, panelHeight);

        RgbImage Compose(int i)
        {
            Array.Clear(output.Pixels);
            var tile = frames[i];
            var image = i == 0 ? first : ContactSheet.Load(tile, first);
            if (baselineFrames is not null)
            {
                var a = baselineFrames[tile.Frame];
                Resample(ContactSheet.Load(a, first), region, output, 0, panelWidth, panelHeight);
                ContactSheet.DrawLabel(output, Font.Value, ContactSheet.Label("A ", a), 0, 0);
                Resample(image, region, output, panelWidth + Gap, panelWidth, panelHeight);
                ContactSheet.DrawLabel(output, Font.Value, ContactSheet.Label("B ", tile), panelWidth + Gap, 0);
            }
            else
            {
                Resample(image, region, output, 0, panelWidth, panelHeight);
                ContactSheet.DrawLabel(output, Font.Value, ContactSheet.Label("", tile), 0, 0);
            }
            return output;
        }

        // Two passes over the frames (palette, then encoding) keep memory to one frame at a time.
        var histogram = new GifHistogram();
        for (int i = 0; i < frames.Count; ++i)
            GifPalette.Accumulate(Compose(i), histogram);
        var palette = GifPalette.Build(histogram);

        var suffix = string.Create(CultureInfo.InvariantCulture, $"_{fps:0.##}fps")
            + (speed != 1 ? string.Create(CultureInfo.InvariantCulture, $"_x{speed:0.###}") : "")
            + (crop is null ? "" : string.Create(CultureInfo.InvariantCulture, $"_crop{region.X}-{region.Y}-{region.Width}-{region.Height}"))
            + string.Create(CultureInfo.InvariantCulture, $"_w{panelWidth}");
        var name = (baseline is null ? "" : $"vs-{Path.GetFileName(baseline)}_")
            + string.Create(CultureInfo.InvariantCulture, $"f{frames[0].Frame}-f{frames[^1].Frame}") + suffix + ".gif";
        var path = Path.Combine(folder, "gifs", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        {
            var gif = new GifWriter(file, output.Width, output.Height, palette);
            for (int i = 0; i < frames.Count; ++i)
                gif.Add(Compose(i), delays[i]);
            gif.Finish();
        }

        var seconds = delays.Sum() / 100.0;
        var comparison = baseline is null ? "" : $" A = {workspace.DisplayCapture(baseline)}, B = {workspace.DisplayCapture(folder)}.";
        return string.Create(CultureInfo.InvariantCulture,
            $"GIF {workspace.Display(path)} ({output.Width}x{output.Height}, {frames.Count} frames f{frames[0].Frame}-f{frames[^1].Frame}, plays in {seconds:0.##} s, {new FileInfo(path).Length / 1024.0:0} KB).{comparison} Give the user this path to open.");
    }

    /// <summary>
    /// The saved frame nearest each step of 1/<paramref name="fps"/> game seconds, without repeats, so
    /// frames saved every N steps or at a lower rate still play at their own times.
    /// </summary>
    internal static List<SheetTile> Pick(List<SheetTile> saved, double fps)
    {
        var picked = new List<SheetTile>();
        var start = saved[0].Seconds;
        var j = 0;
        for (int k = 0; ; ++k)
        {
            var t = start + k / fps;
            if (t > saved[^1].Seconds + 1e-9)
                break;
            while (j + 1 < saved.Count && Math.Abs(saved[j + 1].Seconds - t) <= Math.Abs(saved[j].Seconds - t))
                ++j;
            if (picked.Count == 0 || picked[^1].Frame != saved[j].Frame)
                picked.Add(saved[j]);
        }
        return picked;
    }

    /// <summary>
    /// Each frame's delay in centiseconds, from the game time to the next frame divided by speed, rounded
    /// against the running total so the GIF keeps time (30 fps plays as 3, 3, 4, ...). At least 2.
    /// </summary>
    internal static int[] Delays(List<SheetTile> frames, double fps, double speed)
    {
        var delays = new int[frames.Count];
        var shown = 0;
        for (int i = 0; i < frames.Count; ++i)
        {
            var end = i + 1 < frames.Count ? frames[i + 1].Seconds : frames[i].Seconds + 1 / fps;
            var target = (int)Math.Round((end - frames[0].Seconds) / speed * 100);
            delays[i] = Math.Max(2, target - shown);
            shown += delays[i];
        }
        return delays;
    }

    /// <summary>Area-averages <paramref name="region"/> of <paramref name="source"/> into a panel of <paramref name="target"/>.</summary>
    private static void Resample(RgbImage source, Crop region, RgbImage target, int originX, int width, int height)
    {
        for (int y = 0; y < height; ++y)
        {
            var y0 = region.Y + y * region.Height / height;
            var y1 = Math.Max(y0 + 1, region.Y + (y + 1) * region.Height / height);
            for (int x = 0; x < width; ++x)
            {
                var x0 = region.X + x * region.Width / width;
                var x1 = Math.Max(x0 + 1, region.X + (x + 1) * region.Width / width);
                int r = 0, g = 0, b = 0;
                for (int sy = y0; sy < y1; ++sy)
                {
                    for (int sx = x0, o = (sy * source.Width + x0) * 3; sx < x1; ++sx, o += 3)
                    {
                        r += source.Pixels[o];
                        g += source.Pixels[o + 1];
                        b += source.Pixels[o + 2];
                    }
                }
                var area = (y1 - y0) * (x1 - x0);
                var d = (y * target.Width + originX + x) * 3;
                target.Pixels[d] = (byte)(r / area);
                target.Pixels[d + 1] = (byte)(g / area);
                target.Pixels[d + 2] = (byte)(b / area);
            }
        }
    }
}
