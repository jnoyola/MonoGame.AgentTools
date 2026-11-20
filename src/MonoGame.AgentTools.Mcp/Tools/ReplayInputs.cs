using System.Text.RegularExpressions;

namespace MonoGame.AgentTools.Mcp.Tools;

/// <summary>
/// The capture tool's input and timing, resolved from what an agent passes: a script's text, a file, or a
/// capture's name (its <c>capture.input</c> and <c>capture.timing</c>). Read into memory first, because
/// the capture being replayed may be the folder about to be replaced, then written into the new capture's
/// folder, which so holds everything needed to replay it again.
/// </summary>
internal sealed partial record ReplayInputs(string? Script, byte[]? Timing, string? TimingMode)
{
    public const string InputFile = "capture.input";
    public const string TimingFile = "capture.timing";

    public static ReplayInputs Resolve(Workspace workspace, string? input, string? timing)
    {
        string? script = null;
        byte[]? pairedTiming = null;
        if (input is not null)
        {
            if (input.Contains('\n', StringComparison.Ordinal) || ScriptLine().IsMatch(input))
                script = input;
            else if (Capture(workspace, input) is { } capture)
            {
                var file = Path.Combine(capture, InputFile);
                script = File.Exists(file) ? File.ReadAllText(file) : throw new ToolException($"Capture '{input}' was run without input, so it has no {InputFile} to replay.");
                pairedTiming = ReadIfExists(Path.Combine(capture, TimingFile));
            }
            else if (Path.GetFullPath(input, workspace.Root) is var path && File.Exists(path))
            {
                script = File.ReadAllText(path);
                pairedTiming = ReadIfExists(Path.ChangeExtension(path, ".timing"));
            }
            else
                throw new ToolException($"input '{input}' isn't a script (lines like 'f60 Space down'), an .input file or a capture's name.");
        }

        if (timing is null)
            return new ReplayInputs(script, pairedTiming, null);
        if (timing.Equals("fixed", StringComparison.OrdinalIgnoreCase) || timing.Equals("real", StringComparison.OrdinalIgnoreCase))
            return new ReplayInputs(script, null, timing.ToLowerInvariant());
        if (Capture(workspace, timing) is { } timed)
            return new ReplayInputs(script, ReadIfExists(Path.Combine(timed, TimingFile))
                ?? throw new ToolException($"Capture '{timing}' ran at a fixed step, so it has no {TimingFile} to replay."), null);
        var timingPath = Path.GetFullPath(timing, workspace.Root);
        return File.Exists(timingPath)
            ? new ReplayInputs(script, File.ReadAllBytes(timingPath), null)
            : throw new ToolException($"timing must be 'fixed', 'real', a .timing file or a capture's name; got '{timing}'.");
    }

    /// <summary>Writes the script and timing into the capture folder and returns the arguments that play them.</summary>
    public IEnumerable<string> Write(string output)
    {
        var arguments = new List<string>();
        if (Script is not null)
        {
            var path = Path.Combine(output, InputFile);
            File.WriteAllText(path, Script);
            arguments.AddRange(["--agenttools-input", path]);
        }
        if (Timing is not null)
        {
            var path = Path.Combine(output, TimingFile);
            File.WriteAllBytes(path, Timing);
            arguments.AddRange(["--agenttools-timing", path]);
        }
        else if (TimingMode is not null)
            arguments.AddRange(["--agenttools-timing", TimingMode]);
        return arguments;
    }

    private static string? Capture(Workspace workspace, string name) =>
        Workspace.IsPlainName(name) && Path.Combine(workspace.CapturesDirectory, name) is var folder && Directory.Exists(folder) ? folder : null;

    private static byte[]? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    /// <summary>A one-line script: a comment, a step header, or an event starting with f&lt;n&gt; or t&lt;s&gt; and a space (not a file named f60.input).</summary>
    [GeneratedRegex(@"^\s*(#|step\s|[ft][\d.]+\s)")]
    private static partial Regex ScriptLine();
}
