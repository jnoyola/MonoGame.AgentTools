using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using MonoGame.AgentTools.Diagnostics;
using MonoGame.AgentTools.Hud;
using MonoGame.AgentTools.Runtime;

namespace MonoGame.AgentTools.Live;

/// <summary>
/// Measures live play from inside <c>game.Components</c>: one sample per frame, taken when the last
/// drawable component draws (so <see cref="Microsoft.Xna.Framework.Graphics.GraphicsDevice.Metrics"/>
/// still holds the frame's counts). Allocation-free per frame. The HUD is drawn after sampling, and
/// the counters are re-read after it, so its own draws, allocations and JIT don't count against the game.
/// </summary>
internal sealed class LiveMonitor : DrawableGameComponent
{
    private const int Capacity = 4096;
    private static readonly TimeSpan ComponentGracePeriod = TimeSpan.FromSeconds(2);
    private const double HudRefreshSeconds = 0.25;
    private const double HudWindowSeconds = 1.0;

    private readonly IReadOnlyList<CustomMetric> _metrics;
    private readonly IReadOnlyList<string> _notes;
    private readonly FrameSample[] _ring = new FrameSample[Capacity];
    private readonly double[] _metricRing;
    private readonly WarmupDetector _warmup = new();
    private readonly MemoryHistory _memoryHistory = new();
    private readonly Stopwatch _wall = new();
    private readonly double[] _windowScratch = new double[Capacity];
    private RuntimeCounters _start;
    private RuntimeCounters _previous;
    private double _previousWall;
    private int _frames;
    private int _updates;
    private HudLine[] _hudShown;
    private HudLine[] _hudPending;
    private HudOverlay? _hud;
    private ResourceCounters? _resources;
    private bool _hudVisible;
    private bool _toggleHeld;
    private double _hudRefreshedAt = double.NegativeInfinity;
    private Timer? _watchdog;

    private LiveMonitor(Game game, CommandLineOptions options, AgentToolsSetup setup) : base(game)
    {
        _metrics = setup.CustomMetrics;
        _notes = setup.Notes;
        _metricRing = new double[Capacity * _metrics.Count];
        _hudShown = HudText.CreateLines(_metrics.Count, _notes);
        _hudPending = HudText.CreateLines(_metrics.Count, _notes);
        _hudVisible = options.Hud || setup.HudVisible;
        UpdateOrder = int.MinValue;
        DrawOrder = int.MaxValue;
    }

    public static void Install(Game game, CommandLineOptions options, AgentToolsSetup setup)
    {
        Precompile.Types(typeof(LiveMonitor), typeof(WarmupDetector), typeof(WarmupGate), typeof(FrameSample), typeof(RuntimeCounters),
            typeof(HudText), typeof(HudOverlay), typeof(FrameWindow), typeof(ResourceCounters), typeof(MemoryHistory), typeof(ProcessMemory));
        var monitor = new LiveMonitor(game, options, setup);
        game.Components.Add(monitor);
        monitor.StartWatchdog(GameHooks.Bind(game));
        if (options.StatsPath is not null)
            game.Exiting += (_, _) => monitor.WriteStats(options.StatsPath);
    }

    /// <summary>
    /// Backtick (<see cref="Keys.OemTilde"/>, the key left of 1 on US layouts) toggles the HUD, the key
    /// Unreal, Source and Quake-lineage games use for their developer console.
    /// </summary>
    public static bool IsToggleKey(in KeyboardState keyboard) => keyboard.IsKeyDown(Keys.OemTilde);

    protected override void LoadContent()
    {
        _hud = new HudOverlay(GraphicsDevice);
        _resources = new ResourceCounters(GraphicsDevice);
    }

    public override void Update(GameTime gameTime)
    {
        Interlocked.Increment(ref _updates);
        var pressed = Game.IsActive && IsToggleKey(Keyboard.GetState());
        if (pressed && !_toggleHeld)
        {
            _hudVisible = !_hudVisible;
            _hudRefreshedAt = double.NegativeInfinity;
        }
        _toggleHeld = pressed;
    }

