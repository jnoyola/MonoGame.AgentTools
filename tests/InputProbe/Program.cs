using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using MonoGame.AgentTools;

var exitAfter = Array.IndexOf(args, "--exit-after") is var i and >= 0 ? double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : double.PositiveInfinity;
using var game = new ProbeGame { IsFixedTimeStep = !args.Contains("--variable"), ExitAfterSeconds = exitAfter };
AgentTools.Attach(game, args, new()
{
    CustomMetrics =
    [
        ("key.space", () => game.Keyboard.IsKeyDown(Keys.Space) ? 1 : 0),
        ("key.f1", () => game.Keyboard.IsKeyDown(Keys.F1) ? 1 : 0),
        ("key.capslock", () => game.Keyboard.CapsLock ? 1 : 0),
        ("events.keydown", () => game.KeyDowns),
        ("events.keyup", () => game.KeyUps),
        ("events.text", () => game.TextInputs),
        ("mouse.x", () => game.Mouse.X),
        ("mouse.y", () => game.Mouse.Y),
        ("mouse.left", () => game.Mouse.LeftButton == ButtonState.Pressed ? 1 : 0),
        ("mouse.right", () => game.Mouse.RightButton == ButtonState.Pressed ? 1 : 0),
        ("mouse.wheel", () => game.Mouse.ScrollWheelValue),
        ("pad.connected", () => game.Pad.IsConnected ? 1 : 0),
        ("pad.a", () => game.Pad.IsButtonDown(Buttons.A) ? 1 : 0),
        ("pad.dpadup", () => game.Pad.IsButtonDown(Buttons.DPadUp) ? 1 : 0),
        ("pad.lx", () => game.Pad.ThumbSticks.Left.X),
        ("pad.ly", () => game.Pad.ThumbSticks.Left.Y),
        ("pad.rt", () => game.Pad.Triggers.Right),
        ("pad2.connected", () => game.Pad2.IsConnected ? 1 : 0),
        ("pad2.b", () => game.Pad2.IsButtonDown(Buttons.B) ? 1 : 0),
        ("dt.ms", () => game.ElapsedMs),
    ],
});
game.Run();

/// <summary>Reads every input device once per Update, as a game does; M moves the mouse with SetPosition.</summary>
internal sealed class ProbeGame : Game
{
    public ProbeGame()
    {
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 320, PreferredBackBufferHeight = 180 };
        Window.KeyDown += (_, _) => ++KeyDowns;
        Window.KeyUp += (_, _) => ++KeyUps;
        Window.TextInput += (_, _) => ++TextInputs;
    }

    public KeyboardState Keyboard { get; private set; }
    public MouseState Mouse { get; private set; }
    public GamePadState Pad { get; private set; }
    public GamePadState Pad2 { get; private set; }
    public int KeyDowns { get; private set; }
    public int KeyUps { get; private set; }
    public int TextInputs { get; private set; }
    public double ElapsedMs { get; private set; }

    /// <summary>Live runs in tests exit on their own after this many seconds.</summary>
    public double ExitAfterSeconds { get; init; } = double.PositiveInfinity;

    protected override void Update(GameTime gameTime)
    {
        if (gameTime.TotalGameTime.TotalSeconds > ExitAfterSeconds)
            Exit();
        Keyboard = Microsoft.Xna.Framework.Input.Keyboard.GetState();
        if (Keyboard.IsKeyDown(Keys.M))
            Microsoft.Xna.Framework.Input.Mouse.SetPosition(100, 50);
        Mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
        Pad = GamePad.GetState(PlayerIndex.One, GamePadDeadZone.None);
        Pad2 = GamePad.GetState(PlayerIndex.Two, GamePadDeadZone.None);
        ElapsedMs = gameTime.ElapsedGameTime.TotalMilliseconds;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Keyboard.IsKeyDown(Keys.Space) ? Color.White : Color.Black);
        base.Draw(gameTime);
    }
}
