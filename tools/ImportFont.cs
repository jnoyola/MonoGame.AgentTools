// Imports a PCF bitmap font as the text glyph format read by PixelFont.
// HUD font source: https://github.com/bluescan/proggyfonts (ProggyOriginal/ProggyClean.pcf.gz), MIT license.
// Usage: dotnet run tools/ImportFont.cs -- <ProggyClean.pcf.gz> src/Synthapixel.MonoGame.AgentTools/Hud/Fonts/ProggyClean.txt

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

const int FirstChar = 32;
const int LastChar = 126;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/ImportFont.cs -- <font.pcf[.gz]> <output.txt>");
    return 2;
}

var raw = File.ReadAllBytes(args[0]);
var sha256 = Convert.ToHexStringLower(SHA256.HashData(raw));
var font = new Pcf(raw.AsSpan(0, 2).SequenceEqual((byte[])[0x1F, 0x8B]) ? Decompress(raw) : raw);

var height = font.Ascent + font.Descent;
int? width = null;
var output = new StringBuilder()
    .Append("; ProggyClean, imported by tools/ImportFont.cs from\n")
    .Append("; https://github.com/bluescan/proggyfonts ProggyOriginal/ProggyClean.pcf.gz\n")
    .Append(CultureInfo.InvariantCulture, $"; sha256 {sha256}\n")
    .Append("; Copyright (c) 2004, 2005 Tristan Grimmer. MIT License; see THIRD-PARTY-NOTICES.md.\n")
    .Append(";\n")
    .Append("; Each glyph is a U+XXXX line followed by one line per pixel row: '#' lit, '.' clear.\n");
var glyphs = new StringBuilder();

for (var code = FirstChar; code <= LastChar; code++)
{
    var index = font.GlyphIndex(code) ?? throw new InvalidDataException($"Missing glyph U+{code:X4}.");
    var m = font.Metrics[index];
    width ??= m.Advance;
    if (m.Advance != width)
        throw new InvalidDataException("Font is not monospaced.");

    var cell = new char[height, width.Value];
    for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            cell[y, x] = '.';
    for (var y = 0; y < m.Ascent + m.Descent; y++)
    {
        for (var x = 0; x < m.Right - m.Left; x++)
        {
            if (!font.IsLit(index, x, y))
                continue;
            int px = x + m.Left, py = font.Ascent - m.Ascent + y;
            if (px < 0 || px >= width || py < 0 || py >= height)
                throw new InvalidDataException($"U+{code:X4} draws outside its cell.");
            cell[py, px] = '#';
        }
    }

    glyphs.Append(CultureInfo.InvariantCulture, $"\nU+{code:X4} {(char)code}\n");
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
            glyphs.Append(cell[y, x]);
        glyphs.Append('\n');
    }
}

output.Append(CultureInfo.InvariantCulture, $"\nwidth {width}\nheight {height}\ndefault U+003F\n").Append(glyphs);
File.WriteAllText(args[1], output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"{LastChar - FirstChar + 1} glyphs, {width}x{height}, sha256 {sha256}");
return 0;

static byte[] Decompress(byte[] data)
{
    using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
    using var result = new MemoryStream();
    input.CopyTo(result);
    return result.ToArray();
}

internal readonly record struct GlyphMetrics(int Left, int Right, int Advance, int Ascent, int Descent);

/// <summary>Reader for the X11 Portable Compiled Format, covering the tables a bitmap font needs.</summary>
internal sealed class Pcf
{
    private const int Accelerators = 1 << 1;
    private const int MetricsTable = 1 << 2;
    private const int Bitmaps = 1 << 3;
    private const int BdfEncodings = 1 << 5;
    private const int BdfAccelerators = 1 << 8;

    private readonly byte[] _data;
    private readonly Dictionary<int, int> _tables = [];
    private readonly int[] _bitmapOffsets;
    private readonly int _bitmapStart, _rowPad;
    private readonly bool _msbBitFirst;
    private readonly int _minByte2, _maxByte2, _minByte1, _maxByte1, _encodingFormat, _encodingStart;

