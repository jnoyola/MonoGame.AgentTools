using MonoGame.AgentTools.Diagnostics;
using MonoGame.AgentTools.Hud;

namespace MonoGame.AgentTools.Tests;

public class HudTests
{
    [Fact]
    public void Hud_lines_show_the_measurements_with_status_colors()
    {
        var metrics = new[] { new CustomMetric("particles", () => 0) };
        var lines = HudText.CreateLines(metrics.Length, []);
        var memory = new WarmupGate { State = WarmupState.Stable, Frame = 62, WallSeconds = 1.0 };
        var performance = new WarmupGate();

        HudText.Format(new HudSnapshot
        {
            Frame = 120,
            WallSeconds = 2.04,
            Window = new FrameWindow
            {
                Seconds = 1, Frames = 60, P50Ms = 16.67, P95Ms = 17.2, MaxMs = 40, JitMethodsAllThreads = 3,
                OtherThreadsAllocatedBytes = 2048, GraphicsResourcesAdded = 2,
            },
            HeapBytes = 12 * 1024 * 1024,
            PrivateBytes = 300 * 1024 * 1024,
            GraphicsResources = 41,
            SinceStart = new RuntimeCounters(0, 3, 1, 0, 410, TimeSpan.Zero, 900, 0),
            Memory = memory,
            Performance = performance,
            ActiveSignals = WarmupSignal.JitCompilation,
            Graphics = default,
            CustomMetrics = metrics,
            CustomMetricValues = [1284],
        }, lines);

        Assert.Equal(
        [
            "AgentTools  frame 120 | 2.0 s",
            "frame      60.0 fps | p50  16.67 | p95  17.20 | max  40.00 ms",
            "memory  alloc      0 B/s | other threads   2.0 KB/s | heap  12.0 MB",
            "        gc gen0/1/2 0/0/0 per s | 3/1/0 total",
            "        process 300.0 MB | gpu resources    41 (+2/s)",
            "warm-up memory stable at 1.0 s (frame 62)",
            "        perf   warming up: jit",
            "        jit    compiling 3/s | blocking frames 0/s | 900 total",
            "render  draws     0 | primitives         0 | sprites       0",
            "        textures    0 | targets   0 | shaders 0/0 | clears   0",
            "custom  particles        1,284",
        ], lines.Select(l => l.Text.ToString()));
        Assert.Equal(HudText.Warning, lines[1].Color);   // hitch
        Assert.Equal(HudText.Warning, lines[2].Color);   // other threads allocate
        Assert.Equal(HudText.Normal, lines[3].Color);
        Assert.Equal(HudText.Warning, lines[4].Color);   // gpu resources grew
        Assert.Equal(HudText.Normal, lines[5].Color);   // stable: nothing to look at
        Assert.Equal(HudText.Warning, lines[6].Color);
        Assert.Equal(HudText.Normal, lines[7].Color);
    }

    [Fact]
    public void Notes_follow_the_measurements_after_a_blank_line_and_survive_formatting()
    {
        var lines = HudText.CreateLines(0, ["Controls:\r\nEsc - Exit", "F1 - Help"]);

        HudText.Format(new HudSnapshot
        {
            Frame = 1, WallSeconds = 0, Window = default, HeapBytes = 0, PrivateBytes = 0, GraphicsResources = 0, SinceStart = default,
            Memory = new WarmupGate(), Performance = new WarmupGate(), ActiveSignals = WarmupSignal.None,
            Graphics = default, CustomMetrics = [], CustomMetricValues = [],
        }, lines);

        Assert.Equal(["", "Controls:", "Esc - Exit", "F1 - Help"], lines[HudText.FixedLines..].Select(l => l.Text.ToString()));
    }

    [Fact]
    public void Gate_that_gave_up_names_the_blocking_signals()
    {
        var lines = HudText.CreateLines(0, []);
        var gate = new WarmupGate { State = WarmupState.NeverStabilized, Blocking = WarmupSignal.Allocations };

        HudText.Format(new HudSnapshot
        {
            Frame = 1, WallSeconds = 0, Window = default, HeapBytes = 0, PrivateBytes = 0, GraphicsResources = 0, SinceStart = default,
            Memory = gate, Performance = gate, ActiveSignals = WarmupSignal.None, Graphics = default,
            CustomMetrics = [], CustomMetricValues = [],
        }, lines);

        Assert.Equal("warm-up memory never stabilized: alloc", lines[5].Text.ToString());
        Assert.Equal(HudText.Bad, lines[5].Color);
    }

    [Fact]
    public void Hud_is_live_only()
    {
        Assert.True(CommandLineOptions.Parse(["--agenttools-hud"]).Hud);
        var e = Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-hud"]));
        Assert.Contains("live-only", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_width_widens_at_once_and_narrows_after_the_hold()
    {
        var width = new PeakHold(holdSeconds: 5);

        Assert.Equal(300, width.Update(300, 0));
        Assert.Equal(300, width.Update(250, 1));   // held
        Assert.Equal(300, width.Update(300, 3));   // wide text again restarts the hold
        Assert.Equal(300, width.Update(250, 7.9));
        Assert.Equal(250, width.Update(250, 8));   // 5 s since the wide text
        Assert.Equal(320, width.Update(320, 8.1));
    }

    [Fact]
    public void Variable_lines_stay_within_the_fixed_memory_line_for_long_sessions()
    {
        // About 2.7 hours at 1000 fps; beyond that the panel's peak hold absorbs the extra character.
        var lines = HudText.CreateLines(0, []);
        var stable = new WarmupGate { State = WarmupState.Stable, Frame = 9_999_999, WallSeconds = 9_999.9, DisturbancesAfterStable = 999 };

        HudText.Format(new HudSnapshot
        {
            Frame = 9_999_999, WallSeconds = 9_999.9,
            Window = new FrameWindow
            {
                Seconds = 1, Frames = 9_999, P50Ms = 999.99, P95Ms = 999.99, MaxMs = 999.99,
                Gen0 = 99, Gen1 = 99, Gen2 = 99, JitMethods = 99, JitMethodsAllThreads = 999, GraphicsResourcesAdded = 999,
                AllocatedBytes = 1023 * 1024, OtherThreadsAllocatedBytes = 1023L * 1024 * 1024,
            },
            HeapBytes = 1023L * 1024 * 1024, PrivateBytes = 999L * 1024 * 1024 * 1024, GraphicsResources = 99_999,
            SinceStart = new RuntimeCounters(0, 9_999, 9_999, 9_999, 0, TimeSpan.Zero, 99_999, 0),
            Memory = stable, Performance = stable, ActiveSignals = WarmupSignal.None,
            Graphics = default, CustomMetrics = [], CustomMetricValues = [],
        }, lines);

        var memoryLine = lines[2].Text.Length;
        Assert.Equal("memory  alloc      0 B/s | other threads   2.0 KB/s | heap  12.0 MB".Length, memoryLine); // fixed
        Assert.All(lines, l => Assert.True(l.Text.Length <= memoryLine, $"'{l.Text}' is longer than the memory line"));
    }
}
