using ModelContextProtocol;

namespace Synthapixel.MonoGame.AgentTools.Mcp;

/// <summary>A failure the agent can act on. Its message is returned as the tool's error result.</summary>
internal sealed class ToolException : McpException
{
    public ToolException(string message) : base(message)
    {
    }

    public ToolException()
    {
    }

    public ToolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
