using MonoGame.AgentTools.Hud;

namespace MonoGame.AgentTools.Tests;

public class PixelFontTests
{
    [Fact]
    public void Default_font_covers_printable_ascii()
    {
        var font = PixelFont.LoadDefault();

        Assert.Equal((7, 13), (font.Width, font.Height));
        Assert.Equal(Enumerable.Range(32, 95).Select(c => (char)c), font.Characters);
        Assert.Equal('?', font.DefaultCharacter);
    }

    [Fact]
    public void Default_font_glyph_matches_source_art()
    {
        var font = PixelFont.LoadDefault();
        var glyph = font.Characters.ToList().IndexOf('A');
        string[] expected =
        [
            ".......", ".......", "...##..", "...##..", "..#..#.", "..#..#.", "..####.",
            ".#....#", ".#....#", ".#....#", ".......", ".......", ".......",
        ];

        var rows = Enumerable.Range(0, font.Height)
            .Select(y => string.Concat(Enumerable.Range(0, font.Width).Select(x => font.IsLit(glyph, x, y) ? '#' : '.')));
        Assert.Equal(expected, rows);
    }

    [Theory]
    [InlineData("width 2\nheight 1\nsize 3\n", "unknown key")]
    [InlineData("width 2\nheight 1\nU+0041\n#x\n", "expected 2 pixels")]
    [InlineData("width 2\nheight 2\nU+0041\n#.\n", "incomplete")]
    [InlineData("U+0041\n#.\n", "must precede")]
    [InlineData("width 2\nheight 1\nU+0041\n#.\nU+0041\n.#\n", "duplicate")]
    [InlineData("width 2\nheight 1\ndefault U+0042\nU+0041\n#.\n", "default character")]
    [InlineData("width 2\nheight 1\nU+ZZ\n#.\n", "invalid code point")]
    [InlineData("width 0\n", "positive integer")]
    public void Malformed_fonts_are_rejected_with_the_reason(string text, string reason)
    {
        var e = Assert.Throws<InvalidDataException>(() => PixelFont.Parse(new StringReader(text)));
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
    }
}
