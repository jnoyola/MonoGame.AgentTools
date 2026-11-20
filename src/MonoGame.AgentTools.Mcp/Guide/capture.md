# capture
What capture does, its options, and its output files.

capture builds the game (Debug unless `configuration` names another with the tools enabled), then runs
it with `--agenttools-capture`: the game's real `Update` and `Draw` are called at a fixed step (default 1/60 s) with a hidden window, each frame is read back from the
backbuffer, and the game exits. The same build and arguments produce the same frames, so two captures can
be compared before and after a change. Pixels can differ slightly across GPUs and drivers.

Options: `seconds` (default 1) or `frames`; `fps` (default 60); `size` (default 1280x720, or `native`);
`save` (`all`, `none`, `every:N`, or `1,30,60`); `realtime`; `jitNames`; `gameArgs` for the game's own flags;
`detail`: `brief` (default) returns one plain-text line per check (warm-up, frame time, allocations, GC and
growth, render, hitches, custom metrics), enough to judge a run; `full` returns summary.json itself, with
hitch causes and JIT method names. Both files are in the capture folder either way.

Output in `.agenttools/captures/<name>/` (the folder has its own .gitignore). Unnamed captures are
pruned to the 10 most recent; give a capture a `name` to keep it, such as a baseline before a change.
- `summary.json`: frame-time percentiles, warm-up gates, allocations, GC, JIT, `GraphicsDevice.Metrics`,
  hitches with their likely cause (the 10 largest, plus a count of all), custom metric ranges, notes.
- `frames.csv`: one row per frame with the same measurements; custom metrics are `custom.<name>` columns.
- `frames/fNNNN.png`: saved frames. Frame N is drawn after N steps, at N / fps game seconds.
- `sheet.png`: 12 evenly spaced frames, 4 per row, unlabeled (written by the game itself).
- `sheets/`: labeled contact sheets returned by capture and contact_sheet.
- `error.txt`: only when the capture failed.

To see a moment more closely, call contact_sheet with a time range and crop instead of capturing again.
To compare with a baseline capture, pass `sheet: { against: "<name>" }` (see topic contact-sheets).
