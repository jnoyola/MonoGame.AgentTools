using MonoGame.AgentTools.Diagnostics;

namespace MonoGame.AgentTools.Tests;

public class FrameAnalysisTests
{
    [Fact]
    public void Finds_hitches_and_attributes_them()
    {
        var frames = Enumerable.Range(1, 100).Select(i => new FrameSample { Frame = i, FrameMs = 1.0, UpdateMs = 0.5, DrawMs = 0.5 }).ToArray();
        frames[0].FrameMs = 20;
        frames[0].JitMethods = 50;
        frames[0].JitMs = 15;
        frames[40].FrameMs = 8;
        frames[40].Gen0Collections = 1;
        frames[70].FrameMs = 6;
        frames[80].FrameMs = 2.5;   // below max(2 x median, median + 2 ms)

        var analysis = FrameAnalysis.Run(frames, new WarmupDetector());

        Assert.Equal(3.0, analysis.HitchThresholdMs);
        Assert.Equal([1, 41, 71], analysis.Hitches.Select(h => h.Frame));
        Assert.StartsWith("jit", analysis.Hitches[0].LikelyCause, StringComparison.Ordinal);
        Assert.StartsWith("garbage", analysis.Hitches[1].LikelyCause, StringComparison.Ordinal);
        Assert.StartsWith("unattributed", analysis.Hitches[2].LikelyCause, StringComparison.Ordinal);
        Assert.False(analysis.MeasuredAfterWarmup);
    }

    [Fact]
    public void Lists_only_the_largest_hitches_but_counts_them_all()
    {
        var frames = Enumerable.Range(1, 100).Select(i => new FrameSample { Frame = i, FrameMs = 1.0 }).ToArray();
        for (int i = 0; i < 30; ++i)
            frames[i * 3].FrameMs = 10 + i;

        var analysis = FrameAnalysis.Run(frames, new WarmupDetector());

        Assert.Equal(30, analysis.HitchCount);
        Assert.Equal(Enumerable.Range(20, 10).Select(i => i * 3 + 1), analysis.Hitches.Select(h => h.Frame));
    }
}
