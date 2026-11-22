using System;
using Microsoft.Xna.Framework;

namespace OneShot;

/// <summary>A simple autopilot used for the title-screen attract mode and automated playtests (--bot).</summary>
public class Bot
{
    float wanderT;
    Vector2 wander;
    readonly Random rng;

    // Showcase rule: after a volley, dash within 5 s; after a dash, volley within 5 s.
    float clock, prevVolleyCd, prevDashCd;
    float pendingDashSince = -1, pendingVolleySince = -1;
    public static string LogPath; // --botlog <file>: records each volley/dash with its game time

    public Bot(int seed) => rng = new Random(seed);

    public GameInput Think(World w, float dt)
    {
        var inp = ThinkBase(w, dt);
        var p = w.P;
        clock += dt;
        if (p.Dead) return inp;

        // detect what actually happened last frame
        if (p.VolleyCd > prevVolleyCd + 0.5f)
        {
            Log(w, "VOLLEY", lastVolleyNote);
            pendingVolleySince = -1;
            if (pendingDashSince < 0) pendingDashSince = clock;
        }
        if (p.DashCd > prevDashCd + 0.3f)
        {
            Log(w, "DASH", lastDashNote);
            pendingDashSince = -1;
            if (pendingVolleySince < 0) pendingVolleySince = clock;
        }
        prevVolleyCd = p.VolleyCd;
        prevDashCd = p.DashCd;

        bool volleyReady = p.VolleyCd <= 0;
        bool pendingV = pendingVolleySince >= 0, pendingD = pendingDashSince >= 0;
        bool volleyDeadline = pendingV && clock - pendingVolleySince > 4.4f;
        bool dashDeadline = pendingD && clock - pendingDashSince > 4.4f;

        // Volley: from a central spot, along the longest open line across the arena (preferring lines with goblins on them).
        if (volleyReady && (pendingV || wantVolley) && (VolleySpot(w, p.Pos) || volleyDeadline))
        {
            Vector2 dir = BestVolleyDir(w, p.Pos, out float free);
            inp.Aim = p.Pos + dir * 300;
            inp.Volley = true;
            lastVolleyNote = $"free={free:0}";
        }
        // Paired dash: wait for open grass so the move reads clearly, then dash toward the most open direction.
        else if (pendingD && clock - pendingDashSince > 0.8f && p.DashCd <= 0 && (OpenSpot(w, p.Pos) || dashDeadline))
        {
            inp.Move = BestOpenDir(w, p.Pos);
            inp.DashPressed = true;
            lastDashNote = $"nearest={NearestGoblin(w, p.Pos):0}";
        }
        else if (inp.DashPressed)
        {
            inp.Move = BestOpenDir(w, p.Pos); // emergency dash: still head for open space
            lastDashNote = $"emergency nearest={NearestGoblin(w, p.Pos):0}";
        }
        return inp;
    }

    bool wantVolley;
    string lastVolleyNote = "", lastDashNote = "";

    /// <summary>A banner is up, or the 30 s wave banner is about to appear.</summary>
    static bool BannerSoon(World w) => w.BannerT > 0 || w.Time % 30f > 27.5f;

    /// <summary>Away from the arena edges, and below the banner while one is showing.</summary>
    static bool InClearView(World w, Vector2 pos)
    {
        const float m = 150;
        if (pos.X < m || pos.X > World.W - m || pos.Y < m || pos.Y > World.H - m) return false;
        return !BannerSoon(w) || pos.Y > World.H * 0.5f;
    }

    /// <summary>Central enough that a volley has room to fly across the arena.</summary>
    static bool VolleySpot(World w, Vector2 pos)
    {
        if (pos.X < 280 || pos.X > World.W - 280 || pos.Y < 220 || pos.Y > World.H - 190) return false;
        return InClearView(w, pos);
    }

    /// <summary>No goblins close by and away from the edges.</summary>
    static bool OpenSpot(World w, Vector2 pos) => InClearView(w, pos) && NearestGoblin(w, pos) > 130;

    static float NearestGoblin(World w, Vector2 pos)
    {
        float best = float.MaxValue;
        foreach (var g in w.Gobs) best = MathF.Min(best, Vector2.Distance(g.Pos, pos) - g.Radius);
        return best;
    }

    /// <summary>Distance from pos to the arena edge along dir.</summary>
    static float FreeRun(Vector2 pos, Vector2 dir)
    {
        float e = World.Edge, t = float.MaxValue;
        if (dir.X > 0.001f) t = MathF.Min(t, (World.W - e - pos.X) / dir.X);
        if (dir.X < -0.001f) t = MathF.Min(t, (e - pos.X) / dir.X);
        if (dir.Y > 0.001f) t = MathF.Min(t, (World.H - e - pos.Y) / dir.Y);
        if (dir.Y < -0.001f) t = MathF.Min(t, (e - pos.Y) / dir.Y);
        return t;
    }

