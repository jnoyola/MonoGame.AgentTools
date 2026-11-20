# MonoGame.AgentTools

Let a coding agent run your MonoGame game, see what it draws and measure how it performs.

- **Deterministic capture**: the game's real `Update` and `Draw` at a fixed step with a hidden window.
- **Contact sheets, before-and-after comparisons and GIFs** for judging motion.
- **Measurements with verdicts**: frame times, warm-up, allocations, GC, memory growth, JIT, draw calls,
  hitches and your own custom metrics.
- **MCP server** (`MonoGame.AgentTools.Mcp`), added to your repository's `.mcp.json` when you build.
- **Free when off**: Debug builds only by default; elsewhere the calls are compiled out and the assembly
  is left out of the output.

```xml
<PackageReference Include="MonoGame.AgentTools" Version="0.1.0" />
```

```csharp
using var game = new MyGame();
AgentTools.Attach(game, args);
game.Run();
```

Requires MonoGame 3.8.5 DesktopGL and a game targeting .NET 8 or later. The MCP server runs through
`dotnet dnx`, which needs the .NET 10 SDK or later on your machine.

Build in Debug, then ask your agent to capture the game.

Documentation, examples and images: https://github.com/jnoyola/MonoGame.AgentTools
