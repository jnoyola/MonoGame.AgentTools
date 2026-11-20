using MonoGame.AgentTools.Diagnostics;

namespace MonoGame.AgentTools.Tests;

public class WarmupDetectorTests
{
    private const double Step = 1.0 / 60.0;

    private static void Run(WarmupDetector detector, int frames, Func<int, FrameSample> frame)
    {
        for (int i = 1; i <= frames; ++i)
        {
            var sample = frame(i);
            sample.Frame = i;
            sample.WallSeconds = i * Step;
            if (sample.FrameMs == 0)
                sample.FrameMs = 1.0;
            detector.Observe(sample);
        }
        detector.Finish(frames * Step);
    }

    [Fact]
    public void Quiet_signals_stabilize_both_gates_after_the_minimum()
    {
        var detector = new WarmupDetector();

        Run(detector, 180, i => new FrameSample { AllocatedBytes = i <= 10 ? 100 : 0, JitMethods = i <= 30 ? 5 : 0 });

        Assert.Equal(WarmupState.Stable, detector.Memory.State);
        Assert.Equal(70, detector.Memory.Frame);          // 1 s after the last allocation (frame 10)
        Assert.Equal(WarmupState.Stable, detector.Performance.State);
        Assert.Equal(90, detector.Performance.Frame);     // 1 s after the last JIT (frame 30)
    }

    [Fact]
    public void Steady_allocations_never_stabilize_and_report_the_rate()
    {
        var detector = new WarmupDetector(new WarmupSettings { CapSeconds = 3 });

        Run(detector, 240, _ => new FrameSample { AllocatedBytes = 64 });

        Assert.Equal(WarmupState.NeverStabilized, detector.Memory.State);
        Assert.Equal(WarmupSignal.Allocations, detector.Memory.Blocking);
        Assert.InRange(detector.Memory.AllocatedBytesPerSecond, 64 * 59, 64 * 62);
    }

    [Fact]
    public void Erratic_frame_times_dont_block_the_performance_gate()
    {
        var detector = new WarmupDetector(new WarmupSettings { CapSeconds = 3 });

        Run(detector, 240, i => new FrameSample { FrameMs = i % 4 == 0 ? 10.0 : 1.0 });

        Assert.Equal(WarmupState.Stable, detector.Performance.State);
    }

    [Fact]
    public void Counts_disturbances_after_stable()
    {
        var detector = new WarmupDetector();

        Run(detector, 200, i => new FrameSample { JitMethods = i is 150 or 160 ? 3 : 0 });

        Assert.Equal(WarmupState.Stable, detector.Performance.State);
        Assert.Equal(2, detector.Performance.DisturbancesAfterStable);
        Assert.Equal(150, detector.Performance.FirstDisturbanceFrame);
        Assert.Equal(0, detector.Memory.DisturbancesAfterStable);
    }

    [Fact]
    public void Ends_as_not_reached_when_measurement_is_too_short()
    {
        var detector = new WarmupDetector();

        Run(detector, 30, _ => new FrameSample());

        Assert.Equal(WarmupState.WarmingUp, detector.Memory.State);
        Assert.Equal(WarmupState.WarmingUp, detector.Performance.State);
    }

    [Fact]
    public void Percentile_interpolates()
    {
        double[] sorted = [1, 2, 3, 4, 5];

        Assert.Equal(3, WarmupDetector.Percentile(sorted, 0.5));
        Assert.Equal(4.8, WarmupDetector.Percentile(sorted, 0.95), 6);
        Assert.True(double.IsNaN(WarmupDetector.Percentile([], 0.5)));
    }
}

public class MemoryHistoryTests
{
    [Fact]
    public void Keeps_the_start_and_halves_resolution_when_full()
    {
        var history = new MemoryHistory(capacity: 4);

        for (var second = 0; second < 6; second++)
            history.Record(new MemoryPoint(second, second, second, 0, 0));

        // 0,1,2,3 fill it; 4 compacts to 0,2 then appends 4; 5 is within the new 2 s interval.
        Assert.Equal([0, 2, 4], history.Points.ToArray().Select(p => p.Frame));
    }
}
