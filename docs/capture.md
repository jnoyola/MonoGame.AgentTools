# Capture

The MCP `capture` tool builds your game and runs it with `--agenttools-capture`. You can run the same
thing yourself:

```
MyGame --agenttools-capture out/run1 --agenttools-duration 2
```

The game initializes without entering MonoGame's run loop, so its window is never shown. Then its real
`Update` and `Draw` run at a fixed 1/60 s step, each frame is read back from the backbuffer before it's
presented, and the game exits.

## Determinism

The same build and arguments produce the same frames, because nothing depends on wall-clock time: the
game time advances by exactly one step per frame. This is how game engines make frame-exact recordings
(Unity's `Time.captureFramerate`, Godot's `--fixed-fps` and `--write-movie`, Unreal's `-deterministic`).

For captures to match, the game has to cooperate:

- Seed random number generators (`new Random(1234)`), or derive them from game time.
- Take time from `GameTime`, not `Stopwatch` or `DateTime`.
- Don't draw wall-clock values (an FPS counter) in the frame; the HUD isn't drawn in captures for this
  reason.

Pixels can still differ slightly across GPUs and drivers, so compare captures from the same machine.

## Options

| Option | Default | |
|---|---|---|
| `--agenttools-capture <dir>` | | Capture mode, writing to `<dir>` |
| `--agenttools-duration <s>` | `1` | Game time to capture |
| `--agenttools-frames <n>` | | Overrides duration |
| `--agenttools-fps <n>` | `60` | Fixed step |
| `--agenttools-size <WxH>` \| `native` | `1280x720` | Same framing on every machine |
| `--agenttools-save all\|none\|every:N\|1,30,60` | `all` | Which frames to write |
| `--agenttools-realtime` | off | Pace frames to wall-clock time, as vsync would |
| `--agenttools-jit-names` | off | Name the methods compiled during each hitch |

Any other arguments are left for your game, so its own flags work alongside these.

## Output

| File | Contents |
|---|---|
| `summary.json` | Everything below, summarized: frame-time percentiles, warm-up gates, allocations, GC, memory and GPU-resource growth with a verdict, JIT, `GraphicsDevice.Metrics`, hitches with their likely cause (the 10 largest; `count` has them all), custom metric ranges, notes |
| `frames.csv` | One row per frame: timings, allocations, GCs, memory, JIT, render metrics, custom metrics (`custom.<name>` columns) |
| `frames/fNNNN.png` | Saved frames. Frame N is drawn after N steps, at N / fps game seconds |
| `sheet.png` | 12 evenly spaced frames in a 4-column grid |
| `error.txt` | Only if the capture failed (exit code 1) |

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
from runs with `--agenttools-save none --agenttools-realtime`; see [Performance](performance.md).