    public override void Draw(GameTime gameTime)
    {
        var now = RuntimeCounters.Read();
        if (!_wall.IsRunning)
        {
            _wall.Start();
            _previous = now;
            _start = now;
            return;
        }

        var sample = FrameSample.Between(_previous, now);
        var wall = _wall.Elapsed.TotalSeconds;
        sample.Frame = ++_frames;
        sample.SimSeconds = double.NaN;
        sample.UpdateMs = double.NaN;
        sample.DrawMs = double.NaN;
        sample.FrameMs = (wall - _previousWall) * 1000.0;
        sample.WallSeconds = wall;
        sample.HeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        sample.Graphics = GraphicsDevice.Metrics;
        sample.OtherThreadsAllocatedBytes = now.OtherThreadsAllocatedSince(_previous);
        sample.GraphicsResources = _resources?.GraphicsResources() ?? -1;
        var sampledProcess = _resources?.SamplePrivateBytes(wall) == true;
        sample.PrivateBytes = _resources?.PrivateBytes ?? 0;
        if (sampledProcess)
            _memoryHistory.Record(new MemoryPoint(sample.Frame, wall, sample.HeapBytes, sample.PrivateBytes, sample.GraphicsResources));
        _previous = now;
        _previousWall = wall;

        var slot = (_frames - 1) % Capacity;
        _ring[slot] = sample;
        for (int p = 0; p < _metrics.Count; ++p)
            _metricRing[slot * _metrics.Count + p] = _metrics[p].Read();
        _warmup.Observe(sample);

        // Formats every frame but shows a new text only every HudRefreshSeconds, so the numbers stay
        // readable while the formatting code runs at frame rate. Code called a few times a second keeps
        // getting re-JIT-compiled by tiered compilation for seconds, which would hold the performance gate open.
        if (_hudVisible && _hud is not null)
        {
            FormatHud(now, wall, slot);
            if (wall - _hudRefreshedAt >= HudRefreshSeconds)
            {
                (_hudShown, _hudPending) = (_hudPending, _hudShown);
                _hudRefreshedAt = wall;
            }
            _hud.Draw(_hudShown, wall);
        }

        // Exclude the tools' own work since the sample: the HUD, and the process-memory read where it allocates.
        if ((sampledProcess && _resources!.SamplingAllocates) || (_hudVisible && _hud is not null))
            _previous = RuntimeCounters.Read();
    }

    private void FormatHud(in RuntimeCounters now, double wall, int slot)
    {
        var snapshot = new HudSnapshot
        {
            Frame = _frames,
            WallSeconds = wall,
            Window = MeasureWindow(wall),
            HeapBytes = _ring[slot].HeapBytes,
            PrivateBytes = _ring[slot].PrivateBytes,
            GraphicsResources = _ring[slot].GraphicsResources,
            SinceStart = now.Since(_start),
            Memory = _warmup.Memory,
            Performance = _warmup.Performance,
            ActiveSignals = _warmup.ActiveSignals(wall),
            Graphics = _ring[slot].Graphics,
            CustomMetrics = _metrics,
            CustomMetricValues = _metricRing.AsSpan(slot * _metrics.Count, _metrics.Count),
        };
        HudText.Format(snapshot, _hudPending);
    }

