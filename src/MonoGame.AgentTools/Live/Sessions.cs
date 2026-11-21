using System.Globalization;
using System.Text.RegularExpressions;

namespace MonoGame.AgentTools.Live;

/// <summary>
/// Where live play is recorded: one folder per session under <c>.agenttools/sessions</c> in the
/// repository root, the folder the MCP server works in, so an agent finds the latest session beside its
/// captures. The most recent <see cref="Kept"/> are kept, the way Unreal keeps recent logs in
/// <c>Saved/Logs</c> as timestamped backups.
/// </summary>
internal static partial class Sessions
{
    /// <summary>The runtime setting the build writes when <c>AgentToolsRoot</c> is set.</summary>
    public const string RootSetting = "MonoGame.AgentTools.Root";

    public const int Kept = 10;
    private const string Marker = ".automatic";

    /// <summary>
    /// Creates this session's folder and prunes the oldest beyond <see cref="Kept"/>. Returns null, after a
    /// warning, when the folder can't be written: the game still runs, unrecorded.
    /// </summary>
    public static string? Create(string game, DateTime now)
    {
        try
        {
            var root = Path.Combine(FindRoot(), ".agenttools");
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            var ignore = Path.Combine(root, ".gitignore");
            if (!File.Exists(ignore))
                File.WriteAllText(ignore, "# Created by MonoGame.AgentTools.\n*\n");

            var name = string.Create(CultureInfo.InvariantCulture, $"{game}-{now:yyyyMMdd-HHmmss}");
            var folder = Path.Combine(sessions, name);
            for (int n = 2; Directory.Exists(folder); ++n)
                folder = Path.Combine(sessions, string.Create(CultureInfo.InvariantCulture, $"{name}-{n}"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Marker), "");
            Prune(sessions);
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"MonoGame.AgentTools: this session isn't recorded: {e.Message}");
            return null;
        }
    }

    /// <summary>The <c>AgentToolsRoot</c> the game was built with, else the nearest folder above the game holding <c>.git</c>, else the game's own folder.</summary>
    public static string FindRoot()
    {
        if (AppContext.GetData(RootSetting) is string { Length: > 0 } configured)
            return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return directory.FullName;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Only folders this class made and named are pruned, so renaming a session keeps it, as naming a
    /// capture does.
    /// </summary>
    private static void Prune(string sessions)
    {
        var old = new DirectoryInfo(sessions).EnumerateDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, Marker)) && IsAutomaticName(d.Name))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Skip(Kept);
        foreach (var directory in old)
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Open elsewhere; pruned next time.
            }
        }
    }

    /// <summary><c>&lt;game&gt;-yyyyMMdd-HHmmss</c>, with <c>-n</c> after it for a second session in the same second.</summary>
    internal static bool IsAutomaticName(string name) => AutomaticName().IsMatch(name);

    [GeneratedRegex(@"^.+-\d{8}-\d{6}(-\d+)?$")]
    private static partial Regex AutomaticName();
}
