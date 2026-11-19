using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MonoGame.AgentTools.Hud;
using MonoGame.AgentTools.Mcp.Imaging;

namespace MonoGame.AgentTools.Mcp.Tools;

[McpServerToolType]
internal sealed partial class SheetTools(Workspace workspace)
{
    private const int MaxTiles = 48;

    /// <summary>
    /// Rows of a comparison. Six rows of 16:9 frames fit the 1568 px limit at full detail; more are
    /// allowed, and the client shrinks the sheet.
    /// </summary>
    private const int MaxComparedFrames = 12;
    private static readonly Lazy<PixelFont> Font = new(PixelFont.LoadDefault);

    [McpServerTool(Name = "contact_sheet", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Makes another contact sheet from a capture's saved frames without re-running the game: zoom into a
        moment (a shorter time range, more frames) or an effect (a crop) after looking at capture's sheet,
        or compare it with a baseline capture (against). Tiles are labeled with frame number and game time;
        small crops are enlarged.
        """)]
    public CallToolResult ContactSheet(
        [Description("The capture: its name under .agenttools/captures (as returned by capture), or a folder path.")]
        string capture,
        [Description("Start of the time range in game seconds. Default: the first saved frame.")]
        double? from = null,
        [Description("End of the time range in game seconds, inclusive. Default: the last saved frame.")]
        double? to = null,
        [Description(SheetOptions.CountDescription)]
        int? count = null,
        [Description(SheetOptions.ColumnsDescription)]
        int columns = 4,
        [Description("Region of each frame to show, 'x,y,width,height' in frame pixels.")]
        string? crop = null,
        [Description(SheetOptions.AgainstDescription)]
        string? against = null)
    {
        var options = new SheetOptions { From = from, To = to, Count = count, Columns = columns, Crop = crop, Against = against };
        var sheet = Make(workspace, workspace.ResolveCapture(capture), options);
        return new CallToolResult { Content = [new TextContentBlock { Text = sheet.Caption }, sheet.Image] };
    }

    internal sealed record Sheet(string Caption, ImageContentBlock Image);

    /// <summary>Builds and saves a sheet from <paramref name="folder"/>'s saved frames, or a comparison with <see cref="SheetOptions.Against"/>.</summary>
    internal static Sheet Make(Workspace workspace, string folder, SheetOptions options)
    {
        var comparing = options.Against is not null;
        var (defaultCount, maxCount) = comparing ? (6, MaxComparedFrames) : (12, MaxTiles);
        var count = options.Count ?? defaultCount;
        if (count < 1 || count > maxCount)
            throw new ToolException($"count must be between 1 and {maxCount}{(comparing ? " when comparing" : "")}.");
        if (options.Columns < 1)
            throw new ToolException("columns must be at least 1.");
        var region = options.Crop is null ? (Crop?)null : Crop.Parse(options.Crop);

        var fps = ReadFps(folder);
        var saved = InRange(workspace.DisplayCapture(folder), SavedFrames(Path.Combine(folder, "frames"), fps), options);
        var suffix = string.Create(CultureInfo.InvariantCulture, $"_n{count}")
            + (region is { } r ? string.Create(CultureInfo.InvariantCulture, $"_crop{r.X}-{r.Y}-{r.Width}-{r.Height}") : "");

        if (!comparing)
        {
            var tiles = Imaging.ContactSheet.PickEvenly(saved, count);
            var image = Imaging.ContactSheet.Build(tiles, options.Columns, region, Font.Value);
            var name = string.Create(CultureInfo.InvariantCulture, $"f{tiles[0].Frame}-f{tiles[^1].Frame}_c{options.Columns}") + suffix;
            var png = Save(folder, name, image, out var path);
            var caption = string.Create(CultureInfo.InvariantCulture,
                $"Sheet {workspace.Display(path)} ({image.Width}x{image.Height}), {Math.Min(options.Columns, tiles.Count)} per row: ")
                + string.Join(", ", tiles.Select(t => string.Create(CultureInfo.InvariantCulture, $"f{t.Frame} {t.Seconds:0.000}s")));
            return new Sheet(caption, ImageContentBlock.FromBytes(png, "image/png"));
        }

        var baseline = workspace.ResolveCapture(options.Against!);
        var baselineFps = ReadFps(baseline);
        if (baselineFps != fps)
            throw new ToolException(string.Create(CultureInfo.InvariantCulture,
                $"The captures ran at {baselineFps:0.###} and {fps:0.###} fps, so the same frame number isn't the same moment."));
        var baselineFrames = SavedFrames(Path.Combine(baseline, "frames"), fps).ToDictionary(t => t.Frame);
        var shared = saved.Where(t => baselineFrames.ContainsKey(t.Frame)).ToList();
        if (shared.Count == 0)
            throw new ToolException("The captures have no saved frames in common in that range; capture both with the same save option.");

        var pairs = Imaging.ContactSheet.PickEvenly(shared, count).Select(t => (A: baselineFrames[t.Frame], B: t)).ToList();
        var changed = new double[pairs.Count];
        var comparison = Imaging.ContactSheet.BuildComparison(pairs, region, Font.Value, changed);
        var comparisonName = string.Create(CultureInfo.InvariantCulture,
            $"vs-{Path.GetFileName(baseline)}_f{pairs[0].B.Frame}-f{pairs[^1].B.Frame}") + suffix;
        var comparisonPng = Save(folder, comparisonName, comparison, out var comparisonPath);

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Comparison {workspace.Display(comparisonPath)} ({comparison.Width}x{comparison.Height}). ")
            .Append(CultureInfo.InvariantCulture, $"A = {workspace.DisplayCapture(baseline)}, B = {workspace.DisplayCapture(folder)}. Each row: A | B | changed pixels in red, ")
            .Append("brighter for larger changes; any change counts, since the same build and arguments draw the same frames.\n")
            .AppendJoin(", ", pairs.Select((p, i) => string.Create(CultureInfo.InvariantCulture,
                $"f{p.B.Frame} {p.B.Seconds:0.000}s {Imaging.ContactSheet.ChangedLabel(changed[i])}")));
        var onlyInB = saved.Count - shared.Count;
        if (onlyInB > 0)
            text.Append(CultureInfo.InvariantCulture, $"\n{onlyInB} frames in range were saved only in B and were skipped.");
        return new Sheet(text.ToString(), ImageContentBlock.FromBytes(comparisonPng, "image/png"));
    }

    internal static List<SheetTile> InRange(string capture, List<SheetTile> saved, SheetOptions options)
    {
        if (saved.Count == 0)
            throw new ToolException($"{capture} saved no frames; capture again with save 'all' or 'every:N'.");
        var from = options.From ?? double.NegativeInfinity;
        var to = options.To ?? double.PositiveInfinity;
        var inRange = saved.Where(t => t.Seconds >= from - 1e-9 && t.Seconds <= to + 1e-9).ToList();
        if (inRange.Count == 0)
            throw new ToolException(string.Create(CultureInfo.InvariantCulture,
                $"No saved frames in that range; saved frames span {saved[0].Seconds:0.###} to {saved[^1].Seconds:0.###} s."));
        return inRange;
    }

    private static byte[] Save(string folder, string name, RgbImage image, out string path)
    {
        path = Path.Combine(folder, "sheets", name + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png = Png.Encode(image);
        File.WriteAllBytes(path, png);
        return png;
    }

    internal static double ReadFps(string folder)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "summary.json")));
        return json.RootElement.TryGetProperty("fps", out var fps) ? fps.GetDouble() : 60;
    }

    /// <summary>Saved frames in order. Frame N is drawn after N fixed steps, at N / fps game seconds.</summary>
    internal static List<SheetTile> SavedFrames(string framesFolder, double fps)
    {
        if (!Directory.Exists(framesFolder))
            return [];
        return Directory.EnumerateFiles(framesFolder, "f*.png")
            .Select(path => (path, match: FrameFile().Match(Path.GetFileName(path))))
            .Where(f => f.match.Success)
            .Select(f =>
            {
                var frame = int.Parse(f.match.Groups[1].Value, CultureInfo.InvariantCulture);
                return new SheetTile(frame, frame / fps, f.path);
            })
            .OrderBy(t => t.Frame)
            .ToList();
    }

    [GeneratedRegex(@"^f(\d+)\.png$")]
    private static partial Regex FrameFile();
}
