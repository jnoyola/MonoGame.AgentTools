using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace Synthapixel.MonoGame.AgentTools.Live;

/// <summary>
/// Where live play is recorded: one folder per session under <c>.agenttools/sessions</c> in the
/// repository root, the folder the MCP server works in, so an agent finds the latest session beside its
/// captures. The most recent <see cref="Kept"/> are kept, the way Unreal keeps recent logs in
/// <c>Saved/Logs</c> as timestamped backups.
/// </summary>
internal static partial class Sessions
{
    /// <summary>The runtime setting the build writes when <c>AgentToolsRoot</c> is set.</summary>
    public const string RootSetting = "Synthapixel.MonoGame.AgentTools.Root";

    /// <summary>The runtime setting the build always writes: the game's project folder, where the search for the root starts.</summary>
    public const string ProjectSetting = "Synthapixel.MonoGame.AgentTools.ProjectDirectory";

    public const int Kept = 10;
    private const string Marker = ".automatic";

    /// <summary>
    /// Creates this session's folder and prunes the oldest beyond <see cref="Kept"/>. Returns null, after a
    /// warning, when the folder can't be written: the game still runs, unrecorded.
    /// </summary>
    public static string? Create(string game, DateTime now) => Create(FindRoot(), game, now);

    internal static string? Create(string repository, string game, DateTime now)
    {
        try
        {
            var root = Path.Combine(repository, ".agenttools");
            var sessions = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            WriteIgnore(Path.Combine(root, ".gitignore"));

            var name = string.Create(CultureInfo.InvariantCulture, $"{game}-{now:yyyyMMdd-HHmmss}");
            var folder = Claim(sessions, name);
            Prune(sessions, game);
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warn(e);
            return null;
        }
    }

    /// <summary>Created once, by whichever game gets there first.</summary>
    private static void WriteIgnore(string path)
    {
        if (File.Exists(path))
            return;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            file.Write("# Created by Synthapixel.MonoGame.AgentTools.\n*\n"u8);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
    }

    public static void Warn(Exception e) => Console.Error.WriteLine($"Synthapixel.MonoGame.AgentTools: this session isn't recorded: {e.Message}");

    /// <summary>
    /// The game's executable name (normally its project's name, which captures are named after too), so
    /// two games in one repository keep separate sessions even when both keep the template's <c>Game1</c>.
    /// </summary>
    public static string GameName(Game game) => Assembly.GetEntryAssembly()?.GetName().Name ?? game.GetType().Name;

    /// <summary>
    /// The folder is this session's once it creates the marker: creating a file that must not exist is
    /// atomic, so two games started in the same second get a folder each.
    /// </summary>
    private static string Claim(string sessions, string name)
    {
        for (int n = 1; ; ++n)
        {
            var folder = Path.Combine(sessions, n == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name}-{n}"));
            Directory.CreateDirectory(folder);
            var marker = Path.Combine(folder, Marker);
            try
            {
                new FileStream(marker, FileMode.CreateNew, FileAccess.Write).Dispose();
                return folder;
            }
            catch (IOException) when (File.Exists(marker))
            {
            }
        }
    }

    /// <summary>
    /// The <c>AgentToolsRoot</c> the game was built with, else the nearest folder holding <c>.git</c> above
    /// its project (the rule that adds the server to <c>.mcp.json</c>, so sessions land where the server
    /// looks), else the project folder. A game run without those settings starts from its own folder.
    /// </summary>
    public static string FindRoot()
    {
        if (AppContext.GetData(RootSetting) is string { Length: > 0 } configured)
            return configured;
        var start = AppContext.GetData(ProjectSetting) is string { Length: > 0 } project && Directory.Exists(project)
            ? project
            : AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return directory.FullName;
        }
        return start;
    }

    /// <summary>
    /// Only this game's folders that this class made and named are pruned, so renaming a session keeps it,
    /// as naming a capture does, and another game in the repository keeps its own 10.
    /// </summary>
    private static void Prune(string sessions, string game)
    {
        var old = new DirectoryInfo(sessions).EnumerateDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, Marker)) && IsAutomaticName(d.Name, game))
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
    internal static bool IsAutomaticName(string name, string game) =>
        name.Length > game.Length + 1 && name.StartsWith(game, StringComparison.OrdinalIgnoreCase) && name[game.Length] == '-'
        && AutomaticSuffix().IsMatch(name.AsSpan(game.Length + 1));

    [GeneratedRegex(@"^\d{8}-\d{6}(-\d+)?$")]
    private static partial Regex AutomaticSuffix();
}
