using System;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace OneShot;

public class Game1 : Game
{
    enum State { Title, Playing, Paused, GameOver }

    readonly GraphicsDeviceManager _graphics;
    SpriteBatch _sb;
    RenderTarget2D _canvas, _background;
    State _state = State.Title;
    World _world, _attract;
    Bot _attractBot, _bot;
    int _seed;
    float _stateT, _fade = 1, _realT;
    int _best;
    bool _newBest;

    KeyboardState _kb, _pkb;
    MouseState _ms, _pms;
    Vector2 _mouse;
    Rectangle _dest;

    // command line (used for automated playtests): --bot --start --seed N --time T
    readonly bool _argBot, _argStart;
    readonly float _argTime;

    public Game1()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = (int)World.W,
            PreferredBackBufferHeight = (int)World.H,
            SynchronizeWithVerticalRetrace = true,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = false;
        IsFixedTimeStep = true;
        Window.AllowUserResizing = true;
        Window.Title = "Goblin Gold";
        Deactivated += (_, _) => { if (_state == State.Playing && _bot == null) { _state = State.Paused; _stateT = 0; } };

        _seed = Environment.TickCount;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bot": _argBot = true; break;
                case "--start": _argStart = true; break;
                case "--botlog" when i + 1 < args.Length: Bot.LogPath = args[++i]; break;
                case "--seed" when i + 1 < args.Length: int.TryParse(args[++i], out _seed); break;
                case "--time" when i + 1 < args.Length: float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out _argTime); break;
            }
        }
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        Gfx.Load(GraphicsDevice, _sb);
        _canvas = new RenderTarget2D(GraphicsDevice, (int)World.W, (int)World.H);
        _background = Background.Build(GraphicsDevice, _sb);
        _best = LoadBest();
        NewAttract();
        if (_argStart || _argBot) StartGame();
    }

    void NewAttract()
    {
        _attract = new World(_seed + 777, 20) { HideHud = true };
        _attractBot = new Bot(_seed + 3);
    }

    void StartGame()
    {
        _world = new World(_seed++, _argTime);
        _bot = _argBot ? new Bot(_seed) : null;
        _state = State.Playing;
        _stateT = 0;
        _fade = 1;
        _newBest = false;
    }

    bool Pressed(Keys k) => _kb.IsKeyDown(k) && _pkb.IsKeyUp(k);
    bool Clicked => _ms.LeftButton == ButtonState.Pressed && _pms.LeftButton == ButtonState.Released;

    protected override void Update(GameTime gameTime)
    {
        float dt = MathF.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 1 / 20f);
        _realT += dt;
        _stateT += dt;
        _fade = MathF.Max(0, _fade - dt * 2.5f);

        _pkb = _kb; _pms = _ms;
        _kb = Keyboard.GetState();
        _ms = Mouse.GetState();
        UpdateDest();
        _mouse = new Vector2((_ms.X - _dest.X) * World.W / _dest.Width, (_ms.Y - _dest.Y) * World.H / _dest.Height);

        if (Pressed(Keys.F11) || (Pressed(Keys.Enter) && (_kb.IsKeyDown(Keys.LeftAlt) || _kb.IsKeyDown(Keys.RightAlt))))
        {
            _graphics.HardwareModeSwitch = false;
            _graphics.ToggleFullScreen();
        }

        switch (_state)
        {
            case State.Title:
                _attract.Update(dt, _attractBot.Think(_attract, dt));
                if (_attract.P.Dead && _attract.DeathTimer > 2.5f) NewAttract();
                if (Pressed(Keys.Escape)) Exit();
                if (_stateT > 0.25f && (Clicked || Pressed(Keys.Enter) || Pressed(Keys.Space))) StartGame();
                break;

            case State.Playing:
                {
                    GameInput inp = _bot != null ? _bot.Think(_world, dt) : ReadInput();
                    _world.Update(dt, inp);
                    if (Pressed(Keys.Escape) || Pressed(Keys.P)) { _state = State.Paused; _stateT = 0; }
                    if (_world.P.Dead && _world.DeathTimer > 2.3f)
                    {
                        _state = State.GameOver;
                        _stateT = 0;
                        if (_world.Score > _best)
                        {
                            _best = _world.Score;
                            _newBest = true;
                            if (_bot == null) SaveBest(_best);
                        }
                    }
                    break;
                }

            case State.Paused:
                if (Pressed(Keys.Escape) || Pressed(Keys.P) || Clicked) { _state = State.Playing; _stateT = 0; }
                else if (Pressed(Keys.Q)) { _state = State.Title; _stateT = 0; _fade = 1; }
                break;

            case State.GameOver:
                _world.Update(dt, new GameInput { Aim = _world.P.Pos });
                if (_stateT > 1.2f && (Clicked || Pressed(Keys.Enter) || Pressed(Keys.Space) || Pressed(Keys.R))) StartGame();
                else if (_stateT > 0.5f && Pressed(Keys.Escape)) { _state = State.Title; _stateT = 0; _fade = 1; }
                break;
        }
        base.Update(gameTime);
    }

    GameInput ReadInput()
    {
        Vector2 mv = Vector2.Zero;
        if (_kb.IsKeyDown(Keys.W) || _kb.IsKeyDown(Keys.Up)) mv.Y -= 1;
        if (_kb.IsKeyDown(Keys.S) || _kb.IsKeyDown(Keys.Down)) mv.Y += 1;
        if (_kb.IsKeyDown(Keys.A) || _kb.IsKeyDown(Keys.Left)) mv.X -= 1;
        if (_kb.IsKeyDown(Keys.D) || _kb.IsKeyDown(Keys.Right)) mv.X += 1;
        return new GameInput
        {
            Move = mv,
            Aim = _mouse,
            Fire = _ms.LeftButton == ButtonState.Pressed,
            Volley = _ms.RightButton == ButtonState.Pressed || _kb.IsKeyDown(Keys.E),
            DashPressed = Pressed(Keys.Space) || Pressed(Keys.LeftShift) || Pressed(Keys.RightShift),
        };
    }

    void UpdateDest()
    {
        var pp = GraphicsDevice.PresentationParameters;
        int bw = pp.BackBufferWidth, bh = pp.BackBufferHeight;
        float scale = MathF.Min(bw / World.W, bh / World.H);
        int w = (int)(World.W * scale), h = (int)(World.H * scale);
        _dest = new Rectangle((bw - w) / 2, (bh - h) / 2, Math.Max(1, w), Math.Max(1, h));
    }

    // ======================================================================
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.SetRenderTarget(_canvas);
        GraphicsDevice.Clear(new Color(22, 48, 28));

        World w = _state == State.Title ? _attract : _world;
        w.Draw(_sb, _background);

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        _sb.Draw(Gfx.Vignette, new Rectangle(0, 0, (int)World.W, (int)World.H), Color.White * 0.75f);
        if (w.RedFlash > 0) _sb.Draw(Gfx.Vignette, new Rectangle(0, 0, (int)World.W, (int)World.H), new Color(255, 0, 0) * (w.RedFlash * 0.6f));
        if (!w.P.Dead && w.P.Hp == 1 && _state != State.Title)
        {
            float p = 0.5f + 0.5f * MathF.Sin(_realT * 5);
            _sb.Draw(Gfx.Vignette, new Rectangle(0, 0, (int)World.W, (int)World.H), new Color(160, 0, 0) * (0.25f + 0.2f * p));
        }

        switch (_state)
        {
            case State.Title: DrawTitle(); break;
            case State.Playing: w.DrawHud(_sb); break;
            case State.Paused:
                w.DrawHud(_sb);
                Gfx.Rect(0, 0, World.W, World.H, Color.Black * 0.55f);
                Font.Draw("PAUSED", new Vector2(World.W / 2, 260), 8, Color.White, 0.5f, 4);
                Font.Draw("ESC OR CLICK TO RESUME", new Vector2(World.W / 2, 350), 3, new Color(220, 220, 220), 0.5f, 2);
                Font.Draw("Q TO QUIT TO TITLE", new Vector2(World.W / 2, 390), 3, new Color(180, 180, 180), 0.5f, 2);
                break;
            case State.GameOver: DrawGameOver(); break;
        }

        DrawCursor();
        if (_fade > 0) Gfx.Rect(0, 0, World.W, World.H, Color.Black * _fade);
        _sb.End();

        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        _sb.Draw(_canvas, _dest, Color.White);
        _sb.End();

        base.Draw(gameTime);
    }

    void DrawCursor()
    {
        Vector2 m = _mouse;
        bool firing = _state == State.Playing && _world != null && _world.P.FireCd > World.FireInterval * 0.6f;
        float r = firing ? 13 : 10;
        Color c = new(255, 245, 220);
        Gfx.RingAt(m, r + 1.5f, Color.Black * 0.5f);
        Gfx.RingAt(m, r, c);
        for (int i = 0; i < 4; i++)
        {
            Vector2 d = new(MathF.Cos(i * MathHelper.PiOver2), MathF.Sin(i * MathHelper.PiOver2));
            Gfx.Line(m + d * (r - 2), m + d * (r + 6), 2, c);
        }
        Gfx.Circ(m, 1.8f, new Color(255, 90, 70));
    }

    void DrawTitle()
    {
        float W = World.W;
        Gfx.Rect(0, 0, W, World.H, new Color(8, 18, 10) * 0.45f);
        float bob = MathF.Sin(_realT * 2) * 6;

        // title
        string t = "GOBLIN GOLD";
        float s = 12;
        float tw = Font.Measure(t, s);
        float x = W / 2 - tw / 2;
        for (int i = 0; i < t.Length; i++)
        {
            float wave = MathF.Sin(_realT * 3 - i * 0.45f) * 5;
            string ch = t[i].ToString();
            Vector2 p = new(x + i * 6 * s, 120 + bob + wave);
            Font.Draw(ch, p + new Vector2(0, 8), s, new Color(90, 50, 10), 0, 0);
            Font.Draw(ch, p, s, new Color(255, 205, 60), 0, 0);
        }
        Font.Draw("AN ELF ARCHER, A BOW, AND A FOREST FULL OF GREEDY GOBLINS", new Vector2(W / 2, 236 + bob * 0.5f), 2, new Color(200, 235, 190), 0.5f, 2);

        // controls panel
        float px = W / 2 - 260, py = 300;
        Gfx.Rect(px, py, 520, 214, Color.Black * 0.45f);
        Gfx.Rect(px, py, 520, 3, new Color(255, 205, 60) * 0.8f);
        string[,] rows =
        {
            { "WASD", "MOVE" }, { "MOUSE", "AIM" }, { "LEFT CLICK", "SHOOT (HOLD TO KEEP FIRING)" },
            { "RIGHT CLICK", "ARROW VOLLEY" }, { "SPACE", "DASH (DODGES HITS)" }, { "ESC", "PAUSE" },
        };
        for (int i = 0; i < rows.GetLength(0); i++)
        {
            float y = py + 20 + i * 31;
            Font.Draw(rows[i, 0], new Vector2(px + 180, y), 2.5f, new Color(255, 225, 120), 1, 2);
            Font.Draw(rows[i, 1], new Vector2(px + 205, y), 2.5f, new Color(235, 235, 235), 0, 2);
        }
        Font.Draw("SLAY GOBLINS. GRAB THEIR TREASURE. SURVIVE.", new Vector2(W / 2, 540), 2.5f, new Color(255, 240, 200), 0.5f, 2);

        float blink = 0.55f + 0.45f * MathF.Sin(_realT * 5);
        Font.Draw("CLICK TO BEGIN", new Vector2(W / 2, 590), 4, Color.White * blink, 0.5f, 3);
        if (_best > 0) Font.Draw("BEST HOARD: " + _best.ToString("N0"), new Vector2(W / 2, 650), 2.5f, new Color(255, 215, 90), 0.5f, 2);
    }

    void DrawGameOver()
    {
        var w = _world;
        float W = World.W;
        float k = MathF.Min(1, _stateT * 2);
        Gfx.Rect(0, 0, W, World.H, new Color(10, 4, 6) * (0.65f * k));

        float drop = (1 - EaseOut(MathF.Min(1, _stateT * 2.5f))) * -40;
        Font.Draw("YOU HAVE FALLEN", new Vector2(W / 2, 110 + drop), 8, new Color(255, 90, 80) * k, 0.5f, 4);

        float countK = EaseOut(MathHelper.Clamp((_stateT - 0.4f) / 1.3f, 0, 1));
        int shown = (int)MathF.Round(w.Score * countK);
        Font.Draw("TREASURE HOARDED", new Vector2(W / 2, 220), 3, new Color(230, 210, 170) * k, 0.5f, 2);
        float pop = countK >= 1 ? 1 + MathF.Max(0, 0.3f - (_stateT - 1.7f)) : 1;
        float ss = 10 * MathF.Min(pop, 1.3f);
        string sc = shown.ToString("N0");
        Vector2 sp = new(W / 2, 262 - (ss - 10) * 3.5f);
        // coin icon next to the score
        float sw = Font.Measure(sc, ss);
        Vector2 coin = new(W / 2 - sw / 2 - 44, sp.Y + ss * 3.5f);
        Gfx.Circ(coin, 26, new Color(150, 95, 15) * k);
        Gfx.Circ(coin, 22, new Color(255, 205, 50) * k);
        Gfx.Circ(coin + new Vector2(-6, -6), 8, new Color(255, 240, 160) * k);
        Font.Draw(sc, sp, ss, new Color(255, 215, 70) * k, 0.5f, 5);

        if (_newBest && countK >= 1)
        {
            float b = 0.6f + 0.4f * MathF.Sin(_realT * 8);
            Font.Draw("NEW BEST!", new Vector2(W / 2, 350), 4, new Color(120, 255, 170) * b, 0.5f, 3);
        }
        else if (countK >= 1)
            Font.Draw("BEST: " + _best.ToString("N0"), new Vector2(W / 2, 356), 3, new Color(200, 200, 200), 0.5f, 2);

        if (_stateT > 0.9f)
        {
            float a = MathF.Min(1, (_stateT - 0.9f) * 3);
            int secs = (int)w.Time;
            string[] stats =
            {
                "SURVIVED      " + $"{secs / 60}:{secs % 60:00}",
                "REACHED       WAVE " + w.Wave,
                "GOBLINS SLAIN " + w.Kills,
                "BEST COMBO    " + w.BestCombo,
            };
            for (int i = 0; i < stats.Length; i++)
                Font.Draw(stats[i], new Vector2(W / 2 - 170, 420 + i * 32), 2.5f, new Color(230, 230, 230) * a, 0, 2);
        }
        if (_stateT > 1.2f)
        {
            float blink = 0.55f + 0.45f * MathF.Sin(_realT * 5);
            Font.Draw("CLICK TO PLAY AGAIN", new Vector2(W / 2, 580), 4, Color.White * blink, 0.5f, 3);
            Font.Draw("ESC FOR TITLE", new Vector2(W / 2, 630), 2, new Color(180, 180, 180), 0.5f, 2);
        }
    }

    static float EaseOut(float t) => 1 - (1 - t) * (1 - t) * (1 - t);

    // ---------- best score persistence ----------
    static string BestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoblinGold", "best.txt");

    static int LoadBest()
    {
        try { return File.Exists(BestPath) && int.TryParse(File.ReadAllText(BestPath), out int b) ? b : 0; }
        catch { return 0; }
    }

    static void SaveBest(int b)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BestPath)!);
            File.WriteAllText(BestPath, b.ToString());
        }
        catch { /* best score is a nicety; ignore IO failures */ }
    }
}
