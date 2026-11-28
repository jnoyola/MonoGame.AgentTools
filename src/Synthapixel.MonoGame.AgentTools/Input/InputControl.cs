using System.Collections.Frozen;
using System.Text;
using Microsoft.Xna.Framework.Input;

namespace Synthapixel.MonoGame.AgentTools.Input;

internal enum ControlKind : byte
{
    Key,
    MouseButton,
    MousePosition,
    MouseWheel,
    MouseWheelHorizontal,
    PadButton,
    PadStick,
    PadTrigger,
    Text,
}

internal enum MouseButton : byte { Left, Right, Middle, X1, X2 }

/// <summary>Which thumbstick or trigger: the left or right one.</summary>
internal enum PadSide : byte { Left, Right }

/// <summary>
/// One input control, named the way scripts write it: MonoGame's own names, flat, with the device built in
/// (<c>Space</c>, <c>MouseLeft</c>, <c>PadA</c>, <c>Pad2LeftStick</c>), as Unreal's <c>EKeys</c> and
/// Source's bind names do. A stick and its click are <c>PadLeftStick</c> and <c>PadLeftStickButton</c>, as
/// Unity's Input System names them. <c>Text</c> types characters, as <c>Window.TextInput</c> delivers them.
/// <see cref="Code"/> is the <see cref="Keys"/> value, <see cref="MouseButton"/>,
/// <see cref="Buttons"/> flag or <see cref="PadSide"/>; <see cref="Pad"/> is the gamepad index, 0–3.
/// </summary>
internal readonly record struct InputControl(ControlKind Kind, int Code = 0, int Pad = 0)
{
    public const int MaxPads = 4;

    /// <summary>The gamepad buttons a script can press; the others in <see cref="Buttons"/> are derived from sticks and triggers.</summary>
    internal static readonly Buttons[] PadButtons =
    [
        Buttons.A, Buttons.B, Buttons.X, Buttons.Y, Buttons.Back, Buttons.Start, Buttons.BigButton,
        Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.LeftStick, Buttons.RightStick,
        Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight,
    ];

    /// <summary>Every key with a name, excluding <see cref="Keys.None"/>.</summary>
    internal static readonly Keys[] AllKeys = Enum.GetValues<Keys>().Where(k => k != Keys.None).Distinct().ToArray();

    private static readonly FrozenDictionary<string, InputControl> ByName = BuildNames()
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SuggestionNames = BuildNames()
        .Where(n => !n.Key.StartsWith("Pad", StringComparison.Ordinal) || !char.IsAsciiDigit(n.Key[3]))
        .Select(n => n.Key)
        .ToArray();

    public bool IsButton => Kind is ControlKind.Key or ControlKind.MouseButton or ControlKind.PadButton;
    public bool IsPad => Kind is ControlKind.PadButton or ControlKind.PadStick or ControlKind.PadTrigger;

    public static bool TryParse(string name, out InputControl control) => ByName.TryGetValue(name, out control);

    /// <summary>The valid name closest to <paramref name="name"/> by edit distance, for error messages.</summary>
    public static string Suggest(string name) =>
        SuggestionNames.MinBy(candidate => EditDistance(name.ToUpperInvariant(), candidate.ToUpperInvariant()))!;

    public override string ToString() => Kind switch
    {
        ControlKind.Key => ((Keys)Code).ToString(),
        ControlKind.MouseButton => "Mouse" + ((MouseButton)Code).ToString(),
        ControlKind.MousePosition => "Mouse",
        ControlKind.MouseWheel => "MouseWheel",
        ControlKind.MouseWheelHorizontal => "MouseWheelH",
        ControlKind.PadButton => PadPrefix(Pad) + ButtonName((Buttons)Code),
        ControlKind.PadStick => PadPrefix(Pad) + ((PadSide)Code).ToString() + "Stick",
        ControlKind.PadTrigger => PadPrefix(Pad) + ((PadSide)Code).ToString() + "Trigger",
        ControlKind.Text => "Text",
        _ => "?",
    };

    /// <summary>The name as UTF-8, for the recorder to write without allocating.</summary>
    public byte[] Utf8Name() => Encoding.UTF8.GetBytes(ToString());

    private static string PadPrefix(int pad) => pad == 0 ? "Pad" : $"Pad{pad + 1}";

    private static string ButtonName(Buttons button) => button switch
    {
        Buttons.LeftStick => "LeftStickButton",
        Buttons.RightStick => "RightStickButton",
        _ => button.ToString(),
    };

    private static IEnumerable<KeyValuePair<string, InputControl>> BuildNames()
    {
        foreach (var key in AllKeys)
            yield return Named(new InputControl(ControlKind.Key, (int)key));
        foreach (var button in Enum.GetValues<MouseButton>())
            yield return Named(new InputControl(ControlKind.MouseButton, (int)button));
        yield return Named(new InputControl(ControlKind.MousePosition));
        yield return Named(new InputControl(ControlKind.MouseWheel));
        yield return Named(new InputControl(ControlKind.MouseWheelHorizontal));
        yield return Named(new InputControl(ControlKind.Text));
        for (int pad = 0; pad < MaxPads; ++pad)
        {
            foreach (var button in PadButtons)
                yield return Named(new InputControl(ControlKind.PadButton, (int)button, pad));
            foreach (var side in Enum.GetValues<PadSide>())
            {
                yield return Named(new InputControl(ControlKind.PadStick, (int)side, pad));
                yield return Named(new InputControl(ControlKind.PadTrigger, (int)side, pad));
            }
            if (pad == 0)
            {
                // Pad1A is the same control as PadA.
                foreach (var button in PadButtons)
                    yield return new("Pad1" + ButtonName(button), new InputControl(ControlKind.PadButton, (int)button));
                foreach (var side in Enum.GetValues<PadSide>())
                {
                    yield return new($"Pad1{side}Stick", new InputControl(ControlKind.PadStick, (int)side));
                    yield return new($"Pad1{side}Trigger", new InputControl(ControlKind.PadTrigger, (int)side));
                }
            }
        }
    }

    private static KeyValuePair<string, InputControl> Named(InputControl control) => new(control.ToString(), control);

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; ++j)
            previous[j] = j;
        for (int i = 1; i <= a.Length; ++i)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; ++j)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
