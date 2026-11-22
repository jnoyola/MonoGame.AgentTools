using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OneShot;

/// <summary>Procedurally generated textures and immediate-mode drawing helpers. No asset files.</summary>
public static class Gfx
{
    public static Texture2D Pixel, Circle, Glow, Ring, Tri, Diamond, Vignette;
    public static SpriteBatch SB;
    const int S = 64;
    static readonly Vector2 Mid = new(S / 2f, S / 2f);

    public static void Load(GraphicsDevice gd, SpriteBatch sb)
    {
        SB = sb;
        Pixel = new Texture2D(gd, 1, 1);
        Pixel.SetData(new[] { Color.White });

        Circle = Coverage(gd, (x, y) => (x - 32) * (x - 32) + (y - 32) * (y - 32) <= 31.5f * 31.5f);
        Ring = Coverage(gd, (x, y) =>
        {
            float d = MathF.Sqrt((x - 32) * (x - 32) + (y - 32) * (y - 32));
            return d <= 31.5f && d >= 26f;
        });
        Tri = Coverage(gd, (x, y) =>
        {
            // pointing right: base at x=2, tip at x=62
            if (x < 2 || x > 62) return false;
            float half = 30f * (62 - x) / 60f;
            return MathF.Abs(y - 32) <= half;
        });
        Diamond = Coverage(gd, (x, y) => MathF.Abs(x - 32) / 31f + MathF.Abs(y - 32) / 31f <= 1f);

        Glow = new Texture2D(gd, S, S);
        var g = new Color[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), Mid) / 32f;
                float a = MathHelper.Clamp(1f - d, 0f, 1f);
                a = a * a;
                g[y * S + x] = new Color(a, a, a, a);
            }
        Glow.SetData(g);

        const int VW = 160, VH = 90;
        Vignette = new Texture2D(gd, VW, VH);
        var v = new Color[VW * VH];
        for (int y = 0; y < VH; y++)
            for (int x = 0; x < VW; x++)
            {
                float nx = (x + 0.5f) / VW * 2 - 1, ny = (y + 0.5f) / VH * 2 - 1;
                float d = MathF.Sqrt(nx * nx * 0.85f + ny * ny);
                float a = MathHelper.Clamp((d - 0.55f) / 0.75f, 0, 1);
                a = a * a * (3 - 2 * a);
                v[y * VW + x] = new Color(0, 0, 0, a);
            }
        Vignette.SetData(v);

        Font.Build();
    }

    static Texture2D Coverage(GraphicsDevice gd, Func<float, float, bool> inside)
    {
        var tex = new Texture2D(gd, S, S);
        var data = new Color[S * S];
        const int SS = 4;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < SS; sy++)
                    for (int sx = 0; sx < SS; sx++)
                        if (inside(x + (sx + 0.5f) / SS, y + (sy + 0.5f) / SS)) hits++;
                float a = hits / (float)(SS * SS);
                data[y * S + x] = new Color(a, a, a, a);
            }
        tex.SetData(data);
        return tex;
    }

    // ---------- drawing helpers (premultiplied alpha) ----------

    public static void Circ(Vector2 c, float r, Color col)
        => SB.Draw(Circle, c, null, col, 0, Mid, r / 32f, SpriteEffects.None, 0);

    public static void Ellipse(Vector2 c, float rx, float ry, float rot, Color col)
        => SB.Draw(Circle, c, null, col, rot, Mid, new Vector2(rx / 32f, ry / 32f), SpriteEffects.None, 0);

    public static void GlowAt(Vector2 c, float r, Color col)
        => SB.Draw(Glow, c, null, col, 0, Mid, r / 32f, SpriteEffects.None, 0);

    public static void RingAt(Vector2 c, float r, Color col)
        => SB.Draw(Ring, c, null, col, 0, Mid, r / 32f, SpriteEffects.None, 0);

    public static void Line(Vector2 a, Vector2 b, float thick, Color col)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 0.01f) return;
        SB.Draw(Pixel, a, null, col, MathF.Atan2(d.Y, d.X), new Vector2(0, 0.5f), new Vector2(len, thick), SpriteEffects.None, 0);
    }

    public static void Rect(float x, float y, float w, float h, Color col)
        => SB.Draw(Pixel, new Vector2(x, y), null, col, 0, Vector2.Zero, new Vector2(w, h), SpriteEffects.None, 0);

    /// <summary>Rotated rectangle centered at c.</summary>
    public static void Box(Vector2 c, float len, float thick, float rot, Color col)
        => SB.Draw(Pixel, c, null, col, rot, new Vector2(0.5f, 0.5f), new Vector2(len, thick), SpriteEffects.None, 0);

    /// <summary>Triangle whose base is centered at c, pointing along rot.</summary>
    public static void Triangle(Vector2 c, float length, float width, float rot, Color col)
        => SB.Draw(Tri, c, null, col, rot, new Vector2(2, 32), new Vector2(length / 60f, width / 60f), SpriteEffects.None, 0);

    public static void Gem(Vector2 c, float w, float h, float rot, Color col)
        => SB.Draw(Diamond, c, null, col, rot, Mid, new Vector2(w / 62f, h / 62f), SpriteEffects.None, 0);

    public static void Heart(Vector2 c, float size, Color col)
    {
        float r = size * 0.3f;
        Circ(c + new Vector2(-r * 0.95f, -r * 0.35f), r, col);
        Circ(c + new Vector2(r * 0.95f, -r * 0.35f), r, col);
        Triangle(c + new Vector2(0, -r * 0.1f), size * 0.62f, size * 1.12f, MathHelper.PiOver2, col);
    }

    public static void Star(Vector2 c, float size, float rot, Color col)
    {
        Box(c, size, size * 0.18f, rot, col);
        Box(c, size, size * 0.18f, rot + MathHelper.PiOver2, col);
    }

    public static Color Mul(Color c, float a) => c * MathHelper.Clamp(a, 0, 1);
}

