# setup
Adding MonoGame.AgentTools to a game, and what the game must do for capture to work.

1. Reference the package from the game's executable project:
   `<PackageReference Include="MonoGame.AgentTools" Version="0.1.0-*" />`
2. In `Main`, after constructing the game and before `Run`:
   ```csharp
   AgentTools.Attach(game, args, new()
   {
       Notes = "Controls:\nEsc - Exit",                       // optional, shown on the HUD and in summary.json
       CustomMetrics = [("particles", () => game.ParticleCount)], // optional, recorded every frame
   });
   ```
3. The game's `Update` and `Draw` overrides must call `base.Update(gameTime)` and `base.Draw(gameTime)`.

Everything follows `AgentToolsEnabled`, true by default in Debug: when it's off, `Attach` and its
arguments are compiled out, the assembly is left out of the output and `.mcp.json` isn't touched. capture
builds Debug unless given `configuration`; another configuration needs
`<AgentToolsEnabled>true</AgentToolsEnabled>` in the game's project, and capture says so if it's missing.

Custom metric functions run once per frame on the game thread and must not allocate.

A build with the tools enabled adds this server to `.mcp.json` at the repository root (and to `.vscode/mcp.json` and
`.cursor/mcp.json` where those folders exist), merging into what's there, and updates it when the
package version changes; commit it so everyone gets it. An entry edited by hand is left alone.
`<AgentToolsInstallMcp>false</AgentToolsInstallMcp>` turns this off; `<AgentToolsRoot>` picks another
folder. It's skipped in CI and when the library is referenced as a project rather than a package.
