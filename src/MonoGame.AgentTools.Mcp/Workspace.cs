namespace MonoGame.AgentTools.Mcp;

/// <summary>The folder the server was started in (the repository root) and where it writes output.</summary>
internal sealed class Workspace(string root)
{
    public const string FolderName = ".agenttools";

    public string Root { get; } = Path.GetFullPath(root);

    public string CapturesDirectory => Path.Combine(Root, FolderName, "captures");

    /// <summary>Where the game records live play, one folder per session (written by the library, not the server).</summary>
    public string SessionsDirectory => Path.Combine(Root, FolderName, "sessions");

    /// <summary>
    /// A capture or live session by name, with the input and timing files it keeps: a capture first, then
    /// a session. Null when neither exists or the name is a path.
    /// </summary>
    public RunFolder? FindRun(string name)
    {
        if (!IsPlainName(name))
            return null;
        if (Path.Combine(CapturesDirectory, name) is var capture && Directory.Exists(capture))
            return new RunFolder(capture, RunFolder.CaptureRecording);
        if (Path.Combine(SessionsDirectory, name) is var session && Directory.Exists(session))
            return new RunFolder(session, RunFolder.SessionRecording);
        return null;
    }

    /// <summary>
    /// Creates the output folder with a <c>.gitignore</c> of <c>*</c>, so captures are never committed
    /// and nothing needs adding to the repository's own ignore file (as pytest and Ruff do for their caches).
    /// </summary>
    public string EnsureCapturesDirectory()
    {
        var folder = Path.Combine(Root, FolderName);
        Directory.CreateDirectory(CapturesDirectory);
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
            File.WriteAllText(ignore, "# Created by MonoGame.AgentTools.Mcp.\n*\n");
        return CapturesDirectory;
    }

    /// <summary>Unnamed runs of each kind beyond this many are deleted, oldest first (pytest keeps 3 runs of its temp folders).</summary>
    public const int KeptAutomaticCaptures = 10;
    private const string AutomaticMarker = ".automatic";

    /// <summary>
    /// Creates an empty folder for a run (replacing one of the same name). An unnamed run is marked with its
    /// <paramref name="pool"/> (<c>capture</c> or <c>profile</c>), and old ones in the same pool are pruned,
    /// so profiles never push out the captures an agent is still making sheets from. Folders holding a path
    /// in <paramref name="keep"/> (a baseline, a replayed input) survive, and a user's folder that happens to
    /// look like an unnamed run is never pruned, since it has no marker.
    /// </summary>
    public string PrepareCapture(string name, string? pool = null, IEnumerable<string>? keep = null)
    {
        var captures = EnsureCapturesDirectory();
        var folder = Path.Combine(captures, name);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        if (pool is not null)
            File.WriteAllText(Path.Combine(folder, AutomaticMarker), pool);

        var kept = (keep ?? []).Select(Path.GetFullPath).ToList();
        var old = new DirectoryInfo(captures).EnumerateDirectories()
            .Where(d => pool is not null && Pool(d) == pool)
            .Where(d => !kept.Any(k => IsWithin(k, d.FullName)))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Skip(KeptAutomaticCaptures);
        foreach (var directory in old)
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Open in a viewer; pruned next time.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return folder;
    }

    /// <summary>An unnamed run's pool; captures marked before there were pools have an empty marker.</summary>
    private static string? Pool(DirectoryInfo directory)
    {
        var marker = Path.Combine(directory.FullName, AutomaticMarker);
        if (!File.Exists(marker))
            return null;
        var pool = File.ReadAllText(marker).Trim();
        return pool.Length > 0 ? pool : "capture";
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsWithin(string path, string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A capture's folder: a name under <see cref="CapturesDirectory"/>, or a path.</summary>
    public string ResolveCapture(string capture)
    {
        var path = IsPlainName(capture) ? Path.Combine(CapturesDirectory, capture) : Path.GetFullPath(capture, Root);
        if (!File.Exists(Path.Combine(path, "summary.json")))
            throw new ToolException($"No capture at {Display(path)} (summary.json is missing).");
        return path;
    }

    /// <summary>
    /// A path as tool results show it: relative to the workspace with forward slashes, which the agent
    /// resolves against the same root, at a fraction of the tokens; absolute when outside it.
    /// </summary>
    public string Display(string path)
    {
        var relative = Path.GetRelativePath(Root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative.Replace('\\', '/');
    }

    /// <summary>A capture as tool results name it: its name when it's under <see cref="CapturesDirectory"/>, else its path.</summary>
    public string DisplayCapture(string folder) =>
        string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))), Path.GetFullPath(CapturesDirectory), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))
            : Display(folder);

    public static bool IsPlainName(string name) =>
        name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");
}

/// <summary>A capture's or session's folder and the base name of the input and timing files it keeps.</summary>
internal sealed record RunFolder(string Folder, string Recording)
{
    /// <summary>The library's names for them: <c>CaptureRunner.Recording</c> and <c>LiveMonitor.SessionInput</c>.</summary>
    public const string CaptureRecording = "capture";
    public const string SessionRecording = "session";
    public const string InputExtension = ".input";
    public const string TimingExtension = ".timing";

    public string Input => Recording + InputExtension;

    public string Timing => Recording + TimingExtension;
}
