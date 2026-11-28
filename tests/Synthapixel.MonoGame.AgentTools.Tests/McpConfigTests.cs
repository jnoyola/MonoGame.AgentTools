using System.Text.Json;
using Synthapixel.MonoGame.AgentTools.Build;

namespace Synthapixel.MonoGame.AgentTools.Tests;

public class McpConfigTests
{
    private const string Name = McpConfigEditor.ServerName;

    [Fact]
    public void Creates_the_file_with_the_server()
    {
        var result = McpConfigEditor.Edit("", "mcpServers", Name, "1.0.0");

        Assert.Equal("""
            {
              "mcpServers": {
                "monogame-agenttools": {
                  "type": "stdio",
                  "command": "dotnet",
                  "args": ["dnx", "Synthapixel.MonoGame.AgentTools.Mcp@1.0.0", "--yes", "mcp"]
                }
              }
            }

            """.ReplaceLineEndings("\n"), result.Text);
    }

    [Fact]
    public void Adds_beside_other_servers_keeping_comments_and_trailing_commas()
    {
        const string text = """
            {
              // Shared with the team.
              "servers": {
                "playwright": { "command": "npx", "args": ["@playwright/mcp"] }, // browser
              },
              "inputs": []
            }
            """;

        var result = McpConfigEditor.Edit(text, "servers", Name, "1.0.0").Text!;

        Assert.Contains("// Shared with the team.", result, StringComparison.Ordinal);
        Assert.Contains("\"playwright\": { \"command\": \"npx\", \"args\": [\"@playwright/mcp\"] }, // browser", result, StringComparison.Ordinal);
        var servers = Parse(result).GetProperty("servers");
        Assert.Equal(["playwright", Name], servers.EnumerateObject().Select(p => p.Name));
        Assert.Equal("dotnet", servers.GetProperty(Name).GetProperty("command").GetString());
        Assert.Equal(0, Parse(result).GetProperty("inputs").GetArrayLength());
    }

    [Fact]
    public void Adds_the_container_when_missing_and_fills_an_empty_one()
    {
        var added = McpConfigEditor.Edit("{\n  \"other\": 1\n}\n", "mcpServers", Name, "1.0.0").Text!;
        var filled = McpConfigEditor.Edit("{ \"mcpServers\": {} }", "mcpServers", Name, "1.0.0").Text!;

        Assert.Equal(1, Parse(added).GetProperty("other").GetInt32());
        Assert.True(Parse(added).GetProperty("mcpServers").TryGetProperty(Name, out _));
        Assert.True(Parse(filled).GetProperty("mcpServers").TryGetProperty(Name, out _));
    }

    [Theory]
    [InlineData("\"command\": \"dotnet\", \"args\": [\"dnx\", \"Synthapixel.MonoGame.AgentTools.Mcp@0.9.0\", \"--yes\", \"mcp\"]")]
    [InlineData("\"command\": \"dnx\", \"args\": [\"Synthapixel.MonoGame.AgentTools.Mcp@0.9.0\", \"--yes\", \"mcp\"]")]
    public void Updates_its_own_entry_when_the_version_changes(string entry)
    {
        var text = $$"""{ "mcpServers": { "a": {}, "{{Name}}": { {{entry}} }, "b": {} } }""";

        var result = McpConfigEditor.Edit(text, "mcpServers", Name, "1.0.0");

        Assert.Contains("from 0.9.0 to 1.0.0", result.Note, StringComparison.Ordinal);
        var servers = Parse(result.Text!).GetProperty("mcpServers");
        Assert.Equal(["a", Name, "b"], servers.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Synthapixel.MonoGame.AgentTools.Mcp@1.0.0", servers.GetProperty(Name).GetProperty("args")[1].GetString());
    }

    [Fact]
    public void Leaves_the_file_alone_when_current_or_changed_by_hand()
    {
        var current = McpConfigEditor.Edit("", "mcpServers", Name, "1.0.0").Text!;
        var custom = $$"""{ "mcpServers": { "{{Name}}": { "command": "dotnet", "args": ["run", "--project", "local"] } } }""";

        Assert.Null(McpConfigEditor.Edit(current, "mcpServers", Name, "1.0.0").Text);
        var result = McpConfigEditor.Edit(custom, "mcpServers", Name, "1.0.0");
        Assert.Null(result.Text);
        Assert.Contains("changed by hand", result.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"mcpServers\": { ", "isn't valid JSON")]
    [InlineData("[]", "top level isn't an object")]
    [InlineData("{ \"mcpServers\": [] }", "isn't an object")]
    public void Reports_files_it_cannot_edit(string text, string error)
    {
        var result = McpConfigEditor.Edit(text, "mcpServers", Name, "1.0.0");

        Assert.Null(result.Text);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeps_line_endings_and_indentation()
    {
        var text = "{\r\n\t\"mcpServers\": {\r\n\t\t\"a\": {}\r\n\t}\r\n}\r\n";

        var result = McpConfigEditor.Edit(text, "mcpServers", Name, "1.0.0").Text!;

        Assert.DoesNotContain("\n", result.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\r\n\t\t\"monogame-agenttools\": {\r\n\t\t\t\"type\"", result, StringComparison.Ordinal);
    }

    private static JsonElement Parse(string text) =>
        JsonElement.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
}
