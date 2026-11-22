# Manual tests

Checks that `dotnet test` and CI can't make yet. Run them before each release, and record when each last
passed. A pull request that adds behavior no test can reach adds its entry here. An entry comes off once
a test covers it.

Each entry says what it needs and what keeps it from running automatically, so it's clear what would
retire it.

## End-to-end input tests

`InputCaptureTests` runs the input probe game: scripted input, typed text, record and replay, real
timing with its pacing, and a live session recorded under a repository root.

- **Needs:** Windows, a GPU and a signed-in desktop session.
- **Blocked here by:** CI runners have no GPU or desktop, so the tests skip when `CI` is set. They also skip
  off Windows, where they haven't been checked.
- **Check:** `dotnet test` on a Windows desktop. Pass: no skipped tests in `InputCaptureTests`.
- **Would retire it:** a CI job with a software renderer (Mesa llvmpipe under a virtual display on Linux,
  or WARP on Windows).
- **Last passed:** 2026-11-21, `jnoyola/cli-redesign`, Windows 11, .NET 8 and 11.

## A played session replays identically

A person plays, and a capture replaying their recorded session shows what they saw.

- **Needs:** a game that is deterministic given input and time; a person playing on a physical keyboard,
  mouse and gamepad; a non-US keyboard layout or an accented-letter input method for typed text.
- **Blocked here by:** the probe drives input through the same path it records, so real devices, real
  layouts and a person's timing never reach a test.
- **Check:** play a session of at least a minute that uses every device and types in a text field. Capture
  with `input` set to the session's name. Pass: the capture's frames match the moments played, and the typed
  text arrives as typed.
- **Last passed:** never.

## A crash keeps the recording

- **Needs:** a game that throws an unhandled exception on its game thread while it's played.
- **Blocked here by:** the probe has no way to crash on cue. A switch that throws at a chosen frame would let
  a test cover this.
- **Check:** play until the crash. Pass: the session's `session.input` ends on a whole line and holds the
  events up to the crash; a capture replays it to that point.
- **Last passed:** never.

## Captures on a platform without SDL input

Where MonoGame's SDL input internals aren't there, a capture without a script runs with a note, and one
with a script fails up front with the reason.

- **Needs:** a MonoGame game on a synchronous-loop platform other than DesktopGL, such as WindowsDX.
- **Blocked here by:** the probe and the tests build only for DesktopGL.
- **Check:** capture without `input`. Pass: it completes and the notes say the machine's input isn't shut
  out. Capture with `input`. Pass: it fails before the game starts, naming the missing member.
- **Would retire it:** a WindowsDX build of the probe, run on Windows.
- **Last passed:** never.

## Tests in VS Code

- **Needs:** VS Code with C# Dev Kit.
- **Blocked here by:** discovery and running happen in the editor's Test Explorer.
- **Check:** open the repository, run all tests from Test Explorer. Pass: every test is discovered on each
  target runtime and passes or skips as it does from `dotnet test`. Repeat after removing
  `xunit.runner.visualstudio`, which Microsoft.Testing.Platform shouldn't need.
- **Last passed:** never.
