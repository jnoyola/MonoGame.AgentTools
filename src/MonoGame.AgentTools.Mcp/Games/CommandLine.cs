using System.Text;

namespace MonoGame.AgentTools.Mcp.Games;

internal static class CommandLine
{
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
