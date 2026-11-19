namespace MonoGame.AgentTools;

/// <summary><see cref="AgentToolsOptions"/> validated and copied when <see cref="AgentTools.Attach"/> runs.</summary>
internal sealed class AgentToolsSetup
{
    public AgentToolsSetup(AgentToolsOptions options)
    {
        HudVisible = options.ShowHud;
        Notes = string.IsNullOrWhiteSpace(options.Notes) ? [] : [options.Notes];

        var customMetrics = options.CustomMetrics ?? [];
        var list = new List<CustomMetric>(customMetrics.Count);
        foreach (var (name, read) in customMetrics)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Every custom metric needs a name.", nameof(options));
            if (read is null)
                throw new ArgumentException($"Custom metric '{name}' has no read function.", nameof(options));
            if (list.Exists(g => g.Name == name))
                throw new ArgumentException($"Two custom metrics are named '{name}'.", nameof(options));
            list.Add(new CustomMetric(name, read));
        }
        CustomMetrics = list;
    }

    public bool HudVisible { get; }
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyList<CustomMetric> CustomMetrics { get; }
}

internal sealed record CustomMetric(string Name, Func<double> Read);
