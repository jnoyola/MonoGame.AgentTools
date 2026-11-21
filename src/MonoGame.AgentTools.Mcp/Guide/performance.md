# performance
Getting representative frame times and allocation numbers, and reading the warm-up gates.

Use profile, not capture. A capture saves PNGs, which adds work to every frame, and runs frames back to
back, which isn't how the game runs. profile paces frames to the step in real time and saves none. Keep
its default 8 s or longer: under 5 s the gates may not open. Give it `input` to profile a scripted
scene or a session the user played.

Numbers taken while code is still being compiled or memory is still growing describe startup, not the
game. Two gates in summary.json's `warmup` decide when measurements are representative:
- memory: no game-thread allocations and no garbage collection for 1 s.
- performance: the memory gate, plus no JIT compilation on any thread, for 1 s.
A gate that doesn't settle within 10 s is `never stabilized` and names the signals still active.
`disturbancesAfterStable` counts frames that allocated, collected or compiled afterwards (first use of
new content, for example).

Frame-time spread isn't a gate: uncapped games jitter by nature. Read `frameTimeMs` p50, p95 and max, and
`hitches`, which lists slow frames with their likely cause (JIT, GC, allocation).

Any game-thread allocation after the memory gate is a per-frame allocation worth fixing.
`allocations.otherThreadsBytes` is garbage made by other threads (audio, tasks, libraries).
`allocations.captureBytes` is the capture's own work between frames (reading frames back and saving
them). It's 0 except about 5 KB on the first frame, a one-time check of the readback against
MonoGame's; if it isn't, the notes say why (a fallback readback, for example). It isn't the game's, but
it's on the same heap.

`growth` compares the end of the run with the memory gate, and its `verdict` says whether that shows a
problem. It's only judged over 5 s or more after the gate, so use the settings above; shorter windows
say "too short to judge". `graphicsResources` (textures, render targets, buffers and effects not yet
disposed) rising usually means one created per frame and never disposed. `privateBytes` (everything the
process holds, including native and driver memory) is judged only over 30 s or more, where growth beyond
8 MB means native memory growing: it moves in 4 MB steps, up and down, as the GC commits and releases
regions, so a short run can't tell a step from a leak. `heapBytes` isn't judged: it includes garbage not yet collected, so it rises with any
allocation until a GC, and those allocations are already counted above. Every live session's
`summary.json` and `memory.csv` in .agenttools/sessions record memory once a second for the whole
session, for slower growth.
