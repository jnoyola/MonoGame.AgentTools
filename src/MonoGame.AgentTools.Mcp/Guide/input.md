# input
Driving the keyboard, mouse and gamepads in a capture or profile, replaying sessions the user played, and frame timing.

Pass `input`: the script text, a path to an `.input` file, or the name of a capture or a live session
to replay its input. The game reads it through its normal `Keyboard`/`Mouse`/`GamePad.GetState()` calls and
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
`duration` the capture runs to 1 s after the last event. summary.json `input.byFrame` lists what was
applied on each frame; the brief says how many events applied.

Timing (`timing`): `fixed` (default, the game's own step; repeatable), `real` (measured frame times,
paced to the step like vsync and kept as capture.timing; for frame-rate-dependent bugs in a
variable-step game), or a capture's name or `.timing` file to replay its frame times exactly. A replayed input's own
timing comes with it unless `timing` says otherwise.

Sessions: every time the user plays (a Debug build run without capture or profile), the game records
what it read to `.agenttools/sessions/<Game>-<yyyyMMdd-HHmmss>/`: `session.input`, `session.timing` for a
variable-step game, and `summary.json`. The 10 most recent are kept; a renamed folder is kept for good.
List the folder for the newest. Pass its name as `input` to replay it; its timing comes with it. If the
game is still open, the last second may not be written yet; ask the user to quit it first. To check
a fix against a moment the user played, capture with that input before and after, and compare with
`sheet.against`.

A replay matches only if the game is deterministic: seeded randomness, time from `GameTime`.
