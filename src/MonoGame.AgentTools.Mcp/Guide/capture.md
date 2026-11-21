# capture
What capture and profile do, their options, and their output files.

capture builds the game (Debug unless `configuration` names another with the tools enabled; for a game
with several target frameworks, the first listed unless `framework` names another), then runs
it with `--agenttools-capture`: the game's real `Update` and `Draw` are called at a fixed step (the
game's own `TargetElapsedTime`) with a hidden window, each frame is read back from the backbuffer, and
the game exits. The same build, arguments and input produce the same frames, so two captures can be
compared before and after a change. The machine's own mouse, lock keys and gamepads never reach a
capture; `input` drives them (topic input). Pixels can differ slightly across GPUs and drivers.

Options: `duration` with its unit, `"3s"` or `"150f"` (default 1s, or to 1 s after the last input
event); `fps` (default: the game's own step); `input` and `timing` (topic input); `size` (default
1280x720, or `native`); `save` (`all`, `none`, `every:N`, or `1,30,60`); `jitNames` (names the methods
compiled in each hitch; it allocates on another thread and slows those frames, so it's off by default);
`gameArgs` for the game's own flags;
`detail`: `brief` (default) returns one plain-text line per check (warm-up, frame time, allocations, GC and
growth, render, hitches, custom metrics), enough to judge a run; `full` returns summary.json itself, with
hitch causes and JIT method names. Both files are in the capture folder either way.

Output in `.agenttools/captures/<name>/` (the folder has its own .gitignore). Unnamed captures are
pruned to the 10 most recent; give a capture a `name` to keep it, such as a baseline before a change.
- `summary.json`: frame-time percentiles, warm-up gates, allocations, GC, JIT, `GraphicsDevice.Metrics`,
  hitches with their likely cause (the 10 largest, plus a count of all), custom metric ranges, notes.
- `frames.csv`: one row per frame with the same measurements; custom metrics are `custom.<name>` columns.
- `frames/fNNNN.png`: saved frames; frames.csv `sim_s` is each frame's game time.
- `capture.input`, `capture.timing`: the input and (under real or replayed timing) frame times used;
  pass the capture's name as `input` or `timing` to replay them.
- `sheet.png`: 12 evenly spaced frames, 4 per row, unlabeled (written by the game itself).
- `sheets/`: labeled contact sheets returned by capture and contact_sheet.
- `error.txt`: only when the capture failed.

To see a moment more closely, call contact_sheet with a time range and crop instead of capturing again.
To compare with a baseline capture, pass `sheet: { against: "<name>" }` (see topic contact-sheets).

profile runs the same way with `--agenttools-profile`, paced to the step in real time as a player's frames
are, and saves no frames, so its frame times and allocations are the game's (topic performance). It takes
the same options except `save` and `sheet`, runs 8 s by default, and returns the unlabeled `sheet.png`.
Its output goes in the same folder as captures.

Every option reaching the game is its `--agenttools-` flag in camelCase (`jitNames` is
`--agenttools-jit-names`), so a failed run's message names the parameter to fix.
