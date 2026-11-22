using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace OneShot;

public struct GameInput
{
    public Vector2 Move;      // raw direction, any length
    public Vector2 Aim;       // aim point in virtual screen space
    public bool Fire;         // held
    public bool Volley;       // held
    public bool DashPressed;  // edge
}

public class Player
{
    public const float Radius = 13f;
    public Vector2 Pos, Vel, Kick;
    public float Aim;
    public int Hp = 5, MaxHp = 5;
    public float Invuln, DashTime, DashCd, DashBuffer, FireCd, VolleyCd, WalkPhase, HurtFlash, AfterimageT;
    public Vector2 DashDir;
    public bool Dead;
    public bool Moving;
}

public enum GType { Grunt, Runner, Brute, Slinger, Chief }

public class Goblin
{
    public GType Type;
    public Vector2 Pos, Knock, Move;
    public float Radius, Speed, Mass;
    public int Hp, MaxHp;
    public float Flash, Squash, Age, Face, AttackTimer, Windup, ChargeTime, Phase, HpBarT;
    public Vector2 ChargeDir;
    public bool Dead, Entered;
}

public class Arrow
{
    public Vector2 Pos, Vel;
    public float Rot, StuckT, Life;
    public int Pierce;
    public bool Stuck, Volley, Dead;
    public readonly List<Goblin> Hit = new(4);
}

public class Rock
{
    public Vector2 Pos, Vel;
    public float Spin;
    public bool Dead;
}

public enum TKind { Coin, Gem, Diamond, Heart }

public class Treasure
{
    public TKind Kind;
    public Vector2 Pos, Vel;
    public float Z, VZ, Age, Spin, MagnetT;
    public bool Magnet, Dead;
    public int Value;
    public Color Col;
}

public struct Particle
{
    public Vector2 Pos, Vel;
    public float Life, Max, Size0, Size1, Drag, Rot, RotV, Gravity;
    public Color C0, C1;
    public byte Kind;   // 0 circle, 1 spark, 2 ring, 3 star, 4 square, 5 glow
    public bool Add;
}

public class FloatText
{
    public Vector2 Pos;
    public string Text;
    public Color Col;
    public float Life, Max, Scale;
    public int Amount; // for merging treasure pickups
}

public class SpawnWarning
{
    public Vector2 EdgePos, SpawnPos, Inward;
    public GType Type;
    public float T, Max;
}

public struct Decal
{
    public Vector2 Pos;
    public float R, Life, Max, Rot;
    public Color Col;
}
