using System.Globalization;
using MonoGame.AgentTools.Hud;

namespace MonoGame.AgentTools.Mcp.Imaging;

/// <summary>A region of a frame, in frame pixels.</summary>
internal readonly record struct Crop(int X, int Y, int Width, int Height)
{
    /// <summary>Parses <c>x,y,width,height</c>.</summary>
    public static Crop Parse(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new ToolException($"crop must be 'x,y,width,height' in frame pixels, got '{text}'.");
        var v = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (v[2] == 0 || v[3] == 0)
            throw new ToolException("crop width and height must be positive.");
        return new Crop(v[0], v[1], v[2], v[3]);
    }
}

internal sealed record SheetTile(int Frame, double Seconds, string Path);

/// <summary>
/// Frames side by side in one image, each labeled with its frame number and game time, for judging
/// motion (timing, spacing, arcs) without playing a video: the animation and VFX review sheet.
/// </summary>
internal static class ContactSheet
{
    /// <summary>
    /// Model clients downscale larger images (Claude above a 1568 px long edge), so a wider sheet only
    /// costs tokens.
    /// </summary>
    public const int MaxWidth = 1568;
    private const int Gap = 2;
    private const int MaxUpscale = 4;

    public static RgbImage Build(IReadOnlyList<SheetTile> tiles, int columns, Crop? crop, PixelFont font)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tiles.Count);
        columns = Math.Clamp(columns, 1, tiles.Count);

        var first = Png.Load(tiles[0].Path);
        var layout = Fit(first, crop, columns);
        var rows = (tiles.Count + columns - 1) / columns;
        var sheet = new RgbImage(columns * layout.TileWidth + (columns - 1) * Gap, rows * layout.TileHeight + (rows - 1) * Gap);
        for (int i = 0; i < tiles.Count; ++i)
        {
            var frame = i == 0 ? first : Load(tiles[i], first);
            var originX = i % columns * (layout.TileWidth + Gap);
            var originY = i / columns * (layout.TileHeight + Gap);
            Blit(frame, layout, sheet, originX, originY);
            DrawLabel(sheet, font, Label("", tiles[i]), originX, originY);
        }
        return sheet;
    }

    /// <summary>
    /// One row per moment: frame A, the same frame of B, and where they differ, like Playwright's
    /// expected/actual/diff images. Captures are deterministic, so any changed pixel is a real change and
    /// counts (ImageMagick compare's default of no fuzz), rather than pixelmatch's threshold for
    /// cross-machine noise, which hides faint opacity changes in effects. Changed pixels are red, brighter
    /// for larger changes (pixelmatch's YIQ difference), over a dimmed B. <paramref name="changed"/>
    /// receives each row's fraction of changed pixels within the crop.
    /// </summary>
    public static RgbImage BuildComparison(IReadOnlyList<(SheetTile A, SheetTile B)> pairs, Crop? crop, PixelFont font, double[] changed)
    {
        ArgumentOutOfRangeException.ThrowIfZero(pairs.Count);
        const int columns = 3;
        var first = Png.Load(pairs[0].A.Path);
        var layout = Fit(first, crop, columns);
        var sheet = new RgbImage(columns * layout.TileWidth + (columns - 1) * Gap, pairs.Count * layout.TileHeight + (pairs.Count - 1) * Gap);
        var change = new byte[layout.Region.Width * layout.Region.Height];
        for (int i = 0; i < pairs.Count; ++i)
        {
            var a = i == 0 ? first : Load(pairs[i].A, first);
            var b = Load(pairs[i].B, first);
            changed[i] = Diff(a, b, layout.Region, change);
            var originY = i * (layout.TileHeight + Gap);
            Blit(a, layout, sheet, 0, originY);
            Blit(b, layout, sheet, layout.TileWidth + Gap, originY);
            BlitDiff(b, change, layout, sheet, 2 * (layout.TileWidth + Gap), originY);
            DrawLabel(sheet, font, Label("A ", pairs[i].A), 0, originY);
            DrawLabel(sheet, font, Label("B ", pairs[i].B), layout.TileWidth + Gap, originY);
            DrawLabel(sheet, font, ChangedLabel(changed[i]), 2 * (layout.TileWidth + Gap), originY);
        }
        return sheet;
    }

    public static string ChangedLabel(double changed) =>
        changed == 0 ? "same" : string.Create(CultureInfo.InvariantCulture, $"diff {Math.Max(changed * 100, 0.01):0.00}%");

    public static string Label(string prefix, SheetTile tile) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}f{tile.Frame} {tile.Seconds:0.000}s");

    public static RgbImage Load(SheetTile tile, RgbImage first)
    {
        var frame = Png.Load(tile.Path);
        if (frame.Width != first.Width || frame.Height != first.Height)
            throw new ToolException($"{tile.Path} is {frame.Width}x{frame.Height}; the other frames are {first.Width}x{first.Height}.");
        return frame;
    }

    private readonly record struct Layout(Crop Region, int Down, int Up, int TileWidth, int TileHeight);

    /// <summary>
    /// Shrinks by a whole factor with a box filter, or enlarges small crops by a whole factor (nearest),
    /// so pixels stay crisp and nothing is resampled unevenly.
    /// </summary>
    private static Layout Fit(RgbImage first, Crop? crop, int columns)
    {
        var region = crop ?? new Crop(0, 0, first.Width, first.Height);
        if (region.X < 0 || region.Y < 0 || region.X + region.Width > first.Width || region.Y + region.Height > first.Height)
            throw new ToolException($"crop {region.X},{region.Y},{region.Width},{region.Height} is outside the {first.Width}x{first.Height} frame.");

        var maxTileWidth = (MaxWidth - (columns - 1) * Gap) / columns;
        int down = 1, up = 1;
        if (region.Width > maxTileWidth)
            down = (region.Width + maxTileWidth - 1) / maxTileWidth;
        else
            up = Math.Clamp(maxTileWidth / region.Width, 1, MaxUpscale);
        return new Layout(region, down, up, region.Width / down * up, region.Height / down * up);
    }

    /// <summary>
    /// Each pixel's change, 0 for none, else 1 to 255 by pixelmatch's YIQ difference (perceptual: luma
    /// counts most), scaled so a quarter of the black-to-white difference is full. Returns the changed fraction.
    /// </summary>
    private static double Diff(RgbImage a, RgbImage b, Crop region, byte[] change)
    {
        const double maxDelta = 35215; // the YIQ difference between black and white
        var count = 0;
        for (int y = 0; y < region.Height; ++y)
        {
            var o = ((region.Y + y) * a.Width + region.X) * 3;
            for (int x = 0; x < region.Width; ++x, o += 3)
            {
                double dr = a.Pixels[o] - b.Pixels[o], dg = a.Pixels[o + 1] - b.Pixels[o + 1], db = a.Pixels[o + 2] - b.Pixels[o + 2];
                byte level = 0;
                if (dr != 0 || dg != 0 || db != 0)
                {
                    var dy = dr * 0.29889531 + dg * 0.58662247 + db * 0.11448223;
                    var di = dr * 0.59597799 - dg * 0.27417610 - db * 0.32180189;
                    var dq = dr * 0.21147017 - dg * 0.52261711 + db * 0.31114694;
                    var delta = 0.5053 * dy * dy + 0.299 * di * di + 0.1957 * dq * dq;
                    level = (byte)Math.Clamp(Math.Ceiling(255 * 4 * Math.Sqrt(delta / maxDelta)), 1, 255);
                    ++count;
                }
                change[y * region.Width + x] = level;
            }
        }
        return count / (double)(region.Width * region.Height);
    }

    private static void Blit(RgbImage frame, Layout layout, RgbImage sheet, int originX, int originY)
    {
        var (region, down, up) = (layout.Region, layout.Down, layout.Up);
        var area = down * down;
        for (int ty = 0; ty < layout.TileHeight; ++ty)
        {
            for (int tx = 0; tx < layout.TileWidth; ++tx)
            {
                // Source block for this tile pixel.
                var sx = region.X + tx / up * down;
                var sy = region.Y + ty / up * down;
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < down; ++dy)
                {
                    var o = ((sy + dy) * frame.Width + sx) * 3;
                    for (int dx = 0; dx < down; ++dx, o += 3)
                    {
                        r += frame.Pixels[o];
                        g += frame.Pixels[o + 1];
                        b += frame.Pixels[o + 2];
                    }
                }
                var d = ((originY + ty) * sheet.Width + originX + tx) * 3;
                sheet.Pixels[d] = (byte)(r / area);
                sheet.Pixels[d + 1] = (byte)(g / area);
                sheet.Pixels[d + 2] = (byte)(b / area);
            }
        }
    }

    /// <summary>
    /// Red where any pixel in the source block changed (its largest change, from dark to full red), so a
    /// one-pixel change survives downscaling; elsewhere B's luminance at a third, for context.
    /// </summary>
    private static void BlitDiff(RgbImage frame, byte[] change, Layout layout, RgbImage sheet, int originX, int originY)
    {
        const int minimumRed = 110; // above the dimmed context (at most 85), so the smallest change shows
        var (region, down, up) = (layout.Region, layout.Down, layout.Up);
        var area = down * down;
        for (int ty = 0; ty < layout.TileHeight; ++ty)
        {
            for (int tx = 0; tx < layout.TileWidth; ++tx)
            {
                var mx = tx / up * down;
                var my = ty / up * down;
                var level = 0;
                var luma = 0;
                for (int dy = 0; dy < down; ++dy)
                {
                    var o = ((region.Y + my + dy) * frame.Width + region.X + mx) * 3;
                    for (int dx = 0; dx < down; ++dx, o += 3)
                    {
                        level = Math.Max(level, change[(my + dy) * region.Width + mx + dx]);
                        luma += (frame.Pixels[o] * 77 + frame.Pixels[o + 1] * 150 + frame.Pixels[o + 2] * 29) >> 8;
                    }
                }
                var d = ((originY + ty) * sheet.Width + originX + tx) * 3;
                if (level > 0)
                {
                    sheet.Pixels[d] = (byte)(minimumRed + level * (255 - minimumRed) / 255);
                    sheet.Pixels[d + 1] = 0;
                    sheet.Pixels[d + 2] = 0;
                }
                else
                {
                    sheet.Pixels.AsSpan(d, 3).Fill((byte)(luma / area / 3));
                }
            }
        }
    }

    public static void DrawLabel(RgbImage sheet, PixelFont font, string text, int originX, int originY)
    {
        const int padding = 2;
        var width = Math.Min(text.Length * font.Width + 2 * padding, sheet.Width - originX);
        var height = Math.Min(font.Height + 2 * padding, sheet.Height - originY);
        for (int y = 0; y < height; ++y)
            sheet.Pixels.AsSpan(((originY + y) * sheet.Width + originX) * 3, width * 3).Clear();

        for (int i = 0; i < text.Length; ++i)
        {
            var glyph = font.GlyphIndex(text[i]);
            for (int y = 0; y < font.Height; ++y)
            {
                for (int x = 0; x < font.Width; ++x)
                {
                    var px = originX + padding + i * font.Width + x;
                    var py = originY + padding + y;
                    if (px < originX + width && py < originY + height && font.IsLit(glyph, x, y))
                        sheet.Pixels.AsSpan((py * sheet.Width + px) * 3, 3).Fill(255);
                }
            }
        }
    }

    /// <summary>
    /// Up to <paramref name="count"/> evenly spaced items from <paramref name="candidates"/>, first and last
    /// included.
    /// </summary>
    public static IReadOnlyList<T> PickEvenly<T>(IReadOnlyList<T> candidates, int count)
    {
        count = Math.Min(count, candidates.Count);
        if (count <= 1)
            return candidates.Take(count).ToList();
        return Enumerable.Range(0, count)
            .Select(i => candidates[(int)Math.Round(i * (candidates.Count - 1) / (double)(count - 1))])
            .ToList();
    }
}