    public Pcf(byte[] data)
    {
        _data = data;
        if (!data.AsSpan(0, 4).SequenceEqual("\u0001fcp"u8))
            throw new InvalidDataException("Not a PCF file.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(8 + i * 16);
            _tables[BinaryPrimitives.ReadInt32LittleEndian(entry)] = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
        }

        Metrics = ReadMetrics();

        var (format, p) = Table(Bitmaps);
        var glyphCount = ReadInt(format, p);
        _bitmapOffsets = new int[glyphCount];
        for (var i = 0; i < glyphCount; i++)
            _bitmapOffsets[i] = ReadInt(format, p + 4 + i * 4);
        _bitmapStart = p + 4 + glyphCount * 4 + 16;
        _rowPad = 1 << (format & 3);
        _msbBitFirst = (format & 8) != 0;

        (format, p) = Table(BdfEncodings);
        _minByte2 = ReadShort(format, p);
        _maxByte2 = ReadShort(format, p + 2);
        _minByte1 = ReadShort(format, p + 4);
        _maxByte1 = ReadShort(format, p + 6);
        _encodingStart = p + 10;
        _encodingFormat = format;

        (format, p) = Table(_tables.ContainsKey(BdfAccelerators) ? BdfAccelerators : Accelerators);
        Ascent = ReadInt(format, p + 8);
        Descent = ReadInt(format, p + 12);
    }

    public IReadOnlyList<GlyphMetrics> Metrics { get; }
    public int Ascent { get; }
    public int Descent { get; }

    public int? GlyphIndex(int code)
    {
        int byte1 = code >> 8, byte2 = code & 0xFF;
        if (byte1 < _minByte1 || byte1 > _maxByte1 || byte2 < _minByte2 || byte2 > _maxByte2)
            return null;
        var columns = _maxByte2 - _minByte2 + 1;
        var index = ReadShort(_encodingFormat, _encodingStart + ((byte1 - _minByte1) * columns + byte2 - _minByte2) * 2);
        return index == 0xFFFF ? null : index;
    }

    public bool IsLit(int glyph, int x, int y)
    {
        var m = Metrics[glyph];
        var stride = ((m.Right - m.Left + 7) / 8 + _rowPad - 1) / _rowPad * _rowPad;
        var value = _data[_bitmapStart + _bitmapOffsets[glyph] + y * stride + x / 8];
        var bit = _msbBitFirst ? 7 - x % 8 : x % 8;
        return (value >> bit & 1) != 0;
    }

    private GlyphMetrics[] ReadMetrics()
    {
        var (format, p) = Table(MetricsTable);
        if ((format & 0x100) != 0)
        {
            var compressed = new GlyphMetrics[ReadShort(format, p)];
            for (var i = 0; i < compressed.Length; i++)
            {
                var b = _data.AsSpan(p + 2 + i * 5, 5);
                compressed[i] = new GlyphMetrics(b[0] - 0x80, b[1] - 0x80, b[2] - 0x80, b[3] - 0x80, b[4] - 0x80);
            }
            return compressed;
        }

        var metrics = new GlyphMetrics[ReadInt(format, p)];
        for (var i = 0; i < metrics.Length; i++)
        {
            var q = p + 4 + i * 12;
            metrics[i] = new GlyphMetrics(
                (short)ReadShort(format, q), (short)ReadShort(format, q + 2), (short)ReadShort(format, q + 4),
                (short)ReadShort(format, q + 6), (short)ReadShort(format, q + 8));
        }
        return metrics;
    }

    /// <summary>A table's format and the offset just past its leading format word.</summary>
    private (int Format, int Position) Table(int type)
    {
        if (!_tables.TryGetValue(type, out var offset))
            throw new InvalidDataException($"PCF table {type} is missing.");
        return (BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset)), offset + 4);
    }

    private int ReadInt(int format, int position) => (format & 4) != 0
        ? BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(position))
        : BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(position));

    private int ReadShort(int format, int position) => (format & 4) != 0
        ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(position))
        : BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(position));
}
