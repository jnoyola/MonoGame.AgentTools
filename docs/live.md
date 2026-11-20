# Live play

Captures run a fixed amount of game time with a hidden window. Live play measures the game as you play
it.

## HUD

```
MyGame --agenttools-hud
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

Yellow and red mark what needs attention. Backtick (`` ` ``) toggles it, with or without the option. The
HUD's own drawing, allocations and JIT are left out of the measurements, and it allocates nothing per
frame. It's never drawn in captures, since it shows wall-clock values that would make frames differ
between runs.

To start with it visible without the option:

```csharp
AgentTools.Attach(game, args, new() { ShowHud = true });
```

## Session stats

```
MyGame --agenttools-stats out/live.json
```

Measures every frame of normal play and writes three files when the game exits:

| File | Contents |
|---|---|
| `live.json` | The same summary as a capture, over the session |
| `live.csv` | One row per frame for the last 4096 frames |
| `live.memory.csv` | Heap, private bytes and GPU resources once a second for the whole session |

`growth` in `live.json` covers the session from the memory gate to the end. Over a long session it judges
private bytes too, so steady native growth shows up. Steady growth means something is created repeatedly
and kept, or never disposed.

The game has to exit normally (closing the window or calling `Exit`) for the files to be written.
