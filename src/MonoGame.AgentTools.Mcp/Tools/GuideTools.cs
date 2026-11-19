using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MonoGame.AgentTools.Mcp.Tools;

/// <summary>Long-form guidance, loaded on demand so the always-loaded server instructions stay short.</summary>
[McpServerToolType]
internal sealed class GuideTools
{
    private const string ResourcePrefix = "MonoGame.AgentTools.Mcp.Guide.";

    private static readonly Lazy<SortedDictionary<string, string>> Topics = new(Load);

    [McpServerTool(Name = "guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains MonoGame.AgentTools in depth. Call without a topic to list the topics.")]
    public static string Guide([Description("Topic name, from the list returned without one.")] string? topic = null)
    {
        if (topic is not null && Topics.Value.TryGetValue(topic.Trim().ToLowerInvariant(), out var text))
            return text;
        var list = string.Join('\n', Topics.Value.Select(t => $"- {t.Key}: {Summary(t.Value)}"));
        return (topic is null ? "" : $"No topic '{topic}'. ") + "Topics:\n" + list;
    }

    /// <summary>The first line after the title.</summary>
    private static string Summary(string text) =>
        text.Split('\n').Select(l => l.Trim()).Skip(1).FirstOrDefault(l => l.Length > 0) ?? "";

    private static SortedDictionary<string, string> Load()
    {
        var assembly = typeof(GuideTools).Assembly;
        var topics = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            var topic = name[ResourcePrefix.Length..^".md".Length];
            topics[topic] = reader.ReadToEnd().ReplaceLineEndings("\n");
        }
        return topics;
    }
}
