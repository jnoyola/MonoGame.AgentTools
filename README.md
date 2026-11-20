# MonoGame.AgentTools

Let a coding agent run your MonoGame game, see what it draws and measure how it performs.

![An earth slam effect, captured frame by frame and played back as a GIF](docs/images/earth-slam.gif)

*Captured with the `capture` tool and turned into a GIF with the `gif` tool. The effect is from a
MonoGame visual-effects study.*

Agents can read and write game code, but they can't see the game. MonoGame.AgentTools gives them eyes
and a stopwatch, through an MCP server and a small NuGet package:

- **Deterministic capture.** The game's real `Update` and `Draw` run at a fixed time step with a hidden
  window, so the same build and arguments produce the same frames every time.
- **Contact sheets.** Frames side by side in one labeled image, the way animation and VFX reviews judge
  timing and spacing. Agents read motion from these.
- **Before and after.** The same frames from two captures, with every changed pixel marked.
- **GIFs for people.** Agents can't watch an animation; you can. Any capture becomes a GIF that plays on
  a double-click.
- **Measurements that say what they mean.** Frame times, warm-up, allocations, GC, memory growth, JIT,
  draw calls and hitches, each with a verdict, plus numbers from your own game.
- **Free when off.** Debug builds only by default. In other builds the calls are compiled out and the
  assembly is left out of the output. The tools allocate nothing per frame.

## Quick start

1. Reference the package from your game's executable project:

   ```xml
   <PackageReference Include="MonoGame.AgentTools" Version="0.1.0" />
   ```

2. Attach it in `Main`, before `Run`:

   ```csharp
   using var game = new MyGame();
   AgentTools.Attach(game, args);
   game.Run();
   ```

3. Build in Debug. The build adds the MCP server to `.mcp.json` at the root of your repository:

   ```json
   { "mcpServers": { "monogame-agenttools": { "type": "stdio", "command": "dotnet", "args": ["dnx", "MonoGame.AgentTools.Mcp@0.1.0", "--yes", "mcp"] } } }
   ```

   Commit it, and every agent that reads `.mcp.json` (Claude Code, for example) can use the tools. See
   [MCP server](docs/mcp.md) for VS Code, Cursor and other clients.

Then ask your agent something like *"capture the game and check the explosion's timing"*.

Your `Update` and `Draw` overrides should call `base.Update(gameTime)` and `base.Draw(gameTime)`, as the
MonoGame templates do. The [Fireworks sample](samples/Fireworks) is a complete game set up this way.

## What your agent sees

### A capture

The agent calls `capture`. The tool builds the game, runs it and returns a short reading of the run and
a labeled contact sheet:

```json
{ "seconds": 6, "name": "fireworks", "sheet": { "from": 1, "to": 3, "count": 8 } }
```

```text
Captured Fireworks to .agenttools/captures/fireworks
Sheet .agenttools/captures/fireworks/sheets/f60-f180_c4_n8.png (1286x362), 4 per row: f60 1.000s, f77 1.283s, f94 1.567s, f111 1.850s, f129 2.150s, f146 2.433s, f163 2.717s, f180 3.000s
360 frames at 60 fps: 6 s game time, 2.86 s wall, 1280x720
Notes: Esc - exit
Warm-up: memory stable at f150; performance stable at f277
Frame CPU ms (after warm-up): p50 0.35, p95 0.53, p99 0.8, max 0.88
Allocations after memory stable: game thread 0 B, other threads 0 B
Growth after memory stable: too short to judge: 1.5 s after the memory gate, needs 5 s; heap 0, private +4 MB, GPU resources 0 (heap counts uncollected garbage)
GC: none · JIT on game thread: 20 methods, 1.27 ms
Render per frame (mean/max): draws 1/1, primitives 498/634, target switches 0/0, sprites 249/317
Hitches (> 2.33 ms): none
Custom metrics (first → last, min to max):
  particles: 1 → 249, 1 to 318
```

![A contact sheet of eight fireworks frames, each labeled with its frame number and game time](docs/images/fireworks-sheet.png)

The agent reads the sheet as an image, so it can judge what's on screen and how it moves, then zoom in
with `contact_sheet` (a shorter time range, a crop) without running the game again. Here is a sheet of
the effect at the top of this page:

![Twelve frames of an earth slam effect: impact, dust ring, rocks thrown up and settling](docs/images/earth-slam-sheet.png)

