namespace MonoGame.AgentTools;

/// <summary>Optional settings for <see cref="AgentTools.Attach"/>. Every property has a default.</summary>
public sealed class AgentToolsOptions
{
    /// <summary>Shows the HUD from the start in live play, as <c>--agenttools-hud</c> does. Backtick still toggles it.</summary>
    public bool ShowHud { get; init; }

    /// <summary>
    /// Fixed text for whoever runs the game, such as its controls. Shown under the HUD and written to every
    /// <c>summary.json</c> as <c>notes</c>, so agents see it too. The HUD font covers printable ASCII; other
    /// characters show as <c>?</c>.
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Game values to record every frame, such as a live particle count: shown in the HUD's <c>custom</c>
    /// section, written as <c>custom.&lt;name&gt;</c> CSV columns and summarized in <c>summary.json</c>.
    /// Each function runs on the game thread once per frame and must not allocate. Names must be unique.
    /// Return <see cref="double.NaN"/> when there's no value that frame: the CSV cell is left empty and the
    /// summary skips it.
    /// </summary>
    public IReadOnlyList<(string Name, Func<double> Read)> CustomMetrics { get; init; } = [];
}
