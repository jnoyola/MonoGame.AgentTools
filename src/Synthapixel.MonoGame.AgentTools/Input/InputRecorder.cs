using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Synthapixel.MonoGame.AgentTools.Diagnostics;
using Synthapixel.MonoGame.AgentTools.Timing;

namespace Synthapixel.MonoGame.AgentTools.Input;

/// <summary>
/// Records the input the game reads, once per <c>Update</c>, as an <c>.input</c> script that a capture
/// replays, plus a <c>.timing</c> file of each frame's elapsed time for a variable-step game. Reads
/// through MonoGame's own <c>GetState</c> calls (gamepads without a dead zone, so the replay's game applies
/// its own), writes only changes, and streams both files as it goes, so a crash keeps nearly everything.
/// Characters typed through <c>Window.TextInput</c> are kept as <c>Text</c> lines on the frame they arrived
/// before. Allocation-free per frame (a paste longer than the text buffer grows it once).
/// </summary>
internal sealed class InputRecorder : IDisposable
{
    public const string InputExtension = ".input";
    private const int MaxLine = 96;
    private const int TextPerLine = 32;

    private static readonly byte[][] KeyNames = BuildKeyNames();
    private static readonly byte[][] MouseButtonNames = Enum.GetValues<MouseButton>().Select(b => new InputControl(ControlKind.MouseButton, (int)b).Utf8Name()).ToArray();
    private static readonly byte[][] PadButtonNames = BuildPadNames();
    private static readonly byte[][] StickNames = BuildSideNames(ControlKind.PadStick);
    private static readonly byte[][] TriggerNames = BuildSideNames(ControlKind.PadTrigger);

    private readonly Game _game;
    private readonly StreamedFile _input;
    private readonly string _timingPath;
    private readonly bool[] _keys = new bool[256];
    private readonly GamePadState[] _pads = new GamePadState[InputControl.MaxPads];
    private readonly EventHandler<TextInputEventArgs> _onTextInput;
    private char[] _text = new char[256];
    private int _textLength;
    private StreamedFile? _timing;
    private MouseState _mouse;
    private bool _fixedStep;
    private int _frame;
    private long _total;
    private long _second = -1;
    private int _eventsThisSecond;

