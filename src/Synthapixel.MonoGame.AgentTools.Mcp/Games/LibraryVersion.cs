using System.Reflection;

namespace Synthapixel.MonoGame.AgentTools.Mcp.Games;

/// <summary>
/// The server and the game's library must be on the same minor version (the same major from 1.0 on),
/// since the options the server passes the game change only with a minor bump, which CI enforces
/// (tools/CommandLineContract.cs, which states the same rule). Read from the DLL the game was built
/// with, so a stale project reference is caught as surely as an old package.
/// </summary>
internal static class LibraryVersion
{
    public const string FileName = "Synthapixel.MonoGame.AgentTools.dll";

    public static Version Server { get; } = typeof(LibraryVersion).Assembly.GetName().Version!;

    /// <summary>Refuses a game whose built library is on another minor version than this server.</summary>
    public static void Check(string game, string outputDirectory)
    {
        var path = System.IO.Path.Combine(outputDirectory, FileName);
        if (!File.Exists(path))
            throw new ToolException($"{game}'s build output ({outputDirectory}) has no {FileName}, so its Synthapixel.MonoGame.AgentTools version can't be checked.");
        Check(game, AssemblyName.GetAssemblyName(path).Version!, Server);
    }

    internal static void Check(string game, Version library, Version server)
    {
        var compared = Line(library).CompareTo(Line(server));
        if (compared == 0)
            return;
        throw new ToolException(compared < 0
            ? $"{game} builds Synthapixel.MonoGame.AgentTools {Show(library)}, older than this server's {Show(server)}, and the options the server " +
              $"passes the game change between minor versions. Update the game's Synthapixel.MonoGame.AgentTools package to {Show(server)}, or if " +
              "it references the library's source as a project, check that the path points at the current source."
            : $"{game} builds Synthapixel.MonoGame.AgentTools {Show(library)}, newer than this server's {Show(server)}, and the options the server " +
              $"passes the game change between minor versions. Update the server to {Show(library)}.");
    }

    private static (int Major, int Minor) Line(Version version) => version.Major > 0 ? (version.Major, 0) : (0, version.Minor);

    private static string Show(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
