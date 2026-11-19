# Performance

How to get representative numbers, and how to read each line of a capture's summary.

## Representative runs

Capture without saving frames, in real time, for at least 5 seconds:

```
MyGame --agenttools-capture out/perf --agenttools-duration 8 --agenttools-save none --agenttools-realtime
```

Saving PNGs adds work to every frame, and without `realtime` frames run back to back, which isn't how the
game runs. The warm-up gates are measured in wall time, so short runs may never reach them.

## Warm-up

Numbers taken while code is still being compiled or memory is still growing describe startup, not the
game. Two gates decide when measurements count:

- **Memory**: no game-thread allocations and no garbage collection for 1 s.
- **Performance**: the memory gate, plus no JIT compilation on any thread, for 1 s.

A gate that doesn't settle within 10 s is reported as never stabilized, with the signals still active.
Frames that allocate, collect or compile after a gate count as disturbances: first use of new content,
for example.

Frame-time spread isn't a gate, since uncapped games jitter by nature. It's reported as percentiles.

## Reading the summary

| Line | What it means |
|---|---|
| Frame CPU ms | CPU time of `Update` plus `Draw` on the game thread: p50, p95, p99 and max, after warm-up when the gate was reached |
| Allocations | Bytes allocated by the game thread and by other threads after the memory gate. Any game-thread allocation here is a per-frame allocation worth fixing. Other threads include audio, tasks and libraries. The capture's own allocations are counted separately and are zero after its first frame |
| Growth | Whether memory is being kept. See below |
| GC, JIT | Collections by generation, and methods compiled on the game thread |
| Render | `GraphicsDevice.Metrics` per frame: draw calls, primitives, render-target switches, sprites |
| Hitches | Frames slower than the threshold, with their likely cause: JIT, GC, allocation, first use, or unattributed (GPU, driver or OS scheduling) |
| Custom metrics | Your own values: first, last, min and max |

## Memory growth

`growth` compares the end of the run with the memory gate, and its verdict says whether that shows a
problem:

- Windows under 5 s after the gate are too short to judge.
- **GPU resources** (textures, render targets, buffers and effects not yet disposed) rising means
  something is created repeatedly and never disposed. This is an exact count, so it's judged from 5 s.
- **Private bytes** (everything the process holds, including native and driver memory) are judged only
  over 30 s or more, and growth beyond 8 MB counts as native memory growing. The counter moves in 4 MB
  steps, up and down, as the .NET GC commits and releases memory regions and drivers commit memory, so a
  short capture can't tell a step from a leak. Use live stats for this.
- **Heap size** isn't judged: it includes garbage not yet collected, so it rises with any allocation until
  a GC, and those allocations are already counted on their own line.

## Finding slow growth

Live stats (`--agenttools-stats`) record heap, private bytes and GPU resources once a second for the
whole session, so growth that takes minutes shows up. See [Live play](live.md).

## Prewarming

The tools compile their own per-frame code before measuring, so they never show up as a first-use cost.
Your game can do the same by running its real paths once during loading. For example, `SpriteBatch`
grows its buffers the first time a frame draws more sprites than before; the Fireworks sample draws its
maximum once, invisibly, in `LoadContent`.