    private InputRecorder(Game game, string basePath, string header)
    {
        _game = game;
        InputPath = basePath + InputExtension;
        _timingPath = basePath + TimingFile.Extension;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(InputPath))!);
        File.Delete(_timingPath);
        _input = new StreamedFile(InputPath);
        _input.Write(Encoding.UTF8.GetBytes(header));
        for (int i = 0; i < _pads.Length; ++i)
            _pads[i] = GamePadState.Default;
        _onTextInput = OnTextInput;
        game.Window.TextInput += _onTextInput;
        WarmFormatting();
    }

    /// <summary>
    /// Formats one of each value an event can hold, so the first event of a session doesn't compile the
    /// generic UTF-8 formatting code mid-frame, which <see cref="Runtime.Precompile"/> can't reach.
    /// </summary>
    private static void WarmFormatting()
    {
        Span<byte> scratch = stackalloc byte[MaxLine + 2 * InputText.MaxBytesPerChar];
        Seconds(scratch, TimeSpan.TicksPerSecond + 1);
        Milliseconds(scratch, TimeSpan.TicksPerMillisecond + 1);
        Int(new Line(scratch, 0), -1);
        Float(new Line(scratch, 0), -0.5f);
        (-1.5).TryFormat(scratch, out _, "+0.###;-0.###", System.Globalization.CultureInfo.InvariantCulture);
        InputText.Write(scratch, "é\"");
    }

    public string InputPath { get; }

    /// <summary>The <c>.timing</c> file, once a variable-step game has written one.</summary>
    public string? TimingPath => _timing is null ? null : _timingPath;

    public int Frames => _frame;
    public int Events { get; private set; }
    public int PeakEventsPerSecond { get; private set; }
    public int BufferSwaps => _input.Swaps + (_timing?.Swaps ?? 0);
    public int ExtraBuffers => _input.ExtraBuffers + (_timing?.ExtraBuffers ?? 0);

    /// <summary>The write that stopped the recording (disk full, say), or null; what came before is kept.</summary>
    public Exception? Error => _input.Error ?? _timing?.Error;

    /// <summary>Starts recording to <c>&lt;basePath&gt;.input</c> (and <c>.timing</c>); a given extension is dropped.</summary>
    public static InputRecorder Start(Game game, string basePath)
    {
        if (basePath.EndsWith(InputExtension, StringComparison.OrdinalIgnoreCase) || basePath.EndsWith(TimingFile.Extension, StringComparison.OrdinalIgnoreCase))
            basePath = Path.ChangeExtension(basePath, null);
        var header = $"# Input recorded by Synthapixel.MonoGame.AgentTools {Report.Version} from {game.GetType().Name}, {DateTime.Now:yyyy-MM-dd HH:mm}\n";
        return new InputRecorder(game, basePath, header);
    }

    /// <summary>Samples the input the game reads this frame. Call once per timed <c>Update</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Sample(GameTime time)
    {
        if (_frame == 0)
            WriteStep();
        ++_frame;
        _total = time.TotalGameTime.Ticks;
        if (_timing is not null)
        {
            TimingFile.WriteFrame(_timing.GetSpan(4), time.ElapsedGameTime.Ticks);
            _timing.Advance(4);
        }

        var keyboard = Keyboard.GetState();
        foreach (var key in InputControl.AllKeys)
        {
            var down = keyboard.IsKeyDown(key);
            if (down != _keys[(int)key])
            {
                _keys[(int)key] = down;
                Button(KeyNames[(int)key], down);
            }
        }

        var mouse = Mouse.GetState();
        if (mouse.X != _mouse.X || mouse.Y != _mouse.Y)
        {
            var line = Begin("Mouse"u8);
            line = Int(line, mouse.X);
            line = Byte(line, (byte)',');
            line = Int(line, mouse.Y);
            End(line);
        }
        MouseButtonChange(MouseButton.Left, mouse.LeftButton, _mouse.LeftButton);
        MouseButtonChange(MouseButton.Right, mouse.RightButton, _mouse.RightButton);
        MouseButtonChange(MouseButton.Middle, mouse.MiddleButton, _mouse.MiddleButton);
        MouseButtonChange(MouseButton.X1, mouse.XButton1, _mouse.XButton1);
        MouseButtonChange(MouseButton.X2, mouse.XButton2, _mouse.XButton2);
        if (mouse.ScrollWheelValue != _mouse.ScrollWheelValue)
            Wheel("MouseWheel"u8, mouse.ScrollWheelValue - _mouse.ScrollWheelValue);
        if (mouse.HorizontalScrollWheelValue != _mouse.HorizontalScrollWheelValue)
            Wheel("MouseWheelH"u8, mouse.HorizontalScrollWheelValue - _mouse.HorizontalScrollWheelValue);
        _mouse = mouse;

        for (int pad = 0; pad < _pads.Length; ++pad)
        {
            var state = GamePad.GetState(pad, GamePadDeadZone.None, GamePadDeadZone.None);
            if (!state.IsConnected)
                state = GamePadState.Default;
            ref var previous = ref _pads[pad];
            for (int b = 0; b < InputControl.PadButtons.Length; ++b)
            {
                var button = InputControl.PadButtons[b];
                var down = state.IsButtonDown(button);
                if (down != previous.IsButtonDown(button))
                    Button(PadButtonNames[pad * InputControl.PadButtons.Length + b], down);
            }
            if (state.ThumbSticks.Left != previous.ThumbSticks.Left)
                Pair(StickNames[pad * 2], state.ThumbSticks.Left);
            if (state.ThumbSticks.Right != previous.ThumbSticks.Right)
                Pair(StickNames[pad * 2 + 1], state.ThumbSticks.Right);
            if (state.Triggers.Left != previous.Triggers.Left)
                Single(TriggerNames[pad * 2], state.Triggers.Left);
            if (state.Triggers.Right != previous.Triggers.Right)
                Single(TriggerNames[pad * 2 + 1], state.Triggers.Right);
            previous = state;
        }

        for (int start = 0; start < _textLength; start += TextPerLine)
        {
            var line = Begin("Text"u8, MaxLine + TextPerLine * InputText.MaxBytesPerChar);
            End(line.Advance(InputText.Write(line.Span, _text.AsSpan(start, Math.Min(TextPerLine, _textLength - start)))));
        }
        _textLength = 0;
        _input.Tick();
        _timing?.Tick();
    }

    /// <summary>
    /// Writes every whole event recorded so far and stops recording; safe from any thread, for an
    /// unhandled exception.
    /// </summary>
    public void FlushFinal()
    {
        _input.FlushFinal();
        _timing?.FlushFinal();
    }

    /// <summary>The <c>recording</c> section of <c>summary.json</c>.</summary>
    public Dictionary<string, object> Summary()
    {
        var section = new Dictionary<string, object>
        {
            ["input"] = InputPath,
            ["frames"] = Frames,
            ["events"] = Events,
            ["peakEventsPerSecond"] = PeakEventsPerSecond,
            ["bufferSwaps"] = BufferSwaps,
            ["extraBuffers"] = ExtraBuffers,
        };
        if (TimingPath is { } timing)
            section["timing"] = timing;
        if (Error is { } error)
            section["error"] = $"Recording stopped early: {error.Message}";
        return section;
    }

    public void Dispose()
    {
        _game.Window.TextInput -= _onTextInput;
        _input.Dispose();
        _timing?.Dispose();
    }

    private void WriteStep()
    {
        _fixedStep = _game.IsFixedTimeStep;
        var line = _input.GetSpan(MaxLine);
        var length = Copy(line, "step "u8);
        if (_fixedStep)
            length += Milliseconds(line[length..], _game.TargetElapsedTime.Ticks);
        else
        {
            length += Copy(line[length..], "variable"u8);
            _timing = new StreamedFile(_timingPath);
            TimingFile.WriteHeader(_timing.GetSpan(TimingFile.HeaderSize));
            _timing.Advance(TimingFile.HeaderSize);
        }
        line[length++] = (byte)'\n';
        _input.Advance(length);
    }

    private void MouseButtonChange(MouseButton button, ButtonState now, ButtonState before)
    {
        if (now != before)
            Button(MouseButtonNames[(int)button], now == ButtonState.Pressed);
    }

    private void Button(ReadOnlySpan<byte> name, bool down)
    {
        var line = Begin(name);
        End(Copy(line, down ? "down"u8 : "up"u8));
    }

    private void Wheel(ReadOnlySpan<byte> name, int delta)
    {
        var line = Begin(name);
        (delta / 120.0).TryFormat(line.Span, out var written, "+0.###;-0.###", System.Globalization.CultureInfo.InvariantCulture);
        End(line.Advance(written));
    }

    private void Pair(ReadOnlySpan<byte> name, Vector2 value)
    {
        var line = Begin(name);
        line = Float(line, value.X);
        line = Byte(line, (byte)',');
        End(Float(line, value.Y));
    }

    private void Single(ReadOnlySpan<byte> name, float value) => End(Float(Begin(name), value));

    // Each event is one line written straight into the file's buffer: "<when> <control> <value>\n".
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        // Those MonoGame raises for Enter, Backspace and the like come back with the key on replay.
        if (InputText.IsFromKey(e.Character))
            return;
        if (_textLength == _text.Length)
            Array.Resize(ref _text, _text.Length * 2);
        _text[_textLength++] = e.Character;
    }

    private Line Begin(ReadOnlySpan<byte> name, int room = MaxLine)
    {
        var span = _input.GetSpan(room);
        var length = 0;
        if (_fixedStep)
        {
            span[length++] = (byte)'f';
            _frame.TryFormat(span[length..], out var written, provider: System.Globalization.CultureInfo.InvariantCulture);
            length += written;
            span[length++] = (byte)' ';
        }
        span[length++] = (byte)'t';
        length += Seconds(span[length..], _total);
        span[length++] = (byte)' ';
        length += Copy(span[length..], name);
        span[length++] = (byte)' ';
        return new Line(span, length);
    }

    private void End(Line line)
    {
        line.Whole[line.Length] = (byte)'\n';
        _input.Advance(line.Length + 1);
        ++Events;
        var second = _total / TimeSpan.TicksPerSecond;
        if (second != _second)
        {
            _second = second;
            _eventsThisSecond = 0;
        }
        PeakEventsPerSecond = Math.Max(PeakEventsPerSecond, ++_eventsThisSecond);
    }

    private static Line Copy(Line line, ReadOnlySpan<byte> text) => line.Advance(Copy(line.Span, text));

    private static Line Byte(Line line, byte value)
    {
        line.Span[0] = value;
        return line.Advance(1);
    }

    private static Line Int(Line line, int value)
    {
        value.TryFormat(line.Span, out var written, provider: System.Globalization.CultureInfo.InvariantCulture);
        return line.Advance(written);
    }

    /// <summary>Five decimals: enough that a thumbstick value maps back to the same 16-bit SDL axis value.</summary>
    private static Line Float(Line line, float value)
    {
        // + 0 turns -0 (MonoGame negates a resting stick's Y) into 0.
        (value + 0f).TryFormat(line.Span, out var written, "0.#####", System.Globalization.CultureInfo.InvariantCulture);
        return line.Advance(written);
    }

    private static int Copy(Span<byte> destination, ReadOnlySpan<byte> text)
    {
        text.CopyTo(destination);
        return text.Length;
    }

    /// <summary>Seconds to 7 decimals, exactly one 100 ns tick.</summary>
    internal static int Seconds(Span<byte> destination, long ticks)
    {
        (ticks / TimeSpan.TicksPerSecond).TryFormat(destination, out var length, provider: System.Globalization.CultureInfo.InvariantCulture);
        destination[length++] = (byte)'.';
        (ticks % TimeSpan.TicksPerSecond).TryFormat(destination[length..], out var fraction, "D7", System.Globalization.CultureInfo.InvariantCulture);
        return length + fraction;
    }

    /// <summary>Milliseconds to 4 decimals, exactly one 100 ns tick.</summary>
    internal static int Milliseconds(Span<byte> destination, long ticks)
    {
        (ticks / TimeSpan.TicksPerMillisecond).TryFormat(destination, out var length, provider: System.Globalization.CultureInfo.InvariantCulture);
        destination[length++] = (byte)'.';
        (ticks % TimeSpan.TicksPerMillisecond).TryFormat(destination[length..], out var fraction, "D4", System.Globalization.CultureInfo.InvariantCulture);
        return length + fraction;
    }

    private static byte[][] BuildKeyNames()
    {
        var names = new byte[256][];
        foreach (var key in InputControl.AllKeys)
            names[(int)key] = new InputControl(ControlKind.Key, (int)key).Utf8Name();
        return names;
    }

    private static byte[][] BuildPadNames()
    {
        var count = InputControl.PadButtons.Length;
        var names = new byte[InputControl.MaxPads * count][];
        for (int pad = 0; pad < InputControl.MaxPads; ++pad)
            for (int b = 0; b < count; ++b)
                names[pad * count + b] = new InputControl(ControlKind.PadButton, (int)InputControl.PadButtons[b], pad).Utf8Name();
        return names;
    }

    private static byte[][] BuildSideNames(ControlKind kind)
    {
        var names = new byte[InputControl.MaxPads * 2][];
        for (int pad = 0; pad < InputControl.MaxPads; ++pad)
            for (int side = 0; side < 2; ++side)
                names[pad * 2 + side] = new InputControl(kind, side, pad).Utf8Name();
        return names;
    }

    /// <summary>A line being written: the whole buffer span, and how far it's filled.</summary>
    private readonly ref struct Line(Span<byte> whole, int length)
    {
        public Span<byte> Whole { get; } = whole;
        public int Length { get; } = length;
        public Span<byte> Span => Whole[Length..];
        public Line Advance(int count) => new(Whole, Length + count);
    }
}
