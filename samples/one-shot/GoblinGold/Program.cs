using MonoGame.AgentTools;

using var game = new OneShot.Game1();
AgentTools.Attach(game, args, new() { ShowHud = true });
game.Run();
