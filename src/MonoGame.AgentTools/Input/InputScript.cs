using System.Globalization;

namespace MonoGame.AgentTools.Input;

internal enum InputAction : byte { Down, Up, Set }

/// <summary>
/// One scripted change. <see cref="Frame"/> is 0 when the line gave only a time; <see cref="Ticks"/> is -1
/// when it gave only a frame. Values: mouse position and the position of a <c>down x,y</c> press in X and
/// Y; wheel notches in X; thumbstick X and Y (up positive); trigger value in X.
/// </summary>
internal readonly record struct ScriptEvent(int Line, int Frame, long Ticks, InputControl Control, InputAction Action, double X = 0, double Y = 0, bool HasPosition = false)
{
    public string Describe()
    {
        var value = Action switch
        {
            InputAction.Down when HasPosition => string.Create(CultureInfo.InvariantCulture, $"down {X:0},{Y:0}"),
            InputAction.Down => "down",
            InputAction.Up => "up",
            _ => Control.Kind switch
            {
                ControlKind.MousePosition => string.Create(CultureInfo.InvariantCulture, $"{X:0},{Y:0}"),
                ControlKind.PadStick => string.Create(CultureInfo.InvariantCulture, $"{X:0.#####},{Y:0.#####}"),
                ControlKind.MouseWheel or ControlKind.MouseWheelHorizontal => X.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture),
                _ => X.ToString("0.#####", CultureInfo.InvariantCulture),
            },
        };
        return $"{Control} {value}";
    }
}

/// <summary>
/// A parsed <c>.input</c> script: one event per line, <c>&lt;when&gt; &lt;control&gt; &lt;down|up|value&gt;</c>,
/// where <c>when</c> is <c>f&lt;frame&gt;</c>, <c>t&lt;seconds&gt;</c> or both (then <c>t</c> is used). An
/// optional <c>step &lt;ms&gt;</c> or <c>step variable</c> header says how the recording was timed. <c>#</c>
/// starts a comment.
/// </summary>
internal sealed class InputScript
{
    private InputScript(IReadOnlyList<ScriptEvent> events, long? stepTicks, bool variableStep)
    {
        Events = events;
        StepTicks = stepTicks;
        VariableStep = variableStep;
    }

    /// <summary>In file order.</summary>
    public IReadOnlyList<ScriptEvent> Events { get; }

    /// <summary>The fixed step the script was recorded at, from its <c>step</c> header.</summary>
    public long? StepTicks { get; }

    /// <summary>The script was recorded from a variable-step game (<c>step variable</c>).</summary>
    public bool VariableStep { get; }

    /// <summary>The gamepads the script uses, as a bit per pad index.</summary>
    public int PadMask => Events.Where(e => e.Control.IsPad).Aggregate(0, (mask, e) => mask | (1 << e.Control.Pad));

    public static InputScript Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static InputScript Parse(string text, string source)
    {
        var events = new List<ScriptEvent>();
        long? step = null;
        var variable = false;
        var sawStep = false;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; ++i)
        {
            var number = i + 1;
            var line = lines[i];
            var comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;
            Exception Error(string message) => new FormatException($"{source} line {number}: {message}");

            if (tokens[0] == "step")
            {
                if (sawStep)
                    throw Error("a second step header.");
                if (events.Count > 0)
                    throw Error("the step header goes before the events.");
                sawStep = true;
                if (tokens.Length != 2)
                    throw Error("expected 'step <milliseconds>' or 'step variable'.");
                if (tokens[1] == "variable")
                    variable = true;
                else
                    step = TicksFromMilliseconds(tokens[1]) ?? throw Error($"'{tokens[1]}' isn't a step in milliseconds.");
                continue;
            }

            int frame = 0;
            long ticks = -1;
            var t = 0;
            for (; t < tokens.Length && t < 2; ++t)
            {
                // Lowercase only, so a second token such as F1 (the key) after f60 is the control.
                var token = tokens[t];
                if (frame == 0 && token.Length > 1 && token[0] == 'f' && char.IsAsciiDigit(token[1]))
                {
                    if (!int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out frame) || frame < 1)
                        throw Error($"'{token}': frames are numbered from f1.");
                }
                else if (ticks < 0 && token.Length > 1 && token[0] == 't' && (char.IsAsciiDigit(token[1]) || token[1] == '.'))
                    ticks = TicksFromSeconds(token[1..]) ?? throw Error($"'{token}' isn't a time in seconds.");
                else
                    break;
            }
            if (t == 0)
                throw Error($"'{tokens[0]}': each line starts with a frame (f60) or a time (t1.5).");
            if (t >= tokens.Length)
                throw Error("expected a control after the frame or time.");

