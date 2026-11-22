# Goblin Gold

A top-down arena action game for MonoGame (DesktopGL, .NET 11). You're an elf archer in a forest clearing. Goblins pour in from every side, and each one you kill bursts into treasure. Grab as much as you can before the horde overwhelms you. The treasure you collect is your score.

All graphics are drawn in code, including the pixel font. There are no asset files and no audio.

```
dotnet run
```

## Controls

| Input | Action |
|---|---|
| WASD / arrow keys | Move |
| Mouse | Aim |
| Left mouse (hold) | Shoot (auto-fires while held, ~6 arrows/s) |
| Right mouse / E | Arrow volley: a 7-arrow piercing fan, 4 s cooldown |
| Space / Shift | Dash: a short burst that makes you invulnerable while it lasts, 0.75 s cooldown, buffered if pressed slightly early |
| Esc / P | Pause (Q on the pause screen quits to the title) |
| F11 / Alt+Enter | Toggle fullscreen |

## Design decisions (things the brief left open)

- **Arena:** a single screen (1280×720 virtual resolution), letterboxed when the window is resized. The whole fight stays in view, so there's no camera to manage.
- **Health:** 5 hearts. Touching a goblin or getting hit by a rock costs one heart. After a hit you're invulnerable for a short time, and nearby goblins are shoved back so you're not hit again straight away. Dead goblins occasionally drop a heart (~3.5%, only if you're hurt). Every chief drops one.
- **Treasure / score:** coin = 10, gem = 50, diamond = 250. Loot bounces out of each burst and is pulled toward you when you're close (magnet radius). Uncollected loot blinks and vanishes after 11 s, so you have to keep moving to collect it. Only treasure you actually pick up counts.
- **Combo:** kills less than 2.6 s apart build a combo. Every 8 combo adds an extra gem to each burst (up to +3), and every 10th kill past a 20 combo adds a diamond. Getting hit resets the combo.
- **Enemies:**
  - Grunt: the basic chaser.
  - Runner: fast and fragile, zig-zags. Arrives from 12 s.
  - Slinger: keeps its distance and throws rocks after a visible wind-up. You can shoot the rocks down. Arrives from 40 s.
  - Brute: slow and tanky, swings a club, drops lots of loot. Arrives from 55 s.
  - Goblin Chief: a boss every 90 s with a health bar. Before charging it shakes and shows a red line along its charge path. Each chief has more health than the last.
  - Red chevrons at the screen edge warn where goblins are about to enter.
- **Difficulty:** it rises continuously with survival time. Spawns get more frequent, groups get bigger, goblins get faster (runners speed up only a little, so they never outrun you by much), the number of goblins allowed on screen goes up, and grunts, brutes and slingers get extra health every 2 minutes. A new wave banner appears every 30 s. Difficulty never levels off, so every run ends.
- **Game feel:** hit flash, squash and knockback on goblins; brief hit-stop on kills (longer for big ones); trauma-based screen shake; a small camera kick in the opposite direction when you fire; muzzle sparks; arrow trails; arrows stick into the hedge; slow motion when you die; and a score counter that rolls up.
- **Best score** is saved to `%LOCALAPPDATA%/GoblinGold/best.txt`. If the file can't be written, the game carries on without saving it.
- The title screen plays an AI-controlled demo game in the background.

## Testing aids

Command-line flags, used with the MonoGame.AgentTools capture/profile runs:

- `--bot`: an autopilot plays (best score isn't saved). To show off both abilities, it always follows a volley with a dash, and a dash with a volley, within 5 s. While a banner is on screen it stays in the lower part of the arena, and it saves its volleys for when it's away from the edges, so they're easy to see on camera. Volleys are fired from near the middle of the arena along the longest open line, preferring lines with goblins on them. Paired dashes wait for open ground and head toward the most open direction.
- `--botlog FILE`: write the game time of each autopilot volley and dash to FILE
- `--start`: skip the title screen
- `--seed N`: fix the random seed for repeatable runs
- `--time T`: start at difficulty time T seconds (e.g. `--time 85` to meet the first chief)

## Code layout

- `Game1.cs`: game states (title / playing / paused / game over), input, screen scaling, menus
- `World.cs`: simulation (player, goblins, arrows, loot, spawning, difficulty), effects, world and HUD drawing
- `Entities.cs`: plain data types
- `Gfx.cs`: textures generated at load time, drawing helpers, the 5×7 pixel font
- `Background.cs`: draws the forest clearing once at load and keeps it as a texture
- `Bot.cs`: autopilot for the attract mode and automated playtests
