using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Fireworks;

/// <summary>
/// A small MonoGame scene for trying MonoGame.AgentTools: rockets rise and burst into sparks. It draws
/// with SpriteBatch and one white pixel (no content pipeline), seeds its randomness so every run is the
/// same, and keeps its particles in a fixed pool so nothing is allocated per frame.
/// </summary>
public sealed class FireworksGame : Game
{
    private const int MaxParticles = 4096;
    private const int SparksPerBurst = 120;
    private const float LaunchInterval = 0.7f;
    private const float Gravity = 260f;
    private static readonly Color NightSky = new(14, 18, 38);

    private static readonly Color[] Palette =
    [
        new(255, 214, 90), new(255, 120, 90), new(140, 220, 255), new(190, 140, 255), new(140, 255, 170),
    ];

    private readonly Particle[] _particles = new Particle[MaxParticles];
    private readonly Random _random = new(1234);
    private SpriteBatch? _spriteBatch;
    private Texture2D? _pixel;
    private float _untilLaunch;

    public FireworksGame()
    {
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 720 };
        IsMouseVisible = true;
        Window.Title = "Fireworks";
    }

    /// <summary>Particles alive this frame, reported to AgentTools as a custom metric.</summary>
    public int ParticleCount { get; private set; }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);

        // SpriteBatch grows its buffers the first time a frame draws more sprites than before; draw the
        // most this game ever will once, invisibly, so that happens here rather than mid-game.
        _spriteBatch.Begin();
        for (int i = 0; i < MaxParticles; ++i)
            _spriteBatch.Draw(_pixel, Vector2.Zero, Color.Transparent);
        _spriteBatch.End();
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape))
            Exit();

        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _untilLaunch -= dt;
        if (_untilLaunch <= 0f)
        {
            _untilLaunch += LaunchInterval;
            var viewport = GraphicsDevice.Viewport;
            var x = viewport.Width * (0.2f + 0.6f * _random.NextSingle());
            Spawn(new Vector2(x, viewport.Height), new Vector2(40f * (_random.NextSingle() - 0.5f), -560f - 120f * _random.NextSingle()),
                Color.White, life: 0.7f + 0.2f * _random.NextSingle(), size: 6f, rocket: true);
        }

        var count = 0;
        for (int i = 0; i < _particles.Length; ++i)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0f)
                continue;
            p.Age += dt;
            p.Velocity.Y += Gravity * dt;
            p.Velocity *= p.Rocket ? 1f : 1f - 1.2f * dt;   // sparks slow down in the air
            p.Position += p.Velocity * dt;
            if (p.Age >= p.Life)
            {
                p.Life = 0f;
                if (p.Rocket)
                    Burst(p.Position);
                continue;
            }
            ++count;
        }
        ParticleCount = count;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(NightSky);
        _spriteBatch!.Begin(blendState: BlendState.Additive);
        foreach (ref readonly var p in _particles.AsSpan())
        {
            if (p.Life <= 0f)
                continue;
            var fade = 1f - p.Age / p.Life;
            var size = p.Size * (p.Rocket ? 1f : 0.5f + fade);
            _spriteBatch.Draw(_pixel, p.Position, null, p.Color * fade, 0f, new Vector2(0.5f), size, SpriteEffects.None, 0f);
        }
        _spriteBatch.End();
        base.Draw(gameTime);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pixel?.Dispose();
            _spriteBatch?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Burst(Vector2 position)
    {
        var color = Palette[_random.Next(Palette.Length)];
        for (int i = 0; i < SparksPerBurst; ++i)
        {
            var angle = MathF.Tau * _random.NextSingle();
            var speed = 60f + 220f * MathF.Sqrt(_random.NextSingle());
            Spawn(position, speed * new Vector2(MathF.Cos(angle), MathF.Sin(angle)), color,
                life: 1.1f + 0.6f * _random.NextSingle(), size: 5f, rocket: false);
        }
    }

    private void Spawn(Vector2 position, Vector2 velocity, Color color, float life, float size, bool rocket)
    {
        for (int i = 0; i < _particles.Length; ++i)
        {
            if (_particles[i].Life > 0f)
                continue;
            _particles[i] = new Particle { Position = position, Velocity = velocity, Color = color, Life = life, Size = size, Rocket = rocket };
            return;
        }
    }

    private struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Color Color;
        public float Age;
        public float Life;
        public float Size;
        public bool Rocket;
    }
}
