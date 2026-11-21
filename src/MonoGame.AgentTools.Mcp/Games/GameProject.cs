using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MonoGame.AgentTools.Mcp.Games;

/// <summary>An executable project that references MonoGame.AgentTools, and how to build and run it.</summary>
internal sealed partial class GameProject
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", "node_modules", "artifacts", "TestResults", Workspace.FolderName];
    private const int MaxDepth = 6;

    private GameProject(string path) => Path = path;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>
    /// Finds executable, non-test projects under <paramref name="root"/> that reference the library, by
    /// package or by project.
    /// </summary>
    public static IReadOnlyList<GameProject> Discover(string root)
    {
        var found = new List<GameProject>();
        Search(new DirectoryInfo(root), 0, found);
        found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return found;
    }

    /// <summary>Resolves the tool's <c>project</c> argument: a .csproj, a folder with one, or null to discover.</summary>
    public static GameProject Resolve(string root, string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            var path = System.IO.Path.GetFullPath(project, root);
            if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return new GameProject(path);
            if (Directory.Exists(path))
            {
                var projects = Directory.GetFiles(path, "*.csproj");
                if (projects.Length == 1)
                    return new GameProject(projects[0]);
                throw new ToolException(projects.Length == 0
                    ? $"No .csproj in {path}."
                    : $"{path} has {projects.Length} projects; pass one: {string.Join(", ", projects.Select(System.IO.Path.GetFileName))}.");
            }
            throw new ToolException($"No project at {path}.");
        }

        var games = Discover(root);
        return games.Count switch
        {
            1 => games[0],
            0 => throw new ToolException(
                $"No executable project under {root} references MonoGame.AgentTools. Add the package and call AgentTools.Attach (see guide topic 'setup'), or pass project."),
            _ => throw new ToolException(
                $"Several games found; pass project as one of: {string.Join(", ", games.Select(g => System.IO.Path.GetRelativePath(root, g.Path)))}."),
        };
    }

    /// <summary>
    /// The framework to build and run, for a project that targets several: <paramref name="framework"/>
    /// if it's one of them, else the first listed, as Visual Studio's debug target defaults to. Null for a
    /// project with one framework. Found by evaluating the project, which sees frameworks set in imported
    /// files such as Directory.Build.props, and builds nothing. Skipped (it takes about a second) when the
    /// project file names one framework itself and none was asked for, or that one was.
    /// </summary>
    public async Task<string?> ChooseFrameworkAsync(string configuration, string? framework, CancellationToken cancellationToken)
    {
        var project = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
        if (StatedFramework(project) is { } one && (framework is null || framework.Equals(one, StringComparison.OrdinalIgnoreCase)))
            return null;
        string[] args = ["msbuild", Path, "-nologo", $"-p:Configuration={configuration}", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks"];
        var result = await ProcessRunner.RunAsync("dotnet", args, workingDirectory: null, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0)
            throw new ToolException($"Couldn't read {Name}'s target frameworks (exit code {result.ExitCode}):\n{result.Tail()}");
        return ChooseFramework(result.Output, framework);
    }

    /// <summary>
    /// The one framework the project file sets unconditionally, when it sets no other: then nothing an
    /// import adds can matter, since a project's own properties are set after Directory.Build.props.
    /// Null when in doubt (a condition, a property reference, unreadable XML), so the project is evaluated.
    /// </summary>
    internal static string? StatedFramework(string projectXml)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(projectXml);
        }
        catch (XmlException)
        {
            return null;
        }
        var elements = xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToList();
        if (elements is not [{ Name.LocalName: "TargetFramework" } one]
            || one.Attribute("Condition") is not null || one.Parent?.Attribute("Condition") is not null)
            return null;
        var value = one.Value.Trim();
        return value.Length > 0 && !value.Contains('$', StringComparison.Ordinal) && !value.Contains(';', StringComparison.Ordinal) ? value : null;
    }

    internal static string? ChooseFramework(string evaluationOutput, string? framework)
    {
        var properties = Properties(evaluationOutput, "target frameworks");
        var single = Get(properties, "TargetFramework");
        var frameworks = single.Length > 0
            ? [single]
            : Get(properties, "TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (frameworks.Length == 0)
            throw new ToolException("MSBuild reported no target framework for the project.");
        if (framework is not null && !frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
            throw new ToolException($"framework '{framework}' isn't one the project targets: {string.Join(", ", frameworks)}.");
        if (single.Length > 0)
            return null;
        return framework ?? frameworks.FirstOrDefault();
    }

    /// <summary>
    /// Builds <paramref name="configuration"/> (for <paramref name="framework"/> when the project has
    /// several) and returns how to start the game, after checking that the
    /// build has the tools (AgentToolsEnabled: on in Debug by default, by property otherwise) and speaks
    /// this server's command line. Without the tools AgentTools.Attach is compiled out, so the game would
    /// ignore the capture options and never exit. The
    /// target is explicit: with -getProperty alone, MSBuild only evaluates the project and builds nothing.
    /// </summary>
    public async Task<RunCommand> BuildAsync(string configuration, string? framework, CancellationToken cancellationToken)
    {
        string[] args =
        [
            "build", Path, "-c", configuration, "-nologo", "-t:Build",
            .. framework is null ? Array.Empty<string>() : ["-f", framework],
            "-getProperty:RunCommand", "-getProperty:RunArguments", "-getProperty:RunWorkingDirectory", "-getProperty:TargetPath",
            "-getProperty:AgentToolsEnabled", "-getProperty:AgentToolsProtocol",
        ];
        var result = await ProcessRunner.RunAsync("dotnet", args, workingDirectory: null, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
            throw new ToolException($"Building {Name} took longer than 10 minutes.");
        if (result.ExitCode != 0)
        {
            var errors = result.Output.Split('\n')
                .Select(l => l.Trim())
                .Where(l => ErrorLine().IsMatch(l))
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToList();
            throw new ToolException($"Build of {Name} failed (exit code {result.ExitCode}):\n" +
                (errors.Count > 0 ? string.Join('\n', errors) : result.Tail()));
        }
        var run = ParseRunCommand(result.Output);
        if (!run.ToolsEnabled)
            throw new ToolException(
                $"MonoGame.AgentTools is disabled in {Name}'s {configuration} build, so it can't be captured. It's on by default in Debug; " +
                "for another configuration, or if the project turns it off, set <AgentToolsEnabled>true</AgentToolsEnabled>.");
        CheckProtocol(Name, run.Protocol);
        return run;
    }

    /// <summary>
    /// Refuses a game whose library speaks another command line than this server, as a language server
    /// and its client agree on a protocol version before talking. Without it, a game on an older package
    /// (or a project reference to an old copy of the source) rejects options it doesn't know, in words
    /// that contradict the tool's own description.
    /// </summary>
    internal static void CheckProtocol(string game, string reported)
    {
        var protocol = int.TryParse(reported, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
        if (protocol == CommandLine.Protocol)
            return;
        var version = typeof(GameProject).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "this server's version";
        throw new ToolException(protocol < CommandLine.Protocol
            ? $"{game} builds an older MonoGame.AgentTools than this server ({(protocol == 0 ? "one from before v0.4, which doesn't report its command-line protocol" : $"command-line protocol {protocol}")}; " +
              $"the server's is {CommandLine.Protocol}), so it would reject or ignore options the tools pass. Update its MonoGame.AgentTools " +
              $"package to {version}, or if it references the library's source as a project, check that the path points at the current source."
            : $"{game} builds a newer MonoGame.AgentTools than this server (command-line protocol {protocol}; the server's is " +
              $"{CommandLine.Protocol}). Update the server to the game's package version.");
    }

    internal static RunCommand ParseRunCommand(string buildOutput)
    {
        var properties = Properties(buildOutput, "the game's output path");
        var command = Get(properties, "RunCommand");
        var target = Get(properties, "TargetPath");
        if (command.Length == 0)
            throw new ToolException("The build didn't report a run command; is the project an executable?");
        var workingDirectory = Get(properties, "RunWorkingDirectory");
        if (workingDirectory.Length == 0)
            workingDirectory = System.IO.Path.GetDirectoryName(target.Length > 0 ? target : command)!;
        var enabled = string.Equals(Get(properties, "AgentToolsEnabled"), "true", StringComparison.OrdinalIgnoreCase);
        return new RunCommand(command, Get(properties, "RunArguments"), workingDirectory, enabled, Get(properties, "AgentToolsProtocol"));
    }

    /// <summary>
    /// The properties MSBuild reports with -getProperty: a JSON object starting on its own line, after any
    /// warnings (which can themselves contain braces).
    /// </summary>
    private static JsonElement Properties(string output, string what)
    {
        var start = output.StartsWith('{') ? 0 : output.IndexOf("\n{", StringComparison.Ordinal);
        if (start > 0)
            ++start;
        try
        {
            if (start >= 0)
            {
                using var json = JsonDocument.Parse(output[start..]);
                if (json.RootElement.TryGetProperty("Properties", out var properties))
                    return properties.Clone();
            }
        }
        catch (JsonException)
        {
        }
        throw new ToolException($"MSBuild didn't report {what}:\n{string.Join('\n', output.Split('\n').TakeLast(20))}");
    }

    private static string Get(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static void Search(DirectoryInfo directory, int depth, List<GameProject> found)
    {
        try
        {
            foreach (var file in directory.EnumerateFiles("*.csproj"))
            {
                if (IsGame(File.ReadAllText(file.FullName)))
                    found.Add(new GameProject(file.FullName));
            }
            if (depth == MaxDepth)
                return;
            foreach (var child in directory.EnumerateDirectories())
            {
                if (!SkippedDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    Search(child, depth + 1, found);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Unreadable folders can't hold the game.
        }
    }

    internal static bool IsGame(string projectXml) =>
        LibraryReference().IsMatch(projectXml)
        && ExecutableOutput().IsMatch(projectXml)
        && !TestProject().IsMatch(projectXml);

    // A PackageReference to MonoGame.AgentTools or a ProjectReference to MonoGame.AgentTools.csproj, not .Mcp.
    [GeneratedRegex(@"Include=""[^""]*\bMonoGame\.AgentTools(\.csproj)?""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryReference();

    [GeneratedRegex(@"<OutputType>\s*(Win)?Exe\s*</OutputType>", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableOutput();

    [GeneratedRegex(@"<IsTestProject>\s*true\s*</IsTestProject>|Microsoft\.NET\.Test\.Sdk|""xunit|""NUnit|""MSTest", RegexOptions.IgnoreCase)]
    private static partial Regex TestProject();

    [GeneratedRegex(@": error [A-Z]+\d+:")]
    private static partial Regex ErrorLine();
}

/// <summary>How to start a built game (the same properties <c>dotnet run</c> uses), whether it has the tools, and the command-line protocol they speak (empty before v0.4).</summary>
internal sealed record RunCommand(string FileName, string Arguments, string WorkingDirectory, bool ToolsEnabled, string Protocol);
