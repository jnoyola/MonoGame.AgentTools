# input
Driving the keyboard, mouse and gamepads in a capture, replaying recordings, and frame timing.

Pass capture `input`: the script text, a path to an `.input` file, or a capture's name to replay its
input. The game reads it through its normal `Keyboard`/`Mouse`/`GamePad.GetState()` calls and
`Window.KeyDown`/`KeyUp`; it needs no changes. Captures never read the machine's own input.

Script: one event per line, `<when> <control> <down|up|value>`; `#` comments.
- when: `f<n>` (frame n, as numbered in sheets) or `t<seconds>` (first frame at or after that game time).
- keys: MonoGame `Keys` names (`Space`, `A`, `Left`, `LeftShift`, `Enter`, `F1`): `down`/`up`.
- mouse: `Mouse x,y` (backbuffer pixels); `MouseLeft`/`MouseRight`/`MouseMiddle`/`MouseX1`/`MouseX2`
  `down`/`up`, or `down x,y` to move and press; `MouseWheel +1` (notches).
- gamepads: `PadA`, `PadB`, `PadX`, `PadY`, `PadStart`, `PadBack`, `PadDPadUp`…, `PadLeftShoulder`,
  `PadLeftStickButton` (click) `down`/`up`; `PadLeftStick x,y` (-1..1, y up, raw before dead zone);
  `PadLeftTrigger 0.8` (0..1). `Pad2A`… for players 2–4. Pads used are connected for the whole capture.
- text: `Text "Ada"` (JSON string escapes) raises Window.TextInput once per character, for text
  fields, chat and UI libraries; it presses no keys. Enter, Backspace, Tab, Escape and Delete come from their keys.
- State holds until changed: release keys with `up`, re-center sticks with `0,0`.

Example, a jump then a held move right:
```
f30 Space down
f36 Space up
f40 Right down
f100 Right up
```

A press needs at least one frame down: a down and up on the same frame is never seen by `GetState`
(the notes warn). Hold for a few frames (f30 down, f36 up) for a tap the game reliably sees. Without
`seconds` the capture runs to 1 s after the last event. summary.json `input.byFrame` lists what was
applied on each frame; the brief says how many events applied.

Timing (`timing`): `fixed` (default, the game's own step; repeatable), `real` (measured frame times,
paced to the step like vsync and kept as capture.timing; for frame-rate-dependent bugs in a
variable-step game), or a capture's name or `.timing` file to replay its frame times exactly. A replayed input's own
timing comes with it unless `timing` says otherwise.

Recordings: the user runs the game with `--agenttools-record <path>` and plays; `<path>.input` (and
`<path>.timing`) replay what they did. To check a fix against the moment they recorded, capture with
`input: "<path>.input"` before and after, and compare with `sheet.against`.

A replay matches only if the game is deterministic: seeded randomness, time from `GameTime`.
