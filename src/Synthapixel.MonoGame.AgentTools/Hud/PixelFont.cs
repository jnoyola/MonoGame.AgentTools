using System.Globalization;

namespace Synthapixel.MonoGame.AgentTools.Hud;

/// <summary>
/// A monospaced 1-bit font read from the text glyph format (see <c>Fonts/ProggyClean.txt</c>). It's a
/// pixel font: draw it with point sampling at integer scales. This file has no MonoGame dependency, so
/// the MCP server shares it for labels.
/// </summary>
internal sealed partial class PixelFont
{
    private const string DefaultResource = "Synthapixel.MonoGame.AgentTools.Hud.Fonts.ProggyClean.txt";

    private readonly bool[] _pixels;
    private readonly char[] _characters;

    private PixelFont(int width, int height, char defaultCharacter, char[] characters, bool[] pixels)
    {
        Width = width;
        Height = height;
        DefaultCharacter = defaultCharacter;
        _characters = characters;
        _pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public char DefaultCharacter { get; }
    public IReadOnlyList<char> Characters => _characters;

    public bool IsLit(int glyph, int x, int y) => _pixels[(glyph * Height + y) * Width + x];

    /// <summary>The glyph index for <paramref name="c"/>, or the default character's.</summary>
    public int GlyphIndex(char c)
    {
        var index = Array.IndexOf(_characters, c);
        return index >= 0 ? index : Array.IndexOf(_characters, DefaultCharacter);
    }

    /// <summary>The HUD font, ProggyClean.</summary>
    public static PixelFont LoadDefault()
    {
        using var stream = typeof(PixelFont).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Missing embedded resource {DefaultResource}.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    public static PixelFont Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        int width = 0, height = 0, lineNumber = 0, rowsRemaining = 0;
        char? defaultCharacter = null;
        var characters = new List<char>();
        var pixels = new List<bool>();

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (rowsRemaining > 0)
            {
                if (line.Length != width || line.AsSpan().ContainsAnyExcept(".#"))
                    throw Error(lineNumber, $"expected {width} pixels of '.' or '#'");
                foreach (var c in line)
                    pixels.Add(c == '#');
                rowsRemaining--;
                continue;
            }

            var text = line.Trim();
            if (text.Length == 0 || text[0] == ';')
                continue;

            var space = text.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? text : text[..space];
            var value = space < 0 ? "" : text[(space + 1)..].Trim();

            if (key.StartsWith("U+", StringComparison.Ordinal))
            {
                if (width == 0 || height == 0)
                    throw Error(lineNumber, "width and height must precede the first glyph");
                var character = ParseCodePoint(key, lineNumber);
                if (characters.Contains(character))
                    throw Error(lineNumber, $"duplicate glyph {key}");
                characters.Add(character);
                rowsRemaining = height;
                continue;
            }

            switch (key)
            {
                case "width": width = ParsePositive(value, lineNumber); break;
                case "height": height = ParsePositive(value, lineNumber); break;
                case "default": defaultCharacter = ParseCodePoint(value, lineNumber); break;
                default: throw Error(lineNumber, $"unknown key '{key}'");
            }
        }

        if (rowsRemaining > 0)
            throw Error(lineNumber, "last glyph is incomplete");
        if (characters.Count == 0)
            throw new InvalidDataException("Font has no glyphs.");
        if (defaultCharacter is { } d && !characters.Contains(d))
            throw new InvalidDataException("Font's default character has no glyph.");

        return new PixelFont(width, height, defaultCharacter ?? characters[0], [.. characters], [.. pixels]);
    }

    private static char ParseCodePoint(string text, int lineNumber)
    {
        if (!text.StartsWith("U+", StringComparison.Ordinal)
            || !ushort.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
            || char.IsSurrogate((char)code))
            throw Error(lineNumber, $"invalid code point '{text}'");
        return (char)code;
    }

    private static int ParsePositive(string text, int lineNumber) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw Error(lineNumber, $"expected a positive integer, got '{text}'");

    private static InvalidDataException Error(int lineNumber, string message) =>
        new($"Font line {lineNumber}: {message}.");
}
