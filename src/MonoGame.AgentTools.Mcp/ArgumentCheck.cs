using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MonoGame.AgentTools.Mcp;

/// <summary>
/// Rejects tool calls with arguments the tool's input schema doesn't name, at any depth. The SDK binds
/// arguments by name and drops the rest, so a misspelled or renamed parameter would otherwise run the
/// tool with its default, which looks like success.
/// </summary>
internal static class ArgumentCheck
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            if (context.Params is { } call
                && context.Server.ServerOptions.ToolCollection is { } tools
                && tools.TryGetPrimitive(call.Name, out var tool)
                && Unknown(call.Name, tool.ProtocolTool.InputSchema, call.Arguments) is { } error)
            {
                return ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error }] });
            }
            return next(context, cancellationToken);
        };

    /// <summary>The error for the arguments <paramref name="schema"/> doesn't name, or null if it names them all.</summary>
    public static string? Unknown(string tool, JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
            return null;
        var errors = new List<string>();
        Check(tool, schema, arguments.Select(a => (a.Key, a.Value)), errors);
        return errors.Count > 0 ? string.Join('\n', errors) : null;
    }

    private static void Check(string owner, JsonElement schema, IEnumerable<(string Name, JsonElement Value)> arguments, List<string> errors)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;
        var unknown = new List<string>();
        var nested = new List<(string Name, JsonElement Schema, JsonElement Value)>();
        foreach (var (name, value) in arguments)
        {
            if (!properties.TryGetProperty(name, out var property))
                unknown.Add($"'{name}'");
            else if (value.ValueKind == JsonValueKind.Object)
                nested.Add((name, property, value));
        }
        if (unknown.Count > 0)
        {
            var known = string.Join(", ", properties.EnumerateObject().Select(p => p.Name));
            errors.Add($"{owner} has no {(unknown.Count == 1 ? "parameter" : "parameters")} {string.Join(", ", unknown)}; its parameters are {known}.");
        }
        foreach (var (name, property, value) in nested)
            Check($"{owner}.{name}", property, value.EnumerateObject().Select(p => (p.Name, p.Value)), errors);
    }
}
