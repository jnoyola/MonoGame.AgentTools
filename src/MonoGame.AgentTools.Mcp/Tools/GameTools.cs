using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MonoGame.AgentTools.Mcp.Games;

namespace MonoGame.AgentTools.Mcp.Tools;

/// <summary>
/// The tools that run the game. Each parameter that reaches the game is the camelCase form of the game's
/// <c>--agenttools-</c> flag (<c>jitNames</c> is <c>--agenttools-jit-names</c>), so the tools and the
/// command line read the same; the rest (project, sheet, name, detail, configuration, framework, timeout,
/// gameArgs) are the server's own.
/// </summary>
[McpServerToolType]
internal sealed class GameTools(Workspace workspace)
{
    /// <summary>The library's exit code for invalid <c>--agenttools-</c> options.</summary>
    private const int InvalidArgumentsExitCode = 2;
    private const string LibraryPrefix = "MonoGame.AgentTools: ";

    private const string ProjectDescription = "Game project: a .csproj or its folder, relative to the workspace. Optional when the workspace has one game (see list_games).";
    private const string FpsDescription = "Fixed steps per second. Default: the game's own step (its TargetElapsedTime).";
    private const string InputDescription = "Input to play: the script itself (lines like 'f60 Space down', 't1.5 MouseLeft down 640,360'; see guide topic 'input'), a path to an .input file, or the name of a capture or of a live session in .agenttools/sessions to replay its input.";
    private const string TimingDescription = "Frame timing: 'fixed' (default), 'real' (measured, as live play gets them; kept so the run can be replayed), a .timing file, or a capture's or session's name to replay its frame times. A replayed input's own timing is used unless this says otherwise.";
    private const string SizeDescription = "Backbuffer size 'WIDTHxHEIGHT', or 'native' for the game's own. Default 1280x720, the same on every machine.";
    private const string JitNamesDescription = "Name the methods JIT-compiled during each hitch. Off by default: naming them allocates about 1 MB a run on another thread and slows the frames that compile.";
    private const string GameArgsDescription = "Extra arguments for the game itself, such as its own scenario or camera flags.";
    private const string NameDescription = "Output folder name under .agenttools/captures; a run of the same name is replaced. Named runs are kept until deleted, for comparing before and after a change. Unnamed ones ('<project>-<timestamp>') are pruned to the 10 most recent of each tool.";
    private const string DetailDescription = "'brief' (default): one plain-text line per check, enough to judge the run. 'full': summary.json as written, with every field, hitch causes and JIT method names. Both are saved in the run's folder either way.";
    private const string ConfigurationDescription = "Build configuration. Default Debug, where the tools are on; another configuration needs <AgentToolsEnabled>true</AgentToolsEnabled> in the game's project.";
    private const string FrameworkDescription = "Target framework to build and run, such as 'net8.0', for a game that targets several. Default: the first in its TargetFrameworks.";
    private const string TimeoutDescription = "Give up and kill the game after this many seconds. Default 300.";

    /// <summary>What differs between the two runs: the game's entry flag, and how results name them.</summary>
    private sealed record RunKind(string Entry, string Doing, string Done, string NameSuffix)
    {
        public static readonly RunKind Capture = new("capture", "Capturing", "Captured", "");
        public static readonly RunKind Profile = new("profile", "Profiling", "Profiled", "-profile");
    }

