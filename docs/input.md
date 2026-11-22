# Input and replay

A capture or profile can drive your game's keyboard, mouse and gamepads from a script, so an agent can
see what happens when the player acts. Live play records what you pressed into the same format, so you
play a moment once and an agent replays it exactly. Your game doesn't change: it keeps calling
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
| Typed text | `Text` | a quoted string, escaped as in JSON: `Text "Ada \"the first\""` |

`Text` raises `Window.TextInput` once per character, as typing does, for games with text fields, chat,
consoles or UI libraries that read it. It presses no keys: script those separately if your game also
reads them. Enter, Backspace, Tab, Escape and Delete raise `TextInput` from their key presses, as in
MonoGame, so script them as keys. A recording keeps what you typed as `Text` lines.

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

Through MCP, `capture` and `profile` take `input` as the script itself, a file, or the name of a
capture or a [session](#recording) to replay its input:

```
capture({ "input": "f60 Space down\nf75 Space up", "name": "jump" })
capture({ "input": "jump", "name": "jump-after-fix", "sheet": { "baseline": "jump" } })
```

Without `duration`, the capture runs until 1 s after the last event, to show the reaction to it. The
script is copied into the capture folder as `capture.input`, and `summary.json` lists every event by the
frame it applied on.

## Recording

Every time you play, the game records what it read to `.agenttools/sessions/<Game>-<time>/session.input`
(see [Live play](live.md#sessions)). It's written as you go: a crash keeps it, and even a killed process
loses at most the last second. The agent replays it by the folder's name:

```
capture({ "input": "MyGame-20261121-093000", "name": "boss-before-fix" })
```

From a terminal, pass the file: `--agenttools-input .agenttools/sessions/MyGame-20261121-093000/session.input`.
If writing fails partway (a full disk, say), what was written is kept, the game carries on, and the
session summary's `recording.error` says why.

The recording samples once per `Update`, like your game, so it holds exactly what your game read. A
press and release within one frame is the one thing it misses: `GetState` never showed it.

## Timing

Each frame's game time comes from one of three places:

| Timing | Frame times | Use it for |
|---|---|---|
| `fixed` (default) | your game's `TargetElapsedTime`, or `fps` | Deterministic captures |
| `real` | measured, as live play gets them | A variable-step game's frame-rate bugs |
| a `.timing` file | replayed from a session or a `real` capture | Reproducing those bugs exactly |

A capture never reads the clock under `fixed`, even for a game with `IsFixedTimeStep = false`, so it
sees perfectly even steps. That's what makes captures repeatable, but it hides bugs that need uneven
frames, such as physics that depend on frame rate. `real` measures each frame instead, pacing frames
to the step the way vsync would, so the times are your game's own frame-to-frame variation and the
capture covers the game time asked for. It keeps them in `capture.timing`, so a run that shows a bug can
be replayed frame for frame:

```
capture({ "timing": "real", "duration": "10s", "name": "jitter" })
capture({ "timing": "jitter", "name": "jitter-replay" })
```

A session of a variable-step game also has `session.timing` beside its `.input`; replaying the input
uses it automatically, so your game gets the same input on the same frames with the same elapsed times.
Pass `timing: "fixed"` to replay just the input at an even step instead: inputs keep their times, but
the game's state can drift from the session.

A capture keeps the script and timing it played, as read before the game started, in `capture.input`
and `capture.timing`, so a capture re-run under its own name with its own input (`"name": "jump",
"input": "jump"`) plays the same input again after a change.

A session can be replayed while the game that recorded it is still open. The recording reaches the disk
about once a second, so the last second may be missing: quit the game first to replay all of it.

## Making replays match

A replay reproduces the original only if your game is deterministic given input and time: seed random
number generators, and take time from `GameTime`, not `Stopwatch` or `DateTime`. See
[Capture](capture.md#determinism).

Input works with DesktopGL on Windows, Linux and macOS, like the rest of capture. It feeds MonoGame's own
input path through internal members of MonoGame 3.8.5. If a MonoGame version or platform changes them,
a capture with a script fails up front with the member's name; one without a script still runs, with a
note that the machine's own input isn't shut out. See [Design](design.md#input-timing-and-replay-v02).
