# MonoGame findings

Costs and pitfalls in MonoGame itself that the tools turned up, which every game on the same version
hits. Each has a fix your game applies, so it holds in Release builds, where Synthapixel.MonoGame.AgentTools is
compiled out. The tools never apply these for you: a Debug build would then measure a different game
from the one you ship. The real fixes belong in MonoGame, and an entry goes once it's fixed upstream.

Measured on MonoGame 3.8.5 DesktopGL unless an entry says otherwise.

## The first key press allocates

**Symptom:** about 7.5 KB on the game thread in the frame of the first key press, and a little more each
time more keys are held than ever before. The HUD shows it as an allocation after warm-up.

**Cause:** `KeyboardUtil`, which maps SDL key codes to `Keys`, builds its map in a static constructor on
first use: 7,392 B. `Keyboard`'s list of held keys starts empty and grows as keys go down: 96 B to hold
8.

**Fix:** before `game.Run()`, run the constructor and size the list. Both are internal, so look them up
by name and skip them if they're gone:

```csharp
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Input;

using var game = new MyGame();   // creates the platform, which creates the key list

if (typeof(Keyboard).Assembly.GetType("Microsoft.Xna.Framework.Input.KeyboardUtil") is { } keyboardUtil)
    RuntimeHelpers.RunClassConstructor(keyboardUtil.TypeHandle);
if (typeof(Keyboard).GetField("_keys", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is List<Keys> keys)
    keys.EnsureCapacity(8);

game.Run();
```

**Upstream:** no issue or pull request found (searched 2026-11-21). The fix would build the map
eagerly (or as a static array) and give the list a starting capacity.

## SpriteBatch grows on the busiest frame

**Symptom:** 61 KB the first time a frame draws more than 256 sprites, and again at each new high.

**Cause:** `SpriteBatch` grows its batch on demand.

**Fix:** in `LoadContent`, draw your largest frame's sprites once, invisibly (into a 1x1 render
target, say), so the batch is at full size before play.

**Upstream:** no issue or pull request found (searched 2026-11-21). A constructor argument for the
starting capacity would let a game size it.

## A one-off JIT about three minutes in

**Symptom:** about 1.1 ms of game-thread JIT at frame 9999, about 2 min 47 s into play at 60 fps, long
after warm-up. Every DesktopGL game has it, with or without the tools.

**Cause:** `SdlGamePlatform.RunLoop` is called once and loops forever, so it starts as unoptimized code
with an on-stack-replacement patchpoint. The runtime compiles its optimized version synchronously after
about 10,000 iterations.

**Fix:** `<TieredCompilationQuickJitForLoops>false</TieredCompilationQuickJitForLoops>` in the game's
project compiles methods with loops optimized up front, at some startup cost (not yet measured).

**Upstream:** no issue or pull request found (searched 2026-11-21). It's how .NET's tiered
compilation treats a method that never returns, so a fix in MonoGame would mark `RunLoop`
`AggressiveOptimization`.

## Reading the backbuffer allocates a row

**Symptom:** a game that calls `GraphicsDevice.GetBackBufferData` (for screenshots, say) allocates every
call: 5.1 KB at 1280 pixels wide.

**Cause:** on OpenGL, MonoGame flips the image's rows through a new buffer each call.

**Fix:** call it rarely (a screenshot is fine), not every frame. The capture tools read frames back with
`glReadPixels` and a reused row instead.

**Upstream:** no issue or pull request found (searched 2026-11-21). The fix would reuse the row buffer.
