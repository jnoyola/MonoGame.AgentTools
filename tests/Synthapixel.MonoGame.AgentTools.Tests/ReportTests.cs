using Microsoft.Xna.Framework.Input;
using Synthapixel.MonoGame.AgentTools.Diagnostics;
using Synthapixel.MonoGame.AgentTools.Live;

namespace Synthapixel.MonoGame.AgentTools.Tests;

public class ReportTests
{
    [Fact]
    public void Custom_metric_columns_are_prefixed_and_quoted_when_needed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.csv");
        try
        {
            var info = new ReportInfo
            {
                Mode = "live", Game = "Test", Width = 1, Height = 1, WallSeconds = 1, FrameTimeBasis = "test",
                CustomMetricNames = ["sprites", "a,b"], CustomMetricValues = [1, 2],
            };
            Report.WriteCsv(path, info, [new FrameSample { Frame = 1 }]);

            var header = File.ReadLines(path).First().Split(',');
            Assert.Single(header, "sprites");
            Assert.Equal(["custom.sprites", "\"custom.a", "b\""], header[^3..]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Custom_metric_summary_skips_frames_without_a_value()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.json");
        try
        {
            var frames = Enumerable.Range(1, 4).Select(f => new FrameSample { Frame = f }).ToArray();
            var info = new ReportInfo
            {
                Mode = "capture", Game = "Test", Width = 1, Height = 1, WallSeconds = 1, FrameTimeBasis = "test",
                CustomMetricNames = ["past target"], CustomMetricValues = [double.NaN, -2, 3, double.NaN],
            };
            Report.WriteSummary(path, info, FrameAnalysis.Run(frames, new WarmupDetector()));

            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var metric = json.RootElement.GetProperty("customMetrics").GetProperty("past target");
            Assert.Equal((-2.0, 3.0, -2.0, 3.0), (metric.GetProperty("first").GetDouble(), metric.GetProperty("last").GetDouble(),
                metric.GetProperty("min").GetDouble(), metric.GetProperty("max").GetDouble()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3.0, 0, 0, "too short to judge: 2 s after the memory gate, needs 5 s")]
    [InlineData(8.0, 3, 0, "GPU resources not released: +3")]
    [InlineData(8.0, 0, 20, "steady: no GPU resources leaked (private bytes are judged from 30 s)")]
    [InlineData(40.0, 0, 4, "steady")]
    [InlineData(40.0, 0, 20, "native memory growing: private bytes +20 MB")]
    public void Growth_is_judged_only_over_a_long_enough_window(double seconds, int resourcesCreated, int privateMegabytesAdded, string verdict)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.json");
        try
        {
            var count = (int)(seconds * 60);
            var frames = Enumerable.Range(1, count).Select(i => new FrameSample
            {
                Frame = i, FrameMs = 1, WallSeconds = i / 60.0, SimSeconds = i / 60.0,
                GraphicsResources = 10 + (i == count ? resourcesCreated : 0),
                PrivateBytes = 100_000_000 + (i == count ? privateMegabytesAdded * 1024L * 1024 : 0),
            }).ToArray();
            var warmup = new WarmupDetector();
            foreach (var frame in frames)
                warmup.Observe(frame);
            warmup.Finish(seconds);
            var info = new ReportInfo { Mode = "capture", Game = "Test", Width = 1, Height = 1, WallSeconds = seconds, FrameTimeBasis = "test" };
            Report.WriteSummary(path, info, FrameAnalysis.Run(frames, warmup));

            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(verdict, json.RootElement.GetProperty("growth").GetProperty("verdict").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Custom_metric_names_must_be_unique()
    {
        var e = Assert.Throws<ArgumentException>(() => new AgentToolsSetup(new AgentToolsOptions { CustomMetrics = [("a", () => 1), ("a", () => 2)] }));
        Assert.Contains("'a'", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { Keys.OemTilde }, true)]
    [InlineData(new[] { Keys.LeftShift, Keys.OemTilde }, true)]
    [InlineData(new[] { Keys.Back }, false)]
    public void Hud_toggles_on_backtick(Keys[] keys, bool expected) =>
        Assert.Equal(expected, LiveMonitor.IsToggleKey(new KeyboardState(keys)));
}
