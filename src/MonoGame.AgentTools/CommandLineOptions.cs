using System.Globalization;
using MonoGame.AgentTools.Timing;

namespace MonoGame.AgentTools;

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

/// <summary>Command-line options. Every option is prefixed with <c>--agenttools-</c> so it can't collide with the game's own.</summary>
internal sealed record CommandLineOptions
{
    public const string Prefix = "--agenttools-";
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;

    /// <summary>Batch capture output directory; null in live mode.</summary>
    public string? CaptureDirectory { get; init; }
    /// <summary>Fixed steps per second; null uses the game's <c>TargetElapsedTime</c>.</summary>
    public int? Fps { get; init; }
    /// <summary>Game seconds to capture; null runs 1 s, or to the end of the input or timing being replayed.</summary>
    public double? DurationSeconds { get; init; }
    public int? FrameCount { get; init; }
    /// <summary>Backbuffer size; null keeps the game's own.</summary>
    public (int Width, int Height)? Size { get; init; } = (DefaultWidth, DefaultHeight);
    public FrameSelection Save { get; init; } = FrameSelection.All;
    public bool JitNames { get; init; }
    /// <summary>Pace frames to wall-clock time at the step, as vsync would. For performance runs.</summary>
    public bool Realtime { get; init; }
    /// <summary>Capture: an <c>.input</c> script to play.</summary>
    public string? InputPath { get; init; }
    /// <summary>Capture: how frames are timed; null is fixed, or the <c>.timing</c> file beside <see cref="InputPath"/>.</summary>
    public TimingMode? Timing { get; init; }
    /// <summary>Capture: the <c>.timing</c> file to replay when <see cref="Timing"/> is <see cref="TimingMode.File"/>.</summary>
    public string? TimingPath { get; init; }
    /// <summary>Record the input the game reads to this path's <c>.input</c> (and <c>.timing</c>).</summary>
    public string? RecordPath { get; init; }
    /// <summary>Live mode: write a stats summary here when the game exits.</summary>
    public string? StatsPath { get; init; }
    /// <summary>Live mode: show the HUD from the start (backtick toggles it either way).</summary>
    public bool Hud { get; init; }

    public bool IsCapture => CaptureDirectory is not null;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
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
                "capture" => options with { CaptureDirectory = Path.GetFullPath(Value()) },
                "fps" => options with { Fps = FrameSelection.ParsePositiveInt(Value(), arg) },
                "input" => options with { InputPath = Path.GetFullPath(Value()) },
                "timing" => ParseTiming(options, Value()),
                "record" => options with { RecordPath = Path.GetFullPath(Value()) },
                "duration" => options with { DurationSeconds = ParseSeconds(Value(), arg) },
                "frames" => options with { FrameCount = FrameSelection.ParsePositiveInt(Value(), arg) },
                "size" => options with { Size = ParseSize(Value(), arg) },
                "save" => options with { Save = FrameSelection.Parse(Value()) },
                "jit-names" => options with { JitNames = true },
                "realtime" => options with { Realtime = true },
                "stats" => options with { StatsPath = Path.GetFullPath(Value()) },
                "hud" => options with { Hud = true },
                _ => throw new ArgumentException($"Unknown option {arg}. {Usage}"),
            };
        }
        if (!options.IsCapture && (options.InputPath is not null || options.Timing is not null))
            throw new ArgumentException($"{Prefix}input and {Prefix}timing are capture-only: live play takes its input from the player and its time from the clock.");
        if (options.IsCapture && options.Hud)
            throw new ArgumentException($"{Prefix}hud is live-only: it shows wall-clock measurements, which would make captured frames differ between runs.");
        return options;
    }

    public const string Usage =
        "Options: --agenttools-capture <dir> [--agenttools-duration <s> | --agenttools-frames <n>] [--agenttools-fps <n>] " +
        "[--agenttools-size <WxH>|native] [--agenttools-save all|none|every:N|<f1,f2,...>] [--agenttools-realtime] [--agenttools-jit-names] " +
        "[--agenttools-input <file.input>] [--agenttools-timing fixed|real|<file.timing>]; " +
        "live: [--agenttools-stats <file.json>] [--agenttools-hud]; either: [--agenttools-record <path>]";

    private static CommandLineOptions ParseTiming(CommandLineOptions options, string value) => value.ToLowerInvariant() switch
    {
        "fixed" => options with { Timing = TimingMode.Fixed, TimingPath = null },
        "real" => options with { Timing = TimingMode.Real, TimingPath = null },
        _ => options with { Timing = TimingMode.File, TimingPath = Path.GetFullPath(value) },
    };

    private static double ParseSeconds(string text, string option)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !(value > 0))
            throw new ArgumentException($"{option} expects a positive number of seconds, got '{text}'.");
        return value;
    }

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
