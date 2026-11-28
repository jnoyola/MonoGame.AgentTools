using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Synthapixel.MonoGame.AgentTools.Hud;

internal sealed partial class PixelFont
{
    private const int AtlasColumns = 16;

    /// <summary>Builds a white atlas (premultiplied alpha) and the <see cref="SpriteFont"/> over it.</summary>
    public SpriteFont CreateSpriteFont(GraphicsDevice graphicsDevice)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);

        var count = Characters.Count;
        var rows = (count + AtlasColumns - 1) / AtlasColumns;
        var atlasWidth = AtlasColumns * Width;
        var atlas = new Color[atlasWidth * rows * Height];
        var glyphBounds = new List<Rectangle>(count);
        var cropping = new List<Rectangle>(count);
        var kerning = new List<Vector3>(count);

        for (var i = 0; i < count; i++)
        {
            var originX = i % AtlasColumns * Width;
            var originY = i / AtlasColumns * Height;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (IsLit(i, x, y))
                        atlas[(originY + y) * atlasWidth + originX + x] = Color.White;
                }
            }

            glyphBounds.Add(new Rectangle(originX, originY, Width, Height));
            cropping.Add(new Rectangle(0, 0, Width, Height));
            kerning.Add(new Vector3(0, Width, 0));
        }

        var texture = new Texture2D(graphicsDevice, atlasWidth, rows * Height, mipmap: false, SurfaceFormat.Color);
        texture.SetData(atlas);
        return new SpriteFont(texture, glyphBounds, cropping, [.. Characters], Height, 0, kerning, DefaultCharacter);
    }
}
