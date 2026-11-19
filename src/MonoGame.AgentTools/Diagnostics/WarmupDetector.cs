namespace MonoGame.AgentTools.Diagnostics;

internal enum WarmupState { WarmingUp, Stable, NeverStabilized }

[Flags]
internal enum WarmupSignal
{
    None = 0,
    Allocations = 1,
    GarbageCollections = 2,
    JitCompilation = 4,
}

internal sealed record WarmupSettings
{
    /// <summary>How long every signal must stay quiet.</summary>
    public double QuietSeconds { get; init; } = 1.0;
    public double MinimumSeconds { get; init; } = 1.0;
    /// <summary>After this, a gate that hasn't settled is reported as never stabilized.</summary>
    public double CapSeconds { get; init; } = 10.0;
}

/// <summary>Result of one stability gate.</summary>
internal sealed class WarmupGate
{
    public WarmupState State { get; internal set; }
    /// <summary>Frame at which the gate became stable.</summary>
    public int Frame { get; internal set; }
    public double WallSeconds { get; internal set; }
    /// <summary>Signals still active when the gate gave up (NeverStabilized).</summary>
    public WarmupSignal Blocking { get; internal set; }
    /// <summary>Game-thread allocation rate over the last quiet window, when it gave up.</summary>
    public double AllocatedBytesPerSecond { get; internal set; }
    /// <summary>Frames that broke the steady state after it was reached (new content, first use).</summary>
    public int DisturbancesAfterStable { get; internal set; }
    public int FirstDisturbanceFrame { get; internal set; }
}

/// <summary>
/// Decides when measurements are representative, from signals rather than a fixed frame count.
/// Memory gate: no game-thread allocations and no GC for <see cref="WarmupSettings.QuietSeconds"/>
/// of wall time (the managed heap is then exactly flat). Performance gate: additionally no JIT on any
/// thread (tiered compilation keeps optimizing for about a second after memory settles). Frame-time
/// spread isn't a condition: uncapped games jitter by nature, and the spread is reported, not gated.
/// Windows are wall time, because tiered compilation runs on wall-clock delays.
/// Precedent: BenchmarkDotNet's warm-up with steady-state detection.
/// </summary>
internal sealed class WarmupDetector
{
    private const int Capacity = 4096;

    private readonly WarmupSettings _settings;
    private readonly double[] _wall = new double[Capacity];
    private readonly long[] _allocated = new long[Capacity];
    private int _count;
    private int _next;

    private double _lastAllocation = double.NegativeInfinity;
    private double _lastCollection = double.NegativeInfinity;
    private double _lastJit = double.NegativeInfinity;

    public WarmupDetector(WarmupSettings? settings = null)
    {
        _settings = settings ?? new WarmupSettings();
        // The first sort allocates the runtime's sort helper and JIT-compiles its partitioning code, which
        // only runs above 16 elements. Do both now so the HUD's percentiles don't count against the game.
        var warmup = new double[64];
        for (int i = 0; i < warmup.Length; ++i)
            warmup[i] = warmup.Length - i;
        Array.Sort(warmup);
        _ = Percentile(warmup, 0.95);
    }

    public WarmupGate Memory { get; } = new();
    public WarmupGate Performance { get; } = new();

    public void Observe(in FrameSample sample)
    {
        var wall = sample.WallSeconds;
        _wall[_next] = wall;
        _allocated[_next] = sample.AllocatedBytes;
        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);

        if (sample.AllocatedBytes > 0)
            _lastAllocation = wall;
        if (sample.Gen0Collections > 0)
            _lastCollection = wall;
        if (sample.JitMethods > 0 || sample.JitMethodsAllThreads > 0)
            _lastJit = wall;

        var memoryQuietSince = Math.Max(_lastAllocation, _lastCollection);
        Advance(Memory, sample, memoryQuietSince, MemorySignals);
        Advance(Performance, sample, Math.Max(memoryQuietSince, _lastJit), PerformanceSignals);
    }

    /// <summary>Call when measurement ends, so a gate that ran past the cap is reported as never stabilized.</summary>
    public void Finish(double wallSeconds)
    {
        GiveUpIfCapped(Memory, wallSeconds, MemorySignals);
        GiveUpIfCapped(Performance, wallSeconds, PerformanceSignals);
    }

    private const WarmupSignal MemorySignals = WarmupSignal.Allocations | WarmupSignal.GarbageCollections;
    private const WarmupSignal PerformanceSignals = MemorySignals | WarmupSignal.JitCompilation;

    private void Advance(WarmupGate gate, in FrameSample sample, double quietSince, WarmupSignal signals)
    {
        var wall = sample.WallSeconds;
        if (gate.State == WarmupState.Stable)
        {
            if (quietSince == wall)
            {
                if (gate.DisturbancesAfterStable++ == 0)
                    gate.FirstDisturbanceFrame = sample.Frame;
            }
            return;
        }
        if (gate.State == WarmupState.NeverStabilized)
            return;

        var quiet = wall - quietSince >= _settings.QuietSeconds;
        if (quiet && wall >= _settings.MinimumSeconds)
        {
            gate.State = WarmupState.Stable;
            gate.Frame = sample.Frame;
            gate.WallSeconds = wall;
            return;
        }
        GiveUpIfCapped(gate, wall, signals);
    }

    private void GiveUpIfCapped(WarmupGate gate, double wall, WarmupSignal considered)
    {
        if (gate.State != WarmupState.WarmingUp || wall < _settings.CapSeconds)
            return;
        gate.State = WarmupState.NeverStabilized;
        gate.Blocking = ActiveSignals(wall) & considered;
        gate.AllocatedBytesPerSecond = AllocationRate(wall - _settings.QuietSeconds, wall);
    }

    /// <summary>Signals seen within the last quiet window ending at <paramref name="wall"/>.</summary>
    public WarmupSignal ActiveSignals(double wall)
    {
        var windowStart = wall - _settings.QuietSeconds;
        var active = WarmupSignal.None;
        if (_lastAllocation >= windowStart) active |= WarmupSignal.Allocations;
        if (_lastCollection >= windowStart) active |= WarmupSignal.GarbageCollections;
        if (_lastJit >= windowStart) active |= WarmupSignal.JitCompilation;
        return active;
    }

    private double AllocationRate(double windowStart, double wall)
    {
        long total = 0;
        for (int i = 0; i < _count; ++i)
        {
            var index = (_next - 1 - i + Capacity) % Capacity;
            if (_wall[index] < windowStart)
                break;
            total += _allocated[index];
        }
        var seconds = wall - windowStart;
        return seconds > 0 ? total / seconds : 0;
    }

    /// <summary>Linear-interpolated percentile of sorted values.</summary>
    public static double Percentile(ReadOnlySpan<double> sorted, double p)
    {
        if (sorted.IsEmpty)
            return double.NaN;
        var position = p * (sorted.Length - 1);
        var lower = (int)position;
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
