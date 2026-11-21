# Design

How MonoGame.AgentTools works and why: each decision with the engine or tool precedent it follows, the
alternatives considered, and the measurements behind it. Dated entries record decisions as they were
made. For using the tools, start with the [README](../README.md).

Status: v0.1 implemented (batch capture, live stats, warm-up gates, hitch report, Release stripping, MCP
server with contact sheets, comparisons and GIFs). It was extracted from the capture harness, profiler
and HUD of a MonoGame 3D visual-effects game, the "sample game" that many entries' measurements come
from.

## Purpose

Close the visual feedback loop for development: let a coding agent run the game deterministically,
see the result (frames + numbers), drive input, and iterate. Agents are the primary audience, always.

**Principle: agent-first data, human views.** Every feature exposes structured data and an MCP tool
first. Human-facing UI is a view of that same data — the HUD shows a human exactly what the agent can
read. No feature exists only as pixels on a HUD.

## Packages

| Package | Kind | Contents |
|---|---|---|
| `MonoGame.AgentTools` | Library (`PackageReference`) | In-game runtime: deterministic stepping, capture, custom metrics, input sources, frame timing, render metrics, HUD, session pipe server. `build/` targets. |
| `MonoGame.AgentTools.Mcp` | .NET tool, `McpServer` package type | MCP server (official C# SDK `ModelContextProtocol`): launches/supervises the game, post-processing (contact sheets, A/B, diffs, GIF/strips, metric summaries), server instructions + `guide(topic)` tool. Same executable can host CLI verbs later (e.g. CI image compare). |

Versions are published in lockstep. A tool package cannot also be a library reference, hence two IDs;
the consumer only ever references the library.

Naming: `MonoGame.` prefix is open to third parties (MonoGame.Extended, MonoGame.Aseprite, …; only the
official account shows "verified"). `MonoGame.DevTools` rejected: reads as official next to the real
`MonoGame.Framework.DevTools` (3.8.5+, a `NetworkContentProvider`).

## Onboarding (one thing)

1. `PackageReference MonoGame.AgentTools`
2. One call in `Main`. Zero-config is the goal (capture drives the game's real `Update`/`Draw`, see
   Runtime); registration is optional enrichment:
   ```csharp
   AgentTools.Attach(game, args);                       // enough for capture, stats, HUD
   AgentTools.Attach(game, args, new()                  // optional
   {
       CustomMetrics = [("particles", () => _particles.Count)],
       Input = state => _injected = state,              // fallback only, if automatic injection's self-test fails
   });
   game.Run();
   ```
3. Build. The package's targets merge one entry into `.mcp.json` at the repository root (the folder
   with `.git`), and into `.vscode/mcp.json` (`servers`) and `.cursor/mcp.json` where those folders
   exist, as Aspire's `aspire agent init` configures the agents it detects:
   ```json
   "monogame-agenttools": { "type": "stdio", "command": "dotnet", "args": ["dnx", "MonoGame.AgentTools.Mcp@<same version>", "--yes", "mcp"] }
   ```
   Identical for every user (no machine paths), safe to commit, rewritten only when the version
   changes. `dotnet dnx`, not `dnx`: on Windows `dnx` is a .cmd script, which clients that start
   servers without a shell can't run (Aspire's docs note the same for `npx`). `--yes` skips dnx's
   first-run download confirmation, which would hang a stdio MCP server.
   Opt out: `<AgentToolsInstallMcp>false</AgentToolsInstallMcp>`; root override: `<AgentToolsRoot>`.
   - Merge, never overwrite: Playwright's `init-agents`, Claude Code's own `claude mcp add` and Laravel
     Boost have all had bugs replacing other servers. Edits are minimal text insertions (VS Code's
     jsonc-parser approach to settings.json), so comments, trailing commas and formatting survive;
     `.vscode/mcp.json` often has comments.
   - Only an entry this package wrote is updated (its own command, an older version); a hand-edited one
     is left alone, as Aspire updates only its own deprecated forms. Unparsable files get a warning
     (AGT001) with the entry to add by hand.
   - It follows `AgentToolsEnabled` like everything else (on in Debug by default, by property
     otherwise): the `Attach` call, the assembly in the output and this entry. The MCP `capture` tool builds Debug unless given `configuration`, and reads the evaluated
     `AgentToolsEnabled` from the build (`-getProperty`) so a build without the tools fails at once with
     the property to set, instead of running a game that ignores the capture options until the timeout.
   - Most tools write this from an explicit command (`aspire agent init`, `claude mcp add`). NuGet
     PackageReference has no install hook, so a build target is the only automatic one; it runs in
     Debug builds only and is skipped in CI (`CI`, `TF_BUILD`, `ContinuousIntegrationBuild`) and
     design-time builds. The file is held exclusively while editing, for parallel project builds.
   - The task is source compiled by RoslynCodeTaskFactory (`build/McpConfig.cs`), so it runs under both
     the .NET SDK's MSBuild and Visual Studio's .NET Framework MSBuild (both verified); that's also why
     it uses no JSON library. The package version comes from a `build/MonoGame.AgentTools.props`
     written at pack time; a project reference has none, so the stand-in in this repo skips it.

No skill files are copied. Agent guidance ships in the MCP server: short server `instructions`
(always loaded — keep to a few lines), detailed tool descriptions, and a `guide(topic)` tool for long
material (progressive disclosure, like a skill). Project-specific taste stays in the consumer's CLAUDE.md.

Precedent: `npx @playwright/mcp` / `npx playwright run-test-mcp-server` (name-resolved MCP server),
NuGet MCP server packages run via `dnx`, Chrome DevTools MCP (agent eyes/hands on a running app),
Playwright for .NET shipping its out-of-proc driver inside the NuGet package.

## Release stripping

Every public entry point is `void` and `[Conditional("AGENTTOOLS")]` (precedent: Unity
`Profiler.BeginSample` under `ENABLE_PROFILER`, `Debug.Assert`). Targets define `AGENTTOOLS` in Debug,
or when `<AgentToolsEnabled>true</AgentToolsEnabled>`. Otherwise the compiler removes the calls
(arguments and lambdas included) and the targets drop the DLLs from copy-local/publish.

Consequences for the API:
- No value-returning entry points: `BeginScope/EndScope`, not `using (Scope())`.
- Consumers never implement AgentTools interfaces (that would keep the reference alive in Release);
  everything is passed to `Attach` as an options object.

## Runtime

Rule: use what MonoGame provides before adding anything. The sample game's own harness was a starting
point, not a reference design. Findings from MonoGame `develop` (76ed1c2, 2026-11-03) and MonoGame.Extended
(33a6d4c, 2026-10-09) source are inline below.

- **Deterministic stepping.** `Game.Tick()` is public, but its clock is a private `Stopwatch`. Even with
  `IsFixedTimeStep`, the number of updates per draw depends on wall time (hitches run catch-up
  updates), so `Tick` can't produce deterministic captures. Instead the harness calls the game's
  protected `Update(GameTime)`/`Draw(GameTime)` itself through delegates compiled once via reflection
  (Debug-only, and these signatures have been stable since XNA). It passes a synthetic `GameTime` with
  fixed `ElapsedGameTime`. This captures the *real* game loop with no reset/step/render contract from
  the game. Experimental: verify against a scenario-driven capture. Precedent for driving the real loop
  at a fixed rate: Unity `Time.captureFramerate`, Godot `--fixed-fps`/`--write-movie`, Unreal
  `-deterministic -fps=N`.
- **Frame readback.** `GraphicsDevice.GetBackBufferData<T>()` exists on DirectX, OpenGL and Web, so
  capture can read what the game actually drew, before `Present`. Risk: on OpenGL, pixels of a hidden
  or obscured window's default framebuffer are undefined (pixel-ownership test). Verify on DesktopGL
  with a hidden window; fallback is redirecting into a render target (the sample game's approach at the
  time), which needs the game's cooperation.
- **Hidden window** is the "headless" mode (capture runs before the window shows).
  Real headless/CI (WARP, llvmpipe) is out of scope for now.
- **Modes.** Batch: `--agenttools-capture` writes frames/metrics and exits (works without any agent).
  Session: `--agenttools-session <pipe>` starts paused and serves JSON-RPC (step, render, input,
  probe, set param). The MCP server runs out of process so it survives game crashes and rebuilds.
- **Spike result (2026-11-16, the sample game on DesktopGL 3.8.5, 2560x1440 backbuffer).** Drove the real
  `Update`/`Draw` via reflection-bound delegates with a synthetic 1/60 s `GameTime`, inside
  `LoadContent` before the window is shown.
  - Hidden-window `GetBackBufferData` matched the visible-window readback of the same frame exactly
    (0 of 3.69M pixels differ). Backbuffer readback is the capture path; render-target redirection is
    no longer needed as a fallback on DesktopGL. WindowsDX is still to check.
  - Two process runs: all 90 frames identical outside the HUD, and `GraphicsDevice.Metrics` identical
    every frame. Only the HUD differed, because it shows wall-clock fps and process memory.
    Consequence: the HUD must render from harness-provided time and stats during capture, or be
    hidden during capture.
  - Cost: about 65 ms/frame at 2560x1440 including readback and SHA-256; about 26 ms/frame at 1280x720.
