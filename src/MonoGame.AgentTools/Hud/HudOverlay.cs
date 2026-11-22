using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MonoGame.AgentTools.Hud;

/// <summary>
/// Draws HUD lines in the top-right corner of the backbuffer with <see cref="SpriteBatch"/>, at an
/// integer scale of the pixel font chosen from the backbuffer height. Restores the device state that
/// <see cref="SpriteBatch"/> changes, so the game's next frame renders as it would without the HUD
/// (the same reason Dear ImGui's renderer backends back up and restore graphics state).
/// </summary>
internal sealed class HudOverlay : IDisposable
{
    // 1x up to 4K: a larger window shows more of the game, not bigger debug text.
    private const int ReferenceHeight = 2160;
    private const int Padding = 6;
    // The panel sits as far from the screen edge as its text sits from the panel edge.
    private const int Margin = Padding;
    private const double WidthHoldSeconds = 5;
    private static readonly Color Background = new Color(0, 0, 0) * 0.7f;

    private readonly GraphicsDevice _device;
    private readonly SpriteFont _font;
    private readonly SpriteBatch _batch;
    private readonly Texture2D _pixel;
    private PeakHold _width = new(WidthHoldSeconds);

    public HudOverlay(GraphicsDevice device)
    {
        _device = device;
        _font = PixelFont.LoadDefault().CreateSpriteFont(device);
        _batch = new SpriteBatch(device);
        _pixel = new Texture2D(device, 1, 1);
        _pixel.SetData([Color.White]);
        WarmUp();
    }

    /// <summary>
    /// SpriteBatch grows its vertex and sprite buffers on first use, about 300 KB for a full panel. Doing
    /// that now, at load, keeps it from landing on the heap while the game is being measured.
    /// </summary>
    private void WarmUp()
    {
        const int maxCharacters = 2048;
        var bindings = _device.GetRenderTargets();
        using var target = new RenderTarget2D(_device, 1, 1);
        _device.SetRenderTarget(target);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullCounterClockwise);
        _batch.Draw(_pixel, Vector2.Zero, Color.White);
        _batch.DrawString(_font, new string('M', maxCharacters), Vector2.Zero, Color.White);
        _batch.End();
        _device.SetRenderTargets(bindings);
    }

    /// <summary>Draws the panel and returns the area it covers.</summary>
    public Rectangle Draw(HudLine[] lines, double wallSeconds)
    {
        var pp = _device.PresentationParameters;
        var scale = Math.Max(1, pp.BackBufferHeight / ReferenceHeight);
        var lineHeight = _font.LineSpacing * scale;

        var widest = 0;
        foreach (var line in lines)
            widest = Math.Max(widest, (int)_font.MeasureString(line.Text).X);
        var width = _width.Update(widest, wallSeconds) * scale;
        var panel = new Rectangle(
            pp.BackBufferWidth - width - (Margin + 2 * Padding) * scale,
            Margin * scale,
            width + 2 * Padding * scale,
            lines.Length * lineHeight + 2 * Padding * scale);

        var blend = _device.BlendState;
        var depth = _device.DepthStencilState;
        var rasterizer = _device.RasterizerState;
        var sampler = _device.SamplerStates[0];
        var texture = _device.Textures[0];
        var viewport = _device.Viewport;
        var scissor = _device.ScissorRectangle;

        _device.Viewport = new Viewport(0, 0, pp.BackBufferWidth, pp.BackBufferHeight);
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullCounterClockwise);
        _batch.Draw(_pixel, panel, Background);
        var position = new Vector2(panel.X + Padding * scale, panel.Y + Padding * scale);
        foreach (var line in lines)
        {
            _batch.DrawString(_font, line.Text, position, line.Color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
            position.Y += lineHeight;
        }
        _batch.End();

        _device.BlendState = blend;
        _device.DepthStencilState = depth;
        _device.RasterizerState = rasterizer;
        _device.SamplerStates[0] = sampler;
        _device.Textures[0] = texture;
        _device.Viewport = viewport;
        _device.ScissorRectangle = scissor;
        return panel;
    }

    public void Dispose()
    {
        _batch.Dispose();
        _pixel.Dispose();
        _font.Texture.Dispose();
    }
}
