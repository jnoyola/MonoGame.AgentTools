namespace MonoGame.AgentTools.Mcp.Imaging;

/// <summary>
/// One palette for a whole animation, built with median cut (Heckbert 1982) over a 15-bit histogram of
/// every frame, as ffmpeg's palettegen does: a palette per frame would make colors jump between frames.
/// Boxes are split where their squared error is largest rather than where most pixels are, so small
/// bright areas (glows, sparks) get colors of their own.
/// </summary>
internal sealed class GifPalette
{
    /// <summary>Colors besides the transparent index.</summary>
    public const int MaxColors = 255;

    private readonly short[] _nearest = new short[1 << 18];   // 6 bits per channel

    private GifPalette(byte[] colors)
    {
        Colors = colors;
        Array.Fill(_nearest, (short)-1);
    }

    /// <summary>RGB triplets.</summary>
    public byte[] Colors { get; }

    public int Count => Colors.Length / 3;

    /// <summary>Adds an image's pixels to <paramref name="histogram"/>.</summary>
    public static void Accumulate(RgbImage image, GifHistogram histogram) => histogram.Add(image);

    public static GifPalette Build(GifHistogram histogram)
    {
        var bins = histogram.Bins();
        if (bins.Length == 0)
            bins = [new Bin(0, 0, 0, 1)];

        var boxes = new List<Box> { new(0, bins.Length, bins) };
        while (boxes.Count < MaxColors)
        {
            var index = -1;
            for (int i = 0; i < boxes.Count; ++i)
                if (boxes[i].Length > 1 && (index < 0 || boxes[i].Error > boxes[index].Error))
                    index = i;
            if (index < 0 || boxes[index].Error <= 0)
                break;
            var (low, high) = boxes[index].Split(bins);
            boxes[index] = low;
            boxes.Add(high);
        }

        var colors = new byte[boxes.Count * 3];
        for (int i = 0; i < boxes.Count; ++i)
            boxes[i].Mean.CopyTo(colors.AsSpan(i * 3));
        return new GifPalette(colors);
    }

    /// <summary>The palette index nearest to a color, cached at 6 bits per channel.</summary>
    public int Nearest(int r, int g, int b)
    {
        var key = (r >> 2) << 12 | (g >> 2) << 6 | b >> 2;
        var cached = _nearest[key];
        if (cached >= 0)
            return cached;

        int cr = (r & ~3) | 2, cg = (g & ~3) | 2, cb = (b & ~3) | 2;
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0, o = 0; o < Colors.Length; ++i, o += 3)
        {
            int dr = Colors[o] - cr, dg = Colors[o + 1] - cg, db = Colors[o + 2] - cb;
            var distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
                (best, bestDistance) = (i, distance);
        }
        _nearest[key] = (short)best;
        return best;
    }

    /// <summary>A histogram bin: the mean color of the pixels in it, and how many there were.</summary>
    internal readonly record struct Bin(double R, double G, double B, long Count)
    {
        public double Channel(int c) => c switch { 0 => R, 1 => G, _ => B };
    }

    /// <summary>A run of histogram bins, with the weighted statistics median cut needs.</summary>
    private sealed class Box
    {
        private readonly double[] _sum = new double[3];
        private readonly double[] _squares = new double[3];
        private readonly double _count;

        public Box(int start, int end, Bin[] bins)
        {
            (Start, End) = (start, end);
            for (int i = start; i < end; ++i)
            {
                var bin = bins[i];
                _count += bin.Count;
                for (int c = 0; c < 3; ++c)
                {
                    var v = bin.Channel(c);
                    _sum[c] += v * bin.Count;
                    _squares[c] += v * v * bin.Count;
                }
            }
        }

        public int Start { get; }
        public int End { get; }
        public int Length => End - Start;

        /// <summary>Total squared error of the box's pixels from its mean (within-bin spread aside).</summary>
        public double Error => Variance(0) + Variance(1) + Variance(2);

        public byte[] Mean => [.. Enumerable.Range(0, 3).Select(c => (byte)Math.Clamp(Math.Round(_sum[c] / _count), 0, 255))];

        private double Variance(int c) => _squares[c] - _sum[c] * _sum[c] / _count;

        /// <summary>Splits along the channel with the most error, at the weighted median.</summary>
        public (Box Low, Box High) Split(Bin[] bins)
        {
            var channel = Enumerable.Range(0, 3).MaxBy(Variance);
            Array.Sort(bins, Start, Length, Comparer<Bin>.Create((a, b) => a.Channel(channel).CompareTo(b.Channel(channel))));
            var half = _count / 2;
            double running = 0;
            var split = Start + 1;
            for (int i = Start; i < End - 1; ++i)
            {
                running += bins[i].Count;
                split = i + 1;
                if (running >= half)
                    break;
            }
            return (new Box(Start, split, bins), new Box(split, End, bins));
        }
    }
}