- **Standard capture resolution.** Default 1280x720 (16:9). In capture mode, `Attach` sets
  `GraphicsDeviceManager.PreferredBackBufferWidth/Height` from `game.Services` after the game's
  constructor and before `Run`. Verified: the sample game's constructor asks for the display size (2560x1440
  here), the override took effect (1280x720 backbuffer), and captures stayed deterministic across
  runs. `--capture-size WxH` overrides it. This keeps framing and layout the same on every machine.
  It doesn't make pixels bit-identical across GPUs or drivers (filtering and precision differ), so
  comparing captures across machines needs a tolerance. Precedent: Unity Graphics Test Framework and
  Unreal screenshot comparison both fix the resolution per test and keep separate reference images per
  platform/graphics API.
- **Input.** MonoGame has no public way to inject input; the preferred path writes MonoGame's internal
  state per backend (below). The cooperative `.Input(...)` hook is the fallback. Background: on SDL, `Keyboard` state comes from the
  platform's event pump (internal `Keyboard.SetKeys`), and `Mouse.GetState` reads the *global OS cursor*
  (`SDL_GetGlobalMouseState`), so injecting mouse input would mean moving the user's real cursor. Not
  acceptable. So input needs one game-side hook. The data passed through it is MonoGame's own
  `KeyboardState`/`MouseState`/`GamePadState` (all constructible), not new event types: the harness
  pushes states through `.Input(...)` and the game uses them in place of `GetState()`. This slots into
  the same layer as MonoGame.Extended's `KeyboardExtended`/`MouseExtended` (static facades with
  previous-state tracking). Scripted input is frame-stamped states; `--record` captures live play.
  Precedent: Unity Input System `InputTestFixture`, Doom/Factorio demos.
  **Cross-platform zero-cooperation injection (preferred).** In capture and session modes the harness
  calls `Update` itself, so it can write MonoGame's own input state just before each call. Every
  reader then sees it: the game, MonoGame.Extended, UI libraries. Per backend (MonoGame `develop`
  76ed1c2):
  | Backend | Mouse | Keyboard |
  |---|---|---|
  | WindowsDX | `GameWindow.MouseState` (internal field, returned as-is) | `Keyboard._keys` list (released keys are pruned only while the window is active) |
  | Native (3.8.5+) | `GameWindow.MouseState` ("mouse events keep this updated") | `Keyboard.Keys` (internal static list) |
  | Default / others | `GameWindow.MouseState` | `Keyboard._keys` |
  | DesktopGL (SDL) | **recomputed from the OS cursor every call** → swap `Sdl.Mouse.GetGlobalState` (below) | `Keyboard._keys` (the platform's list; overwrite its contents) |
  A self-test runs at attach: write a known state, call `Mouse.GetState()`/`Keyboard.GetState()`,
  compare. If MonoGame internals have changed, it fails loudly at startup instead of silently
  injecting nothing, and falls back to the cooperative hook. Gamepad: `GamePad.GetState` goes through
  per-platform statics in the same way (to inventory).
  **Compile-time alternative considered: C# interceptors.** A source generator in the package rewrites
  the game's own `Keyboard.GetState()`/`Mouse.GetState()` call sites (stable API since the .NET 9
  SDK, `InterceptableLocation`; used by ASP.NET Core's Request Delegate Generator). It works on every
  platform with no internals, and emits nothing in Release. Rejected as the primary path because it
  only covers call sites compiled with the generator: it misses `Keyboard.GetState()` called inside
  third-party DLLs such as MonoGame.Extended's `KeyboardExtended`, and window `TextInput` events.
  **Zero-cooperation, DesktopGL detail.** MonoGame calls SDL through
  delegates stored in writable static fields of the internal `Sdl` class: `Sdl.PollEvent`,
  `Sdl.Mouse.GetGlobalState`, `Sdl.Keyboard.GetModState`, `Sdl.Window.GetWindowFlags`. Swapping them
  (via reflection, Debug-only) injects input below MonoGame:
  - wrap `PollEvent` to deliver the real events first, then queued synthetic `SDL_Event`s (key
    down/up, text input, wheel), so `Keyboard`, `Window.KeyDown`/`TextInput` and the scroll wheel all
    see them;
  - replace `GetGlobalState` to report the scripted cursor position (window client origin + local
    x/y) and button mask, without touching the real cursor.
  No IL patching needed. The cost is depending on MonoGame internals, so pin the tested MonoGame
  versions and fall back to the `.Input(...)` hook when the fields aren't found. Other platforms
  (WindowsDX reads WinForms messages; Native differs) would need Harmony-style method patching of
  `Mouse.GetState`/`Keyboard.GetState`, which risks being bypassed where the JIT has inlined those
  small methods. The cooperative hook stays the documented, portable path.
- **Render metrics.** Use `GraphicsDevice.Metrics` as-is: draws, primitives, sprites, clears, render
  targets, and texture/vertex-shader/pixel-shader *switches* (counted on DirectX, OpenGL and Native).
  It resets in `Present()`, has a public setter, and defines `+`/`-` operators, so per-pass scopes are
  just snapshot deltas: `BeginScope("bloom")`/`EndScope()` records `Metrics` delta + CPU time.
  Precedent: Unity profiler markers, Unreal `SCOPE_CYCLE_COUNTER`, PIX/RenderDoc markers. It replaces
  counting primitives by hand in a game's own HUD. No GPU timing: MonoGame exposes no timestamp queries;
  use RenderDoc/PIX.
  Note: a game whose `Update`/`Draw` don't call `base.Update`/`base.Draw` never runs `game.Components`
  (the sample game didn't call them at first). The MonoGame templates do call them. **Decision:** live play relies on the convention
  (`base.Update`/`base.Draw` drive the AgentTools components). `Attach` detects a component that never
  ran (no Update/Draw callback within the first second of live frames) and warns once with the fix.
  Harness-driven frames don't depend on it: the harness updates and draws its own pieces around the
  game's calls. Rejected: Harmony-patching `Game.DoUpdate`/`DoDraw` (a runtime-patching dependency for
  a convenience), and a harness-owned live loop (it would have to replicate each platform's event
  pump).
  Caveat (live play only): `EndDraw` is protected and can't be hooked from outside, so live stats are
  read in a `DrawableGameComponent` with `DrawOrder = int.MaxValue` inside `base.Draw`. Anything drawn
  after `base.Draw` lands in the next frame's numbers. In harness-driven frames this doesn't apply,
  because the harness reads `Metrics` after `Draw` and before `Present`.
- **Frame timing.** MonoGame.Extended's `FramesPerSecondCounter`/`Component` counts `Draw` calls over
  one-second windows: an integer fps, no frame-time distribution. Take its shape (an
  `IUpdateable`/`DrawableGameComponent` added to `game.Components`), but record a ring buffer of
  per-frame CPU update/draw times, giving p50/p95/p99, hitches and `GameTime.IsRunningSlowly`. Fps is
  derived from that.
- **Warm-up / stabilization (generic).** The sample game's own profiler waited 60 draws (for first-use
  costs: JIT, shader/texture upload) plus 0.5 s (for heap growth), forced a full GC, then recorded a
  memory/GC baseline. Measured on the sample game (1800 frames at about 1 ms each, two runs, 1280x720):
  - **JIT is the long pole, and it runs on wall time, not frames.** Compiled methods: 1725 at frame
    60, about 2820–2920 at 1.0 s wall, settled about 1.4–1.7 s. Tiered compilation recompiles hot
    methods in background batches on a wall-clock delay. At 1000 fps, "60 draws" is 60 ms, far too
    early; at 60 fps it's 1 s. Warm-up windows must be in wall seconds.
  - **New code paths JIT on first use.** Frame 121 (sim t = 2.0 s, the first effect firing) compiled
    110 methods and took about 14.5 ms in four of five runs (164 ms in the first run after a build,
    likely a cold driver shader cache; unconfirmed). Breakdown: about 10 ms game-thread JIT, about
    4.5 ms GPU/driver first use (first instanced draw with the particle shader, first vertex-buffer
    upload), 5.2 KB one-time type initialization. So a per-frame JIT delta is also the best way to
    explain a hitch, and "stable" can be broken later by new content (report it, don't hide it).
  - **Pre-running first-use costs (measured).** `RuntimeHelpers.PrepareMethod` over every method of the
    game and MonoGame assemblies took 340–400 ms and only cut frame 121 to 9 ms: it misses generic
    instantiations over game types (`Array.Sort<Particle>`, `SetData<ParticleVertex>`) and all GPU
    setup. Firing each effect once at load and running one simulate + draw took 52 ms and cut frame
    121 to 0.6 ms (frame 1 also fell from 37 ms to 0.8 ms). This belongs in the game, not the
    package: the sample game now prewarms every effect at load, restoring its RNG and pools afterwards,
    and its captures stay byte-identical. Precedent: Unity
    `ShaderVariantCollection.WarmUp`, Unreal PSO precaching (5.1+), Godot 4.4 pipeline precompilation,
    and pooled effects fired during loading screens. The package's part is to find and attribute these
    hitches (Hitch report, below) so the game knows what to prewarm.
  - **Game-thread allocations** were zero after frame 145. No GCs after startup.
  - **`GC.GetTotalMemory(false)` is exactly flat from 0.29 s.** It changed only at frame 121 (first-use
    JIT) and frame 350. An earlier run showed steady growth, but that was the measuring harness itself:
    a per-frame `StringBuilder` log, and `new GameTime(...)` every frame (`GameTime` is a 40-byte class).
    With both removed, the heap is flat. Lessons for the harness: reuse one `GameTime`, and never
    allocate in the frame loop. Check instrumentation allocations: `GC.GetTotalMemory`,
    `GetTotalAllocatedBytes`, `CollectionCount`, `JitInfo.*` and `Stopwatch.Elapsed` allocate 0 B per
    call; `Environment.WorkingSet` about 1.5 B; `Process.Refresh()` + `WorkingSet64` about 3 KB.
    `Process.WorkingSet64` without `Refresh()` returns its first value forever, so a HUD reading it that
    way shows memory that never moves.
  - `GetTotalMemory` moves in about 8 KB steps (allocation-context chunks), so a game allocating a few
    bytes per frame can look flat for a while. Report exact per-frame allocations
    (`GC.GetAllocatedBytesForCurrentThread()`) alongside it.
  - **Frame time is noisy from the OS** (isolated 13 ms spikes with no JIT activity), so the
    coefficient of variation is unreliable. Use robust statistics instead (p95/p50 over the window).

  Two separate gates, because they protect different numbers:
  - **Memory baseline** (for the "Additional" memory/GC metrics): `GC.GetTotalMemory(false)` unchanged
    for at least 1 s wall, with no GC. If it never holds within the cap, Additional reports *never stabilized* plus the
    measured allocation rate. JIT doesn't affect this gate, because it uses native memory.
  - **Performance** (frame-time statistics): the full rule below. JIT continues for about 1 s after
    memory is already flat, and frame times aren't representative until it stops.

  Rule (performance gate). Phases `WarmingUp → Stable → Measuring`. Stable when, over a trailing **1 s wall** window:
  - JIT compiled-method delta is 0;
  - game-thread allocated bytes are 0 (or below a configurable threshold) and no GC occurred;
  - plus a minimum of 1 s since start.
  Frame-time spread (p95/p50 below 1.5) was a condition until the sample game showed it never holding: uncapped
  at about 1,000 fps, frames of 0.9 ms with 0.7 ms of jitter give 1.8. Removed (2026-11-17): uncapped
  games jitter by nature, and the gate's job is to say first-use costs are over, which JIT and
  allocations already show. The spread is reported, not gated.
  Cap at 10 s. If not stable by then, `stable=false` names the failing signal(s), and the "Additional"
  memory/GC metrics are reported as *never stabilized* (with the measured allocation rate) instead of
  being measured against a premature baseline. On becoming stable, optionally force a full GC, then
  record the baseline.

  While measuring, track allocations per frame with `GC.GetAllocatedBytesForCurrentThread()` (exact
  and cheap, and directly tests the zero-allocations-per-frame rule), plus GC collections per
  generation. This replaces `GetTotalMemory` deltas, which only change when a GC runs. The agent sees
  the phase, the frame where it stabilized and why, so perf numbers are never read during warm-up.
  Precedent: BenchmarkDotNet's warm-up stage with steady-state detection; Unreal `-benchmark`
  warm-up frames. Warm-up only gates *performance* numbers; deterministic visual captures don't need
  it.
- **HUD.** A view of the metric data the agent reads, as a `DrawableGameComponent` using
  `SpriteBatch` + `SpriteFont` (MonoGame.Extended has no stats overlay beyond the fps counter). Needs
  its own font that doesn't depend on the consumer's content pipeline; no custom-font option.
  Decided: ProggyClean (MIT), Dear ImGui's built-in default font, embedded as generated glyph data
  (see Implementation).

## Implementation (v0.1)

Layout: `src/MonoGame.AgentTools` (net8.0, compiles against `MonoGame.Framework.DesktopGL` 3.8.5 with
`PrivateAssets=all`, so the consumer's platform package supplies MonoGame), `tests/` (xunit v3 on
Microsoft.Testing.Platform), `build/` targets. A game in the same build can reference it as a project
plus an explicit `Import` of the targets, the dev-time stand-in for the package (the input probe in
`tests/` does).

Decisions made while building it, each verified by running:

- **Capture takes over before `Run`, not in `LoadContent`.** `Attach` calls MonoGame's
  `GamePlatform.BeforeRun`, internal `Game.DoInitialize` (device, `Initialize`, `LoadContent`) and
  `BeginRun`, then drives frames itself and exits. MonoGame shows the window only in the platform's
  `RunLoop`, which never starts. No override needed in the game. Like the real `Run`, one `Update`
  with a zero `GameTime` precedes frame 1. Each frame: `FrameworkDispatcher.Update`, `Update`,
  `BeginDraw`/`Draw`, read `Metrics`, read back, then the game's own `EndDraw` (presents).
- **Options are prefixed `--agenttools-`** so they can't collide with the game's own (a game may
  already have a `--capture` of its own, as the sample game did). Invalid options print usage and exit 2; a failed capture writes `error.txt` and
  exits 1.
- **Memory gate uses exact allocations, not `GetTotalMemory`.** "No game-thread allocations and no
  GC for 1 s" implies an exactly flat heap, has no 8 KB granularity, and isn't polluted by the
  harness's own allocations (PNG encoding happens outside the measured `Update`/`Draw`). The heap is
  still reported.
- **Wall-time gates need wall-time pacing.** Batch capture runs faster than real time (20 s of game
  time in 1.4 s), so gates measured in wall seconds never settle. `--agenttools-realtime` paces frames
  to the fps the way vsync does (since v0.3, a profile does this; see below). Performance runs: `--agenttools-save none --agenttools-realtime`,
  5 s or more; on the sample game memory settles at frame 62 and performance at frame 242, identically across
  runs. Saving PNGs also tiers up the harness's own code in the background, which the all-threads JIT
  signal can't tell apart from the game's.
- **JIT is split by thread.** `JitInfo.GetCompiledMethodCount(currentThread: true)` counts the compiles
  that block the frame; all-thread counts include background tiering (which changes code speed, so the
  performance gate waits for it). With `--agenttools-jit-names`, an `EventListener` on the runtime's
  JIT events names the methods; MethodFlags bits 7–9 give the tier, and tiers 1–3 (MinOpts,
  Optimized, QuickJitted) are the blocking ones, matching thread IDs in the measurements.
- **The tools must not perturb what they measure.** Live verification found the monitor itself
  causing a 24 B allocation (the runtime's sort helper, first created when the frame-time check first
  sorted) and first-use JIT. Fix: the monitor precompiles its own non-generic per-frame types at install
  and warms the generic sort with a realistic size (its partition path only runs above 16 elements).
  Result on a sample game: 0 game-thread allocations and 0 game-thread JIT after warm-up.
- **PNG and contact sheet without the GPU.** A small RGB PNG encoder (Sub filter, in-house zlib encoder;
  see "The capture allocates nothing" below) and a box-filtered 12-frame sheet; no texture round trip.
- **HUD font: ProggyClean as text art, read at startup.** Dear ImGui builds the same font in for
  zero-setup debug UI; Nez embeds a prebuilt `.xnb` (`NezDefaultBMFont`); MonoGame embeds its stock
  effects as raw bytes loaded through the `Effect` constructor, bypassing `ContentManager`. Here
  `Hud/Fonts/ProggyClean.txt` is the checked-in source: each glyph is a `U+XXXX` line and 13 rows of
  `.`/`#` (7×13 cells, ASCII 32–126), so no raw bytes are checked in and a diff shows the pixels.
  `tools/ImportFont.cs` (a .NET 10 file-based app, `dotnet run tools/ImportFont.cs`, so contributors need
  only the .NET SDK) re-imports it from the upstream PCF and records its SHA-256. `PixelFont`
  parses the embedded text once (strict, with line-numbered errors) and builds a 112×78 atlas through
  the public `SpriteFont` constructor: no content pipeline, no `.xnb` format coupling. Parsing at
  startup rather than generating at build: it costs well under a millisecond once, and a unit test
  catches malformed data before release just as a build step would. Pixel font, so `PointClamp` at
  integer scales. License in `THIRD-PARTY-NOTICES.md`, packed with the package.
- **HUD** (`ShowHud`; backtick toggles; in captures and profiles with `--agenttools-hud` since v0.4). Part of the live monitor, so it shows exactly
  what the stats files record. Drawn after the frame's sample is taken, then the counters are re-read,
  so its draws, allocations and JIT never count against the game (`GraphicsDevice.Metrics` is sampled
  before it draws). It restores the device state `SpriteBatch` changes (blend, depth, rasterizer,
  sampler 0, texture 0, viewport, scissor), as Dear ImGui's renderer backends do. Integer font scale
  `max(1, backbufferHeight / 2160)`: 1x up to 4K, since a larger window should show more of the game,
  not bigger debug text (pixel fonts only scale by whole steps; Dear ImGui likewise scales by display
  DPI, not window size, and MonoGame exposes no DPI on DesktopGL). Anchored top-right like Unreal's `stat fps`/`stat unit`, so it stays clear of a game's own
  top-left debug text, with text left-aligned and margin equal to padding. Numbers that change every
  refresh have fixed-width fields (Unreal's stat tables, RivaTuner's OSD), and the panel width follows
  the text with a 5 s peak hold (an audio meter's hold-then-release), so the edge stays still. Color
  only marks what needs attention (yellow, red); a stable gate is plain white. Backtick toggle: the developer-console key in Unreal (`stat fps`, `stat unit`), Source and Quake-lineage games (Unity's Rendering Debugger uses Ctrl+Backspace). A game that already binds backtick sees both react; making the key configurable is deferred until that happens. Was live-only
  until v0.4, since wall-clock values make captures differ between runs; see "HUD in captures". Verified on a sample game at 720p and 1440p: 0 game-thread
  allocations with the HUD on, and the performance gate settles (2.13 s vs 1.85 s without it).
  - **Text is formatted every frame and shown every 0.25 s.** The first version formatted only at
    0.25 s; tiered compilation then kept re-compiling the formatting code in the background for
    seconds (methods are promoted after about 30 calls), so the performance gate never settled.
    Running it at frame rate lets it tier up with the game's own code.
- **Garbage beyond the game thread** (2026-11-17). Per frame: allocations on other threads
  (`GC.GetTotalAllocatedBytes(precise: true)`, about 3 us, minus the game thread's exact count over the
  same interval, so the tools' own work between frames cancels out) and live graphics resources
  (MonoGame's private `GraphicsDevice._resources` set, read under its lock; there's no public count).
  Once a second: process private bytes (Windows `GetProcessMemoryInfo` PrivateUsage, Performance
  Monitor's leak counter; Linux `RssAnon`; elsewhere `Process`, which allocates and is excluded). A
  session-long memory history (1 s points, halved in place when full, round-robin-database style)
  feeds summary.json `growth` and `<stats>.memory.csv`, because live frames only cover the last 4096.
  Other-thread allocations are reported, not gated, until a game shows whether background threads
  allocate legitimately (the sample game: 0).
- **The HUD allocated during its first seconds, hidden by its own exclusion.** Measured directly: about
  300 KB on the first draw (SpriteBatch growing its buffers) and then 95, 21 and 4 KB/s of formatting
  until 3 s. The formatting came from boxing in unoptimized first-tier code: interpolated appends go
  through the generic `AppendFormatted<T>`, and `Enum.HasFlag` boxes until the optimizing JIT
  recompiles it. `AggressiveOptimization` on our methods didn't help (the boxing is in the runtime's
  generic code). Fixed with non-generic `TryFormat` into a stack buffer, bitwise flag tests, and a
  warm-up draw of a full panel into a 1x1 target at load. The sample game with the HUD: heap change 0 over 12 s.
- **Custom metric CSV columns are prefixed `custom.`** (and quoted when needed): one named `sprites`
  produced a duplicate column next to the render metric.
- **Configuration is an options object, `Attach(game, args, new() { ... })`.** `[Conditional]`
  requires a `void` return, and capture runs inside `Attach` and exits, so everything has to be passed
  into the call; the object initializer is stripped with it in Release. Options classes with `init`
  properties are the standard .NET shape for this (`JsonSerializerOptions`, `ProcessStartInfo`,
  `WebApplicationOptions`). Rejected alternatives: a configure lambda (`Action<Builder>`, ASP.NET
  Core's style) reads worse for a one-call setup; optional parameters bake their defaults into the
  caller, so adding one changes `Attach`'s signature and breaks already-compiled callers, while a new
  property doesn't. A class rather than a struct: a struct's `default` would skip property
  initializers (`CustomMetrics = []`), and it's created once.
- **Custom metrics are named numbers read every frame**, the shape of .NET's
  `Meter.CreateObservableGauge(name, Func<T>)` and Godot's `Performance.add_custom_monitor`. Numbers,
  not text: a value can be sampled without allocating, written per frame to the CSV and summarized,
  which free-form text can't. Fixed text goes in `notes`. The HUD shows them under `custom`, as
  Godot's custom monitors do;
  `customMetrics`, the `custom` HUD section and `custom.<name>` CSV columns share the word.
- **Release stripping verified through the packed package** on a scratch consumer: Debug defines
  `AGENTTOOLS` and copies the DLL; Release has no DLL in build or publish output, no entry in
  `deps.json`, no assembly reference in the compiled binary, and starts normally.

Hitch report (`summary.json` → `hitches`): frames over `max(2 × median, median + 2 ms)`, with update/draw
split, game-thread JIT count and time, allocations, collections, a likely cause (GC; JIT if it explains
at least half the excess; first use if JIT or allocations are present; otherwise unattributed
GPU/driver/OS) and, with `--agenttools-jit-names`, the compiled methods. On the sample game after its prewarm, the
only remaining hitch is frame 1 (62 methods, all named), plus one unattributed 12 ms draw in one run.

Found along the way (MonoGame, not fixed): every DesktopGL game takes a one-off ~1.1 ms game-thread JIT
at frame 9999. `SdlGamePlatform.RunLoop` is called once and loops forever, so it starts as unoptimized
code with an on-stack-replacement patchpoint, and the runtime compiles the optimized (OSR, tier flag 5)
version synchronously after about 10,000 iterations: about 2 min 47 s into play at 60 fps, long after
warm-up. Identical with and without the HUD; named with a JIT `EventListener`. A game can avoid it with
`<TieredCompilationQuickJitForLoops>false</TieredCompilationQuickJitForLoops>` (methods with loops are
then compiled optimized up front), at some startup cost; not yet measured. Findings like this, with the
fix a game applies, are collected in [MonoGame findings](monogame-findings.md). The tools never apply
them: they're compiled out of Release, so a fix only they made would measure a different game from the
one that ships.

## MCP server (v0.1)

`src/MonoGame.AgentTools.Mcp` (net10.0 .NET tool, `PackageType McpServer`, official C# SDK
`ModelContextProtocol` 2.4.0 on `Microsoft.Extensions.Hosting`, the shape of `dotnet new mcpserver`).
Started as `monogame-agenttools mcp` over stdio, with the workspace as its current directory.

- **Tools.** `list_games`; `capture` (build, run with `--agenttools-capture`, return a brief reading of
  summary.json and the 12-frame sheet as an image, with progress notifications for build and run); `contact_sheet`
  (re-lays saved frames over a time range and crop, labeled with frame and game time, without
  re-running, or compares with a baseline capture); `guide(topic)` (embedded Markdown topics: setup, capture, performance, contact-sheets).
  Server instructions are five lines; the rest is in tool descriptions and the guide, as decided under
  Onboarding.
- **Finding the game.** Executable, non-test projects under the workspace whose XML references
  `MonoGame.AgentTools` by package or project; with several, the agent passes `project`. Text matching
  rather than MSBuild evaluation: evaluating every project costs seconds, and the explicit argument
  covers the misses.
- **Building and starting it.** One `dotnet build -c Debug -t:Build -getProperty:RunCommand
  -getProperty:RunArguments -getProperty:RunWorkingDirectory -getProperty:TargetPath` builds and
  reports how to start the game: the same properties `dotnet run` uses, so custom launchers work.
  Working directory defaults to the output folder, as Visual Studio's does. Debug always: the tools
  are compiled out of Release. `-t:Build` is required: with `-getProperty` and no target, MSBuild only
  evaluates the project, so v0.1 silently ran the last binary built (found when an A/B comparison of a
  changed effect showed no change).
- **Process handling.** stdin/stdout/stderr of the game are redirected (the server's own stdio is the
  MCP channel); on timeout or cancellation the whole process tree is killed. Failures return
  `isError` with error.txt, the exit-code meaning and the tail of the game's output.
- **Output** goes to `.agenttools/captures/<name>` under the workspace. The server writes
  `.agenttools/.gitignore` containing `*`, as pytest and Ruff do for their cache folders, so nothing
  needs adding to the repository's ignore file. There's no cross-tool standard folder for agent output
  (AGENTS.md covers instructions only); Playwright MCP likewise defaults to its own `.playwright-mcp/`.
  Unnamed captures are marked and pruned to the 10 most recent on each capture (pytest keeps its last
  3 temp runs); named captures are never pruned, since they're how an agent keeps a baseline.
- **Contact sheets.** capture returns one directly (`sheet`: time range, count, columns, crop;
  default 12 frames across the run), so the common case is one call; contact_sheet re-sheets an
  existing capture, for zooming in after the first look (0.8 s vs about 9 s to rebuild and re-run).
  They're built here rather than in the game: they only need the saved PNGs, so an
  agent can re-sheet a capture without re-running it. Tiles shrink by whole factors (box filter) or
  enlarge small crops by whole factors up to 4x (nearest), so pixels stay crisp; sheets stay within
  1568 px wide, the long edge Claude keeps without downscaling. Labels use the HUD font, whose
  MonoGame-free half (`PixelFont.cs`) is compiled into both packages.
- **Before and after.** `against` (`baseline` since v0.4; on `sheet` and contact_sheet) names a baseline capture. Each row is
  the same frame from both and a diff tile: Playwright's expected/actual/diff layout, and the
  baseline comparison of Unreal's Screenshot Comparison tool and Unity's Graphics Test Framework. One
  image, because a model compares tiles side by side far more reliably than two images held in
  context, and it's the A/B a person reviewing the change asks for. Frames pair by number,
  so both captures must share fps (checked) and size; frames saved in only one are skipped and counted.
  - Any changed pixel counts, as ImageMagick `compare` with no fuzz. pixelmatch's 0.1 threshold (which
    Playwright uses) was tried first and missed a shock ring going from 150 to 255 alpha over a pale
    ground: it exists to absorb cross-machine anti-aliasing noise, and captures have none, because the
    same build and arguments draw identical frames. Brightness follows pixelmatch's YIQ difference, so
    larger changes stand out; a block shows its largest change, so one pixel survives downscaling.
  - What a game draws from wall-clock time or memory (a memory readout in its own HUD) differs in every
    frame; crop excludes it. A mask option, as Playwright's `mask`, if crops prove awkward.
  - Numbers aren't diffed: the agent reads both summary.json files and decides which fields matter for
    the change. A baseline in use is never pruned by the capture that compares against it, and a
    capture can't replace the baseline it's compared with.
  - Verified on the sample game: two captures of the same build differ only in such a readout (0.05% of
    pixels); raising an effect's shock ring alpha showed the ring in red at 2.2 to 2.4 s, 2.5% to 0.9% of
    a crop, and identical frames once it had faded.
- **Brief results by default** (2026-11-18). An independent agent doing visual checks found the
  returned summary.json mostly unread: about 3.5 KB of indented JSON per short capture, 11.5 KB with a
  long hitch list, and its `files.sheetFrames` described a sheet the agent never sees. capture now
  returns one plain-text line per check (`detail: "brief"`, the default) and the JSON on request
  (`"full"`), the concise/detailed switch Anthropic's guidance on writing tools for agents recommends.
  - Plain lines rather than JSON or Markdown: models read all three, but JSON spends tokens on quotes,
    braces and indentation, hides units in key names and leaves verdicts to inference. Each line states
    its units and verdict ("memory stable at f53", "0 B after memory stable"); the exact field names
    stay in the file, for anything that parses them.
  - Brief must not mean blind: every check keeps a line (warm-up, frame time, allocations, GC and
    growth, render, hitches, custom metrics), so a regression is still in front of the agent. A clean
    run is about 10 lines, roughly 1.2 KB.
  - The hitch list was the part without a limit, so summary.json lists the 10 largest (in frame order)
    and counts them all; frames.csv still has every frame.
  - The brief is built from summary.json, and a test runs a real `Report.WriteSummary` through it, so
    the two can't drift apart.
  - Paths are relative to the workspace (`.agenttools/captures/<name>/...`) and comparisons name their
    captures: two absolute paths cost about 100 tokens a call, and the agent resolves them against the
    same root.
- **Heap growth was the capture's own garbage** (2026-11-18). The same agent saw "heap +2.8 MB" and
  "+9.4 MB" over 0.4 s with no verdict. The game thread allocated 0 B after the gate in both: the heap
  grew from the PNG writer, which built every frame in a fresh `MemoryStream` and copied it out (3 s
  with `save none`: +48 KB; 1.4 to 2.5 s saving every frame: +2 to +13 MB).
  - The capture's own allocations are counted exactly per frame (`alloc_capture_b`,
    `allocations.captureBytes`), and are now zero; see the next entry. BMP would also avoid compression
    but is 24x larger on disk (2.7 MB a frame against about 115 KB).
  - `growth.verdict` judges only windows of 5 s or more after the memory gate (the performance guide's
    run length), and only signals that mean retention: GPU resources rising, and private bytes up more
    than 8 MB over a window of 30 s or more. The private-bytes rule was first 1 MB over 5 s; captures of
    the sample game and of the Fireworks sample then showed private bytes moving in 4 MB steps, up as well as
    down (+4 MB, -1.6 MB, +4 MB in 7 s for a game with flat heap and no allocations), which matches the
    GC's 4 MB basic region size on 64-bit since .NET 7. A short capture can't tell such a step from a
    leak, so private bytes are left to live sessions, and the short-window verdict says so ("steady: no
    GPU resources leaked (private bytes are judged from 30 s)"). Heap size without a collection includes
    garbage, so it's reported but not judged; a true retained-memory check would compare after forced
    full collections, as memory-profiler snapshot diffs do, but forcing one mid-capture disturbs the
    frames being measured.
- **The capture allocates nothing** (2026-11-18). Reusing buffers left about 6 KB per saved frame,
  measured piece by piece: MonoGame's `GetBackBufferData` allocates a row on every call to flip
  OpenGL's rows (5.1 KB at 1280 wide), `ZLibStream` can't be reset (232 B), `FileStream` (168 B) and
  formatting the path (184 B). Each is now gone, verified at 0 B per frame in a capture and by tests:
  - Readback calls glReadPixels (an OpenGL 1.1 export of every desktop GL library) and flips with a
    reused row. The first frame is compared with `GetBackBufferData`; a mismatch, or a missing library,
    falls back to it and says so in the notes. The fix belongs upstream in MonoGame (reuse the row).
  - Compression is an in-house zlib encoder: one fixed-Huffman deflate block and a single-probe hash
    match finder, like stb_image_write's, indexing positions inside short matches only, like zlib's
    fastest level. On the sample game's 720p frames, Release: about 5 ms a frame, the same as `ZLibStream` at
    `Fastest`, with files 2 to 10% larger. Replace it with .NET 11's `ZLibEncoder` (span-based,
    reusable through `Reset`) once the package's minimum framework has it; requiring .NET 11 now would
    force every game onto a short-term-support release.
  - Packages measured instead; see the encoder benchmark below. ZLibDotNet (a managed zlib port) also
    allocates nothing but is 2.5 to 4x slower, with files 15 to 30% smaller; SharpZipLib's `Deflater`
    allocates 48 KB to 1.1 MB a frame even with `Reset`.
  - SIMD where the work is data-parallel, as zlib-ng does: Adler-32 a vector block at a time
    (`Vector<T>`, so AVX2 on x64 and NEON on ARM; 1.4 to 0.35 ms on a 2.7 MB frame), and the Sub filter
    four pixels at a time (subtract, then drop alpha with a byte shuffle). Match finding and bit packing
    are serial. CRC-32 is slice-by-8 (zlib's table method; 0.1 ms clean, 1.7 ms noisy):
    System.IO.Hashing's carry-less-multiply `Crc32` is faster, but as a package it would ship in every
    game's output, and the build targets can't safely remove it (the game or another package may use
    it). A whole clean 720p frame now encodes in about 3 ms, from about 5.
  - **Encoder benchmark** (2026-11-18, after the SIMD work). One 1280x720 frame of the sample game (2.7 MB once
    filtered), compression only (the filter is the same work for every encoder), Release, x64 with
    AVX2, mean of 50 runs. Noise is uniform ±N per channel, re-filtered, standing in for grain or dense
    particles. Absolute times vary with machine load between runs (`ZLibStream` measured 1.6 to 2.1 ms
    clean on the same machine); compare ratios.

    | Frame | Encoder | Time | Size | Allocated per frame |
    |---|---|---|---|---|
    | Clean | `ZLibStream` (Fastest) | 2.1 ms | 118 KB | 232 B |
    | | ours | 2.8 ms | 121 KB | 0 |
    | | ZLibDotNet 0.1.1 (level 1) | 11.1 ms | 100 KB | 0 |
    | | SharpZipLib 1.4.2 `Deflater` (level 1) | 18.0 ms | 100 KB | 48 KB |
    | Noise ±4 | `ZLibStream` (Fastest) | 28.0 ms | 2,282 KB | 234 B |
    | | ours | 28.2 ms | 2,327 KB | 0 |
    | | ZLibDotNet 0.1.1 (level 1) | 56.0 ms | 1,594 KB | 0 |
    | | SharpZipLib 1.4.2 `Deflater` (level 1) | 88.1 ms | 1,594 KB | 489 KB |
    | Noise ±16 | `ZLibStream` (Fastest) | 34.8 ms | 2,828 KB | 234 B |
    | | ours | 39.0 ms | 2,772 KB | 0 |
    | | ZLibDotNet 0.1.1 (level 1) | 101.4 ms | 2,092 KB | 0 |
    | | SharpZipLib 1.4.2 `Deflater` (level 1) | 128.3 ms | 2,092 KB | 1.1 MB |

    Ours does less work than zlib (fixed codes, one match candidate), which costs little on filtered game
    frames: they are mostly runs. Its Adler-32 takes 0.4 to 0.5 ms of its time. Before the SIMD work it
    was 1.75x `ZLibStream` on the clean frame; now 1.3x, and level with it under light noise.
  - Files are written with the OS's create/write/close (`CreateFileW`, `creat`): every .NET file API
    wraps the handle in a managed object. `creat`, not `open`, because `open` is variadic and P/Invoke
    can't call variadic functions portably (Apple arm64). Falls back to `FileStream` where libc can't
    be loaded. Paths are built before the frame loop.
  - The library builds optimized in every configuration, so a Debug game referencing the project
    captures as fast as the Release package (the encoder is several times slower unoptimized).
  - Nothing is allocated for the first saved frame either: before the frame loop the capture reads back
    and saves one frame at the real size (to a scratch file), which sizes the buffers, runs the static
    constructors, compiles the generic SIMD code `PrepareMethod` can't reach and binds the native calls.
    The readback's check buffer is allocated up front too. What remains is MonoGame's own 5 KB row in
    that one-time check on frame 1, since `GetBackBufferData` is the reference.
  - The per-frame save and readback methods are `AggressiveOptimization`. Without it, 48 to 144 B
    appeared once per run on a random frame; with `DOTNET_TieredCompilation=0` it vanished, so it was
    the runtime promoting a method mid-capture. Marking the methods also cut the wall time of a 150-frame
    save-everything capture from 4.7 s to 1.6 s (no unoptimized first tier). Verified over three runs:
    the capture's own allocations are 5,144 B on frame 1 and 0 on every other frame.
  - Live play re-checked after these changes (23 s of the sample game with the HUD, about 18,000 frames): 0 B on
    every thread, no GCs, no JIT after warm-up, and the heap at the same byte count every second.
  - Rejected: a separate writer process with a shared-memory ring, as OBS's game capture uses. With
    one encoding thread, back-pressure holds the game to the encoder's pace once the ring fills, the
    same as encoding in process; only parallel encoding helps, and worker threads would do that too.
    Either way, encoding competes with the game for cores and the readback stalls the GPU, so a run
    that saves frames never times as cleanly as `save none`. Pictures and performance stay separate
    runs, as the performance guide says.
- **Framing is the game's job, not a crop's.** The same agent asked for an automatic crop after guessing
  one twice. Its full frames showed the effect well placed but small: the game's effect cameras sat at
  a fixed distance whatever the effect's size. A crop also loses what the player sees around the effect.
  So the fix was in the game (effect cameras fit to each effect's bounds, as an editor's Frame Selected
  does), and `crop` stays a manual zoom for detail. Rejected: crop to the pixels that change (no engine
  precedent, and it fails with moving cameras or ambient particles); a game-reported subject rectangle
  for the MCP to crop to (exact, but only needed if a moving gameplay-camera review needs a tracked crop).
- **GIFs for people** (2026-11-18). Agents judge motion from contact sheets; people want to watch it, so
  the `gif` tool writes an animated GIF from saved frames and returns only its path.
  - GIF, not APNG, WebP or MP4: it's the one format that plays on a double-click in Windows (Photos shows
    APNG as a still) and in chat apps, with no ffmpeg to install. Its 256 colors are the cost.
  - One global palette by median cut over every frame (ffmpeg's palettegen, two passes), split where
    squared error is largest so small bright areas keep their colors, with exact mean colors per bin.
    8x8 Bayer dithering at ffmpeg paletteuse's default strength (bayer_scale 2): fixed from frame to
    frame, where error diffusion shimmers in animation.
  - Each frame stores the changed rectangle with unchanged pixels transparent (ffmpeg diff_mode=rectangle,
    gifsicle's optimization); unchanged frames extend the previous delay. 3 s of the sample game at 640x360 and
    30 fps is about 340 KB.
  - Delays are rounded against the running total so playback keeps game time (30 fps plays 3, 4, 3 cs);
    at most 50 fps, since browsers play delays under 2 cs at 10. `speed` below 1 is slow motion.
  - LZW follows giflib's encoder. Tests decode with a decoder written from the spec, through table
    clears; GDI+ (Windows' own decoder) reads the output with the expected frames and delays.
- **Fireworks sample** (2026-11-18). A small game in `samples/` gives the docs reproducible images and
  CI a game to build, without depending on the sample game. It draws with SpriteBatch and one white pixel (no
  content pipeline), seeds its randomness and keeps a fixed particle pool. Its first capture found
  MonoGame's `SpriteBatch` allocating 61 KB the first time a frame drew more than 256 sprites (its batch
  grows on demand); the sample draws its maximum once, invisibly, in `LoadContent`, which is the
  prewarming the performance guide recommends to games. Removed before 0.1 (2026-11-20): the docs
  use the sample game's Earth Slam instead, and a fuller sample comes once input lets it be played.
- **PNG decoding is in-house** (8-bit RGB/RGBA, all five filters, no interlace): the server only
  reads frames the library writes. ImageSharp's license isn't plain MIT and SkiaSharp adds native
  binaries for every platform; either is the fallback if more formats are ever needed.
- **Framework-dependent**, unlike the template's self-contained single-file default: users already
  have the .NET SDK (dnx needs it), and one small package beats one per runtime identifier.
- Verified end to end with the SDK's client over stdio from the repository root: discovery found
  the sample game, capture built and ran it (2 s, about 9 s total), invalid options came back as an error with
  the usage text, and a cropped 8-frame sheet came back labeled.

## Input, timing and replay (v0.2)

A deterministic game's frames depend only on what comes from outside it: input, and the time it's given
each frame (plus its own random seed, which it owns). Lockstep RTS replays and Quake demos record exactly
those two streams, and so does this: an `.input` script and, when frame times aren't an even step, a
`.timing` file. They're independent; a capture can replay either, both or neither. Captures drive an
unmodified game's input, so an agent sees what happens when the player acts and two builds can be
compared under the same input; live play records into the same format, so a person plays a moment once
and an agent replays it exactly.

Before this, captures read the machine's real input: the keyboard was empty, but the mouse position and
buttons, Caps Lock / Num Lock and any connected gamepad leaked in, so a game that read them wasn't
deterministic. Every capture is now isolated, script or not.

### Scripts (`.input`)

One event per line, `<when> <control> <down|up|value>`; `#` starts a comment; lines sharing a frame
apply in file order.

```text
f60  Space down
t1.5 MouseLeft down 640,360
f140 PadLeftStick 0.5,-1
```

- **When.** `f<n>` is the nth frame (frame 1 is the first timed `Update`, after MonoGame's zero-time
  one, matching sheets, `frames.csv` and file names). `t<seconds>` is a game time: the first frame
  whose `TotalGameTime` is at or after it. A line may carry both; `t` wins, `f` is for reading. Under
  fixed timing or a replayed `.timing` every frame's time is known before the run, so `t` is resolved
  to a frame at load time, exactly; under real timing it applies on the first frame whose measured
  total reaches it. The when-tokens are lowercase, so `f10 F1 down` is the F1 key, and a frame is
  read only as a line's first token, so `t1.5 f5 down` is the F5 key too.
- **Typed text** is its own control, `Text "..."` (a JSON string), raising `Window.TextInput` once per
  character and pressing no keys. Text and keys are separate streams in SDL (`SDL_TEXTINPUT` versus
  key events, after layout, dead keys and IME), so a key press can't stand in for a character; the
  same split as Playwright's `keyboard.insertText()` (input events only) versus `keyboard.type()`
  (key events). The recorder listens to `TextInput` and writes what arrived before each frame as
  `Text` lines, skipping the control characters MonoGame raises from Enter, Backspace, Tab, Escape and
  Delete key presses, which replay with the key.
- **Controls** are MonoGame's own names, flat, with the device built in (Unreal's `EKeys` and Source's
  bind names are flat the same way): `Keys` names bare (`Space`, `F1`, `LeftShift`); `MouseLeft`,
  `MouseRight`, `MouseMiddle`, `MouseX1`, `MouseX2`; `Mouse x,y` (window client pixels); `MouseWheel`
  and `MouseWheelH` in notches (one notch is 120 in `ScrollWheelValue`); `Pad` + `Buttons` names
  (`PadA`, `PadStart`, `PadDPadUp`); `PadLeftStick x,y` and `PadRightStick` (-1..1, y up, as
  `GamePadThumbSticks`), `PadLeftTrigger`/`PadRightTrigger` (0..1); `Pad2A`… for players 2–4. The
  one departure from MonoGame's names is the stick click, `PadLeftStickButton` rather than
  `Buttons.LeftStick`: engines disagree on which of "stick" and "thumbstick" is the click (Unreal's
  `Gamepad_LeftThumbstick` is; MonoGame's `LeftStick` is), so the names follow Unity's Input System
  (`leftStick`, `leftStickButton`), where the stick is the movement and the button says it's a button.
  `PadLeftStick down` fails with a pointer to the button. Case doesn't matter. Stick values are raw,
  before the game's dead zone.
- **State holds until changed.** Gamepads up to the highest one a script names are connected for the
  whole capture (a player plugs in before starting, and adding one mid-run would allocate in the frame
  loop). The mouse starts at 0,0 with no buttons.
- **Errors** name the line and the nearest valid control by edit distance (`s.input line 4: unknown
  control 'Spcae'; did you mean 'Space'?`), and a bad script or `.timing` exits as invalid arguments
  with the message, not a stack trace. A down and up of a button on the same frame is a warning in the
  notes: `GetState` never shows it pressed.
- Precedent: TAS input logs (BizHawk, libTAS) key input to frames for exact replay; they store every
  frame's full state, which suits tools rather than hand-writing, so this stores only changes.

### Timing

| `timing` | Frame times | Writes `capture.timing` |
|---|---|---|
| `fixed` (default) | an even step: the game's `TargetElapsedTime`, or `1/fps` | no; the step is in `summary.json` |
| `real` | measured: wall time since the previous frame started, minus the capture's own readback and saving | yes |
| a `.timing` file | replayed: frame n gets the nth recorded `ElapsedGameTime`; `TotalGameTime` is their sum | yes (a copy) |

- **The default step is the game's own** `TargetElapsedTime`, read after `Initialize`, exact to the
  tick. It used to be `1/fps` with fps defaulting to 60, which ignored a game's own step and was a tick
  short of MonoGame's 60 Hz default (166,666 against 166,667 ticks), drifting a tick a frame against live
  play. `fps` is an override, as Unity's `Time.captureFramerate` and Unreal's `-fps=N` are. A script's
  `step` header sets it too; a conflicting `fps` is an error (within one tick, since 60 fps is 166,666).
- **A capture never reads the clock** under `fixed` or a replayed file, even for a game with
  `IsFixedTimeStep = false`; pacing (a profile, or `real`) only paces wall time (by the replayed times
  when there are some). So a variable-step game normally gets a perfectly even step in a capture, and bugs
  that need uneven frame times (frame-rate-dependent physics, smoothing jitter) only show under `real`
  or a replayed recording. `real` is non-deterministic once and reproducible after: its
  `capture.timing` replays the run frame for frame. A hidden window isn't synced to a display, so `real`
  paces frames to the step itself, as vsync would: unpaced, frames measured a few milliseconds each
  and a capture planned for a duration ended far short of it, with later input never applied. A fixed-step game asks for even steps, so `real` gives
  it measured times it wouldn't get live; it's meant for variable-step games. MonoGame's catch-up when
  a fixed-step game falls behind isn't emulated, and `IsRunningSlowly` isn't reproduced.
- **`.timing` format:** a 16-byte header (`MGAT`, format version, first frame, reserved), then each
  frame's `ElapsedGameTime` as a little-endian 32-bit tick count: exact to `GameTime`'s 100 ns tick, so
  replayed totals match the originals with no rounding drift. Binary because nobody reads it by hand, and
  as text it would bury an `.input` file's events in numbers an agent would read past. Values near
  16.67 ms differ in their last digits, so run-length coding wouldn't shrink it. Per hour: 0.9 MB at
  60 fps, 2.1 MB at 144 fps, 14 MB uncapped at 1,000 fps. A frame over 214 s is stored as that maximum
  (MonoGame caps a step at `MaxElapsedTime`, 0.5 s by default).
- **Length.** Frames, else the frame nearest the duration, else a replayed `.timing` file's length (the
  session it came from), else 1 s extended to the last event plus 1 s, to show the reaction to it.
  Events after the end are counted in the notes (`12 input events after f300 not applied`): replaying
  the start of a long recording is legitimate. Seeded uneven timing (`jitter:<seed>`) is a possible
  later mode.

### Injection

The game keeps calling `Keyboard.GetState()`, `Mouse.GetState()` and `GamePad.GetState()` and handling
`Window.KeyDown`/`KeyUp`; nothing in it changes. Verified against MonoGame 3.8.5's source and assembly:

| Device | How | Why |
|---|---|---|
| Keyboard | Write `Keyboard._keys` (the list `GetState` returns, filled by `SdlGamePlatform` from SDL key events) and raise `Window.OnKeyDown`/`OnKeyUp` on edges, plus `OnTextInput` for Back, Tab, Enter, Escape and Delete, as the platform does for keys whose SDL keycode is a control character | The same list and events a real key produces |
| Lock keys | Swap `Sdl.Keyboard.GetModState` to report none | Real Caps Lock / Num Lock otherwise leak in |
| Mouse | Swap `Sdl.Mouse.GetGlobalState` to return the scripted position (plus the window origin `Mouse.PlatformGetState` subtracts) and buttons, and `Sdl.Mouse.WarpInWindow` so `Mouse.SetPosition` moves the scripted cursor, never the user's; set `Mouse.ScrollX`/`ScrollY` | `Mouse.GetState` reads the OS cursor (`SDL_GetGlobalMouseState`), not SDL events, so events can't drive it |
| Gamepads | An SDL virtual game controller per pad (`SDL_JoystickAttachVirtualEx`, SDL 2.24+; MonoGame 3.8.5 ships 2.32.10), registered with `GamePad.AddDevice`; each frame's changes set with `SDL_JoystickSetVirtualButton`/`Axis`, then `SDL_JoystickUpdate`. Real pads are removed from `GamePad.Gamepads` for the run | Through SDL's real controller layer and MonoGame's unchanged `GetState`; a managed fake would pass fake handles to SDL calls such as `GetCapabilities`. Axes invert MonoGame's `GetFromSdlAxis` (negative over 32768, positive over 32767, Y flipped); SDL rests a virtual trigger at the axis minimum and maps the full range onto 0..32767, so a trigger value t is `t × 65535 − 32768` |

- MonoGame's DesktopGL platform reaches SDL through delegates in mutable static fields, so swapping a
  field is enough: no runtime detour. Replacements are bridged to the fields' internal delegate types by
  a `DynamicMethod` built once (the return types differ only as enum and underlying integer); scroll
  fields are set through a `DynamicMethod` setter, since `FieldInfo.SetValue` boxes. Nothing per frame
  allocates: a probe game capture with keyboard, mouse and two gamepads allocated 0 B on the game thread.
- Mouse and lock-key swaps happen before `Initialize`. After it (MonoGame reads SDL's events once
  then, so keys held and pads connected at startup register), the key list is emptied, real pads are
  hidden and the virtual ones connected. Originals are restored when the capture ends. Not isolated:
  MonoGame's raw `Joystick` API.
- Every member is bound up front, the way `GameHooks` binds `Update` and `Draw`; a MonoGame version that
  renames one fails before the first frame with the member's name, never half-working.
- **Status: experimental.** Feeding synthetic input into the engine's normal input path, so game code
  can't tell it apart, is how Unity's `InputTestFixture` and Godot's `Input.parse_input_event` work; doing
  it by swapping MonoGame's internal SDL bindings depends on non-public members. The standard
  alternative is a public input-source hook in MonoGame; no issue requests one yet
  ([#4558](https://github.com/MonoGame/MonoGame/issues/4558), for the pipeline tool, is the nearest),
  and contributing one would let this use it. A runtime detour (MonoMod, Harmony) was rejected: it
  rewrites compiled code, adds a dependency and is exposed to JIT inlining and platform memory
  protection, for nothing over swapping fields MonoGame already exposes. DesktopGL only, like the rest
  of capture: the same managed bindings on Windows, Linux and macOS; WindowsDX reads input from Windows
  messages.

### Recording

Live play writes `session.input`, and `session.timing` for a variable-step game, to its session folder
(v0.3; before, `--agenttools-record <path>` chose the path).

- Sampled once per `Update` (the monitor component in live play; in the replay test, the probe game's own
  `Update` inside a capture, recording what the game read, which is how it checks injection) through MonoGame's own
  `GetState` calls, gamepads with `GamePadDeadZone.None` so the replaying game applies its own dead
  zone. Counts `Update` calls, not draws. Only changes are written.
- Fixed step writes `step <TargetElapsedTime ms to 4 decimals>` and events as `f<n> t<s>`; variable step
  writes `step variable`, events as `t<s>` only and the `.timing` file. Times are written to the tick
  (seconds to 7 decimals). Sticks get 5 decimals, enough to map back to the same 16-bit SDL value;
  verified: a recorded stick of 0.5 reads 0.50002 and replays to the same axis value.
- A press and release within one frame is lost: `GetState` never shows it, though `KeyDown`/`KeyUp`
  fire, so the replay lacks those two events. Verified end to end: recording a scripted capture and
  replaying the recording reproduced every value the probe game read on every frame, except the
  event counts of a Caps Lock tap inside one frame, and with `real` timing every frame's elapsed time.
- **Written as it goes** through two 64 KB buffers per file flushed by a worker thread, the way Unreal
  Insights' trace buffers and ETW sessions bound memory and keep data on a crash. Lines are formatted on
  the game thread straight into the buffer (`IUtf8SpanFormattable.TryFormat`, no allocation); a full
  buffer, or one that has waited 1 s, goes to the worker. If the worker falls behind, a third buffer is
  allocated and the stats count it; input is never dropped. The partial buffer is flushed at exit and on
  an unhandled exception; a process killed outright (a debugger stopping it) runs no handlers and loses
  at most the last second, verified by killing a recording game.
- Sizing, to be confirmed on real play: a few to about 20 button events/s (an RTS player at 300–400
  actions per minute is about 10 presses and 10 releases a second), plus up to one event per frame each
  for mouse motion and moving sticks, about 8 per frame at worst (about 500/s at 60 fps, 8 KB/s); 64 KB
  is about 8 s at that rate, and the 1 s flush means the worker rarely sees a full one. The live stats'
  `recording` section reports events, peak events per second, buffer swaps and extra buffers. A 3 s
  uncapped variable-step probe session (15,664 frames) recorded with 0 B allocated on any thread.

### Determinism

A replay matches the original only if the game is deterministic given input and time: seeded
randomness, and no gameplay reads of the wall clock, `Stopwatch` or `DateTime`. The capture can't detect
a divergence on its own; comparing a replay's sheet with the original's shows one.

### Not in this step

- Input on contact-sheet labels. Held state alone misses taps between tiles; a later version could show
  held input plus presses and releases since the previous tile (as fighting-game input histories do),
  and mark the mouse position, since the OS cursor isn't in the frame.
- Touch, mouse-relative mode (`SDL_SetRelativeMouseMode` for mouse-look), key repeat (a held key raises
  one `KeyDown`, where SDL repeats it).
- Driving input in live play (bots, assisted play): replay is capture-only.
- Contributing the MonoGame input hook.

## Command line, profile and sessions (v0.3)

The options grew one flag at a time, and two runs needed several flags together: a performance run was
`--agenttools-save none --agenttools-realtime --agenttools-duration 8`, and replaying a session needed
`--agenttools-record` at the time it was played. v0.3 regroups them by what a run is for.

- **One entry flag per run, then options.** `--agenttools-capture <dir>` or `--agenttools-profile <dir>`;
  both, or an option with neither, is an error naming the choices; neither runs live. Precedent:
  subcommands in `git` and `dotnet` (`dotnet test` / `dotnet run`), and Unity's `-batchmode
  -executeMethod`, where one flag picks the run and the rest qualify it.
- **The MCP tools are the entry flags, and parameters are the flags in camelCase** (`jitNames` is
  `--agenttools-jit-names`); only the server's own (project, sheet, name, detail, configuration,
  timeoutSeconds, gameArgs) have no flag. `ToolMappingTests` compares the tools' parameters with the
  library's option list, so neither can change alone. The server builds each flag from the parameter's
  name, and a rejected option comes back with the flag rewritten as the parameter.
- **`profile`** is a capture paced to the step in real time with no frames saved, 8 s by default, with a
  note under 5 s (gates open a few seconds in). It replaces `realtime` and the `save none` advice.
  Measuring frames as played follows Unreal's FPS charts and Unity Performance Testing's
  `Measure.Frames`, here over a run that repeats frame for frame.
- **`--agenttools-duration 3s|150f`** replaces `duration` (seconds) and `frames`: one option, with a
  required unit, as Unity Performance Testing counts frames or Godot's `--quit-after` frames, and as
  `ffmpeg -t` takes a time. A bare number is an error, since it could be either.
- **`jitNames` stays opt-in.** Measured on the input probe, 3 s profiles, three runs each: other-thread
  allocations 12 KB without it and 1.45 MB with it, and the frames that compiled 0.45 ms against 0.58 ms.
  It costs most on exactly the frames it explains, so it isn't folded into `profile`.
- **Every live session is recorded** to `.agenttools/sessions/<Game>-<yyyyMMdd-HHmmss>/` under the
  repository root, beside the captures the MCP server writes, so a bug the user just hit can be replayed without
  having planned to record it. Precedent for always-on, keep-recent recording: Unreal keeps recent logs
  as timestamped backups in `Saved/Logs`, and console background recording (Xbox Game Bar, NVIDIA
  Instant Replay) keeps the last minutes so a moment can be saved after it happens. The 10 most recent
  are kept (as are the 10 most recent unnamed captures and, separately, profiles); a renamed folder is
  kept for good. `RecordSessions = false` turns it off. `--agenttools-record` and `--agenttools-stats`
  are gone: the session folder holds both.
- **Sessions find the root the way `.mcp.json` does**: the build passes the game its project folder
  (or `AgentToolsRoot`) as a runtime setting, and the game walks up from there to `.git`. Walking up
  from the binaries instead would put sessions where the server can't see them whenever the output is
  outside the repository. A session is named after the executable, as captures are after the project,
  since templates all call the class `Game1`, and claims its folder by creating a marker that must not
  exist, so two games started in the same second can't share one.
- **Precompiling runs static constructors** too. Recording inside a measured `Update` (the replay test's
  probe) showed 33 KB on the frame of the first key press: the recorder's name tables, built lazily on
  first use. The recorder also formats one value of each kind when it starts, for the generic UTF-8
  formatting `Precompile` can't reach (64 B on the first stick move). Live play has 0 B per frame from
  the recorder since.
- **The `.timing` pairing rule lives in the library only.** The server passes files (a capture's or
  session's `.input`, or a script written into the new folder) and the game pairs the `.timing` beside
  it. A capture re-run with its own input gets it back: the server reads the files before clearing the
  folder and writes them back after, at the paths the game was given.
- **A game with several target frameworks runs the first listed**, as Visual Studio's debug target
  defaults to; `framework` picks another (`dotnet run -f`). `dotnet run` itself refuses to guess, but an
  agent is better served by a default it can change, and the result names the framework that ran.
  MSBuild reports no run command for a multi-targeted project's outer build, so the server evaluates the
  project's frameworks (about 1 s, skipped when the project file names one `TargetFramework` itself) and
  builds only the one it runs.

## HUD in captures, baseline and diff (v0.4)

- **`hud` draws the HUD into a capture or profile**, so an agent can see what the user sees in play, or
  reproduce a report made with the HUD on, and so the docs' HUD screenshot comes from the tools. It's
  the live monitor itself, installed only to draw, as Unity Recorder's "Capture UI" and Unreal Movie
  Render Queue's UI pass add the overlay to a recording on request. Its numbers are wall-clock, so the
  summary's `hud.region` records the area it covered and a note says the run differs there, and that
  draw counts and frame times include it. In an unpaced capture its frame times show the capture's
  pace; a profile, paced as play is, shows the player's. Experimental: whether agents use it beyond
  screenshots; the standard alternative is a person's own screenshot of live play.
- **Comparisons leave the HUD out.** The union of both captures' HUD regions isn't compared, isn't
  counted in the percentage changed, and shows in a dark blue in the diff tile, as Playwright's
  `toHaveScreenshot({ mask })` and Percy's and Chromatic's ignore regions do for dynamic content. The
  rest of the frame stays deterministic: the HUD restores the device state it changes.
- **`against` is renamed `baseline`**, the word Playwright, Applitools and Unreal's Screenshot
  Comparison use for the image a run is compared with, and the one the docs already used to explain it.
- **`diff` chooses whether the changed pixels show.** On by default in sheets, where the agent reads
  them; `diff: false` gives larger A and B tiles. Off by default in GIFs, for people, who judge motion
  best from A and B side by side (the GIF's purpose); on, it adds the third panel, drawn as the sheet
  draws it. Playwright's report likewise offers expected, actual and diff as separate views. The choice
  of defaults is a judgment, not precedent, made by comparing two GIFs: for a large change (a floaty
  against a heavy earth slam) the red covers what A and B already show, and for a small one (a crack
  decal's edge softened) A and B look alike and the red finds it. So the tools tell agents to turn
  `diff` on when the sheet reports a small fraction changed but not none.

## Open questions

- Confirm the VS Code extension honours `.mcp.json` from the repo root when the workspace is a subfolder.
- `server.json`'s registry name `io.github.jnoyola/monogame-agenttools` follows the MCP Registry's
  GitHub namespace (publishing requires signing in as that account); confirmed 2026-11-18. Only the
  registry listing uses it; `.mcp.json` runs the NuGet package through `dotnet dnx`.
- Custom metrics with no value in a frame (a smoke particle's distance before any smoke exists) return
  NaN: an empty CSV cell, skipped by the summary's first/last/min/max, and `-` in the HUD.
- CI pass/fail story: `dotnet test` helpers comparing captures to goldens with tolerance (Playwright
  `toHaveScreenshot`, Unity Graphics Test Framework) — later.

## Extraction plan

0. ~~Spike~~ done (see Runtime → Spike result): real-loop capture + hidden backbuffer readback works
   and is deterministic on DesktopGL.
1. ~~New `MonoGame.AgentTools` project~~ done: real-loop capture, PNG/sheet, frame CSV + JSON summary,
   warm-up gates, hitch report, custom metrics, live stats, base-call warning. ~~Move the sample game
   onto it~~ done (2026-11-18): its scenes and cameras are its own flags (passed through `gameArgs`), its
   simulation numbers are custom metrics, and its own HUD, profiler and harness are gone. A generic
   tracked average (the game reports a subject's screen position as custom metrics, the MCP crops and
   averages saved frames) is a candidate MCP feature if a review needs one.
2. ~~`[Conditional]` stripping + targets~~ done and verified via the packed package.
3. ~~`GraphicsDevice.Metrics`~~ done (per frame). Remaining: `BeginScope`/`EndScope` per-pass deltas;
   ~~HUD~~ done.
4. ~~Scripted input, record/replay~~ done (2026-11-19; see Input, timing and replay), by injection into
   MonoGame's own input path rather than an `IInputSource` the game would have to adopt.
5. Session mode over a named pipe.
6. `MonoGame.AgentTools.Mcp` server: ~~tools, guide content~~ done (see MCP server). ~~`.mcp.json`
   merge from the library's targets~~ done (2026-11-18; see Setup step 3).
7. Pack and publish both packages.
