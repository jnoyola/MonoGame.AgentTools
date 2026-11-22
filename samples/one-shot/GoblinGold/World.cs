using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OneShot;

/// <summary>The play field: simulation, effects and world/HUD rendering.</summary>
public class World
{
    public const float W = 1280, H = 720;
    public const float Edge = 34;          // hedge thickness around the field

    // --- tuning ---
    const float MaxSpeed = 290, DashSpeed = 860, DashDuration = 0.16f, DashCooldown = 0.75f;
    public const float FireInterval = 0.17f, VolleyCooldown = 4f;
    const float ArrowSpeed = 1150, MagnetRadius = 115;
    const int CoinValue = 10, GemValue = 50, DiamondValue = 250;

    readonly Random rng;
    public readonly Player P = new();
    public readonly List<Goblin> Gobs = new();
    public readonly List<Arrow> Arrows = new();
    public readonly List<Rock> Rocks = new();
    public readonly List<Treasure> Loot = new();
    public readonly List<FloatText> Texts = new();
    public readonly List<SpawnWarning> Warnings = new();
    readonly List<Decal> decals = new();
    readonly Particle[] parts = new Particle[4000];
    int partCount;
    readonly List<Goblin> drawOrder = new();

    public float Time, Trauma, Hitstop, TimeScale = 1, DeathTimer, ScorePulse, BannerT, ComboTimer, RedFlash, RealTime;
    public int Score, Kills, Combo, BestCombo, Wave = 1, TreasureCollected;
    public string Banner = "", BannerSub = "";
    public Color BannerCol = Color.White;
    public float DisplayScore;
    float spawnTimer = 1.2f;
    int chiefCount;
    bool chiefPending;
    public Goblin Chief;
    public bool HideHud;

    static readonly string[] WaveFlavor =
    {
        "MORE GOBLINS SMELL THE GOLD", "THEY ARE GETTING FASTER", "THE HORDE GROWS",
        "THE WAR DRUMS GROW LOUDER", "NO REST FOR THE ELVEN", "THE FOREST CRAWLS WITH THEM",
    };

    static readonly Color[] GemColors =
    {
        new(235, 45, 80), new(60, 130, 255), new(175, 85, 245), new(40, 220, 140), new(255, 150, 40),
    };

    public World(int seed, float startTime = 0)
    {
        rng = new Random(seed);
        P.Pos = new Vector2(W / 2, H / 2 + 40);
        P.Aim = -MathHelper.PiOver2;
        Time = startTime;
        Wave = 1 + (int)(Time / 30);
        chiefCount = (int)(Time / 90);
        spawnTimer = 0.6f;
    }