/// <summary>
/// Pixel counts and color sums in 32768 bins (5 bits per channel), so palette colors are the exact mean
/// of the pixels they stand for rather than bin centers (pure white stays 255).
/// </summary>
internal sealed class GifHistogram
{
    private readonly long[] _count = new long[1 << 15];
    private readonly long[] _sums = new long[3 << 15];

    public void Add(RgbImage image)
    {
        var p = image.Pixels;
        for (int i = 0; i < p.Length; i += 3)
        {
            var bin = (p[i] >> 3) << 10 | (p[i + 1] >> 3) << 5 | p[i + 2] >> 3;
            ++_count[bin];
            _sums[bin * 3] += p[i];
            _sums[bin * 3 + 1] += p[i + 1];
            _sums[bin * 3 + 2] += p[i + 2];
        }
    }

    internal GifPalette.Bin[] Bins()
    {
        var bins = new List<GifPalette.Bin>();
        for (int i = 0; i < _count.Length; ++i)
        {
            var n = _count[i];
            if (n > 0)
                bins.Add(new GifPalette.Bin(_sums[i * 3] / (double)n, _sums[i * 3 + 1] / (double)n, _sums[i * 3 + 2] / (double)n, n));
        }
        return [.. bins];
    }
}

/// <summary>
/// Writes an animated GIF (GIF89a) a frame at a time with one global palette. Frames are mapped to the
/// palette with 8x8 ordered (Bayer) dithering at ffmpeg paletteuse's default strength (bayer_scale 2):
/// the pattern stays put from frame to frame, where error diffusion shimmers in animation. Each frame is
/// stored as the rectangle that changed, with unchanged pixels transparent over the previous frame
/// (ffmpeg's diff_mode=rectangle, gifsicle's transparency optimization); a frame with no change adds its
/// delay to the previous one. LZW coding follows giflib's encoder.
/// </summary>
internal sealed class GifWriter
{
    private const int Transparent = GifPalette.MaxColors;

    private readonly Stream _output;
    private readonly GifPalette _palette;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _shown;     // the image as displayed so far, as palette indices
    private readonly byte[] _current;
    private readonly int[] _dither = new int[64];
    private readonly Lzw _lzw = new();
    private Pending? _pending;

    public GifWriter(Stream output, int width, int height, GifPalette palette)
    {
        (_output, _width, _height, _palette) = (output, width, height, palette);
        _shown = new byte[width * height];
        _current = new byte[width * height];
        for (int p = 0; p < 64; ++p)
        {
            var q = p ^ (p >> 3);
            var value = (p & 4) >> 2 | (q & 4) >> 1 | (p & 2) << 1 | (q & 2) << 2 | (p & 1) << 4 | (q & 1) << 5;
            _dither[p] = (value >> 2) - 8;
        }

        Write("GIF89a"u8);
        WriteShort(width);
        WriteShort(height);
        _output.WriteByte(0xF7);   // global color table of 256 entries, 8-bit color resolution
        _output.WriteByte(0);      // background color
        _output.WriteByte(0);      // square pixels
        Span<byte> table = stackalloc byte[256 * 3];
        table.Clear();
        palette.Colors.CopyTo(table);
        _output.Write(table);
        // Loop forever (the NETSCAPE2.0 application extension).
        Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    /// <summary>Adds a frame shown for <paramref name="delayCentiseconds"/> (2 or more: browsers slow shorter delays down).</summary>
    public void Add(RgbImage image, int delayCentiseconds)
    {
        if (image.Width != _width || image.Height != _height)
            throw new ArgumentException($"Frame is {image.Width}x{image.Height}; the GIF is {_width}x{_height}.", nameof(image));
        var p = image.Pixels;
        for (int y = 0, i = 0; y < _height; ++y)
        {
            for (int x = 0; x < _width; ++x, ++i)
            {
                var d = _dither[(y & 7) << 3 | (x & 7)];
                _current[i] = (byte)_palette.Nearest(Clamp(p[i * 3] + d), Clamp(p[i * 3 + 1] + d), Clamp(p[i * 3 + 2] + d));
            }
        }

        var first = _pending is null;
        int left = _width, top = _height, right = -1, bottom = -1;
        for (int y = 0; y < _height; ++y)
        {
            for (int x = 0; x < _width; ++x)
            {
                var i = y * _width + x;
                if (first || _current[i] != _shown[i])
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }
        if (right < 0)
        {
            _pending = _pending!.Value with { Delay = _pending.Value.Delay + delayCentiseconds };
            return;
        }

        Flush();
        var w = right - left + 1;
        var h = bottom - top + 1;
        var indices = new byte[w * h];
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                var i = (top + y) * _width + left + x;
                indices[y * w + x] = !first && _current[i] == _shown[i] ? (byte)Transparent : _current[i];
                _shown[i] = _current[i];
            }
        }
        _pending = new Pending(left, top, w, h, indices, delayCentiseconds);
    }

