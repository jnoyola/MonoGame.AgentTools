using System.Diagnostics;
using Microsoft.Xna.Framework;
using MonoGame.AgentTools.Capture;
using MonoGame.AgentTools.Live;

namespace MonoGame.AgentTools;

/// <summary>Entry point. Call <see cref="Attach"/> once, after constructing the game and before <c>Run</c>.</summary>
public static class AgentTools
{
    /// <summary>The conditional-compilation symbol that keeps <see cref="Attach"/> calls in a build.</summary>
    public const string Symbol = "AGENTTOOLS";

    /// <summary>Process exit code when a capture fails.</summary>
    public const int CaptureFailedExitCode = 1;

    /// <summary>Process exit code when an <c>--agenttools-</c> option is invalid.</summary>
    public const int InvalidArgumentsExitCode = 2;

    /// <summary>
    /// Attaches the agent tools to <paramref name="game"/> according to <paramref name="args"/>.
    /// With <c>--agenttools-capture</c> or <c>--agenttools-profile</c>, runs that batch run and exits the
    /// process (this call doesn't return). Otherwise installs the live monitor, which records the session, and returns.
    /// </summary>
    /// <param name="game">The game to measure and capture.</param>
    /// <param name="args">The command line; <c>--agenttools-</c> options are read and the rest ignored.</param>
    /// <param name="options">Optional settings; see <see cref="AgentToolsOptions"/>.</param>
    /// <remarks>
    /// The call is compiled only when <see cref="Symbol"/> is defined, which the package does for Debug
    /// builds or when <c>AgentToolsEnabled</c> is true. In other builds the call and its arguments,
    /// including the options and their functions, are removed by the compiler.
    /// </remarks>
    [Conditional(Symbol)]
    public static void Attach(
        Game game,
        string[] args,
        AgentToolsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(args);
        var setup = new AgentToolsSetup(options ?? new AgentToolsOptions());

        CommandLineOptions commandLine;
        try
        {
            commandLine = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"MonoGame.AgentTools: {e.Message}");
            Console.Error.WriteLine(CommandLineOptions.Usage);
            Environment.Exit(InvalidArgumentsExitCode);
            return;
        }

        if (commandLine.IsBatch)
            CaptureRunner.RunAndExit(game, commandLine, setup);
        else
            LiveMonitor.Install(game, setup);
    }
}
