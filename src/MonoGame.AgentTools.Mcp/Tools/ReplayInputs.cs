using System.Text.RegularExpressions;
using MonoGame.AgentTools.Mcp.Games;

namespace MonoGame.AgentTools.Mcp.Tools;

/// <summary>
/// The input and timing a run plays, from what an agent passes: a script's text, a file, or the name of
/// a capture or live session. Files go to the game as they are; the game itself pairs an <c>.input</c>
/// with the <c>.timing</c> beside it and keeps both in the new run's folder.
/// </summary>
internal sealed partial record ReplayInputs(string? Script, string? InputPath, string? Timing)
{
    /// <summary>The input file a capture folder keeps, and the one an inline script is written to.</summary>
    public const string CaptureInput = RunFolder.CaptureRecording + RunFolder.InputExtension;

    public static ReplayInputs Resolve(Workspace workspace, string? input, string? timing)
    {
        string? script = null, inputPath = null;
        if (input is not null)
        {
            if (input.Contains('\n', StringComparison.Ordinal) || ScriptLine().IsMatch(input))
                script = input;
            else if (workspace.FindRun(input) is { } run)
                inputPath = File.Exists(Path.Combine(run.Folder, run.Input)) ? Path.Combine(run.Folder, run.Input)
                    : throw new ToolException($"'{input}' was run without input, so it has no {run.Input} to replay.");
            else if (Path.GetFullPath(input, workspace.Root) is var path && File.Exists(path))
                inputPath = path;
            else
                throw new ToolException($"input '{input}' isn't a script (lines like 'f60 Space down'), an .input file, or a capture's or session's name.");
        }

        string? timingArgument = null;
        if (timing is null)
            return new ReplayInputs(script, inputPath, null);
        if (timing.Equals("fixed", StringComparison.OrdinalIgnoreCase) || timing.Equals("real", StringComparison.OrdinalIgnoreCase))
            timingArgument = timing.ToLowerInvariant();
        else if (workspace.FindRun(timing) is { } timed)
            timingArgument = File.Exists(Path.Combine(timed.Folder, timed.Timing)) ? Path.Combine(timed.Folder, timed.Timing)
                : throw new ToolException($"'{timing}' ran at a fixed step, so it has no {timed.Timing} to replay.");
        else if (Path.GetFullPath(timing, workspace.Root) is var path && File.Exists(path))
            timingArgument = path;
        else
            throw new ToolException($"timing must be 'fixed', 'real', a .timing file, or a capture's or session's name; got '{timing}'.");
        return new ReplayInputs(script, inputPath, timingArgument);
    }

    /// <summary>
    /// Throws when a file this replays is in <paramref name="output"/>, the folder about to be replaced:
    /// the run would delete what it plays.
    /// </summary>
    public void CheckNotIn(string output, string name)
    {
        if (Sources.Any(source => Workspace.IsWithin(source, output)))
            throw new ToolException($"'{name}' is the capture being replayed, and running it again replaces it; give this run another name.");
    }

    /// <summary>The files this replays, which must outlive the pruning a new run does.</summary>
    public IEnumerable<string> Sources =>
        new[] { InputPath, Timing }.OfType<string>().Where(Path.IsPathRooted);

    /// <summary>The game's arguments; an inline script is first written into the run's folder, where the game keeps its input anyway.</summary>
    public IEnumerable<string> Arguments(string output)
    {
        var arguments = new List<string>();
        var input = InputPath;
        if (Script is not null)
        {
            input = Path.Combine(output, CaptureInput);
            File.WriteAllText(input, Script);
        }
        // The tools' parameter names, so these are the flags the tools are checked against.
        if (input is not null)
            arguments.AddRange([CommandLine.Flag("input"), input]);
        if (Timing is not null)
            arguments.AddRange([CommandLine.Flag("timing"), Timing]);
        return arguments;
    }

    /// <summary>A one-line script: a comment, a step header, or an event starting with f&lt;n&gt; or t&lt;s&gt; and a space (not a file named f60.input).</summary>
    [GeneratedRegex(@"^\s*(#|step\s|[ft][\d.]+\s)")]
    private static partial Regex ScriptLine();
}
