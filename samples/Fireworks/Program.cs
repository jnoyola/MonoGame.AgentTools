using MonoGame.AgentTools;
using Fireworks;

using var game = new FireworksGame();
AgentTools.Attach(game, args, new()
{
    Notes = "Esc - exit",
    CustomMetrics = [("particles", () => game.ParticleCount)],
});
game.Run();