    [McpServerTool(Name = "list_games", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the executable projects in the workspace that reference MonoGame.AgentTools; capture and profile run one of them.")]
    public string ListGames()
    {
        var games = GameProject.Discover(workspace.Root);
        if (games.Count == 0)
            return $"No executable project under {workspace.Root} references MonoGame.AgentTools. See guide topic 'setup'.";
        return string.Join('\n', games.Select(g => Path.GetRelativePath(workspace.Root, g.Path)));
    }

    [McpServerTool(Name = "capture", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Builds the game (Debug by default) and runs its real Update/Draw at a fixed time step with a hidden window, then
        exits. The same build, arguments and input produce the same frames. input drives the keyboard, mouse and
        gamepads as a player would (guide topic 'input'); the machine's own input never reaches a capture. Returns a
        brief reading of summary.json (warm-up gates, frame-time percentiles, allocations, GC, memory growth, JIT,
        render metrics, hitches, custom metrics; detail 'full' returns the JSON itself) and a contact sheet of saved
        frames, labeled with frame number and game time: 12 frames across the run by default, or the time range,
        count and crop given in sheet, or a frame-by-frame comparison with a baseline capture (sheet.against).
        Frames run back to back, so take performance numbers from profile instead. Output is saved under
        .agenttools/captures/<name>; contact_sheet and gif make more from it without re-running.
        """)]
    public Task<CallToolResult> Capture(
        [Description(ProjectDescription)] string? project = null,
        [Description("How long to capture, with its unit: '3s' (game seconds) or '150f' (frames). Default 1s, or to 1 s after the last input event, or the length of a replayed timing.")]
        string? duration = null,
        [Description(FpsDescription)] int? fps = null,
        [Description(InputDescription)] string? input = null,
        [Description(TimingDescription)] string? timing = null,
        [Description(SizeDescription)] string? size = null,
        [Description("Which frames to write as PNG: 'all' (default), 'none', 'every:N', or a list such as '1,30,60'. contact_sheet and gif can only use saved frames.")]
        string? save = null,
        [Description(JitNamesDescription)] bool jitNames = false,
        [Description(GameArgsDescription)] string[]? gameArgs = null,
        [Description("The contact sheet to return: time range, frame count, columns and crop. Default: 12 frames across the whole run.")]
        SheetOptions? sheet = null,
        [Description(NameDescription)] string? name = null,
        [Description(DetailDescription)] string detail = "brief",
        [Description(ConfigurationDescription)] string configuration = "Debug",
        [Description(FrameworkDescription)] string? framework = null,
        [Description(TimeoutDescription)] int timeoutSeconds = 300,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(RunKind.Capture, project, duration, fps, input, timing, size, save, jitNames, gameArgs, sheet, name, detail,
            configuration, framework, timeoutSeconds, progress, cancellationToken);

    [McpServerTool(Name = "profile", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Builds the game (Debug by default) and runs it with a hidden window paced to its step in real time, as a
        player's frames are, saving no frames: the run for performance numbers. The same input and timing options as
        capture, so a moment the user played or a scripted scene can be profiled exactly. Returns the brief reading of
        summary.json (frame-time percentiles after warm-up, allocations, GC, memory growth, hitches with their likely
        cause; detail 'full' for the JSON) and the game's own 12-frame sheet, to confirm what ran. Runs 8 s by
        default: shorter runs can end before the warm-up gates open. Output is saved under .agenttools/captures/<name>.
        """)]
    public Task<CallToolResult> Profile(
        [Description(ProjectDescription)] string? project = null,
        [Description("How long to profile, with its unit: '10s' (game seconds) or '600f' (frames). Default 8s, or to 1 s after the last input event if later, or the length of a replayed timing. Under 5s may not reach the performance gate.")]
        string? duration = null,
        [Description(FpsDescription)] int? fps = null,
        [Description(InputDescription)] string? input = null,
        [Description(TimingDescription)] string? timing = null,
        [Description(SizeDescription)] string? size = null,
        [Description(JitNamesDescription)] bool jitNames = false,
        [Description(GameArgsDescription)] string[]? gameArgs = null,
        [Description(NameDescription)] string? name = null,
        [Description(DetailDescription)] string detail = "brief",
        [Description(ConfigurationDescription)] string configuration = "Debug",
        [Description(FrameworkDescription)] string? framework = null,
        [Description(TimeoutDescription)] int timeoutSeconds = 300,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(RunKind.Profile, project, duration, fps, input, timing, size, save: null, jitNames, gameArgs, sheet: null, name, detail,
            configuration, framework, timeoutSeconds, progress, cancellationToken);

    private async Task<CallToolResult> RunAsync(RunKind kind, string? project, string? duration, int? fps, string? input, string? timing,
        string? size, string? save, bool jitNames, string[]? gameArgs, SheetOptions? sheet, string? name, string detail,
        string configuration, string? framework, int timeoutSeconds, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var game = GameProject.Resolve(workspace.Root, project);
        var automaticName = name is null;
        name ??= string.Create(CultureInfo.InvariantCulture, $"{game.Name}{kind.NameSuffix}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (!Workspace.IsPlainName(name))
            throw new ToolException($"name must be a plain folder name, got '{name}'.");
        var full = detail switch
        {
            "brief" => false,
            "full" => true,
            _ => throw new ToolException($"detail must be 'brief' or 'full', got '{detail}'."),
        };
        var folder = Path.Combine(workspace.CapturesDirectory, name);
        var baseline = sheet?.Against is { } against ? workspace.ResolveCapture(against) : null;
        if (baseline is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseline)), folder, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("sheet.against is the capture being replaced; give this capture another name.");
        var replay = ReplayInputs.Resolve(workspace, input, timing);
        replay.CheckNotIn(folder, name);

        framework = await game.ChooseFrameworkAsync(configuration, framework, cancellationToken).ConfigureAwait(false);
        var built = framework is null ? game.Name : $"{game.Name} ({framework})";
        progress?.Report(new ProgressNotificationValue { Progress = 0, Total = 2, Message = $"Building {built}, {configuration}" });
        var run = await game.BuildAsync(configuration, framework, cancellationToken).ConfigureAwait(false);

        // What this run reads survives the pruning that makes room for it.
        var output = workspace.PrepareCapture(name, automaticName ? kind.Entry : null, baseline is null ? replay.Sources : replay.Sources.Append(baseline));
        var arguments = new List<string> { CommandLine.Prefix + kind.Entry, output };
        void Option(string parameter, object? value)
        {
            if (value is not null)
                arguments.AddRange([CommandLine.Flag(parameter), Convert.ToString(value, CultureInfo.InvariantCulture)!]);
        }
        Option(nameof(duration), duration);
        Option(nameof(fps), fps);
        Option(nameof(size), size);
        Option(nameof(save), save);
        if (jitNames)
            arguments.Add(CommandLine.Flag(nameof(jitNames)));
        arguments.AddRange(replay.Arguments(output));
        arguments.AddRange(gameArgs ?? []);

        progress?.Report(new ProgressNotificationValue { Progress = 1, Total = 2, Message = $"{kind.Doing} {built}" });
        var commandLine = string.Join(' ', new[] { run.Arguments, CommandLine.Join(arguments) }.Where(a => a.Length > 0));
        var result = await ProcessRunner.RunAsync(run.FileName, commandLine, run.WorkingDirectory,
            TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)), cancellationToken).ConfigureAwait(false);

        var summaryPath = Path.Combine(output, "summary.json");
        if (result.TimedOut || result.ExitCode != 0 || !File.Exists(summaryPath))
            return Failure(game, kind, output, result, timeoutSeconds);

        var summary = await File.ReadAllTextAsync(summaryPath, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{kind.Done} {built} to {workspace.Display(output)}\n");
        ContentBlock image;
        if (SheetTools.SavedFrames(Path.Combine(output, "frames"), fps: 60).Count > 0)
        {
            var made = SheetTools.Make(workspace, output, sheet ?? new SheetOptions());
            text.Append(made.Caption).Append('\n');
            image = made.Image;
        }
        else
        {
            // Nothing saved (a profile, or save 'none'): the library's own 12-frame sheet, which has no labels.
            if (sheet is not null)
                text.Append("No frames were saved, so the sheet options don't apply.\n");
            text.Append(SheetCaption(summary, output));
            image = ImageContentBlock.FromBytes(await File.ReadAllBytesAsync(Path.Combine(output, "sheet.png"), cancellationToken).ConfigureAwait(false), "image/png");
        }
        if (full)
            text.Append("\nsummary.json:\n").Append(summary);
        else
            text.Append(CaptureBrief.Format(summary)).Append("summary.json and frames.csv (every frame) are in the run's folder; detail 'full' returns the summary.\n");
        return new CallToolResult { Content = [new TextContentBlock { Text = text.ToString() }, image] };
    }

    private static string SheetCaption(string summaryJson, string folder)
    {
        using var json = JsonDocument.Parse(summaryJson);
        var root = json.RootElement;
        if (!root.TryGetProperty("files", out var files) || !files.TryGetProperty("sheetFrames", out var sheetFrames))
            return "";
        var fps = root.TryGetProperty("fps", out var f) ? f.GetDouble() : 60;
        var times = SheetTools.FrameTimes(Path.Combine(folder, "frames.csv"));
        var columns = files.TryGetProperty("sheetColumns", out var c) ? c.GetInt32() : 4;
        var tiles = sheetFrames.EnumerateArray().Select(frame =>
            string.Create(CultureInfo.InvariantCulture, $"f{frame.GetInt32()} {(times.TryGetValue(frame.GetInt32(), out var s) ? s : frame.GetInt32() / fps):0.000}s"));
        return $"Sheet tiles, {columns} per row: {string.Join(", ", tiles)}\n";
    }

    private static CallToolResult Failure(GameProject game, RunKind kind, string output, ProcessResult result, int timeoutSeconds)
    {
        var text = new StringBuilder();
        // The game's own words for a bad option, with each flag named as the parameter it came from.
        var rejected = result.ExitCode == InvalidArgumentsExitCode
            ? result.Output.Split('\n').Where(l => l.StartsWith(LibraryPrefix, StringComparison.Ordinal))
                .Select(l => CommandLine.AsParameters(l[LibraryPrefix.Length..].TrimEnd('\r'))).Distinct().ToList()
            : [];
        if (result.TimedOut)
            text.Append(CultureInfo.InvariantCulture, $"{game.Name} didn't finish within {timeoutSeconds} s and was killed.\n");
        else if (rejected.Count > 0)
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{game.Name} rejected the {kind.Entry} options: {string.Join(" ", rejected)}" }],
            };
        else
            text.Append(CultureInfo.InvariantCulture, $"The {kind.Entry} of {game.Name} failed (exit code {result.ExitCode}).\n");

        var error = Path.Combine(output, "error.txt");
        if (File.Exists(error))
            text.Append("error.txt:\n").Append(File.ReadAllText(error)).Append('\n');
        if (result.ExitCode == 0 && !result.TimedOut)
            text.Append("The game exited without writing summary.json; does its Main call AgentTools.Attach(game, args) before game.Run()?\n");
        text.Append("Game output:\n").Append(result.Tail());
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text.ToString() }] };
    }
}