    // ---------- helpers ----------
    float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
    float RAngle() => (float)rng.NextDouble() * MathHelper.TwoPi;
    static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));
    static Vector2 Perp(Vector2 v) => new(-v.Y, v.X);
    static float Ang(Vector2 v) => MathF.Atan2(v.Y, v.X);

    static Vector2 Approach(Vector2 v, Vector2 target, float maxDelta)
    {
        Vector2 d = target - v;
        float len = d.Length();
        return len <= maxDelta ? target : v + d / len * maxDelta;
    }

    static float LerpAngle(float a, float b, float t)
    {
        float d = MathHelper.WrapAngle(b - a);
        return a + d * MathHelper.Clamp(t, 0, 1);
    }

    static bool SegCircle(Vector2 a, Vector2 b, Vector2 c, float r)
    {
        Vector2 ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 > 0 ? MathHelper.Clamp(Vector2.Dot(c - a, ab) / l2, 0, 1) : 0;
        return Vector2.DistanceSquared(a + ab * t, c) <= r * r;
    }

    public void AddTrauma(float t) => Trauma = MathF.Min(1f, Trauma + t);

    void ShowBanner(string title, string sub, Color col)
    {
        Banner = title; BannerSub = sub; BannerCol = col; BannerT = 3f;
    }

    public float Difficulty => Time;
    float SpeedMult => 1f + MathF.Min(0.85f, Time / 300f);

    // ======================================================================
    //                                UPDATE
    // ======================================================================
    public void Update(float dt, GameInput inp)
    {
        RealTime += dt;
        Trauma = MathF.Max(0, Trauma - dt * 1.5f);
        BannerT -= dt;
        RedFlash = MathF.Max(0, RedFlash - dt * 2.2f);
        ScorePulse = MathF.Max(0, ScorePulse - dt * 4f);
        DisplayScore = MathHelper.Lerp(DisplayScore, Score, 1 - MathF.Exp(-dt * 9));
        if (MathF.Abs(DisplayScore - Score) < 1) DisplayScore = Score;

        if (Hitstop > 0) { Hitstop -= dt; return; }

        if (P.Dead)
        {
            DeathTimer += dt;
            TimeScale = MathHelper.Lerp(TimeScale, 1f, 1 - MathF.Exp(-dt * 1.2f));
        }
        float sdt = dt * TimeScale;
        if (!P.Dead) Time += sdt;

        int wave = 1 + (int)(Time / 30);
        if (wave > Wave && !P.Dead)
        {
            Wave = wave;
            ShowBanner("WAVE " + Wave, WaveFlavor[(Wave - 2) % WaveFlavor.Length], new Color(255, 220, 120));
        }

        UpdatePlayer(sdt, inp);
        UpdateSpawns(sdt);
        UpdateGoblins(sdt);
        UpdateArrows(sdt);
        UpdateRocks(sdt);
        UpdateTreasure(sdt);
        UpdateParticles(sdt);

        for (int i = Texts.Count - 1; i >= 0; i--)
        {
            var t = Texts[i];
            t.Life += sdt;
            t.Pos.Y -= 46 * sdt * (1 - t.Life / t.Max);
            if (t.Life >= t.Max) Texts.RemoveAt(i);
        }
        for (int i = decals.Count - 1; i >= 0; i--)
        {
            var d = decals[i];
            d.Life += sdt;
            if (d.Life >= d.Max) decals.RemoveAt(i); else decals[i] = d;
        }

        if (Combo > 0)
        {
            ComboTimer -= sdt;
            if (ComboTimer <= 0) Combo = 0;
        }
    }

    // ---------------------------------------------------------------- player
    void UpdatePlayer(float dt, GameInput inp)
    {
        var p = P;
        p.Invuln -= dt; p.DashCd -= dt; p.FireCd -= dt; p.VolleyCd -= dt; p.DashBuffer -= dt; p.HurtFlash -= dt;
        p.Kick *= MathF.Exp(-18 * dt);
        if (p.Dead)
        {
            p.Vel *= MathF.Exp(-6 * dt);
            p.Pos += p.Vel * dt;
            return;
        }

        Vector2 toAim = inp.Aim - p.Pos;
        if (toAim.LengthSquared() > 25) p.Aim = Ang(toAim);
        Vector2 aimDir = Dir(p.Aim);

        Vector2 mv = inp.Move;
        if (mv.LengthSquared() > 1) mv.Normalize();
        p.Moving = mv.LengthSquared() > 0.01f;

        if (inp.DashPressed) p.DashBuffer = 0.15f;
        if (p.DashBuffer > 0 && p.DashCd <= 0 && p.DashTime <= 0)
        {
            p.DashBuffer = 0;
            p.DashDir = p.Moving ? Vector2.Normalize(mv) : aimDir;
            p.DashTime = DashDuration;
            p.DashCd = DashCooldown;
            p.Invuln = MathF.Max(p.Invuln, DashDuration + 0.06f);
            for (int i = 0; i < 10; i++)
            {
                float a = Ang(-p.DashDir) + R(-1.2f, 1.2f);
                Emit(0, p.Pos + new Vector2(0, 8), Dir(a) * R(60, 200), R(0.25f, 0.45f), R(5, 9), 0,
                    new Color(220, 210, 170) * 0.7f, new Color(200, 190, 150) * 0f, drag: 5);
            }
        }

        if (p.DashTime > 0)
        {
            p.DashTime -= dt;
            p.Vel = p.DashDir * DashSpeed;
            p.AfterimageT -= dt;
            if (p.AfterimageT <= 0)
            {
                p.AfterimageT = 0.025f;
                Emit(0, p.Pos, Vector2.Zero, 0.22f, 13, 11, new Color(120, 255, 170) * 0.45f, new Color(120, 255, 170) * 0f, add: true);
            }
            if (p.DashTime <= 0) p.Vel = p.DashDir * MaxSpeed;
        }
        else
        {
            Vector2 target = mv * MaxSpeed;
            float accel = p.Moving ? 3000 : 3600;
            p.Vel = Approach(p.Vel, target, accel * dt);
        }
        p.Pos += p.Vel * dt;
        p.Pos.X = MathHelper.Clamp(p.Pos.X, Edge + Player.Radius, W - Edge - Player.Radius);
        p.Pos.Y = MathHelper.Clamp(p.Pos.Y, Edge + Player.Radius, H - Edge - Player.Radius);
        if (p.Moving) p.WalkPhase += dt * 15;

        if (inp.Volley && p.VolleyCd <= 0)
        {
            for (int i = -3; i <= 3; i++) FireArrow(Dir(p.Aim + i * 0.085f), true);
            p.VolleyCd = VolleyCooldown;
            p.FireCd = FireInterval;
            p.Kick = -aimDir * 9;
            AddTrauma(0.22f);
            Vector2 tip = p.Pos + aimDir * 22;
            Emit(5, tip, Vector2.Zero, 0.16f, 60, 10, new Color(150, 255, 200), new Color(150, 255, 200) * 0, add: true);
            Emit(2, tip, Vector2.Zero, 0.25f, 10, 46, new Color(180, 255, 210), new Color(180, 255, 210) * 0, add: true);
        }
        else if (inp.Fire && p.FireCd <= 0)
        {
            FireArrow(aimDir, false);
            p.FireCd = FireInterval;
            p.Kick = -aimDir * 3.5f;
        }
    }

    void FireArrow(Vector2 dir, bool volley)
    {
        var a = new Arrow
        {
            Pos = P.Pos + dir * 16,
            Vel = dir * (volley ? ArrowSpeed * 1.05f : ArrowSpeed),
            Rot = Ang(dir),
            Pierce = volley ? 3 : 1,
            Volley = volley,
        };
        Arrows.Add(a);
        Vector2 tip = P.Pos + dir * 24;
        for (int i = 0; i < 3; i++)
            Emit(1, tip, Dir(Ang(dir) + R(-0.5f, 0.5f)) * R(150, 380), R(0.06f, 0.12f), R(5, 9), 2,
                new Color(255, 250, 220), new Color(255, 220, 140) * 0, add: true, drag: 8);
        Emit(5, tip, Vector2.Zero, 0.07f, 16, 4, new Color(255, 245, 210) * 0.8f, Color.Transparent, add: true);
    }

    public void HurtPlayer(Vector2 src)
    {
        var p = P;
        if (p.Dead || p.Invuln > 0) return;
        p.Hp--;
        p.Invuln = 1.3f;
        p.HurtFlash = 0.25f;
        Vector2 away = p.Pos - src;
        away = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitY;
        p.Vel = away * 650;
        RedFlash = 1;
        AddTrauma(0.55f);
        Hitstop = MathF.Max(Hitstop, 0.1f);
        Combo = 0;
        // shove nearby goblins away for breathing room
        foreach (var g in Gobs)
        {
            Vector2 d = g.Pos - p.Pos;
            float l = d.Length();
            if (l < 150 && l > 0.1f) g.Knock += d / l * (520 * (1 - l / 150) + 120) / MathF.Sqrt(g.Mass);
        }
        Emit(2, p.Pos, Vector2.Zero, 0.35f, 12, 150, new Color(255, 90, 90), new Color(255, 60, 60) * 0, add: true);
        for (int i = 0; i < 14; i++)
            Emit(1, p.Pos, Dir(RAngle()) * R(200, 500), R(0.15f, 0.3f), R(8, 14), 2,
                new Color(255, 120, 110), new Color(255, 50, 50) * 0, add: true, drag: 5);
        if (p.Hp <= 0) KillPlayer();
    }

    void KillPlayer()
    {
        var p = P;
        p.Dead = true;
        TimeScale = 0.2f;
        DeathTimer = 0;
        AddTrauma(1f);
        Hitstop = 0.22f;
        Emit(5, p.Pos, Vector2.Zero, 0.6f, 200, 20, new Color(255, 255, 230), Color.Transparent, add: true);
        Emit(2, p.Pos, Vector2.Zero, 0.7f, 20, 260, new Color(255, 230, 160), new Color(255, 200, 100) * 0, add: true);
        for (int i = 0; i < 40; i++)
        {
            Color c = i % 3 == 0 ? new Color(60, 150, 70) : i % 3 == 1 ? new Color(240, 210, 90) : new Color(255, 255, 220);
            Emit(i % 2 == 0 ? (byte)4 : (byte)0, p.Pos, Dir(RAngle()) * R(80, 420), R(0.6f, 1.4f), R(4, 9), 0, c, c * 0,
                drag: 3, rotV: R(-10, 10));
        }
        foreach (var t in Loot) { t.Magnet = false; t.Vel = Vector2.Zero; }
        Warnings.Clear();
    }

    // ---------------------------------------------------------------- spawns
    void UpdateSpawns(float dt)
    {
        if (P.Dead) return;
        for (int i = Warnings.Count - 1; i >= 0; i--)
        {
            var w = Warnings[i];
            w.T += dt;
            if (w.T >= w.Max)
            {
                SpawnGoblin(w.Type, w.SpawnPos, w.Inward);
                Warnings.RemoveAt(i);
            }
        }

        spawnTimer -= dt;
        int cap = (int)MathF.Min(120, 45 + Time / 5);
        if (spawnTimer <= 0)
        {
            float interval = MathF.Max(0.14f, 1.25f / (1 + Time / 36f));
            spawnTimer = interval * R(0.75f, 1.25f);
            if (Gobs.Count + Warnings.Count < cap)
            {
                int pack = 1;
                if (rng.NextDouble() < Math.Min(0.6, 0.1 + Time / 160)) pack += rng.Next(1, 2 + (int)MathF.Min(4, Time / 45));
                PickEdge(out var edgePos, out var inward, out var spawnPos);
                Vector2 tan = Perp(inward);
                for (int i = 0; i < pack; i++)
                {
                    Vector2 off = tan * (i - (pack - 1) / 2f) * 36;
                    AddWarning(PickType(), edgePos + off, spawnPos + off, inward, 0.75f);
                }
            }
        }

        if (!chiefPending && Chief == null && Time >= 90 * (chiefCount + 1))
        {
            chiefCount++;
            chiefPending = true;
            PickEdge(out var edgePos, out var inward, out var spawnPos);
            AddWarning(GType.Chief, edgePos, spawnPos + inward * -20, inward, 1.8f);
            ShowBanner("THE GOBLIN CHIEF!", "HE WANTS HIS GOLD BACK", new Color(255, 90, 70));
        }
    }

    void AddWarning(GType type, Vector2 edge, Vector2 spawn, Vector2 inward, float time)
    {
        edge.X = MathHelper.Clamp(edge.X, 20, W - 20);
        edge.Y = MathHelper.Clamp(edge.Y, 20, H - 20);
        spawn.X = MathHelper.Clamp(spawn.X, -80, W + 80);
        spawn.Y = MathHelper.Clamp(spawn.Y, -80, H + 80);
        Warnings.Add(new SpawnWarning { Type = type, EdgePos = edge, SpawnPos = spawn, Inward = inward, Max = time });
    }

    void PickEdge(out Vector2 edgePos, out Vector2 inward, out Vector2 spawnPos)
    {
        edgePos = spawnPos = inward = Vector2.Zero;
        for (int tries = 0; tries < 10; tries++)
        {
            int side = rng.Next(4);
            float u = R(0.1f, 0.9f);
            switch (side)
            {
                case 0: edgePos = new(u * W, 18); inward = Vector2.UnitY; break;
                case 1: edgePos = new(u * W, H - 18); inward = -Vector2.UnitY; break;
                case 2: edgePos = new(18, u * H); inward = Vector2.UnitX; break;
                default: edgePos = new(W - 18, u * H); inward = -Vector2.UnitX; break;
            }
            spawnPos = edgePos - inward * 48;
            if (Vector2.Distance(edgePos, P.Pos) > 300) break;
        }
    }

    GType PickType()
    {
        float grunt = 10;
        float runner = Time > 12 ? MathF.Min(6, (Time - 12) / 8) : 0;
        float slinger = Time > 40 ? MathF.Min(3.2f, (Time - 40) / 18) : 0;
        float brute = Time > 55 ? MathF.Min(3f, (Time - 55) / 25) : 0;
        float r = R(0, grunt + runner + slinger + brute);
        if ((r -= grunt) < 0) return GType.Grunt;
        if ((r -= runner) < 0) return GType.Runner;
        if ((r -= slinger) < 0) return GType.Slinger;
        return GType.Brute;
    }

    void SpawnGoblin(GType type, Vector2 pos, Vector2 inward)
    {
        var g = new Goblin { Type = type, Pos = pos, Face = Ang(inward), Phase = RAngle() };
        int tier = (int)(Time / 120);
        switch (type)
        {
            case GType.Grunt: g.Radius = 15; g.Speed = 92; g.Hp = 2 + tier; g.Mass = 1; break;
            case GType.Runner: g.Radius = 11; g.Speed = 172; g.Hp = 1; g.Mass = 0.7f; break;
            case GType.Brute: g.Radius = 25; g.Speed = 58; g.Hp = 8 + tier * 3; g.Mass = 4; break;
            case GType.Slinger: g.Radius = 14; g.Speed = 88; g.Hp = 2 + tier / 2; g.Mass = 1; g.AttackTimer = R(1.2f, 2.2f); break;
            case GType.Chief:
                g.Radius = 38; g.Speed = 50; g.Hp = 40 + 22 * (chiefCount - 1); g.Mass = 14; g.AttackTimer = 2.5f;
                Chief = g; chiefPending = false;
                AddTrauma(0.4f);
                break;
        }
        if (type == GType.Runner) g.Speed *= (1 + (SpeedMult - 1) * 0.4f) * R(0.92f, 1.08f); // never much faster than the elf
        else if (type != GType.Chief) g.Speed *= SpeedMult * R(0.92f, 1.08f);
        g.MaxHp = g.Hp;
        Gobs.Add(g);
    }

    // ---------------------------------------------------------------- goblins
    void UpdateGoblins(float dt)
    {
        foreach (var g in Gobs)
        {
            g.Age += dt; g.Flash -= dt; g.HpBarT -= dt;
            g.Squash = MathF.Max(0, g.Squash - dt * 6);
            Vector2 to = P.Pos - g.Pos;
            float dist = to.Length();
            Vector2 dir = dist > 0.1f ? to / dist : Vector2.UnitX;
            Vector2 desired = Vector2.Zero;

            if (P.Dead)
            {
                desired = Vector2.Zero; // they stop to gloat
            }
            else switch (g.Type)
            {
                case GType.Runner:
                    desired = Vector2.Normalize(dir + Perp(dir) * MathF.Sin(g.Age * 5 + g.Phase) * 0.55f) * g.Speed;
                    break;
                case GType.Slinger:
                    if (!g.Entered || dist > 340) desired = dir;
                    else if (dist < 230) desired = -dir;
                    else desired = Perp(dir) * (MathF.Sin(g.Phase) > 0 ? 0.7f : -0.7f);
                    desired *= g.Speed;
                    if (g.Entered)
                    {
                        if (g.Windup > 0)
                        {
                            g.Windup -= dt;
                            desired *= 0.15f;
                            if (g.Windup <= 0) ThrowRock(g);
                        }
                        else
                        {
                            g.AttackTimer -= dt;
                            if (g.AttackTimer <= 0 && dist < 650)
                            {
                                g.Windup = 0.6f;
                                g.AttackTimer = R(2.0f, 3.0f) / SpeedMult;
                            }
                        }
                    }
                    break;
                case GType.Chief:
                    if (g.ChargeTime > 0)
                    {
                        g.ChargeTime -= dt;
                        desired = g.ChargeDir * 580;
                        if (rng.NextDouble() < 0.6)
                            Emit(0, g.Pos + new Vector2(R(-20, 20), g.Radius * 0.6f), -g.ChargeDir * R(30, 90), R(0.3f, 0.5f), R(6, 11), 2,
                                new Color(200, 180, 130) * 0.6f, new Color(200, 180, 130) * 0, drag: 3);
                    }
                    else if (g.Windup > 0)
                    {
                        g.Windup -= dt;
                        g.ChargeDir = Vector2.Normalize(Vector2.Lerp(g.ChargeDir, dir, 1 - MathF.Exp(-dt * 3)));
                        if (g.Windup <= 0) { g.ChargeTime = 0.75f; AddTrauma(0.25f); }
                    }
                    else
                    {
                        desired = dir * g.Speed;
                        g.AttackTimer -= dt;
                        if (g.AttackTimer <= 0 && g.Entered && dist < 700)
                        {
                            g.Windup = 0.8f;
                            g.ChargeDir = dir;
                            g.AttackTimer = R(3f, 4.5f);
                        }
                    }
                    break;
                default:
                    desired = dir * g.Speed;
                    break;
            }

            g.Move = desired;
            g.Pos += (desired + g.Knock) * dt;
            g.Knock *= MathF.Exp(-9 * dt);
            float faceTarget = g.Type == GType.Chief && (g.ChargeTime > 0 || g.Windup > 0) ? Ang(g.ChargeDir) : Ang(dir);
            g.Face = LerpAngle(g.Face, faceTarget, dt * 10);

            float m = Edge + g.Radius * 0.5f;
            if (!g.Entered)
            {
                if (g.Pos.X > m && g.Pos.X < W - m && g.Pos.Y > m && g.Pos.Y < H - m) g.Entered = true;
            }
            else
            {
                g.Pos.X = MathHelper.Clamp(g.Pos.X, m, W - m);
                g.Pos.Y = MathHelper.Clamp(g.Pos.Y, m, H - m);
            }
        }

        // separation
        for (int i = 0; i < Gobs.Count; i++)
        {
            var a = Gobs[i];
            for (int j = i + 1; j < Gobs.Count; j++)
            {
                var b = Gobs[j];
                Vector2 d = b.Pos - a.Pos;
                float min = (a.Radius + b.Radius) * 0.9f;
                float l2 = d.LengthSquared();
                if (l2 >= min * min || l2 < 0.0001f) continue;
                float l = MathF.Sqrt(l2);
                Vector2 n = d / l;
                float push = (min - l) * 0.5f;
                float tm = a.Mass + b.Mass;
                a.Pos -= n * push * (b.Mass / tm);
                b.Pos += n * push * (a.Mass / tm);
            }
        }

        // contact damage
        if (!P.Dead && P.Invuln <= 0)
        {
            foreach (var g in Gobs)
            {
                float hitR = Player.Radius + g.Radius * 0.78f;
                if (Vector2.DistanceSquared(g.Pos, P.Pos) < hitR * hitR)
                {
                    HurtPlayer(g.Pos);
                    break;
                }
            }
        }
        Gobs.RemoveAll(g => g.Dead);
    }

    void ThrowRock(Goblin g)
    {
        Vector2 target = P.Pos + P.Vel * 0.18f;
        Vector2 d = target - g.Pos;
        if (d.LengthSquared() < 1) return;
        d.Normalize();
        Rocks.Add(new Rock { Pos = g.Pos + d * (g.Radius + 4), Vel = d * 300 });
        Emit(5, g.Pos + d * g.Radius, Vector2.Zero, 0.15f, 26, 6, new Color(200, 120, 255), Color.Transparent, add: true);
    }

    // ---------------------------------------------------------------- arrows
    void UpdateArrows(float dt)
    {
        foreach (var a in Arrows)
        {
            if (a.Stuck)
            {
                a.StuckT += dt;
                if (a.StuckT > 2.2f) a.Dead = true;
                continue;
            }
            Vector2 prev = a.Pos;
            a.Pos += a.Vel * dt;
            a.Life += dt;
            foreach (var g in Gobs)
            {
                if (g.Dead || a.Hit.Contains(g)) continue;
                if (SegCircle(prev, a.Pos, g.Pos, g.Radius + 4))
                {
                    a.Hit.Add(g);
                    HitGoblin(g, a);
                    if (--a.Pierce <= 0) { a.Dead = true; break; }
                }
            }
            if (a.Dead) continue;
            foreach (var r in Rocks)
            {
                if (r.Dead || !SegCircle(prev, a.Pos, r.Pos, 11)) continue;
                r.Dead = true;
                RockBreak(r.Pos, true);
                if (!a.Volley) { a.Dead = true; break; }
            }
            if (a.Dead) continue;
            const float m = Edge - 8;
            if (a.Pos.X < m || a.Pos.X > W - m || a.Pos.Y < m || a.Pos.Y > H - m)
            {
                if (a.Pos.X > -10 && a.Pos.X < W + 10 && a.Pos.Y > -10 && a.Pos.Y < H + 10)
                {
                    a.Stuck = true;
                    a.Pos = new Vector2(MathHelper.Clamp(a.Pos.X, m, W - m), MathHelper.Clamp(a.Pos.Y, m, H - m));
                    for (int i = 0; i < 4; i++)
                        Emit(0, a.Pos, Dir(a.Rot + MathHelper.Pi + R(-1, 1)) * R(40, 120), R(0.2f, 0.35f), R(3, 5), 0,
                            new Color(90, 140, 60), new Color(90, 140, 60) * 0, drag: 6);
                }
                else a.Dead = true;
            }
        }
        Arrows.RemoveAll(a => a.Dead);
    }

    void HitGoblin(Goblin g, Arrow a)
    {
        Vector2 d = Vector2.Normalize(a.Vel);
        g.Hp -= 1;
        g.Flash = 0.09f;
        g.Squash = 1;
        g.HpBarT = 2f;
        float kb = g.Type == GType.Chief && g.ChargeTime > 0 ? 10 : 260;
        g.Knock += d * kb / g.Mass;
        Vector2 hp = g.Pos - d * g.Radius * 0.6f;
        for (int i = 0; i < 7; i++)
            Emit(1, hp, Dir(Ang(-d) + R(-1.1f, 1.1f)) * R(180, 480), R(0.1f, 0.22f), R(6, 12), 2,
                new Color(255, 255, 220), new Color(255, 200, 80) * 0, add: true, drag: 6);
        for (int i = 0; i < 3; i++)
            Emit(4, hp, Dir(Ang(d) + R(-0.8f, 0.8f)) * R(80, 220), R(0.25f, 0.45f), R(3, 5), 1,
                BodyColor(g.Type), BodyColor(g.Type) * 0, drag: 4, rotV: R(-12, 12));
        Emit(5, hp, Vector2.Zero, 0.08f, g.Radius * 2.2f, g.Radius, new Color(255, 255, 230) * 0.9f, Color.Transparent, add: true);
        AddTrauma(0.045f);
        if (g.Hp <= 0) KillGoblin(g, d);
    }

    void KillGoblin(Goblin g, Vector2 d)
    {
        g.Dead = true;
        Kills++;
        Combo++;
        ComboTimer = 2.6f;
        BestCombo = Math.Max(BestCombo, Combo);
        bool chief = g.Type == GType.Chief, brute = g.Type == GType.Brute;
        Hitstop = MathF.Max(Hitstop, chief ? 0.2f : brute ? 0.06f : 0.025f);
        AddTrauma(chief ? 0.9f : brute ? 0.32f : 0.13f);

        float r = g.Radius;
        Color body = BodyColor(g.Type);
        Emit(5, g.Pos, Vector2.Zero, 0.2f, r * 5, r, new Color(255, 240, 180), Color.Transparent, add: true);
        Emit(2, g.Pos, Vector2.Zero, 0.32f, r, r * 4f, new Color(255, 240, 190), new Color(255, 200, 80) * 0, add: true);
        int puffs = 8 + (int)(r * 0.6f);
        for (int i = 0; i < puffs; i++)
            Emit(0, g.Pos + Dir(RAngle()) * R(0, r * 0.6f), Dir(RAngle()) * R(50, 240) + d * 80, R(0.35f, 0.7f), R(r * 0.35f, r * 0.7f), 0,
                body, Color.Lerp(body, new Color(40, 60, 30), 0.5f) * 0, drag: 4.5f);
        for (int i = 0; i < 10 + (int)r; i++)
            Emit(1, g.Pos, Dir(RAngle()) * R(250, 650), R(0.15f, 0.35f), R(8, 16), 3,
                new Color(255, 230, 120), new Color(255, 160, 40) * 0, add: true, drag: 5);
        for (int i = 0; i < 4 + (int)(r / 5); i++)
            Emit(3, g.Pos + Dir(RAngle()) * R(0, r), Dir(RAngle()) * R(20, 90), R(0.35f, 0.6f), R(10, 16), 0,
                new Color(255, 250, 200), new Color(255, 220, 100) * 0, add: true, drag: 3, rotV: R(-4, 4));
        decals.Add(new Decal { Pos = g.Pos + new Vector2(0, r * 0.4f), R = r * 1.1f, Max = 6f, Rot = RAngle(), Col = new Color(45, 75, 30) * 0.22f });

        // ---- treasure burst
        int coins, gems = 0, diamonds = 0;
        switch (g.Type)
        {
            case GType.Runner: coins = rng.Next(2, 4); if (rng.NextDouble() < 0.15) gems++; break;
            case GType.Slinger: coins = 3; gems = rng.Next(1, 3); break;
            case GType.Brute: coins = rng.Next(10, 15); gems = rng.Next(2, 4); break;
            case GType.Chief: coins = 30; gems = 10; diamonds = 2; break;
            default: coins = rng.Next(3, 6); if (rng.NextDouble() < 0.25) gems++; break;
        }
        gems += Math.Min(3, Combo / 8);
        if (Combo >= 20 && Combo % 10 == 0) diamonds++;
        bool heart = chief || (P.Hp < P.MaxHp && rng.NextDouble() < 0.035);
        float sp = 1 + r / 40f;
        for (int i = 0; i < coins; i++) SpawnLoot(TKind.Coin, g.Pos, d, sp);
        for (int i = 0; i < gems; i++) SpawnLoot(TKind.Gem, g.Pos, d, sp);
        for (int i = 0; i < diamonds; i++) SpawnLoot(TKind.Diamond, g.Pos, d, sp);
        if (heart) SpawnLoot(TKind.Heart, g.Pos, d, sp * 0.6f);

        if (chief)
        {
            Chief = null;
            ShowBanner("CHIEF SLAIN!", "HIS HOARD IS YOURS", new Color(255, 220, 90));
            Emit(2, g.Pos, Vector2.Zero, 0.6f, 20, 320, new Color(255, 230, 150), new Color(255, 200, 80) * 0, add: true);
        }
    }

    void SpawnLoot(TKind kind, Vector2 pos, Vector2 bias, float speedScale)
    {
        Vector2 dir = Vector2.Normalize(Dir(RAngle()) + bias * 0.35f);
        var t = new Treasure
        {
            Kind = kind,
            Pos = pos + dir * 4,
            Vel = dir * R(60, 250) * speedScale,
            Z = 6,
            VZ = R(240, 430),
            Spin = RAngle(),
        };
        switch (kind)
        {
            case TKind.Coin: t.Value = CoinValue; t.Col = new Color(255, 205, 50); break;
            case TKind.Gem: t.Value = GemValue; t.Col = GemColors[rng.Next(GemColors.Length)]; break;
            case TKind.Diamond: t.Value = DiamondValue; t.Col = new Color(190, 250, 255); break;
            case TKind.Heart: t.Value = 0; t.Col = new Color(255, 70, 90); break;
        }
        Loot.Add(t);
    }

    void RockBreak(Vector2 pos, bool byArrow)
    {
        for (int i = 0; i < 7; i++)
            Emit(4, pos, Dir(RAngle()) * R(60, 220), R(0.25f, 0.45f), R(3, 6), 1,
                new Color(140, 130, 120), new Color(100, 90, 80) * 0, drag: 4, rotV: R(-12, 12));
        if (byArrow)
        {
            Emit(5, pos, Vector2.Zero, 0.1f, 30, 6, new Color(255, 255, 220), Color.Transparent, add: true);
            AddTrauma(0.04f);
        }
    }

    void UpdateRocks(float dt)
    {
        foreach (var r in Rocks)
        {
            r.Pos += r.Vel * dt;
            r.Spin += dt * 9;
            if (!P.Dead && P.Invuln <= 0 && Vector2.DistanceSquared(r.Pos, P.Pos) < (Player.Radius + 7) * (Player.Radius + 7))
            {
                r.Dead = true;
                RockBreak(r.Pos, false);
                HurtPlayer(r.Pos - r.Vel);
            }
            if (r.Pos.X < Edge - 10 || r.Pos.X > W - Edge + 10 || r.Pos.Y < Edge - 10 || r.Pos.Y > H - Edge + 10)
            {
                r.Dead = true;
                RockBreak(r.Pos, false);
            }
        }
        Rocks.RemoveAll(r => r.Dead);
    }

    // ---------------------------------------------------------------- treasure
    void UpdateTreasure(float dt)
    {
        foreach (var t in Loot)
        {
            t.Age += dt;
            t.Spin += dt * 7;
            if (!t.Magnet)
            {
                t.VZ -= 1250 * dt;
                t.Z += t.VZ * dt;
                if (t.Z <= 0)
                {
                    t.Z = 0;
                    if (t.VZ < -90) { t.VZ = -t.VZ * 0.42f; t.Vel *= 0.6f; }
                    else t.VZ = 0;
                }
                if (t.Z == 0) t.Vel *= MathF.Exp(-6 * dt);
                t.Pos += t.Vel * dt;
                float m = Edge + 6;
                if (t.Pos.X < m) { t.Pos.X = m; t.Vel.X = MathF.Abs(t.Vel.X) * 0.5f; }
                if (t.Pos.X > W - m) { t.Pos.X = W - m; t.Vel.X = -MathF.Abs(t.Vel.X) * 0.5f; }
                if (t.Pos.Y < m) { t.Pos.Y = m; t.Vel.Y = MathF.Abs(t.Vel.Y) * 0.5f; }
                if (t.Pos.Y > H - m) { t.Pos.Y = H - m; t.Vel.Y = -MathF.Abs(t.Vel.Y) * 0.5f; }
                if (!P.Dead && t.Age > 0.35f && Vector2.DistanceSquared(t.Pos, P.Pos) < MagnetRadius * MagnetRadius)
                {
                    if (t.Kind != TKind.Heart || P.Hp < P.MaxHp) t.Magnet = true;
                }
                if (t.Age > 11f)
                {
                    t.Dead = true;
                    Emit(0, t.Pos, new Vector2(0, -30), 0.3f, 6, 0, new Color(255, 255, 255) * 0.6f, Color.Transparent);
                }
            }
            else
            {
                t.MagnetT += dt;
                Vector2 to = P.Pos - t.Pos;
                float d = to.Length();
                float spd = 260 + t.MagnetT * 2400;
                Vector2 want = d > 0.01f ? to / d * spd : Vector2.Zero;
                t.Vel = Vector2.Lerp(t.Vel, want, 1 - MathF.Exp(-dt * 14));
                Vector2 step = t.Vel * dt;
                t.Z = MathHelper.Lerp(t.Z, 8, 1 - MathF.Exp(-dt * 10));
                if (d < 18 || step.LengthSquared() >= d * d || t.MagnetT > 1.2f) Collect(t);
                else t.Pos += step;
            }
        }
        Loot.RemoveAll(t => t.Dead);
    }

    void Collect(Treasure t)
    {
        t.Dead = true;
        if (t.Kind == TKind.Heart)
        {
            P.Hp = Math.Min(P.MaxHp, P.Hp + 1);
            Texts.Add(new FloatText { Pos = P.Pos + new Vector2(0, -34), Text = "+1 HEART", Col = new Color(255, 110, 120), Max = 1.1f, Scale = 2 });
            Emit(2, P.Pos, Vector2.Zero, 0.4f, 10, 70, new Color(255, 120, 140), new Color(255, 80, 100) * 0, add: true);
            return;
        }
        Score += t.Value;
        TreasureCollected++;
        ScorePulse = 1;
        AddPickupText(t.Value, t.Kind == TKind.Diamond ? new Color(200, 250, 255) : new Color(255, 225, 90));
        Vector2 at = P.Pos + Dir(RAngle()) * R(4, 14);
        Emit(3, at, new Vector2(0, -40), R(0.25f, 0.4f), t.Kind == TKind.Coin ? 12 : 20, 0, Color.Lerp(t.Col, Color.White, 0.4f), t.Col * 0, add: true, rotV: 6);
        if (t.Kind != TKind.Coin)
            Emit(2, P.Pos, Vector2.Zero, 0.3f, 8, t.Kind == TKind.Diamond ? 90 : 45, t.Col, t.Col * 0, add: true);
    }

    void AddPickupText(int value, Color col)
    {
        foreach (var ft in Texts)
        {
            if (ft.Amount > 0 && ft.Life < 0.3f)
            {
                ft.Amount += value;
                ft.Text = "+" + ft.Amount;
                ft.Life = 0;
                ft.Pos = P.Pos + new Vector2(0, -32);
                ft.Scale = MathF.Min(4, 2 + ft.Amount / 250f);
                if (value >= DiamondValue) ft.Col = col;
                return;
            }
        }
        Texts.Add(new FloatText { Pos = P.Pos + new Vector2(0, -32), Text = "+" + value, Amount = value, Col = col, Max = 0.9f, Scale = 2 });
    }

    // ---------------------------------------------------------------- particles
    void Emit(byte kind, Vector2 pos, Vector2 vel, float life, float s0, float s1, Color c0, Color c1,
        bool add = false, float drag = 0, float rotV = 0, float gravity = 0)
    {
        if (partCount >= parts.Length) return;
        parts[partCount++] = new Particle
        {
            Kind = kind, Pos = pos, Vel = vel, Max = life, Size0 = s0, Size1 = s1, C0 = c0, C1 = c1,
            Add = add, Drag = drag, RotV = rotV, Rot = rotV != 0 ? RAngle() : MathF.Atan2(vel.Y, vel.X), Gravity = gravity,
        };
    }

    void UpdateParticles(float dt)
    {
        for (int i = 0; i < partCount; i++)
        {
            ref var p = ref parts[i];
            p.Life += dt;
            if (p.Life >= p.Max)
            {
                parts[i] = parts[--partCount];
                i--;
                continue;
            }
            p.Pos += p.Vel * dt;
            if (p.Drag > 0) p.Vel *= MathF.Exp(-p.Drag * dt);
            p.Vel.Y += p.Gravity * dt;
            p.Rot += p.RotV * dt;
        }
    }

    // ======================================================================
    //                                 DRAW
    // ======================================================================
    public Vector2 ShakeOffset()
    {
        float s = Trauma * Trauma * 16f;
        float t = RealTime * 60;
        return new Vector2(MathF.Sin(t * 1.13f) * MathF.Cos(t * 0.71f + 1.3f), MathF.Sin(t * 0.97f + 2.1f) * MathF.Cos(t * 1.37f)) * s + P.Kick;
    }

    public void Draw(SpriteBatch sb, Texture2D background)
    {
        Vector2 shake = ShakeOffset();
        Matrix m = Matrix.CreateTranslation(MathF.Round(shake.X), MathF.Round(shake.Y), 0);

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, null, m);
        sb.Draw(background, Vector2.Zero, Color.White);

        foreach (var d in decals)
        {
            float a = 1 - MathF.Max(0, (d.Life / d.Max - 0.6f) / 0.4f);
            Gfx.Ellipse(d.Pos, d.R, d.R * 0.5f, 0, d.Col * a);
            Gfx.Ellipse(d.Pos + new Vector2(MathF.Cos(d.Rot) * d.R * 0.8f, MathF.Sin(d.Rot) * d.R * 0.35f), d.R * 0.3f, d.R * 0.18f, 0, d.Col * a);
        }

        DrawWarnings();

        // shadows
        foreach (var t in Loot)
        {
            float s = 1f / (1 + t.Z / 40f);
            float w = t.Kind == TKind.Diamond ? 10 : 7;
            Gfx.Ellipse(t.Pos + new Vector2(0, 3), w * s, w * 0.45f * s, 0, Color.Black * 0.28f);
        }
        foreach (var g in Gobs)
            Gfx.Ellipse(g.Pos + new Vector2(0, g.Radius * 0.55f), g.Radius * 1.05f, g.Radius * 0.5f, 0, Color.Black * 0.3f * SpawnScale(g));
        if (!P.Dead || DeathTimer < 0.05f)
            Gfx.Ellipse(P.Pos + new Vector2(0, 10), 14, 6.5f, 0, Color.Black * 0.32f);

        foreach (var t in Loot) DrawTreasure(t);
        foreach (var a in Arrows) if (a.Stuck) DrawArrow(a, MathHelper.Clamp(1 - (a.StuckT - 1.6f) / 0.6f, 0, 1));

        // y-sorted characters
        drawOrder.Clear();
        drawOrder.AddRange(Gobs);
        drawOrder.Sort((a, b) => a.Pos.Y.CompareTo(b.Pos.Y));
        bool playerDrawn = P.Dead;
        foreach (var g in drawOrder)
        {
            if (!playerDrawn && P.Pos.Y < g.Pos.Y) { DrawPlayer(); playerDrawn = true; }
            DrawGoblin(g);
        }
        if (!playerDrawn) DrawPlayer();

        foreach (var a in Arrows) if (!a.Stuck) DrawArrow(a, 1);
        foreach (var r in Rocks)
        {
            Gfx.Ellipse(r.Pos + new Vector2(0, 8), 6, 3, 0, Color.Black * 0.25f);
            Gfx.Circ(r.Pos, 8, new Color(70, 55, 60));
            Gfx.Circ(r.Pos, 6.5f, new Color(135, 120, 115));
            Gfx.Circ(r.Pos + Dir(r.Spin) * 2 + new Vector2(-1, -1), 2.2f, new Color(185, 175, 165));
        }

        DrawParticles(false);
        sb.End();

        sb.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp, null, null, null, m);
        foreach (var a in Arrows)
        {
            if (a.Stuck) continue;
            Vector2 d = Vector2.Normalize(a.Vel);
            float len = MathF.Min(a.Life * ArrowSpeed, a.Volley ? 70 : 52);
            Color tc = a.Volley ? new Color(120, 255, 180) : new Color(255, 240, 200);
            Gfx.Line(a.Pos - d * len, a.Pos - d * 10, a.Volley ? 3 : 2, tc * 0.35f);
        }
        foreach (var t in Loot)
        {
            if (t.Kind == TKind.Coin) continue;
            float pulse = 0.75f + 0.25f * MathF.Sin(t.Age * 6);
            Gfx.GlowAt(t.Pos - new Vector2(0, t.Z + 4), t.Kind == TKind.Diamond ? 34 : 20, t.Col * 0.35f * pulse);
        }
        foreach (var r in Rocks) Gfx.GlowAt(r.Pos, 18, new Color(170, 90, 255) * 0.35f);
        foreach (var g in Gobs)
        {
            if (g.Type == GType.Slinger && g.Windup > 0)
            {
                float k = 1 - g.Windup / 0.6f;
                Vector2 hand = g.Pos + Dir(g.Face) * g.Radius * 0.9f + Perp(Dir(g.Face)) * g.Radius * 0.7f;
                Gfx.GlowAt(hand, 10 + k * 18, new Color(190, 110, 255) * (0.4f + k * 0.6f));
            }
        }
        DrawParticles(true);
        sb.End();

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, null, m);
        foreach (var ft in Texts)
        {
            float t = ft.Life / ft.Max;
            float pop = ft.Life < 0.08f ? 1 + (1 - ft.Life / 0.08f) * 0.5f : 1;
            float a = t < 0.7f ? 1 : 1 - (t - 0.7f) / 0.3f;
            Font.Draw(ft.Text, ft.Pos, MathF.Round(ft.Scale * pop * 2) / 2, ft.Col * a, 0.5f, 2);
        }
        sb.End();
    }

    void DrawParticles(bool additive)
    {
        for (int i = 0; i < partCount; i++)
        {
            ref var p = ref parts[i];
            if (p.Add != additive) continue;
            float t = p.Life / p.Max;
            float s = MathHelper.Lerp(p.Size0, p.Size1, t);
            Color c = Color.Lerp(p.C0, p.C1, t);
            switch (p.Kind)
            {
                case 0: Gfx.Circ(p.Pos, s, c); break;
                case 1:
                    {
                        Vector2 v = p.Vel;
                        float l = v.Length();
                        if (l < 1) break;
                        Vector2 d = v / l;
                        float len = MathF.Max(2, s * MathF.Min(1, l / 200f));
                        Gfx.Line(p.Pos - d * len, p.Pos, MathF.Max(1, p.Size1 + 1.5f * (1 - t)), c);
                        break;
                    }
                case 2: Gfx.RingAt(p.Pos, s, c); break;
                case 3: Gfx.Star(p.Pos, s, p.Rot, c); break;
                case 4: Gfx.Box(p.Pos, s, s, p.Rot, c); break;
                case 5: Gfx.GlowAt(p.Pos, s, c); break;
            }
        }
    }

    void DrawWarnings()
    {
        foreach (var w in Warnings)
        {
            float k = w.T / w.Max;
            float pulse = 0.55f + 0.45f * MathF.Sin(w.T * 22);
            bool chief = w.Type == GType.Chief;
            float size = chief ? 30 : w.Type == GType.Brute ? 22 : 15;
            Color c = chief ? new Color(255, 60, 40) : new Color(255, 80, 60);
            float a = MathF.Min(1, k * 4) * pulse;
            float rot = Ang(w.Inward);
            Vector2 p = w.EdgePos + w.Inward * (4 + k * 6);
            Gfx.Triangle(p - w.Inward * 2, size + 4, size * 1.1f + 6, rot, Color.Black * 0.35f * a);
            Gfx.Triangle(p, size, size * 1.1f, rot, c * a);
            if (chief) Font.Draw("!", p + w.Inward * (size + 8) - new Vector2(0, 10), 3, c * a, 0.5f, 2);
        }
    }

    float SpawnScale(Goblin g)
    {
        float t = MathHelper.Clamp(g.Age / 0.22f, 0, 1);
        // back-out ease
        const float c1 = 1.9f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }

    // ---------------------------------------------------------------- characters
    void DrawPlayer()
    {
        var p = P;
        if (p.Dead) return;
        bool blink = p.Invuln > 0 && p.DashTime <= 0 && ((int)(p.Invuln * 18) % 2 == 0);
        float alpha = blink ? 0.35f : 1f;
        bool white = p.HurtFlash > 0.15f;
        Color C(Color c) => (white ? new Color(255, 255, 255) : c) * alpha;

        Vector2 d = Dir(p.Aim), n = Perp(d);
        float bob = p.Moving ? MathF.Sin(p.WalkPhase) * 1.5f : 0;
        Vector2 pos = p.Pos + new Vector2(0, -2 + bob * 0.5f);

        // feet
        if (p.Moving)
        {
            Vector2 mvd = p.Vel.LengthSquared() > 1 ? Vector2.Normalize(p.Vel) : d;
            Vector2 mp = Perp(mvd);
            float sw = MathF.Sin(p.WalkPhase) * 6;
            Gfx.Ellipse(pos + mp * 5 + mvd * sw + new Vector2(0, 6), 4, 3, Ang(mvd), C(new Color(90, 60, 35)));
            Gfx.Ellipse(pos - mp * 5 - mvd * sw + new Vector2(0, 6), 4, 3, Ang(mvd), C(new Color(90, 60, 35)));
        }

        // cloak
        Gfx.Ellipse(pos - d * 4, 14, 13, p.Aim, C(new Color(28, 85, 48)));
        // quiver
        Gfx.Box(pos - d * 8 + n * 6, 16, 6, p.Aim + 0.5f, C(new Color(110, 70, 40)));
        Gfx.Circ(pos - d * 14 + n * 9, 2, C(new Color(230, 230, 230)));
        Gfx.Circ(pos - d * 13 + n * 5, 2, C(new Color(220, 60, 60)));
        // body / tunic
        Gfx.Circ(pos, 11.5f, C(new Color(64, 158, 74)));
        Gfx.Circ(pos + d * 2 - n * 2, 7, C(new Color(84, 182, 92)));
        // belt
        Gfx.Box(pos + d * 1, 3, 20, p.Aim, C(new Color(120, 80, 40)));

        // bow arm
        float pull = MathHelper.Clamp(1 - p.FireCd / FireInterval, 0, 1);
        Vector2 bowC = pos + d * 16;
        Vector2 prevPt = Vector2.Zero;
        Color bowCol = C(new Color(140, 88, 40));
        for (int i = 0; i <= 8; i++)
        {
            float t = i / 4f - 1;
            Vector2 pt = bowC + n * t * 15 + d * ((1 - t * t) * 5 - 2);
            if (i > 0) Gfx.Line(prevPt, pt, 3.2f, bowCol);
            prevPt = pt;
        }
        Vector2 tipA = bowC + n * 15 - d * 2, tipB = bowC - n * 15 - d * 2;
        Vector2 pullPt = bowC - d * (1 + pull * 10);
        Gfx.Line(tipA, pullPt, 1.2f, C(new Color(235, 230, 210)));
        Gfx.Line(tipB, pullPt, 1.2f, C(new Color(235, 230, 210)));
        if (pull > 0.35f)
        {
            // nocked arrow
            Gfx.Line(pullPt, bowC + d * 8, 2, C(new Color(150, 110, 60)));
            Gfx.Triangle(bowC + d * 7, 7, 6, p.Aim, C(new Color(200, 205, 215)));
        }
        // hands
        Gfx.Circ(bowC + d * 1, 3.2f, C(new Color(240, 200, 160)));
        Gfx.Circ(pullPt, 3.2f, C(new Color(240, 200, 160)));

        // head: ears, face, hair
        Vector2 head = pos + d * 1.5f;
        Gfx.Triangle(head + n * 6, 10, 5, p.Aim + MathHelper.PiOver2 + 0.6f, C(new Color(232, 188, 150)));
        Gfx.Triangle(head - n * 6, 10, 5, p.Aim - MathHelper.PiOver2 - 0.6f, C(new Color(232, 188, 150)));
        Gfx.Circ(head, 8.2f, C(new Color(242, 204, 166)));
        Gfx.Circ(head - d * 2.6f, 7.8f, C(new Color(236, 206, 96)));
        Gfx.Circ(head - d * 4.5f - n * 2, 4.5f, C(new Color(250, 226, 130)));
        // eyes (peek forward)
        Gfx.Circ(head + d * 5.5f + n * 2.6f, 1.3f, C(new Color(40, 70, 50)));
        Gfx.Circ(head + d * 5.5f - n * 2.6f, 1.3f, C(new Color(40, 70, 50)));

        // dash cooldown pip
        if (p.DashCd > 0)
        {
            float k = 1 - p.DashCd / DashCooldown;
            Gfx.Rect(p.Pos.X - 12, p.Pos.Y + 20, 24, 3, Color.Black * 0.4f);
            Gfx.Rect(p.Pos.X - 12, p.Pos.Y + 20, 24 * k, 3, new Color(140, 255, 190) * 0.9f);
        }
    }

    public static Color BodyColor(GType t) => t switch
    {
        GType.Runner => new Color(150, 192, 72),
        GType.Brute => new Color(76, 128, 56),
        GType.Slinger => new Color(112, 168, 90),
        GType.Chief => new Color(92, 150, 56),
        _ => new Color(100, 172, 62),
    };

    void DrawGoblin(Goblin g)
    {
        bool flash = g.Flash > 0;
        Color C(Color c) => flash ? Color.White : c;
        float sc = SpawnScale(g) * (1 + g.Squash * 0.22f);
        float r = g.Radius * sc;
        Vector2 d = Dir(g.Face), n = Perp(d);
        float walk = g.Move.LengthSquared() > 1 ? MathF.Sin(g.Age * (g.Type == GType.Runner ? 22 : 13) + g.Phase) : 0;
        float hop = P.Dead ? MathF.Abs(MathF.Sin(g.Age * 9 + g.Phase)) * 6 : 0; // celebrating
        Vector2 pos = g.Pos + n * walk * r * 0.08f - new Vector2(0, MathF.Abs(walk) * 1.5f + hop);
        if (g.Type == GType.Chief && g.Windup > 0) pos += new Vector2(MathF.Sin(g.Age * 80) * 2.5f, 0);

        Color body = BodyColor(g.Type);
        Color dark = Color.Lerp(body, Color.Black, 0.35f);
        Color light = Color.Lerp(body, Color.White, 0.2f);

        // chief charge telegraph
        if (g.Type == GType.Chief && g.Windup > 0)
        {
            float k = 1 - g.Windup / 0.8f;
            Gfx.Line(g.Pos, g.Pos + g.ChargeDir * 460, 6 + k * 30, new Color(255, 40, 30) * (0.12f + 0.2f * k));
        }

        // feet
        float sw = walk * r * 0.35f;
        Gfx.Ellipse(pos + n * r * 0.5f + d * sw + new Vector2(0, r * 0.4f), r * 0.28f, r * 0.2f, g.Face, C(dark));
        Gfx.Ellipse(pos - n * r * 0.5f - d * sw + new Vector2(0, r * 0.4f), r * 0.28f, r * 0.2f, g.Face, C(dark));

        if (g.Type == GType.Chief)
            Gfx.Ellipse(pos - d * r * 0.35f, r * 1.05f, r * 1.0f, g.Face, C(new Color(150, 30, 40)));
        if (g.Type == GType.Runner)
        {
            // bandana tails
            Vector2 back = pos - d * r * 0.8f;
            Gfx.Line(back, back - d * r * 0.9f + n * walk * 3, 3, C(new Color(205, 45, 45)));
            Gfx.Line(back, back - d * r * 0.8f + n * (4 - walk * 3), 3, C(new Color(185, 35, 35)));
        }

        // ears
        Color earC = C(Color.Lerp(body, new Color(200, 150, 120), 0.15f));
        Gfx.Triangle(pos + n * r * 0.65f - d * r * 0.1f, r * 0.95f, r * 0.55f, g.Face + MathHelper.PiOver2 + 0.35f, earC);
        Gfx.Triangle(pos - n * r * 0.65f - d * r * 0.1f, r * 0.95f, r * 0.55f, g.Face - MathHelper.PiOver2 - 0.35f, earC);

        // body
        Gfx.Circ(pos, r, C(dark));
        Gfx.Circ(pos - new Vector2(0, r * 0.06f), r * 0.92f, C(body));
        Gfx.Circ(pos + d * r * 0.18f - new Vector2(0, r * 0.2f), r * 0.55f, C(light));

        if (g.Type == GType.Slinger)
        {
            Gfx.Circ(pos - d * r * 0.25f, r * 0.85f, C(new Color(105, 55, 150)));
            Gfx.Circ(pos - d * r * 0.4f - new Vector2(0, r * 0.15f), r * 0.5f, C(new Color(130, 75, 175)));
        }
        if (g.Type == GType.Runner)
            Gfx.Box(pos - d * r * 0.35f, r * 0.45f, r * 1.7f, g.Face, C(new Color(205, 45, 45)));

        // eyes look at player
        Color eyeC = g.Type == GType.Chief || (g.Type == GType.Brute) ? new Color(255, 120, 60) : new Color(255, 236, 110);
        for (int s = -1; s <= 1; s += 2)
        {
            Vector2 e = pos + d * r * 0.5f + n * r * 0.33f * s;
            Gfx.Circ(e, r * 0.21f, C(eyeC));
            Gfx.Circ(e + d * r * 0.08f, r * 0.1f, C(new Color(20, 15, 10)));
        }
        // brow / teeth
        Gfx.Box(pos + d * r * 0.62f, r * 0.12f, r * 0.9f, g.Face, C(dark));
        Gfx.Triangle(pos + d * r * 0.78f + n * r * 0.18f, r * 0.22f, r * 0.16f, g.Face, C(Color.White));
        Gfx.Triangle(pos + d * r * 0.78f - n * r * 0.18f, r * 0.22f, r * 0.16f, g.Face, C(Color.White));

        if (g.Type == GType.Brute)
        {
            Vector2 hand = pos + n * r * 0.95f + d * r * 0.2f;
            float clubRot = g.Face + 0.25f + walk * 0.25f;
            Vector2 cd = Dir(clubRot);
            Gfx.Line(hand, hand + cd * r * 1.25f, r * 0.22f, C(new Color(110, 72, 38)));
            Gfx.Circ(hand + cd * r * 1.25f, r * 0.3f, C(new Color(125, 82, 44)));
            Gfx.Circ(hand + cd * r * 1.25f + Perp(cd) * r * 0.25f, r * 0.08f, C(new Color(200, 200, 200)));
            Gfx.Circ(hand + cd * r * 1.45f, r * 0.08f, C(new Color(200, 200, 200)));
            Gfx.Circ(hand, r * 0.2f, C(body));
        }
        if (g.Type == GType.Slinger)
        {
            Vector2 hand = pos + d * r * 0.9f + n * r * 0.7f;
            Gfx.Circ(hand, r * 0.22f, C(body));
            if (g.Windup > 0) Gfx.Circ(hand, 5, C(new Color(150, 135, 130)));
        }
        if (g.Type == GType.Chief)
        {
            // crown on the back of the head
            Vector2 cc = pos - d * r * 0.25f - new Vector2(0, r * 0.25f);
            Gfx.Box(cc, r * 0.9f, r * 0.28f, 0, C(new Color(230, 180, 40)));
            for (int i = -1; i <= 1; i++)
                Gfx.Triangle(cc + new Vector2(i * r * 0.3f, -r * 0.1f), r * 0.35f, r * 0.24f, -MathHelper.PiOver2, C(new Color(255, 205, 60)));
            Gfx.Circ(cc, r * 0.07f, C(new Color(230, 40, 60)));
        }

        // health bar for tough ones
        if (g.MaxHp > 2 && g.Hp < g.MaxHp && g.HpBarT > 0 && g.Type != GType.Chief)
        {
            float w = g.Radius * 2;
            float a = MathF.Min(1, g.HpBarT * 2);
            float y = g.Pos.Y - g.Radius - 12;
            Gfx.Rect(g.Pos.X - w / 2 - 1, y - 1, w + 2, 5, Color.Black * 0.6f * a);
            Gfx.Rect(g.Pos.X - w / 2, y, w * g.Hp / g.MaxHp, 3, new Color(255, 80, 60) * a);
        }
    }

    void DrawArrow(Arrow a, float alpha)
    {
        Vector2 d = Dir(a.Rot);
        Vector2 tip = a.Pos;
        float len = 24;
        Vector2 tail = tip - d * len;
        if (a.Stuck) tip -= d * 6; // head buried in the hedge
        Gfx.Line(tail, tip - d * 4, 2.2f, new Color(165, 118, 64) * alpha);
        if (!a.Stuck) Gfx.Triangle(tip - d * 6, 8, 6.5f, a.Rot, new Color(215, 220, 230) * alpha);
        Color f = a.Volley ? new Color(120, 255, 170) : new Color(240, 240, 240);
        Vector2 n = Perp(d);
        Gfx.Line(tail + d * 6, tail + n * 3.5f, 2, f * alpha);
        Gfx.Line(tail + d * 6, tail - n * 3.5f, 2, f * alpha);
        Gfx.Line(tail + d * 9, tail + d * 3 + n * 3.5f, 2, f * alpha);
        Gfx.Line(tail + d * 9, tail + d * 3 - n * 3.5f, 2, f * alpha);
    }

    void DrawTreasure(Treasure t)
    {
        float blink = 1;
        if (t.Age > 8 && !t.Magnet) blink = ((int)(t.Age * 10) % 2 == 0) ? 0.35f : 1f;
        Vector2 p = t.Pos - new Vector2(0, t.Z + 4);
        switch (t.Kind)
        {
            case TKind.Coin:
                {
                    float sx = MathF.Max(0.2f, MathF.Abs(MathF.Cos(t.Spin)));
                    Gfx.Ellipse(p, 8f * sx, 8f, 0, new Color(170, 110, 20) * blink);
                    Gfx.Ellipse(p, 6.5f * sx, 6.5f, 0, new Color(255, 205, 50) * blink);
                    Gfx.Ellipse(p - new Vector2(sx * 1.5f, 1.5f), 3.2f * sx, 3.2f, 0, new Color(255, 240, 150) * blink);
                    if (MathF.Sin(t.Spin * 0.5f + t.Pos.X) > 0.97f)
                        Gfx.Star(p + new Vector2(-2, -3), 9, 0.3f, Color.White * 0.9f * blink);
                    break;
                }
            case TKind.Gem:
            case TKind.Diamond:
                {
                    float big = t.Kind == TKind.Diamond ? 1.8f : 1.2f;
                    float sx = 0.65f + 0.35f * MathF.Abs(MathF.Cos(t.Spin * 0.6f));
                    Gfx.Gem(p, 13 * big * sx, 17 * big, 0, Color.Lerp(t.Col, Color.Black, 0.35f) * blink);
                    Gfx.Gem(p, 10 * big * sx, 13.5f * big, 0, t.Col * blink);
                    Gfx.Gem(p + new Vector2(-1.5f, -2.5f) * big, 4 * big * sx, 5.5f * big, 0, Color.Lerp(t.Col, Color.White, 0.7f) * blink);
                    float glint = MathF.Sin(t.Age * 3 + t.Pos.Y);
                    if (glint > 0.9f) Gfx.Star(p + new Vector2(3, -4) * big, 12 * big * (glint - 0.9f) * 10, 0, Color.White * blink);
                    break;
                }
            case TKind.Heart:
                {
                    float s = 15 + MathF.Sin(t.Age * 7) * 1.5f;
                    Gfx.Heart(p, s + 3, new Color(120, 20, 35) * blink);
                    Gfx.Heart(p, s, new Color(255, 70, 90) * blink);
                    Gfx.Circ(p + new Vector2(-s * 0.28f, -s * 0.18f), s * 0.11f, new Color(255, 200, 210) * blink);
                    break;
                }
        }
    }

    // ---------------------------------------------------------------- HUD
    public void DrawHud(SpriteBatch sb)
    {
        if (HideHud) return;
        float rt = RealTime;
        // score
        float pulse = 1 + ScorePulse * 0.18f;
        Vector2 coin = new(42, 40);
        Gfx.Circ(coin, 15 * pulse, new Color(150, 95, 15));
        Gfx.Circ(coin, 12.5f * pulse, new Color(255, 205, 50));
        Gfx.Circ(coin + new Vector2(-3, -3), 5 * pulse, new Color(255, 240, 160));
        string sc = ((int)MathF.Round(DisplayScore)).ToString("N0");
        Font.Draw(sc, new Vector2(66, 40 - 14 * pulse), MathF.Round(4 * pulse * 2) / 2, Color.Lerp(new Color(255, 225, 100), Color.White, ScorePulse * 0.6f), 0, 3);

        // hearts
        for (int i = 0; i < P.MaxHp; i++)
        {
            Vector2 hp = new(36 + i * 30, 82);
            bool full = i < P.Hp;
            float beat = full && P.Hp == 1 ? 1 + MathF.Max(0, MathF.Sin(rt * 8)) * 0.15f : 1;
            Gfx.Heart(hp, 24 * beat, Color.Black * 0.45f);
            Gfx.Heart(hp, 20 * beat, full ? new Color(255, 70, 90) : new Color(70, 40, 50) * 0.8f);
            if (full) Gfx.Circ(hp + new Vector2(-5, -3.5f), 2.2f, new Color(255, 200, 210));
        }

        // wave & time
        Font.Draw("WAVE " + Wave, new Vector2(W - 28, 24), 3, new Color(255, 235, 200), 1, 3);
        int secs = (int)Time;
        Font.Draw($"{secs / 60}:{secs % 60:00}", new Vector2(W - 28, 54), 2, new Color(220, 220, 220) * 0.85f, 1, 2);
        Font.Draw(Kills + " SLAIN", new Vector2(W - 28, 76), 2, new Color(200, 230, 180) * 0.8f, 1, 2);

        // combo
        if (Combo >= 2)
        {
            float k = MathHelper.Clamp(ComboTimer / 2.6f, 0, 1);
            float s = 3 + MathF.Min(3, Combo / 12f);
            Color cc = Combo >= 20 ? new Color(255, 120, 255) : Combo >= 10 ? new Color(255, 170, 60) : new Color(255, 240, 160);
            string txt = Combo + "X COMBO";
            Font.Draw(txt, new Vector2(W / 2, 22), s, cc * (0.5f + 0.5f * k), 0.5f, 3);
            float bw = Font.Measure(txt, s);
            Gfx.Rect(W / 2 - bw / 2, 22 + s * 8 + 4, bw, 4, Color.Black * 0.4f);
            Gfx.Rect(W / 2 - bw / 2, 22 + s * 8 + 4, bw * k, 4, cc);
        }

        // chief bar
        if (Chief != null)
        {
            float bw = 420, y = H - 54;
            Font.Draw("GOBLIN CHIEF", new Vector2(W / 2, y - 22), 2, new Color(255, 150, 120), 0.5f, 2);
            Gfx.Rect(W / 2 - bw / 2 - 3, y - 3, bw + 6, 16, Color.Black * 0.6f);
            Gfx.Rect(W / 2 - bw / 2, y, bw * Chief.Hp / Chief.MaxHp, 10, new Color(220, 50, 40));
            Gfx.Rect(W / 2 - bw / 2, y, bw * Chief.Hp / Chief.MaxHp, 3, new Color(255, 130, 110));
        }

        // abilities
        DrawAbility(new Vector2(46, H - 48), "VOLLEY", "RMB", 1 - MathHelper.Clamp(P.VolleyCd / VolleyCooldown, 0, 1), new Color(120, 255, 170));
        DrawAbility(new Vector2(46 + 150, H - 48), "DASH", "SPACE", 1 - MathHelper.Clamp(P.DashCd / DashCooldown, 0, 1), new Color(140, 220, 255));

        // banner
        if (BannerT > 0)
        {
            float a = MathF.Min(1, MathF.Min(BannerT * 2, (3 - BannerT) * 4));
            float slide = (1 - MathF.Min(1, (3 - BannerT) * 4)) * 30;
            Gfx.Rect(0, 180 - 6 + slide, W, 92, Color.Black * 0.35f * a);
            Font.Draw(Banner, new Vector2(W / 2, 186 + slide), 6, BannerCol * a, 0.5f, 4);
            Font.Draw(BannerSub, new Vector2(W / 2, 244 + slide), 2, new Color(230, 230, 230) * a, 0.5f, 2);
        }
    }

    void DrawAbility(Vector2 c, string name, string key, float ready, Color col)
    {
        bool ok = ready >= 1;
        Gfx.Circ(c, 22, Color.Black * 0.5f);
        // radial fill approximated by a rising disc
        Gfx.Circ(c, 18 * ready, col * (ok ? 0.9f : 0.35f));
        Gfx.RingAt(c, 23, ok ? col : col * 0.4f);
        if (ok)
        {
            float p = 0.5f + 0.5f * MathF.Sin(RealTime * 5);
            Gfx.RingAt(c, 25 + p * 3, col * 0.3f);
        }
        Font.Draw(name, new Vector2(c.X + 30, c.Y - 14), 2, ok ? Color.White : new Color(160, 160, 160), 0, 2);
        Font.Draw(key, new Vector2(c.X + 30, c.Y + 4), 2, ok ? col : col * 0.5f, 0, 2);
    }
}
