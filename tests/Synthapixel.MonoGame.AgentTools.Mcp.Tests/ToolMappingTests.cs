extern alias Library;

using Library::Synthapixel.MonoGame.AgentTools;
using Synthapixel.MonoGame.AgentTools.Mcp.Games;
using Synthapixel.MonoGame.AgentTools.Mcp.Tools;

namespace Synthapixel.MonoGame.AgentTools.Mcp.Tests;

/// <summary>The tools and the game's command line name the same options the same way.</summary>
public class ToolMappingTests
{
    /// <summary>Parameters the server handles itself; every other one is passed to the game as its flag.</summary>
    private static readonly string[] ServerOnly =
        ["project", "gameArgs", "sheet", "name", "detail", "configuration", "framework", "timeoutSeconds", "progress", "cancellationToken"];

    [Theory]
    [InlineData(nameof(GameTools.Capture))]
    [InlineData(nameof(GameTools.Profile))]
    public void Each_game_option_is_a_parameter_named_as_its_flag_in_camelCase(string tool)
    {
        var parameters = typeof(GameTools).GetMethod(tool)!.GetParameters().Select(p => p.Name!).Except(ServerOnly);
        var flags = parameters.Select(p => CommandLine.Flag(p)[CommandLine.Prefix.Length..]).Order();

        // A profile saves no frames, so it's the one option a tool leaves out.
        var expected = CommandLineOptions.Options.Where(o => tool == nameof(GameTools.Capture) || o != "save").Order();
        Assert.Equal(expected, flags);
    }

    [Fact]
    public void Entry_flags_are_the_tool_names() =>
        Assert.Equal(["capture", "profile"], CommandLineOptions.Entries);

    /// <summary>The server finds the files a capture or session keeps by the names the game writes them under.</summary>
    [Fact]
    public void Recorded_file_names_match_the_library()
    {
        Assert.Equal(Library::Synthapixel.MonoGame.AgentTools.Capture.CaptureRunner.Recording, RunFolder.CaptureRecording);
        Assert.Equal(Library::Synthapixel.MonoGame.AgentTools.Live.LiveMonitor.SessionInput, RunFolder.SessionRecording);
        Assert.Equal(Library::Synthapixel.MonoGame.AgentTools.Input.InputRecorder.InputExtension, RunFolder.InputExtension);
        Assert.Equal(Library::Synthapixel.MonoGame.AgentTools.Timing.TimingFile.Extension, RunFolder.TimingExtension);
    }

    [Fact]
    public void Flags_and_parameters_convert_both_ways()
    {
        Assert.Equal("--agenttools-jit-names", CommandLine.Flag("jitNames"));
        Assert.Equal("--agenttools-duration", CommandLine.Flag("duration"));
        Assert.Equal("duration expects a length with its unit; jitNames is on",
            CommandLine.AsParameters("--agenttools-duration expects a length with its unit; --agenttools-jit-names is on"));
    }
}
