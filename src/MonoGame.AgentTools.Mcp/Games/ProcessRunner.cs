using System.Diagnostics;
using System.Text;

namespace MonoGame.AgentTools.Mcp.Games;

internal sealed record ProcessResult(int ExitCode, bool TimedOut, string Output)
{
    /// <summary>The end of the output, where errors usually are.</summary>
    public string Tail(int characters = 4000) =>
        Output.Length <= characters ? Output : "..." + Output[^characters..];
}

internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion with stdout and stderr merged in arrival order. On timeout or
    /// cancellation the whole process tree is killed, so a hung game doesn't outlive the request.
    /// </summary>
    public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory ?? "" };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return RunAsync(info, timeout, cancellationToken);
    }

    /// <summary>As above, with arguments given as one command-line string (MSBuild's <c>RunArguments</c>).</summary>
    public static Task<ProcessResult> RunAsync(string fileName, string arguments, string? workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken) =>
        RunAsync(new ProcessStartInfo(fileName, arguments) { WorkingDirectory = workingDirectory ?? "" }, timeout, cancellationToken);

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // The server's own stdin/stdout are the MCP channel; the child must never inherit them.
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        // Build errors are matched in English.
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        void Append(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
                return;
            lock (output)
                output.Append(e.Data).Append('\n');
        }
        process.OutputDataReceived += Append;
        process.ErrorDataReceived += Append;

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        lock (output)
            return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, output.ToString());
    }
}