    /// <summary>Writes the last frame and the trailer.</summary>
    public void Finish()
    {
        Flush();
        _output.WriteByte(0x3B);
    }

    private void Flush()
    {
        if (_pending is not { } frame)
            return;
        // Graphic control extension: keep the previous image under this one (disposal 1), transparency on.
        Write([0x21, 0xF9, 0x04, 1 << 2 | 1]);
        WriteShort(frame.Delay);
        Write([Transparent, 0x00]);
        _output.WriteByte(0x2C);
        WriteShort(frame.Left);
        WriteShort(frame.Top);
        WriteShort(frame.Width);
        WriteShort(frame.Height);
        _output.WriteByte(0);   // no local color table, not interlaced
        _lzw.Encode(frame.Indices, _output);
        _pending = null;
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

    private void Write(ReadOnlySpan<byte> bytes) => _output.Write(bytes);

    private void WriteShort(int value)
    {
        _output.WriteByte((byte)value);
        _output.WriteByte((byte)(value >> 8));
    }

    private readonly record struct Pending(int Left, int Top, int Width, int Height, byte[] Indices, int Delay);

    /// <summary>GIF's variable-length LZW with 8-bit pixels, as giflib's EGifCompressLine writes it.</summary>
    private sealed class Lzw
    {
        private const int MinCodeSize = 8;
        private const int Clear = 1 << MinCodeSize;
        private const int End = Clear + 1;
        private const int MaxCode = 4095;
        private const int TableSize = 8192;   // power of two, at least twice the 4096 codes

        private readonly int[] _keys = new int[TableSize];
        private readonly short[] _codes = new short[TableSize];
        private readonly byte[] _block = new byte[256];
        private Stream _output = Stream.Null;
        private int _blockLength;
        private int _bits;
        private int _bitCount;
        private int _codeSize;
        private int _nextCode;

        public void Encode(ReadOnlySpan<byte> pixels, Stream output)
        {
            _output = output;
            _output.WriteByte(MinCodeSize);
            (_blockLength, _bits, _bitCount) = (0, 0, 0);
            Reset();
            Emit(Clear);

            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; ++i)
            {
                int pixel = pixels[i];
                var key = prefix << 8 | pixel;
                var slot = Find(key);
                if (_keys[slot] == key)
                {
                    prefix = _codes[slot];
                    continue;
                }
                Emit(prefix);
                if (_nextCode >= MaxCode)
                {
                    Emit(Clear);
                    Reset();
                }
                else
                {
                    _keys[slot] = key;
                    _codes[slot] = (short)_nextCode++;
                }
                prefix = pixel;
            }
            Emit(prefix);
            Emit(End);
            if (_bitCount > 0)
                Put((byte)_bits);
            if (_blockLength > 0)
                FlushBlock();
            _output.WriteByte(0);   // block terminator
        }

        private void Reset()
        {
            Array.Fill(_keys, -1);
            _codeSize = MinCodeSize + 1;
            _nextCode = End + 1;
        }

        private int Find(int key)
        {
            var slot = ((uint)key * 0x9E3779B1u) >> 19;   // 13 bits
            while (_keys[slot] != -1 && _keys[slot] != key)
                slot = (slot + 1) & (TableSize - 1);
            return (int)slot;
        }

        private void Emit(int code)
        {
            _bits |= code << _bitCount;
            _bitCount += _codeSize;
            while (_bitCount >= 8)
            {
                Put((byte)_bits);
                _bits >>= 8;
                _bitCount -= 8;
            }
            // The decoder widens codes once the next code no longer fits; it lags one code behind.
            if (_nextCode >= 1 << _codeSize && _codeSize < 12)
                ++_codeSize;
        }

        private void Put(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == 255)
                FlushBlock();
        }

        private void FlushBlock()
        {
            _output.WriteByte((byte)_blockLength);
            _output.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }
    }
}
