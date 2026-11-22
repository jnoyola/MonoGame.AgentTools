# MonoGame.AgentTools

Close the loop on agentic game dev. Your coding agent builds, plays, captures and profiles your MonoGame
game, then iterates autonomously.

- **Deterministic capture**: the game's real `Update` and `Draw` at a fixed step with a hidden window,
  or paced in real time for a profile.
- **Input and replay**: scripted keyboard, mouse, gamepad and text, and every session you play recorded
  so the agent can replay it frame for frame.
- **Contact sheets, before-and-after comparisons and GIFs** for judging motion.
- **Measurements with verdicts**: frame times, warm-up, allocations, GC, memory growth, JIT, draw calls,
  hitches and your own custom metrics.
- **MCP server** (`MonoGame.AgentTools.Mcp`), added to your repository's `.mcp.json` when you build.
- **Free when off**: Debug builds only by default; elsewhere the calls are compiled out and the assembly
  is left out of the output.

```xml
<PackageReference Include="MonoGame.AgentTools" Version="0.2.0" />
```

```csharp
using MonoGame.AgentTools;

using var game = new MyGame();
AgentTools.Attach(game, args);
game.Run();
```

Requires MonoGame 3.8.5 DesktopGL and a game targeting .NET 8 or later. The MCP server runs through
`dotnet dnx`, which needs the .NET 10 SDK or later on your machine.

Build in Debug, then ask your agent to capture the game.

Documentation, examples and images: https://github.com/jnoyola/MonoGame.AgentTools
