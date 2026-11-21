using System.Text.Json;
using ModelContextProtocol.Server;
using MonoGame.AgentTools.Mcp.Tools;

namespace MonoGame.AgentTools.Mcp.Tests;

/// <summary>Calls the server would otherwise run with defaults in place of what the agent meant.</summary>
public class ArgumentCheckTests
{
    private static JsonElement Schema<T>(string method) =>
        McpServerTool.Create(typeof(T).GetMethod(method)!, _ => null!).ProtocolTool.InputSchema;

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Unknown_parameters_are_named_with_the_ones_the_tool_has()
    {
        var error = ArgumentCheck.Unknown("gif", Schema<GifTools>(nameof(GifTools.Gif)),
            Arguments("""{ "capture": "after", "against": "before", "frames": 3 }"""));

        Assert.Equal("gif has no parameters 'against', 'frames'; its parameters are capture, from, to, fps, speed, width, crop, baseline, diff.", error);
    }

    [Fact]
    public void Unknown_fields_of_an_object_parameter_are_named_after_the_tools_own()
    {
        var error = ArgumentCheck.Unknown("capture", Schema<GameTools>(nameof(GameTools.Capture)),
            Arguments("""{ "frames": 150, "sheet": { "against": "before", "count": 3 } }"""));

        var lines = error!.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("capture has no parameter 'frames'; its parameters are project, duration,", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("capture.sheet has no parameter 'against'; its parameters are", lines[1], StringComparison.Ordinal);
        Assert.Contains("baseline", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Known_and_missing_arguments_pass()
    {
        var schema = Schema<GameTools>(nameof(GameTools.Capture));

        Assert.Null(ArgumentCheck.Unknown("capture", schema, Arguments("""{ "duration": "2s", "sheet": { "baseline": "before", "diff": false }, "gameArgs": ["--x"] }""")));
        Assert.Null(ArgumentCheck.Unknown("capture", schema, null));
    }
}
