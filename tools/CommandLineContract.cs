// Fails when the command line the MCP server passes the game changed between two builds of the library
// without a minor version bump (a major bump from 1.0 on), since the server refuses a game whose library
// is on another minor. Run by CI against the PR's base; bump VersionPrefix in Directory.Build.props when
// it fails, or by hand when an option keeps its name but changes its meaning, which this can't see.
// Usage: dotnet run tools/CommandLineContract.cs -- <base Synthapixel.MonoGame.AgentTools.dll> <head Synthapixel.MonoGame.AgentTools.dll>

// It loads the libraries to read their options, which native AOT (on by default for file-based apps) can't do.
#:property PublishAot=false

using System.Reflection;
using System.Runtime.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run tools/CommandLineContract.cs -- <base Synthapixel.MonoGame.AgentTools.dll> <head Synthapixel.MonoGame.AgentTools.dll>");
    return 2;
}

var before = Read(args[0]);
var after = Read(args[1]);
if (before is null)
{
    Console.WriteLine("The base library has no command line to compare with.");
    return 0;
}
if (after is null)
{
    Console.Error.WriteLine("The library no longer has CommandLineOptions in its root namespace; update this tool to read its command line.");
    return 1;
}

Console.WriteLine($"Base {before.Line}: {string.Join(' ', before.Flags)}");
Console.WriteLine($"Head {after.Line}: {string.Join(' ', after.Flags)}");
if (before.Flags.SequenceEqual(after.Flags))
{
    Console.WriteLine("The command line is unchanged.");
    return 0;
}
var added = after.Flags.Except(before.Flags).ToList();
var removed = before.Flags.Except(after.Flags).ToList();
var change = string.Join("; ", new[] { added.Count > 0 ? "added " + string.Join(", ", added) : null, removed.Count > 0 ? "removed " + string.Join(", ", removed) : null }.OfType<string>());
if (after.Line.CompareTo(before.Line) > 0)
{
    Console.WriteLine($"The command line changed ({change}), and the version moved from {before.Line} to {after.Line}.");
    return 0;
}
var next = before.Line.Major > 0 ? $"{before.Line.Major + 1}.0.0" : $"0.{before.Line.Minor + 1}.0";
Console.Error.WriteLine($"The command line changed ({change}) but the version is still {after.Line}, so a server and a game that " +
    $"both report {after.Line} could disagree about these options. Set VersionPrefix in Directory.Build.props to {next}.");
return 1;

static Contract? Read(string path)
{
    var context = new AssemblyLoadContext(path, isCollectible: true);
    try
    {
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        // The root namespace is the assembly name, so this reads the options on either side of a rename.
        if (assembly.GetType($"{assembly.GetName().Name}.CommandLineOptions") is not { } options)
            return null;
        string Value(string name) => (string)options.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        IEnumerable<string> Values(string name) => (IEnumerable<string>)options.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var prefix = Value("Prefix");
        var flags = Values("Entries").Concat(Values("Options")).Select(f => prefix + f).Order(StringComparer.Ordinal).ToList();
        var version = assembly.GetName().Version!;
        return new Contract(flags, new Line(version.Major, version.Major > 0 ? 0 : version.Minor));
    }
    finally
    {
        context.Unload();
    }
}

/// <summary>The versions a server and library must share: the major from 1.0 on, the minor before it.</summary>
internal readonly record struct Line(int Major, int Minor) : IComparable<Line>
{
    public int CompareTo(Line other) => (Major, Minor).CompareTo((other.Major, other.Minor));

    public override string ToString() => Major > 0 ? $"{Major}.x" : $"0.{Minor}";
}

internal sealed record Contract(List<string> Flags, Line Line);