            var name = tokens[t];
            if (!InputControl.TryParse(name, out var control))
                throw Error($"unknown control '{name}'; did you mean '{InputControl.Suggest(name)}'?");
            var values = tokens.AsSpan(t + 1);
            events.Add(ParseValue(number, frame, ticks, control, values, Error));
        }
        return new InputScript(events, step, variable);
    }

    private static ScriptEvent ParseValue(int line, int frame, long ticks, InputControl control, ReadOnlySpan<string> values, Func<string, Exception> error)
    {
        var first = values.Length > 0 ? values[0] : "";
        if (control.IsButton)
        {
            if (first is not ("down" or "up"))
                throw error($"{control} takes 'down' or 'up'{(control.Kind == ControlKind.MouseButton ? " (down may be followed by x,y)" : "")}.");
            var action = first == "down" ? InputAction.Down : InputAction.Up;
            if (control.Kind == ControlKind.MouseButton && action == InputAction.Down && values.Length == 2)
            {
                var (x, y) = Pair(values[1], error, $"{control} down x,y");
                return new ScriptEvent(line, frame, ticks, control, action, Math.Round(x), Math.Round(y), HasPosition: true);
            }
            if (values.Length != 1)
                throw error($"{control} takes one value, 'down' or 'up'.");
            return new ScriptEvent(line, frame, ticks, control, action);
        }

        if (values.Length != 1)
            throw error($"{control} takes one value.");
        switch (control.Kind)
        {
            case ControlKind.MousePosition:
            {
                var (x, y) = Pair(first, error, "Mouse x,y");
                return new ScriptEvent(line, frame, ticks, control, InputAction.Set, Math.Round(x), Math.Round(y));
            }
            case ControlKind.MouseWheel or ControlKind.MouseWheelHorizontal:
                return new ScriptEvent(line, frame, ticks, control, InputAction.Set, Number(first, error, $"{control} takes a number of notches, such as +1 or -2"));
            case ControlKind.PadStick:
            {
                if (first is "down" or "up")
                    throw error($"{control} takes x,y; the click is {control}Button.");
                var (x, y) = Pair(first, error, $"{control} x,y");
                if (Math.Abs(x) > 1 || Math.Abs(y) > 1)
                    throw error($"{control} values are from -1 to 1.");
                return new ScriptEvent(line, frame, ticks, control, InputAction.Set, x, y);
            }
            default:
            {
                var value = Number(first, error, $"{control} takes a value from 0 to 1");
                if (value is < 0 or > 1)
                    throw error($"{control} values are from 0 to 1.");
                return new ScriptEvent(line, frame, ticks, control, InputAction.Set, value);
            }
        }
    }

    private static (double X, double Y) Pair(string text, Func<string, Exception> error, string expected)
    {
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
            throw error($"expected {expected}, got '{text}'.");
        return (Number(text[..comma], error, $"expected {expected}"), Number(text[(comma + 1)..], error, $"expected {expected}"));
    }

    private static double Number(string text, Func<string, Exception> error, string expected) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw error($"{expected}, got '{text}'.");

    private static long? TicksFromSeconds(string text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds < 100_000_000
            ? (long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero)
            : null;

    private static long? TicksFromMilliseconds(string text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ms) && ms > 0 && ms < 1_000_000
            ? (long)Math.Round(ms * TimeSpan.TicksPerMillisecond, MidpointRounding.AwayFromZero)
            : null;
}