### Before and after

Capture a baseline with a `name`, make a change, and capture again with `sheet: { against: "<name>" }`.
Each row shows the same frame from both captures and every pixel that changed, in red. Captures are
deterministic, so any red is a real difference. Here, bloom is off in A and on in B:

![Three rows: the effect without bloom, with bloom, and the changed pixels in red](docs/images/bloom-comparison.png)

### A GIF for you

When a choice is about how something feels in motion, the agent can make a GIF and give you its path.
`against` plays the two versions side by side:

![The same effect without and with bloom, playing side by side](docs/images/bloom-ab.gif)

### Performance

For numbers that describe the game rather than the capture, the agent captures without saving frames, in
real time, for at least 5 seconds:

```json
{ "seconds": 8, "save": "none", "realtime": true }
```

```text
480 frames at 60 fps: 8 s game time, 8.4 s wall, 1280x720
Warm-up: memory stable at f62; performance stable at f193
Frame CPU ms (after warm-up): p50 0.14, p95 0.17, p99 0.2, max 0.27
Allocations after memory stable: game thread 0 B, other threads 6 KB
Growth after memory stable, 7 s: steady: no GPU resources leaked (private bytes are judged from 30 s); heap +8 KB, private +6.3 MB, GPU resources 0 (heap counts uncollected garbage)
GC: none · JIT on game thread: 20 methods, 1.4 ms
Render per frame (mean/max): draws 1/1, primitives 492/642, target switches 0/0, sprites 246/321
Hitches (> 2.13 ms): none
```

Numbers taken while code is still compiling or memory is still growing describe startup, not the game,
so two warm-up gates decide when measurements count. Any game-thread allocation after the memory gate is
a per-frame allocation worth fixing. See [Performance](docs/performance.md) for how to read each line.

## While you play

```
MyGame --agenttools-hud
```

The HUD shows the same measurements in the corner while you play: frame times, allocations, GCs, memory,
GPU resources, JIT, the warm-up gates, draw calls and your custom metrics. Backtick toggles it.
`--agenttools-stats out/live.json` records a whole session and writes it when the game exits. See
[Live play](docs/live.md).

<!-- TODO: docs/images/hud.png, a screenshot of the Fireworks sample with the HUD on. -->

## Telling the agent about your game

```csharp
AgentTools.Attach(game, args, new()
{
    Notes = "Controls:\nEsc - Exit",
    CustomMetrics = [("particles", () => game.ParticleCount), ("enemies", () => game.Enemies.Count)],
});
```

Notes appear on the HUD and in every capture's summary. Custom metrics are recorded every frame and
summarized (first, last, min, max), so an agent can check that the particle count really settles or that
the enemies really spawned. Return `double.NaN` for a frame with no value. They run on the game thread
every frame, so they must not allocate.

Your game's own command-line flags pass through `gameArgs`, so a scene or camera setup is a capture
argument rather than a code change.

## Documentation

- [MCP server](docs/mcp.md): setup for each client, and every tool with examples
- [Capture](docs/capture.md): command-line options, determinism and output files
- [Performance](docs/performance.md): warm-up gates, allocations, memory growth and hitches
- [Live play](docs/live.md): the HUD and session stats
- [Design](docs/design.md): how it works and why, with the precedents it follows

Agents get the same guidance from the server's `guide` tool.

## Requirements

- MonoGame 3.8.5 DesktopGL, .NET 8 or later.
- The MCP server needs the .NET 10 SDK (for `dotnet dnx`).
- Tested on Windows. CI builds and runs the unit tests on Linux and macOS too, but captures haven't been
  run there yet.
- Pixels can differ slightly across GPUs and drivers, so comparisons across machines need a tolerance.

## Enabling in other configurations

Everything follows one property, true by default in Debug:

```xml
<AgentToolsEnabled>true</AgentToolsEnabled>
```

Set it to `true` to use the tools in Release (the MCP `capture` tool takes `configuration: "Release"`), or
`false` to turn them off in Debug too.

## Building from source

The repository builds with the .NET SDK pinned in [`global.json`](global.json) (11.0.100) and runs its
tests on .NET 11:

```
dotnet build
dotnet test
dotnet format --verify-no-changes
```

CI runs the same three on Windows, Linux and macOS, in Debug and Release, and they must pass before a
pull request can merge.

## License

MIT. See [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md).
