using System.Reflection;
using Microsoft.Xna.Framework;

namespace MonoGame.AgentTools.Runtime;

/// <summary>
/// Delegates to the protected and internal parts of <see cref="Game"/> that the harness drives. Bound once
/// via reflection; virtual members dispatch to the game's overrides. Tested against MonoGame 3.8.5 and
/// develop 76ed1c2; binding fails with a clear message if MonoGame changes them.
/// </summary>
internal sealed class GameHooks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private GameHooks(Game game)
    {
        var type = typeof(Game);
        Update = Bind<Action<GameTime>>(type, "Update", game, typeof(GameTime));
        Draw = Bind<Action<GameTime>>(type, "Draw", game, typeof(GameTime));
        BeginDraw = Bind<Func<bool>>(type, "BeginDraw", game);
        EndDraw = Bind<Action>(type, "EndDraw", game);
        BeginRun = Bind<Action>(type, "BeginRun", game);
        Initialize = Bind<Action>(type, "DoInitialize", game);
        IsInitialized = Bind<Func<bool>>(type, "get_Initialized", game);

        var platform = type.GetField("Platform", Instance)?.GetValue(game)
            ?? throw Incompatible("Game.Platform");
        BeforeRun = Bind<Func<bool>>(platform.GetType(), "BeforeRun", platform);
    }

    public Action<GameTime> Update { get; }
    public Action<GameTime> Draw { get; }
    public Func<bool> BeginDraw { get; }
    /// <summary>Presents the frame (the game may override it).</summary>
    public Action EndDraw { get; }
    public Action BeginRun { get; }
    /// <summary><c>Game.DoInitialize</c>: creates the device, calls <c>Initialize</c> and so <c>LoadContent</c>.</summary>
    public Action Initialize { get; }
    public Func<bool> IsInitialized { get; }
    /// <summary><c>GamePlatform.BeforeRun</c>; false on platforms that can't be driven synchronously.</summary>
    public Func<bool> BeforeRun { get; }

    public static GameHooks Bind(Game game) => new(game);

    private static T Bind<T>(Type type, string name, object target, params Type[] parameters) where T : Delegate
    {
        var method = type.GetMethod(name, Instance, binder: null, parameters, modifiers: null)
            ?? throw Incompatible($"{type.Name}.{name}");
        return method.CreateDelegate<T>(target);
    }

    internal static InvalidOperationException Incompatible(string member) =>
        new($"MonoGame.AgentTools can't find {member} in MonoGame {typeof(Game).Assembly.GetName().Version}. " +
            "This MonoGame version isn't supported yet.");
}
