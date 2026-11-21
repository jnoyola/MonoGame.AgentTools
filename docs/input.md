# Input and replay

A capture can drive your game's keyboard, mouse and gamepads from a script, so an agent can see what
happens when the player acts. Live play can record what you pressed into the same format, so you play a
moment once and an agent replays it exactly. Your game doesn't change: it keeps calling
`Keyboard.GetState()`, `Mouse.GetState()` and `GamePad.GetState()`.

Every capture is also isolated from the machine it runs on: your cursor, Caps Lock and any connected
gamepad never reach it, with or without a script.

## Scripts

One event per line: when, what, and how.

```text
# Jump, then shoot at the crate.
f60  Space down
f75  Space up
t1.5 MouseLeft down 640,360
f96  MouseLeft up
f120 PadA down
f120 PadLeftStick 0.5,-1
f150 PadLeftStick 0,0
```

**When** is `f<n>` (the nth frame, numbered as in sheets and `frames.csv`) or `t<seconds>` (game time:
the first frame at or after it). Lines on the same frame apply in order. `#` starts a comment.

**Controls** use MonoGame's own names:

| Device | Controls | Values |
|---|---|---|
| Keyboard | `Keys` names: `Space`, `A`, `Left`, `LeftShift`, `F1` | `down`, `up` |
| Mouse buttons | `MouseLeft`, `MouseRight`, `MouseMiddle`, `MouseX1`, `MouseX2` | `down`, `up`, or `down x,y` to move and press |
| Mouse position | `Mouse` | `x,y` in window pixels (the capture's backbuffer size) |
| Mouse wheel | `MouseWheel`, `MouseWheelH` | notches, such as `+1` or `-2` |
| Gamepad buttons | `Pad` + `Buttons` names: `PadA`, `PadStart`, `PadDPadUp`, `PadLeftShoulder`; stick clicks are `PadLeftStickButton`, `PadRightStickButton` | `down`, `up` |
| Thumbsticks | `PadLeftStick`, `PadRightStick` | `x,y` from -1 to 1, y up |
| Triggers | `PadLeftTrigger`, `PadRightTrigger` | 0 to 1 |

`Pad` is player one; `Pad2A` to `Pad4A` are the others. Gamepads a script uses are connected for the
whole capture. Everything holds until changed: a key stays down until its `up`, a stick stays where it
was put. Stick values are raw, before your game's dead zone.

Mistakes come back with the line and a suggestion: `line 4: unknown control 'Spcae'; did you mean
'Space'?`. A button pressed and released on the same frame is flagged in the notes, since `GetState`
never shows it pressed.

## Running a script

```
MyGame --agenttools-capture out/jump --agenttools-input jump.input
```

Through MCP, `input` takes the script itself, a file, or a capture's name to replay its input:

```json
{ "input": "f60 Space down\nf75 Space up", "name": "jump" }
{ "input": "jump", "name": "jump-after-fix", "sheet": { "against": "jump" } }
```

Without `seconds`, the capture runs until 1 s after the last event, to show the reaction to it. The
script is copied into the capture folder as `capture.input`, and `summary.json` lists every event by the
frame it applied on.

## Recording

```
MyGame --agenttools-record sessions/boss
```

Play as usual. `sessions/boss.input` is written as you go (a crash keeps it, and even a killed process
loses at most the last second). Replay it with `--agenttools-input sessions/boss.input`, or give the
agent its path. If writing fails partway (a full disk, say), what was written is kept, the game carries
on, and the session stats' `recording.error` says why.

The recording samples once per `Update`, like your game, so it holds exactly what your game read. A
press and release within one frame is the one thing it misses: `GetState` never showed it.

## Timing

Each frame's game time comes from one of three places:

| Timing | Frame times | Use it for |
|---|---|---|
| `fixed` (default) | your game's `TargetElapsedTime`, or `fps` | Deterministic captures |
| `real` | measured, as live play gets them | A variable-step game's frame-rate bugs |
| a `.timing` file | replayed from a recording or a `real` capture | Reproducing those bugs exactly |

A capture never reads the clock under `fixed`, even for a game with `IsFixedTimeStep = false`, so it
sees perfectly even steps. That's what makes captures repeatable, but it hides bugs that need uneven
frames, such as physics that depend on frame rate. `real` measures each frame instead, pacing frames
to the step the way vsync would, so the times are your game's own frame-to-frame variation and the
capture covers the game time asked for. It keeps them in `capture.timing`, so a run that shows a bug can
be replayed frame for frame:

```json
{ "timing": "real", "seconds": 10, "name": "jitter" }
{ "timing": "jitter", "name": "jitter-replay" }
```

Recording a variable-step game also writes `<path>.timing` beside the `.input`; replaying the `.input`
uses it automatically, so your game gets the same input on the same frames with the same elapsed times.
Pass `timing: "fixed"` to replay just the input at an even step instead: inputs keep their times, but
the game's state can drift from the session.

A capture keeps the script and timing it played, as read before the game started, in `capture.input`
and `capture.timing`. Recording during a capture to the same path as the input it replays is refused,
since it would overwrite that input.

## Making replays match

A replay reproduces the original only if your game is deterministic given input and time: seed random
number generators, and take time from `GameTime`, not `Stopwatch` or `DateTime`. See
[Capture](capture.md#determinism).

Input works with DesktopGL on Windows, Linux and macOS, like the rest of capture. It feeds MonoGame's own
input path through internal members of MonoGame 3.8.5. If a MonoGame version or platform changes them,
a capture with a script fails up front with the member's name; one without a script still runs, with a
note that the machine's own input isn't shut out.

Scripts press keys, but don't type text yet: games that read `Window.TextInput` (text fields, chat,
consoles, UI libraries) get key presses but no characters. See [Design](design.md#input-timing-and-replay-v02).
