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
            return new RunFolder(capture, "capture.input", "capture.timing");
        if (Path.Combine(SessionsDirectory, name) is var session && Directory.Exists(session))
            return new RunFolder(session, "session.input", "session.timing");
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

    /// <summary>Unnamed captures beyond this many are deleted, oldest first (pytest keeps 3 runs of its temp folders).</summary>
    public const int KeptAutomaticCaptures = 10;
    private const string AutomaticMarker = ".automatic";

    /// <summary>
    /// Creates an empty folder for a capture (replacing one of the same name) and prunes old unnamed
    /// captures, except <paramref name="keep"/>. Unnamed captures are marked, so a user's folder that happens
    /// to look like one is never pruned.
    /// </summary>
    public string PrepareCapture(string name, bool automatic, string? keep = null)
    {
        var captures = EnsureCapturesDirectory();
        var folder = Path.Combine(captures, name);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        if (automatic)
            File.WriteAllText(Path.Combine(folder, AutomaticMarker), "");

        var old = new DirectoryInfo(captures).EnumerateDirectories()
            .Where(d => File.Exists(Path.Combine(d.FullName, AutomaticMarker)))
            .Where(d => keep is null || !string.Equals(d.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
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

/// <summary>A capture's or session's folder and the names of the input and timing files in it.</summary>
internal sealed record RunFolder(string Folder, string Input, string Timing);