    static Vector2 BestVolleyDir(World w, Vector2 pos, out float bestFree)
    {
        Vector2 best = -Vector2.UnitY;
        float bestScore = float.MinValue;
        bestFree = 0;
        for (int i = 0; i < 32; i++)
        {
            float a = i / 32f * MathHelper.TwoPi;
            Vector2 d = new(MathF.Cos(a), MathF.Sin(a));
            float free = FreeRun(pos, d);
            int hits = 0;
            foreach (var g in w.Gobs)
            {
                Vector2 to = g.Pos - pos;
                float along = Vector2.Dot(to, d);
                if (along < 0 || along > free) continue;
                float side = MathF.Abs(to.X * d.Y - to.Y * d.X);
                if (side < g.Radius + along * 0.27f) hits++; // inside the fan
            }
            // a volley up into the banner would be hidden
            float bannerPenalty = BannerSoon(w) && d.Y < -0.3f ? 3 : 0;
            float score = free / 250f + MathF.Min(hits, 6) * 0.6f - bannerPenalty;
            if (score > bestScore) { bestScore = score; best = d; bestFree = free; }
        }
        return best;
    }

    static Vector2 BestOpenDir(World w, Vector2 pos)
    {
        Vector2 best = Vector2.UnitX;
        float bestScore = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float a = i / 16f * MathHelper.TwoPi;
            Vector2 d = new(MathF.Cos(a), MathF.Sin(a));
            float free = MathF.Min(FreeRun(pos, d), 400);
            Vector2 end = pos + d * 140; // where the dash lands
            float clear = MathF.Min(NearestGoblin(w, end), 200);
            float bannerPenalty = BannerSoon(w) && end.Y < World.H * 0.5f ? 150 : 0;
            float score = clear + free * 0.5f - bannerPenalty;
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    static void Log(World w, string what, string note = "")
    {
        if (LogPath == null) return;
        try { System.IO.File.AppendAllText(LogPath, $"{w.Time:0.00} {what} pos={w.P.Pos.X:0},{w.P.Pos.Y:0} banner={(w.BannerT > 0 ? 1 : 0)} {note}" + System.Environment.NewLine); } catch { }
    }

    GameInput ThinkBase(World w, float dt)
    {
        wantVolley = false;
        var p = w.P;
        var inp = new GameInput { Aim = p.Pos + new Vector2(0, -100) };
        if (p.Dead) return inp;

        Goblin target = null;
        float best = float.MaxValue;
        int near = 0;
        Vector2 flee = Vector2.Zero;
        foreach (var g in w.Gobs)
        {
            if (!g.Entered) continue;
            float d = Vector2.Distance(g.Pos, p.Pos);
            float score = d - (g.Type == GType.Slinger ? 120 : 0);
            if (score < best) { best = score; target = g; }
            if (d < 420) near++;
            float danger = 150 + g.Radius * 2.5f + (g.Type == GType.Chief ? 120 : 0);
            if (d < danger && d > 0.1f) flee += (p.Pos - g.Pos) / d * (1 - d / danger) * 2.2f;
        }
        foreach (var r in w.Rocks)
        {
            Vector2 to = p.Pos - r.Pos;
            float d = to.Length();
            if (d < 160 && Vector2.Dot(r.Vel, to) > 0)
            {
                Vector2 side = new(-r.Vel.Y, r.Vel.X);
                side.Normalize();
                if (Vector2.Dot(side, to) < 0) side = -side;
                flee += side * 2.5f;
            }
        }

        if (target != null)
        {
            float d = Vector2.Distance(target.Pos, p.Pos);
            Vector2 lead = target.Move * (d / 1150f);
            inp.Aim = target.Pos + lead;
            inp.Fire = true;
            wantVolley = near >= 6;
        }

        // gather nearby treasure when safe
        Vector2 seek = Vector2.Zero;
        if (flee.LengthSquared() < 0.6f)
        {
            Treasure tt = null;
            float bd = 450;
            foreach (var t in w.Loot)
            {
                if (BannerSoon(w) && t.Pos.Y < World.H / 3 + 60) continue;
                float d = Vector2.Distance(t.Pos, p.Pos);
                if (d < bd) { bd = d; tt = t; }
            }
            if (tt != null) seek = Vector2.Normalize(tt.Pos - p.Pos);
        }

        // keep toward the centre; while a banner is up, hold the lower two-thirds so the elf stays visible
        Vector2 home;
        if (BannerSoon(w))
        {
            home = (new Vector2(World.W / 2, World.H * 0.68f) - p.Pos) / 150f;
            if (p.Pos.Y < World.H * 0.5f) home.Y += 3f; // get well clear of the banner
        }
        else home = (new Vector2(World.W / 2, World.H / 2) - p.Pos) / 900f;
        // and away from the edges
        const float em = 140;
        if (p.Pos.X < em) home.X += (em - p.Pos.X) / em * 1.5f;
        if (p.Pos.X > World.W - em) home.X -= (p.Pos.X - (World.W - em)) / em * 1.5f;
        if (p.Pos.Y < em) home.Y += (em - p.Pos.Y) / em * 1.5f;
        if (p.Pos.Y > World.H - em) home.Y -= (p.Pos.Y - (World.H - em)) / em * 1.5f;

        wanderT -= dt;
        if (wanderT <= 0)
        {
            wanderT = 1.2f;
            float a = (float)(rng.NextDouble() * Math.PI * 2);
            wander = new Vector2(MathF.Cos(a), MathF.Sin(a)) * 0.3f;
        }

        Vector2 mv = flee + seek + home + wander;
        if (mv.LengthSquared() > 0.04f) inp.Move = Vector2.Normalize(mv);

        // dash out of trouble
        foreach (var g in w.Gobs)
        {
            float d = Vector2.Distance(g.Pos, p.Pos);
            if (d < Player.Radius + g.Radius + 22 && p.DashCd <= 0)
            {
                inp.DashPressed = true;
                break;
            }
        }
        return inp;
    }
}
