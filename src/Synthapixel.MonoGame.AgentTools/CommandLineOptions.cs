using System.Globalization;
using Synthapixel.MonoGame.AgentTools.Timing;

namespace Synthapixel.MonoGame.AgentTools;

/// <summary>Which captured frames are written as PNG files.</summary>
internal readonly record struct FrameSelection(FrameSelectionKind Kind, int Every = 1, int[]? Frames = null)
{
    public static readonly FrameSelection All = new(FrameSelectionKind.All);
    public static readonly FrameSelection None = new(FrameSelectionKind.None);

    public bool Includes(int frame) => Kind switch
    {
        FrameSelectionKind.All => true,
        FrameSelectionKind.Every => frame % Every == 0,
        FrameSelectionKind.List => Array.BinarySearch(Frames!, frame) >= 0,
        _ => false,
    };

    public static FrameSelection Parse(string value)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return None;
        if (value.StartsWith("every:", StringComparison.OrdinalIgnoreCase))
        {
            var every = ParsePositiveInt(value["every:".Length..], "--agenttools-save every:N");
            return new FrameSelection(FrameSelectionKind.Every, every);
        }
        var frames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => ParsePositiveInt(f, "--agenttools-save"))
            .Distinct()
            .Order()
            .ToArray();
        if (frames.Length == 0)
            throw new ArgumentException("--agenttools-save expects all, none, every:N or a comma-separated frame list.");
        return new FrameSelection(FrameSelectionKind.List, Frames: frames);
    }

    internal static int ParsePositiveInt(string text, string option)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new ArgumentException($"{option} expects a positive integer, got '{text}'.");
        return value;
    }
}

internal enum FrameSelectionKind { All, None, Every, List }

/// <summary>What a run does: live play, or one of the two batch runs an agent starts.</summary>
internal enum RunMode
{
    /// <summary>The game as usual, with the HUD and a recorded session.</summary>
    Live,
    /// <summary>Frames as fast as the game renders them, saved as PNGs and a contact sheet.</summary>
    Capture,
    /// <summary>Paced to the step in real time with no frames saved, for performance numbers.</summary>
    Profile,
}

/// <summary>A run's length: game seconds (<c>3s</c>) or frames (<c>150f</c>).</summary>
internal readonly record struct RunLength(double? Seconds, int? Frames)
{
    /// <summary>
    /// The longest run: every frame's measurements are kept in memory (about 150 MB at the frame limit),
    /// and longer play is what live sessions record.
    /// </summary>
    public const int MaxFrames = 1_000_000;
    public const double MaxSeconds = 3600;

    public static RunLength Parse(string text, string option)
    {
        var unit = text.Length > 0 ? char.ToLowerInvariant(text[^1]) : '\0';
        var number = text.Length > 0 ? text[..^1] : "";
        RunLength? length = null;
        if (unit == 'f' && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var frames) && frames > 0)
            length = new RunLength(null, frames);
        else if (unit == 's' && double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            length = new RunLength(seconds, null);
        if (length is null)
            throw new ArgumentException($"{option} expects a length with its unit, such as 3s or 150f; got '{text}'.");
        if (length.Value.Frames > MaxFrames || length.Value.Seconds > MaxSeconds)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{option} is at most {MaxSeconds:0}s or {MaxFrames}f; got '{text}'. Live play records sessions of any length."));
        return length.Value;
    }
}

/// <summary>
/// Command-line options, all prefixed with <c>--agenttools-</c> so they can't collide with the game's own.
/// A run takes one entry flag (<c>capture</c> or <c>profile</c>, each with an output folder) and any
/// options; with neither, the game runs live. Each MCP tool parameter that reaches the game is the
/// camelCase form of one of these (<c>jitNames</c> is <c>--agenttools-jit-names</c>).
/// </summary>
internal sealed record CommandLineOptions
{
    public const string Prefix = "--agenttools-";
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;

    /// <summary>The entry flags, one per batch run.</summary>
    public static readonly IReadOnlyList<string> Entries = ["capture", "profile"];

    /// <summary>The options a batch run takes after its entry flag.</summary>
    public static readonly IReadOnlyList<string> Options = ["duration", "fps", "size", "save", "input", "timing", "jit-names", "hud"];

