# Live play

Captures and profiles run a set amount of game time with a hidden window. Live play is your game as you
play it, with the HUD to see the measurements and every session recorded for the agent.

## HUD

```csharp
AgentTools.Attach(game, args, new() { ShowHud = true });
```

<!-- TODO: images/hud.png, a screenshot of a game with the HUD on. -->

The HUD shows, in the top-right corner:

- frame-time percentiles over the last second
- allocations on the game thread and other threads
- GCs, process memory and live GPU resources
- JIT
- the warm-up gates
- `GraphicsDevice.Metrics` for the game's own draws
- your notes and custom metrics

Yellow and red mark what needs attention. Backtick (`` ` ``) toggles it, whether `ShowHud` is on or not.
The HUD's own drawing, allocations and JIT are left out of the measurements, and it allocates nothing per
frame. `ShowHud` applies to live play only: captures and profiles never draw the HUD, since it shows
wall-clock values that would make frames differ between runs.

## Sessions

Every time you play, the game records the session to its own folder under `.agenttools/sessions` at the
root of your repository, beside the agent's captures:

```
.agenttools/sessions/MyGame-20261121-093000/
```

| File | Contents |
|---|---|
| `session.input` | What the game read from the keyboard, mouse and gamepads, and what you typed, frame by frame |
| `session.timing` | Each frame's elapsed time, for a variable-step game |
| `summary.json` | The same summary as a capture, over the session, with a `recording` section |
| `frames.csv` | One row per frame for the last 4096 frames |
| `memory.csv` | Heap, private bytes and GPU resources once a second for the whole session |

The input is written as you go, so a crash keeps it, and a killed process loses at most the last second.
The other files are written when the game exits normally (closing the window or calling `Exit`). An
agent replays a session by its folder's name, exactly; see [Input and replay](input.md#recording).

The 10 most recent sessions are kept and older ones deleted when a new one starts. Rename a session's
folder to keep it. Recording allocates nothing per frame.

`growth` in `summary.json` covers the session from the memory gate to the end. Over a long session it
judges private bytes too, so steady native growth shows up. Steady growth means something is created
repeatedly and kept, or never disposed.

The folder is the nearest one above the game's output that holds `.git`, the same root the build adds
`.mcp.json` to, or `AgentToolsRoot` if the project sets it. `.agenttools` gets its own `.gitignore`, so
sessions are never committed. To record nothing:

```csharp
AgentTools.Attach(game, args, new() { RecordSessions = false });
```

Sessions hold what you type, so a session in which you typed a password holds the password. They stay on
your machine unless you share them.
