using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;

namespace MonoGame.AgentTools.Imaging;

/// <summary>A grid of downscaled frames, in order, for seeing motion in one image.</summary>
internal sealed class ContactSheet
{
    private const int Gap = 2;

    private readonly int _columns;
    private readonly int _scale;
    private readonly int _tileWidth;
    private readonly int _tileHeight;
    private readonly Color[] _pixels;

    public ContactSheet(int frameWidth, int frameHeight, int tiles, int columns = 4, int maxTileWidth = 320)
    {
        _columns = Math.Min(columns, tiles);
        _scale = Math.Max(1, (int)Math.Ceiling(frameWidth / (double)maxTileWidth));
        _tileWidth = frameWidth / _scale;
        _tileHeight = frameHeight / _scale;
        var rows = (tiles + _columns - 1) / _columns;
        Width = _columns * _tileWidth + (_columns - 1) * Gap;
        Height = rows * _tileHeight + (rows - 1) * Gap;
        _pixels = new Color[Width * Height];
        Array.Fill(_pixels, Color.Black);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Box-filters <paramref name="frame"/> into tile <paramref name="tile"/> (row-major).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Add(int tile, ReadOnlySpan<Color> frame, int frameWidth)
    {
        var originX = tile % _columns * (_tileWidth + Gap);
        var originY = tile / _columns * (_tileHeight + Gap);
        var area = _scale * _scale;
        for (int ty = 0; ty < _tileHeight; ++ty)
        {
            for (int tx = 0; tx < _tileWidth; ++tx)
            {
                int r = 0, g = 0, b = 0;
                for (int sy = 0; sy < _scale; ++sy)
                {
                    var row = frame.Slice((ty * _scale + sy) * frameWidth + tx * _scale, _scale);
                    foreach (var c in row)
                    {
                        r += c.R;
                        g += c.G;
                        b += c.B;
                    }
                }
                _pixels[(originY + ty) * Width + originX + tx] = new Color(r / area, g / area, b / area);
            }
        }
    }

    public void Save(string path)
        => new PngEncoder().Save(path, Width, Height, _pixels);

    /// <summary>Evenly spaced frame numbers (1-based) across <paramref name="frames"/>, first and last included.</summary>
    public static int[] PickFrames(int frames, int tiles)
    {
        var count = Math.Min(frames, tiles);
        if (count == 1)
            return [frames];
        return Enumerable.Range(0, count)
            .Select(i => 1 + (int)Math.Round(i * (frames - 1) / (double)(count - 1)))
            .ToArray();
    }
}