/// <summary>5x7 pixel font, defined in code and drawn as horizontal runs of scaled pixels.</summary>
public static class Font
{
    static readonly Dictionary<char, (int x, int y, int len)[]> Glyphs = new();

    static readonly string[] Data =
    {
        "A", ".###.#...##...#######...##...##...#",
        "B", "####.#...##...#####.#...##...#####.",
        "C", ".###.#...##....#....#....#...#.###.",
        "D", "####.#...##...##...##...##...#####.",
        "E", "######....#....####.#....#....#####",
        "F", "######....#....####.#....#....#....",
        "G", ".###.#...##....#.####...##...#.####",
        "H", "#...##...##...#######...##...##...#",
        "I", ".###...#....#....#....#....#...###.",
        "J", "..###...#....#....#.#..#.#..#..##..",
        "K", "#...##..#.#.#..##...#.#..#..#.#...#",
        "L", "#....#....#....#....#....#....#####",
        "M", "#...###.###.#.##.#.##...##...##...#",
        "N", "#...##...###..##.#.##..###...##...#",
        "O", ".###.#...##...##...##...##...#.###.",
        "P", "####.#...##...#####.#....#....#....",
        "Q", ".###.#...##...##...##.#.##..#..##.#",
        "R", "####.#...##...#####.#.#..#..#.#...#",
        "S", ".#####....#.....###.....#....#####.",
        "T", "#####..#....#....#....#....#....#..",
        "U", "#...##...##...##...##...##...#.###.",
        "V", "#...##...##...##...##...#.#.#...#..",
        "W", "#...##...##...##.#.##.#.##.#.#.#.#.",
        "X", "#...##...#.#.#...#...#.#.#...##...#",
        "Y", "#...##...#.#.#...#....#....#....#..",
        "Z", "#####....#...#...#...#...#....#####",
        "0", ".###.#...##..###.#.###..##...#.###.",
        "1", "..#...##....#....#....#....#...###.",
        "2", ".###.#...#....#...#...#...#...#####",
        "3", "####.....#....#.###.....#....#####.",
        "4", "...#...##..#.#.#..#.#####...#....#.",
        "5", "######....####.....#....##...#.###.",
        "6", ".###.#....#....####.#...##...#.###.",
        "7", "#####....#...#...#...#....#....#...",
        "8", ".###.#...##...#.###.#...##...#.###.",
        "9", ".###.#...##...#.####....#....#.###.",
        ".", "..........................##...##..",
        ",", "....................##....#...#....",
        "!", "..#....#....#....#....#.........#..",
        "?", ".###.#...#....#...#...#.........#..",
        ":", "......##...##.........##...##......",
        "-", "...............###.................",
        "+", "......#....#..#####..#....#........",
        "*", "..........#...#.#.#...#...#.#.#...#",
        "/", "....#....#...#...#...#...#....#....",
        "'", "..#....#...#.......................",
        "(", "...#...#...#....#....#.....#.....#.",
        ")", ".#.....#.....#....#....#...#...#...",
        "%", "##..###..#...#...#...#...#..###..##",
        ">", ".#.....#.....#.....#...#...#...#...",
        "<", "...#...#...#...#.....#.....#.....#.",
        "[", ".###..#....#....#....#....#....###.",
        "]", ".###....#....#....#....#....#..###.",
    };

    public static void Build()
    {
        for (int i = 0; i < Data.Length; i += 2)
        {
            char ch = Data[i][0];
            string bits = Data[i + 1];
            // normalise to 35 cells; tolerate small typos by padding/trimming
            if (bits.Length > 35) bits = bits.Substring(bits.Length - 35);
            bits = bits.PadRight(35, '.');
            var runs = new List<(int, int, int)>();
            for (int y = 0; y < 7; y++)
            {
                int x = 0;
                while (x < 5)
                {
                    if (bits[y * 5 + x] == '#')
                    {
                        int s = x;
                        while (x < 5 && bits[y * 5 + x] == '#') x++;
                        runs.Add((s, y, x - s));
                    }
                    else x++;
                }
            }
            Glyphs[ch] = runs.ToArray();
        }
    }

    public static float Measure(string s, float scale) => s.Length == 0 ? 0 : (s.Length * 6 - 1) * scale;

    /// <summary>align: 0 = left, 0.5 = centered, 1 = right. Position is the top edge.</summary>
    public static void Draw(string s, Vector2 pos, float scale, Color col, float align = 0, float shadow = 0)
    {
        if (shadow > 0)
            DrawRaw(s, pos + new Vector2(shadow, shadow), scale, new Color(0, 0, 0) * (col.A / 255f * 0.6f), align);
        DrawRaw(s, pos, scale, col, align);
    }

    static void DrawRaw(string s, Vector2 pos, float scale, Color col, float align)
    {
        float x = pos.X - Measure(s, scale) * align;
        foreach (char c0 in s)
        {
            char c = char.ToUpperInvariant(c0);
            if (Glyphs.TryGetValue(c, out var runs))
                foreach (var (rx, ry, len) in runs)
                    Gfx.SB.Draw(Gfx.Pixel, new Vector2(x + rx * scale, pos.Y + ry * scale), null, col, 0,
                        Vector2.Zero, new Vector2(len * scale, scale), SpriteEffects.None, 0);
            x += 6 * scale;
        }
    }
}