    public RunMode Mode { get; init; }
    /// <summary>The batch run's output folder; null in live play.</summary>
    public string? OutputDirectory { get; init; }
    /// <summary>Fixed steps per second; null uses the game's <c>TargetElapsedTime</c>.</summary>
    public int? Fps { get; init; }
    /// <summary>How long to run; null runs the mode's default, or to the end of the input or timing being replayed.</summary>
    public RunLength? Duration { get; init; }
    /// <summary>Backbuffer size; null keeps the game's own.</summary>
    public (int Width, int Height)? Size { get; init; } = (DefaultWidth, DefaultHeight);
    /// <summary>Frames written as PNGs; a profile writes none.</summary>
    public FrameSelection Save { get; init; } = FrameSelection.All;
    public bool JitNames { get; init; }
    /// <summary>Draw the HUD into the frames, as the player sees it. Its numbers are wall-clock measurements, so the frames differ between runs where it is.</summary>
    public bool Hud { get; init; }
    /// <summary>An <c>.input</c> script to play.</summary>
    public string? InputPath { get; init; }
    /// <summary>How frames are timed; null is fixed, or the <c>.timing</c> file beside <see cref="InputPath"/>.</summary>
    public TimingMode? Timing { get; init; }
    /// <summary>The <c>.timing</c> file to replay when <see cref="Timing"/> is <see cref="TimingMode.File"/>.</summary>
    public string? TimingPath { get; init; }

    public bool IsBatch => Mode != RunMode.Live;

    /// <summary>Default length when nothing replayed sets it: a capture shows a moment, a profile needs time to warm up.</summary>
    public double DefaultSeconds => Mode == RunMode.Profile ? 8 : 1;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        var given = new List<string>();
        for (int i = 0; i < args.Count; ++i)
        {
            var arg = args[i];
            if (!arg.StartsWith(Prefix, StringComparison.Ordinal))
                continue;
            var name = arg[Prefix.Length..];
            string Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{arg} expects a value.");
                return args[++i];
            }
            options = name switch
            {
                _ when Entries.Contains(name) => Enter(options, Enum.Parse<RunMode>(name, ignoreCase: true), Value()),
                "duration" => options with { Duration = RunLength.Parse(Value(), arg) },
                "fps" => options with { Fps = FrameSelection.ParsePositiveInt(Value(), arg) },
                "size" => options with { Size = ParseSize(Value(), arg) },
                "save" => options with { Save = FrameSelection.Parse(Value()) },
                "input" => options with { InputPath = Path.GetFullPath(Value()) },
                "timing" => ParseTiming(options, Value()),
                "jit-names" => options with { JitNames = true },
                "hud" => options with { Hud = true },
                _ => throw new ArgumentException($"Unknown option {arg}."),
            };
            if (Options.Contains(name))
                given.Add(arg);
        }
        if (options.Mode == RunMode.Live && given.Count > 0)
            throw new ArgumentException($"{given[0]} needs an entry flag: {Prefix}capture <dir> or {Prefix}profile <dir>. Without one the game runs live.");
        if (options is { Duration.Seconds: double seconds, Fps: int fps } && seconds * fps > RunLength.MaxFrames)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{Prefix}duration is {seconds * fps:0} frames at {fps} fps; the most is {RunLength.MaxFrames}."));
        if (options.Mode == RunMode.Profile)
        {
            if (given.Contains(Prefix + "save"))
                throw new ArgumentException($"{Prefix}save is capture-only: a profile saves no frames, so writing them can't skew its timings.");
            options = options with { Save = FrameSelection.None };
        }
        return options;
    }

    public const string Usage =
        "Usage: --agenttools-capture <dir> | --agenttools-profile <dir>, then any of " +
        "--agenttools-duration <3s|150f> --agenttools-input <file.input> --agenttools-timing fixed|real|<file.timing> " +
        "--agenttools-fps <n> --agenttools-size <WxH>|native --agenttools-save all|none|every:N|<f1,f2,...> (capture only) " +
        "--agenttools-jit-names --agenttools-hud. Without an entry flag the game runs live.";

    private static CommandLineOptions Enter(CommandLineOptions options, RunMode mode, string directory)
    {
        if (options.Mode != RunMode.Live)
            throw new ArgumentException($"A run takes one of {Prefix}capture or {Prefix}profile, not both.");
        return options with { Mode = mode, OutputDirectory = Path.GetFullPath(directory) };
    }

    private static CommandLineOptions ParseTiming(CommandLineOptions options, string value) => value.ToLowerInvariant() switch
    {
        "fixed" => options with { Timing = TimingMode.Fixed, TimingPath = null },
        "real" => options with { Timing = TimingMode.Real, TimingPath = null },
        _ => options with { Timing = TimingMode.File, TimingPath = Path.GetFullPath(value) },
    };

    private static (int, int)? ParseSize(string text, string option)
    {
        if (text.Equals("native", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = text.Split('x', 'X');
        if (parts.Length != 2)
            throw new ArgumentException($"{option} expects WxH or native, got '{text}'.");
        return (FrameSelection.ParsePositiveInt(parts[0], option), FrameSelection.ParsePositiveInt(parts[1], option));
    }
}
