using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoGame.AgentTools.Mcp;
using MonoGame.AgentTools.Mcp.Tools;

const string Usage = """
    Usage: monogame-agenttools mcp
      mcp   Run the MCP server over stdio, for the workspace in the current directory.
    """;

if (args is not ["mcp"])
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var builder = Host.CreateApplicationBuilder();
// stdout carries the MCP protocol, so every log goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(new Workspace(Environment.CurrentDirectory));
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new()
        {
            Name = "monogame-agenttools",
            Version = typeof(Workspace).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
        };
        o.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithTools<GameTools>()
    .WithTools<SheetTools>()
    .WithTools<GifTools>()
    .WithTools<GuideTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