    private FrameWindow MeasureWindow(double wall)
    {
        var window = new FrameWindow();
        var count = Math.Min(_frames, Capacity);
        var oldest = wall;
        var oldestResources = count > 0 ? _ring[(_frames - 1) % Capacity].GraphicsResources : 0;
        var newestResources = oldestResources;
        for (int i = 0; i < count; ++i)
        {
            ref readonly var sample = ref _ring[(_frames - 1 - i) % Capacity];
            if (sample.WallSeconds < wall - HudWindowSeconds)
                break;
            _windowScratch[window.Frames++] = sample.FrameMs;
            window.AllocatedBytes += sample.AllocatedBytes;
            window.OtherThreadsAllocatedBytes += sample.OtherThreadsAllocatedBytes;
            window.Gen0 += sample.Gen0Collections;
            window.Gen1 += sample.Gen1Collections;
            window.Gen2 += sample.Gen2Collections;
            window.JitMethods += sample.JitMethods;
            window.JitMethodsAllThreads += sample.JitMethodsAllThreads;
            oldest = sample.WallSeconds - sample.FrameMs / 1000.0;
            oldestResources = sample.GraphicsResources;
        }
        window.GraphicsResourcesAdded = newestResources - oldestResources;
        window.Seconds = wall - oldest;
        if (window.Frames > 0)
        {
            var times = _windowScratch.AsSpan(0, window.Frames);
            times.Sort();
            window.P50Ms = WarmupDetector.Percentile(times, 0.50);
            window.P95Ms = WarmupDetector.Percentile(times, 0.95);
            window.MaxMs = times[^1];
        }
        return window;
    }

    /// <summary>
    /// Components only run if the game's <c>Update</c>/<c>Draw</c> overrides call the base methods.
    /// Warns once if they never run, so missing measurements don't go unnoticed.
    /// </summary>
    private void StartWatchdog(GameHooks hooks)
    {
        var initializedAt = TimeSpan.Zero;
        var clock = Stopwatch.StartNew();
        _watchdog = new Timer(_ =>
        {
            if (Volatile.Read(ref _frames) > 0)
            {
                _watchdog?.Dispose();
                return;
            }
            if (!hooks.IsInitialized())
                return;
            if (initializedAt == TimeSpan.Zero)
                initializedAt = clock.Elapsed;
            if (clock.Elapsed - initializedAt < ComponentGracePeriod)
                return;
            _watchdog?.Dispose();
            var missing = Volatile.Read(ref _updates) == 0 ? "base.Update(gameTime) and base.Draw(gameTime)" : "base.Draw(gameTime)";
            var message = $"MonoGame.AgentTools: no frames measured. {Game.GetType().Name} must call {missing} " +
                          "at the end of its overrides, or game components (including AgentTools) never run.";
            Console.Error.WriteLine(message);
            Debug.WriteLine(message);
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    private void WriteStats(string path)
    {
        var count = Math.Min(_frames, Capacity);
        var frames = new FrameSample[count];
        var metricValues = new double[count * _metrics.Count];
        for (int i = 0; i < count; ++i)
        {
            var slot = (_frames - count + i) % Capacity;
            frames[i] = _ring[slot];
            Array.Copy(_metricRing, slot * _metrics.Count, metricValues, i * _metrics.Count, _metrics.Count);
        }
        var wall = _wall.Elapsed.TotalSeconds;
        _warmup.Finish(wall);
        var pp = GraphicsDevice.PresentationParameters;
        var info = new ReportInfo
        {
            Mode = "live",
            Game = Game.GetType().FullName ?? Game.GetType().Name,
            Width = pp.BackBufferWidth,
            Height = pp.BackBufferHeight,
            WallSeconds = wall,
            FrameTimeBasis = $"wall time between frames (last {Capacity} frames kept)",
            CustomMetricNames = _metrics.Select(p => p.Name).ToArray(),
            CustomMetricValues = metricValues,
            Notes = _notes,
            MemoryHistory = _memoryHistory.Points.ToArray(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Report.WriteSummary(path, info, FrameAnalysis.Run(frames, _warmup));
        Report.WriteCsv(Path.ChangeExtension(path, ".csv"), info, frames);
        Report.WriteMemoryCsv(Path.ChangeExtension(path, ".memory.csv"), info.MemoryHistory);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog?.Dispose();
            _hud?.Dispose();
            _resources?.Dispose();
        }
        base.Dispose(disposing);
    }
}
