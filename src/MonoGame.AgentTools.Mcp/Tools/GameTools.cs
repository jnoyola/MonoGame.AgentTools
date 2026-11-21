using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MonoGame.AgentTools.Mcp.Games;

namespace MonoGame.AgentTools.Mcp.Tools;

[McpServerToolType]
internal sealed class GameTools(Workspace workspace)
{
    /// <summary>The library's exit code for invalid <c>--agenttools-</c> options.</summary>
    private const int InvalidArgumentsExitCode = 2;

    [McpServerTool(Name = "list_games", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the executable projects in the workspace that reference MonoGame.AgentTools; capture runs one of them.")]
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
        gamepads as a player would (guide topic 'input'); the machine's own input never reaches a capture. Returns a brief reading of summary.json
        (warm-up gates, frame-time percentiles, allocations, GC, memory growth, JIT, render metrics, hitches,
        custom metrics; detail 'full' returns the JSON itself) and a contact sheet of saved frames, labeled
        with frame number and game time: 12 frames
        across the run by default, or the time range, count and crop given in sheet, or a frame-by-frame
        comparison with a baseline capture (sheet.against). Output is saved under
        .agenttools/captures/<name>; contact_sheet makes more sheets from it without re-running.
        """)]
    public async Task<CallToolResult> Capture(
        [Description("Game project: a .csproj or its folder, relative to the workspace. Optional when the workspace has one game (see list_games).")]
        string? project = null,
        [Description("Game time to capture, in seconds. Default 1, or to 1 s after the last input event, or the length of a replayed timing.")]
        double? seconds = null,
        [Description("Number of frames to capture; overrides seconds.")]
        int? frames = null,
        [Description("Fixed steps per second. Default: the game's own step (its TargetElapsedTime).")]
        int? fps = null,
        [Description("Input to play: the script itself (lines like 'f60 Space down', 't1.5 MouseLeft down 640,360'; see guide topic 'input'), a path to an .input file, or a capture's name to replay its input.")]
        string? input = null,
        [Description("Frame timing: 'fixed' (default), 'real' (measured, as live play gets them; kept so the run can be replayed), a .timing file, or a capture's name to replay its frame times. A replayed input's own timing is used unless this says otherwise.")]
        string? timing = null,
        [Description("Backbuffer size 'WIDTHxHEIGHT', or 'native' for the game's own. Default 1280x720, the same on every machine.")]
        string? size = null,
        [Description("Which frames to write as PNG: 'all' (default), 'none', 'every:N', or a list such as '1,30,60'. contact_sheet can only use saved frames.")]
        string? save = null,
        [Description("Pace frames to wall-clock time, as vsync would. Use with save 'none' and at least 5 s for performance numbers.")]
        bool realtime = false,
        [Description("Name the methods JIT-compiled during each hitch.")]
        bool jitNames = false,
        [Description("Extra arguments for the game itself, such as its own scenario or camera flags.")]
        string[]? gameArgs = null,
        [Description("The contact sheet to return: time range, frame count, columns and crop. Default: 12 frames across the whole run.")]
        SheetOptions? sheet = null,
        [Description("Output folder name under .agenttools/captures; a capture of the same name is replaced. Named captures are kept until deleted, for comparing before and after a change. Unnamed ones ('<project>-<timestamp>') are pruned to the 10 most recent.")]
        string? name = null,
        [Description("'brief' (default): one plain-text line per check, enough to judge the run. 'full': summary.json as written, with every field, hitch causes and JIT method names. Both are saved in the capture folder either way.")]
        string detail = "brief",
        [Description("Build configuration. Default Debug, where the tools are on; another configuration needs <AgentToolsEnabled>true</AgentToolsEnabled> in the game's project.")]
        string configuration = "Debug",
        [Description("Give up and kill the game after this many seconds. Default 300.")]
        int timeoutSeconds = 300,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = GameProject.Resolve(workspace.Root, project);
        var automaticName = name is null;
        name ??= string.Create(CultureInfo.InvariantCulture, $"{game.Name}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (!Workspace.IsPlainName(name))
            throw new ToolException($"name must be a plain folder name, got '{name}'.");
        var full = detail switch
        {
            "brief" => false,
            "full" => true,
            _ => throw new ToolException($"detail must be 'brief' or 'full', got '{detail}'."),
        };
        var baseline = sheet?.Against is { } against ? workspace.ResolveCapture(against) : null;
        if (baseline is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseline)), Path.Combine(workspace.CapturesDirectory, name), StringComparison.OrdinalIgnoreCase))
            throw new ToolException("sheet.against is the capture being replaced; give this capture another name.");

        // Read before the output folder is replaced, which may be the capture being replayed.
        var replay = ReplayInputs.Resolve(workspace, input, timing);

        progress?.Report(new ProgressNotificationValue { Progress = 0, Total = 2, Message = $"Building {game.Name} ({configuration})" });
        var run = await game.BuildAsync(configuration, cancellationToken).ConfigureAwait(false);

        var output = workspace.PrepareCapture(name, automaticName, keep: baseline);
        var arguments = new List<string> { "--agenttools-capture", output };
        arguments.AddRange(replay.Write(output));
        void Option(string option, object? value)
        {
            if (value is not null)
                arguments.AddRange(["--agenttools-" + option, Convert.ToString(value, CultureInfo.InvariantCulture)!]);
        }
        Option("duration", seconds);
        Option("frames", frames);
        Option("fps", fps);
        Option("size", size);
        Option("save", save);
        if (realtime)
            arguments.Add("--agenttools-realtime");
        if (jitNames)
            arguments.Add("--agenttools-jit-names");
        arguments.AddRange(gameArgs ?? []);

        progress?.Report(new ProgressNotificationValue { Progress = 1, Total = 2, Message = $"Capturing {game.Name}" });
        var commandLine = string.Join(' ', new[] { run.Arguments, CommandLine.Join(arguments) }.Where(a => a.Length > 0));
        var result = await ProcessRunner.RunAsync(run.FileName, commandLine, run.WorkingDirectory,
            TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)), cancellationToken).ConfigureAwait(false);

        var summaryPath = Path.Combine(output, "summary.json");
        if (result.TimedOut || result.ExitCode != 0 || !File.Exists(summaryPath))
            return Failure(game, output, result, timeoutSeconds);

        var summary = await File.ReadAllTextAsync(summaryPath, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Captured {game.Name} to {workspace.Display(output)}\n");
        ContentBlock image;
        if (SheetTools.SavedFrames(Path.Combine(output, "frames"), fps: 60).Count > 0)
        {
            var made = SheetTools.Make(workspace, output, sheet ?? new SheetOptions());
            text.Append(made.Caption).Append('\n');
            image = made.Image;
        }
        else
        {
            // Nothing saved (save 'none'): the library's own 12-frame sheet, which has no labels.
            if (sheet is not null)
                text.Append("No frames were saved, so the sheet options don't apply.\n");
            text.Append(SheetCaption(summary, output));
            image = ImageContentBlock.FromBytes(await File.ReadAllBytesAsync(Path.Combine(output, "sheet.png"), cancellationToken).ConfigureAwait(false), "image/png");
        }
        if (full)
            text.Append("\nsummary.json:\n").Append(summary);
        else
            text.Append(CaptureBrief.Format(summary)).Append("summary.json and frames.csv (every frame) are in the capture folder; detail 'full' returns the summary.\n");
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

    private static CallToolResult Failure(GameProject game, string output, ProcessResult result, int timeoutSeconds)
    {
        var text = new StringBuilder();
        if (result.TimedOut)
            text.Append(CultureInfo.InvariantCulture, $"{game.Name} didn't finish within {timeoutSeconds} s and was killed.\n");
        else if (result.ExitCode == InvalidArgumentsExitCode)
            text.Append(CultureInfo.InvariantCulture, $"{game.Name} rejected the capture options.\n");
        else
            text.Append(CultureInfo.InvariantCulture, $"Capture of {game.Name} failed (exit code {result.ExitCode}).\n");

        var error = Path.Combine(output, "error.txt");
        if (File.Exists(error))
            text.Append("error.txt:\n").Append(File.ReadAllText(error)).Append('\n');
        if (result.ExitCode == 0 && !result.TimedOut)
            text.Append("The game exited without writing summary.json; does its Main call AgentTools.Attach(game, args) before game.Run()?\n");
        text.Append("Game output:\n").Append(result.Tail());
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text.ToString() }] };
    }
}
