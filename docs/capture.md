# Capture

The MCP `capture` and `profile` tools build your game and run it with `--agenttools-capture` or
`--agenttools-profile`. These flags are for agents and tools; the same run from a terminal is:

```
MyGame --agenttools-capture out/run1 --agenttools-duration 2s
```

The game initializes without entering MonoGame's run loop, so its window is never shown. Then its real
`Update` and `Draw` run at a fixed step (your game's `TargetElapsedTime`), each frame is read back from
the backbuffer before it's presented, and the game exits.

A profile is the same run paced to the step in real time, as your frames are when you play, with no
frames saved, so its frame times and allocations are the game's own. It runs 8 s by default, since the
[warm-up gates](performance.md) typically open a few seconds in.

## Determinism

The same build, arguments and input produce the same frames, because nothing depends on wall-clock time
or the machine: the game time advances by exactly one step per frame, and the machine's own mouse,
lock keys and gamepads are kept out (a script drives them instead; see [Input and replay](input.md)). This is how game engines make frame-exact recordings
(Unity's `Time.captureFramerate`, Godot's `--fixed-fps` and `--write-movie`, Unreal's `-deterministic`).

For captures to match, the game has to cooperate:

- Seed random number generators (`new Random(1234)`), or derive them from game time.
- Take time from `GameTime`, not `Stopwatch` or `DateTime`.
- Don't draw wall-clock values (an FPS counter) in the frame. The HUD is drawn in captures only with
  `--agenttools-hud`, and comparisons leave out its area, which the summary records.

Pixels can still differ slightly across GPUs and drivers, so compare captures from the same machine.

## Options

A run takes one entry flag, then any options. Without an entry flag the game runs live, and options alone
are an error.

| Entry | |
|---|---|
| `--agenttools-capture <dir>` | Capture, writing to `<dir>` |
| `--agenttools-profile <dir>` | Profile, writing to `<dir>` |

| Option | Default | |
|---|---|---|
| `--agenttools-duration 3s\|150f` | capture `1s`, profile `8s` | Game seconds or frames, with the unit. With input, at least to 1 s after its last event; with replayed timing, its length. At most `3600s` or `1000000f` |
| `--agenttools-input <file>` | | An input script to play ([Input and replay](input.md)) |
| `--agenttools-timing fixed\|real\|<file>` | `fixed` | Frame timing: even steps, measured, or replayed from a `.timing` file (one beside the input is used unless this says otherwise) |
| `--agenttools-fps <n>` | the game's step | Fixed steps per second, overriding `TargetElapsedTime` |
| `--agenttools-size <WxH>` \| `native` | `1280x720` | Same framing on every machine |
| `--agenttools-save all\|none\|every:N\|1,30,60` | `all` | Which frames to write; capture only |
| `--agenttools-jit-names` | off | Name the methods compiled during each hitch. It allocates on another thread and slows the frames that compile, so it's off unless a hitch needs explaining |
| `--agenttools-hud` | off | Draw the HUD into the frames, as in play. Its area (`hud.region` in `summary.json`) differs between runs, and the draw counts and frame times include it |

Each MCP parameter that reaches the game is the camelCase form of its flag: `duration`, `input`,
`timing`, `fps`, `size`, `save`, `jitNames`. Any other arguments are left for your game, so its own
flags work alongside these.

## Output

| File | Contents |
|---|---|
| `summary.json` | Everything below, summarized: frame-time percentiles, warm-up gates, allocations, GC, memory and GPU-resource growth with a verdict, JIT, `GraphicsDevice.Metrics`, hitches with their likely cause (the 10 largest; `count` has them all), custom metric ranges, notes |
| `frames.csv` | One row per frame: timings, allocations, GCs, memory, JIT, render metrics, custom metrics (`custom.<name>` columns) |
| `frames/fNNNN.png` | Saved frames; each frame's game time is `sim_s` in `frames.csv` (N steps under fixed timing) |
| `capture.input` | The input script played, to replay the capture |
| `capture.timing` | The frame times, under real or replayed timing |
| `sheet.png` | 12 evenly spaced frames in a 4-column grid |
| `error.txt` | Only if the run failed (exit code 1), or its input or timing was invalid (exit code 2) |

The MCP server adds `sheets/` (labeled contact sheets) and `gifs/` to the same folder.

`frames.csv` columns:

| Column | |
|---|---|
| `frame`, `sim_s`, `wall_s` | Frame number, game time, wall time |
| `frame_ms`, `update_ms`, `draw_ms` | CPU time of `Update` and `Draw` on the game thread |
| `alloc_b`, `alloc_other_threads_b`, `alloc_capture_b` | Bytes allocated by the game thread, by other threads, and by the capture itself between frames |
| `gc0`, `gc1`, `gc2` | Collections of each generation during the frame |
| `heap_b`, `private_b`, `gpu_resources` | Managed heap, process private bytes, live graphics resources |
| `jit_methods`, `jit_ms`, `jit_methods_all_threads` | Methods JIT-compiled on the game thread (and time), and on all threads |
| `draws`, `primitives`, `sprites`, `targets`, `clears`, `textures`, `vertex_shaders`, `pixel_shaders` | `GraphicsDevice.Metrics` for the frame |
| `custom.<name>` | Your custom metrics; empty for a frame with no value |

## Cost

The capture's own work allocates nothing per frame: frames are read back with a reused buffer, compressed
by an allocation-free PNG encoder and written with the operating system's file calls. Saving a 1280x720
frame takes about 3 ms. Saving still adds that time to every frame, which is why performance numbers come
from a profile; see [Performance](performance.md).
