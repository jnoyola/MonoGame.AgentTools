namespace MonoGame.AgentTools.Diagnostics;

internal readonly record struct Distribution(double P50, double P95, double P99, double Max, double Mean, int Count)
{
    public static Distribution Of(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).Order().ToArray();
        if (sorted.Length == 0)
            return new Distribution(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        return new Distribution(
            WarmupDetector.Percentile(sorted, 0.50),
            WarmupDetector.Percentile(sorted, 0.95),
            WarmupDetector.Percentile(sorted, 0.99),
            sorted[^1],
            sorted.Average(),
            sorted.Length);
    }
}

internal sealed record Hitch(int Frame, double FrameMs, double ExcessMs, string LikelyCause, FrameSample Sample)
{
    public IReadOnlyList<string> Methods { get; init; } = [];
}

/// <summary>Summary statistics over recorded frames. Runs once at the end, so it may allocate.</summary>
internal sealed class FrameAnalysis
{
    /// <summary>Hitches listed in the summary (the largest); <see cref="HitchCount"/> counts them all.</summary>
    public const int MaxHitches = 10;

    private FrameAnalysis(FrameSample[] frames, WarmupDetector warmup)
    {
        Frames = frames;
        Warmup = warmup;
        var stableFrame = warmup.Performance.State == WarmupState.Stable ? warmup.Performance.Frame : int.MaxValue;
        Measured = frames.Where(f => f.Frame > stableFrame).ToArray();
        MeasuredAfterWarmup = Measured.Length > 0;
        if (!MeasuredAfterWarmup)
            Measured = frames;

        FrameTime = Distribution.Of(Measured.Select(f => f.FrameMs));
        var median = Distribution.Of(frames.Select(f => f.FrameMs)).P50;
        HitchThresholdMs = Math.Max(2 * median, median + 2.0);
        var hitches = frames.Where(f => f.FrameMs > HitchThresholdMs).ToArray();
        HitchCount = hitches.Length;
        Hitches = hitches
            .OrderByDescending(f => f.FrameMs)
            .Take(MaxHitches)
            .OrderBy(f => f.Frame)
            .Select(f => new Hitch(f.Frame, f.FrameMs, f.FrameMs - median, LikelyCause(f, median), f))
            .ToArray();
    }

    public FrameSample[] Frames { get; }
    public WarmupDetector Warmup { get; }
    /// <summary>Frames after the performance gate, or all frames if it was never reached.</summary>
    public FrameSample[] Measured { get; }
    public bool MeasuredAfterWarmup { get; }
    public Distribution FrameTime { get; }
    public double HitchThresholdMs { get; }
    /// <summary>The <see cref="MaxHitches"/> largest hitches, in frame order.</summary>
    public IReadOnlyList<Hitch> Hitches { get; private set; }
    public int HitchCount { get; }

    public static FrameAnalysis Run(FrameSample[] frames, WarmupDetector warmup) => new(frames, warmup);

    public void AttachMethodNames(Func<int, IReadOnlyList<string>> namesForFrame) =>
        Hitches = Hitches.Select(h => h with { Methods = namesForFrame(h.Frame) }).ToArray();

    private static string LikelyCause(in FrameSample frame, double median)
    {
        var excess = frame.FrameMs - median;
        if (frame.Gen0Collections > 0)
            return "garbage collection";
        if (frame.JitMs >= 0.5 * excess)
            return "jit compilation (first use of code)";
        if (frame.JitMethods > 0 || frame.AllocatedBytes > 0)
            return "first use (jit, one-time initialization, gpu resource setup)";
        return "unattributed (gpu/driver work or os scheduling)";
    }
}
