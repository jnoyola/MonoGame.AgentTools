using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OneShot;

/// <summary>Bakes the forest clearing into a texture once at load.</summary>
public static class Background
{
    public static RenderTarget2D Build(GraphicsDevice gd, SpriteBatch sb)
    {
        int w = (int)World.W, h = (int)World.H;
        var rt = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        var rng = new Random(1234);
        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        gd.SetRenderTarget(rt);
        gd.Clear(new Color(78, 132, 62));
        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        // soft colour variation
        for (int i = 0; i < 180; i++)
        {
            bool lighter = rng.NextDouble() < 0.5;
            Color c = lighter ? new Color(110, 165, 80) : new Color(55, 105, 50);
            Gfx.GlowAt(new Vector2(R(0, w), R(0, h)), R(60, 170), c * R(0.12f, 0.25f));
        }
        // dirt patches
        for (int i = 0; i < 7; i++)
        {
            Vector2 p = new(R(200, w - 200), R(150, h - 150));
            for (int j = 0; j < 6; j++)
                Gfx.GlowAt(p + new Vector2(R(-50, 50), R(-30, 30)), R(40, 80), new Color(125, 110, 70) * 0.18f);
        }
        // grass tufts
        for (int i = 0; i < 1500; i++)
        {
            Vector2 p = new(R(0, w), R(0, h));
            Color c = rng.NextDouble() < 0.5 ? new Color(60, 112, 48) : new Color(112, 170, 82);
            c *= R(0.5f, 0.9f);
            for (int b = 0; b < 3; b++)
            {
                float a = -MathHelper.PiOver2 + (b - 1) * 0.45f + R(-0.15f, 0.15f);
                float len = R(4, 8);
                Gfx.Line(p, p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * len, 1.5f, c);
            }
        }
        // flowers
        Color[] petals = { new(235, 240, 235), new(225, 150, 195), new(165, 160, 245) };
        for (int i = 0; i < 55; i++)
        {
            Vector2 p = new(R(40, w - 40), R(40, h - 40));
            Color c = petals[rng.Next(petals.Length)] * 0.7f;
            for (int k = 0; k < 5; k++)
            {
                float a = k / 5f * MathHelper.TwoPi;
                Gfx.Circ(p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * 2.2f, 1.8f, c * 0.9f);
            }
            Gfx.Circ(p, 1.2f, new Color(200, 170, 90));
        }
        // stones
        for (int i = 0; i < 10; i++)
        {
            Vector2 p = new(R(60, w - 60), R(60, h - 60));
            float s = R(5, 11);
            Gfx.Ellipse(p + new Vector2(1, s * 0.45f), s * 1.1f, s * 0.55f, 0, Color.Black * 0.2f);
            Gfx.Ellipse(p, s, s * 0.75f, R(-0.3f, 0.3f), new Color(105, 112, 98));
            Gfx.Ellipse(p - new Vector2(s * 0.25f, s * 0.25f), s * 0.5f, s * 0.35f, 0, new Color(130, 138, 120));
        }

        // hedge border
        float e = World.Edge;
        Color hedgeDark = new(28, 62, 34), hedge = new(42, 92, 44), hedgeLight = new(66, 124, 58);
        Gfx.Rect(0, 0, w, e, hedgeDark);
        Gfx.Rect(0, h - e, w, e, hedgeDark);
        Gfx.Rect(0, 0, e, h, hedgeDark);
        Gfx.Rect(w - e, 0, e, h, hedgeDark);
        // inner shadow
        for (int i = 0; i < 10; i++)
        {
            float a = 0.18f * (1 - i / 10f);
            Gfx.Rect(e, e + i, w - 2 * e, 1, Color.Black * a);
            Gfx.Rect(e, h - e - i - 1, w - 2 * e, 1, Color.Black * a * 0.5f);
            Gfx.Rect(e + i, e, 1, h - 2 * e, Color.Black * a * 0.7f);
            Gfx.Rect(w - e - i - 1, e, 1, h - 2 * e, Color.Black * a * 0.7f);
        }
        void Bush(Vector2 p)
        {
            float s = R(15, 24);
            Gfx.Circ(p + new Vector2(2, 4), s, Color.Black * 0.25f);
            Gfx.Circ(p, s, hedge);
            Gfx.Circ(p - new Vector2(s * 0.25f, s * 0.3f), s * 0.6f, hedgeLight);
            if (rng.NextDouble() < 0.15) Gfx.Circ(p + new Vector2(R(-6, 6), R(-6, 6)), 2.5f, new Color(220, 50, 60));
        }
        for (float x = 0; x < w + 20; x += R(18, 28)) { Bush(new Vector2(x, R(8, e - 6))); Bush(new Vector2(x, h - R(8, e - 6))); }
        for (float y = 0; y < h + 20; y += R(18, 28)) { Bush(new Vector2(R(8, e - 6), y)); Bush(new Vector2(w - R(8, e - 6), y)); }

        sb.End();
        gd.SetRenderTarget(null);
        return rt;
    }
}
