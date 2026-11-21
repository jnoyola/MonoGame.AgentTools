using System.Text;
using System.Text.RegularExpressions;

namespace MonoGame.AgentTools.Mcp.Games;

internal static partial class CommandLine
{
    public const string Prefix = "--agenttools-";

    /// <summary>The game's flag for a tool parameter: <c>jitNames</c> is <c>--agenttools-jit-names</c>.</summary>
    public static string Flag(string parameter) => Prefix + UpperLetter().Replace(parameter, m => "-" + char.ToLowerInvariant(m.Value[0]));

    /// <summary>The game's own words with each flag named as the tool parameter it came from, for an error the agent can act on.</summary>
    public static string AsParameters(string text) =>
        FlagName().Replace(text, m => DashLetter().Replace(m.Groups[1].Value, d => char.ToUpperInvariant(d.Value[1]).ToString()));

    [GeneratedRegex("[A-Z]")]
    private static partial Regex UpperLetter();

    [GeneratedRegex("--agenttools-([a-z]+(?:-[a-z]+)*)")]
    private static partial Regex FlagName();

    [GeneratedRegex("-[a-z]")]
    private static partial Regex DashLetter();

    /// <summary>
    /// Joins arguments into one command line that <c>CommandLineToArgvW</c> and the .NET runtime split
    /// back into the same arguments (the rules <c>ProcessStartInfo.ArgumentList</c> applies).
    /// </summary>
    public static string Join(IEnumerable<string> arguments)
    {
        var line = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (line.Length > 0)
                line.Append(' ');
            Append(line, argument);
        }
        return line.ToString();
    }

    private static void Append(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote; then each must be doubled.
            line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            line.Append(c);
            backslashes = 0;
        }
        line.Append('\\', backslashes * 2).Append('"');
    }
}
