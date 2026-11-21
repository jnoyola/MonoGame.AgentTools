using MonoGame.AgentTools.Capture;
using MonoGame.AgentTools.Timing;

namespace MonoGame.AgentTools.Tests;

public class OptionsTests
{
    [Fact]
    public void Defaults_to_live_mode_at_the_standard_size()
    {
        var options = CommandLineOptions.Parse(["--some-game-flag", "value"]);

        Assert.False(options.IsCapture);
        Assert.Equal((1280, 720), options.Size);
        Assert.Null(options.Fps);
        Assert.Equal(60, CaptureRunner.FrameCount(options, FrameClock.Fixed(166_667), null, null));
    }

    [Fact]
    public void Parses_a_full_capture_command()
    {
        var options = CommandLineOptions.Parse([
            "--agenttools-capture", "out", "--agenttools-duration", "2.5", "--agenttools-fps", "30",
            "--agenttools-size", "640x360", "--agenttools-save", "every:5", "--agenttools-realtime", "--agenttools-jit-names"]);

        Assert.True(options.IsCapture);
        Assert.Equal(Path.GetFullPath("out"), options.CaptureDirectory);
        Assert.Equal(30, options.Fps);
        Assert.Equal(75, CaptureRunner.FrameCount(options, FrameClock.Fixed(TimeSpan.TicksPerSecond / 30), null, null));
        Assert.Equal((640, 360), options.Size);
        Assert.True(options.Save.Includes(10));
        Assert.False(options.Save.Includes(11));
        Assert.True(options.Realtime);
        Assert.True(options.JitNames);
    }

    [Fact]
    public void Frame_count_overrides_duration_and_native_keeps_the_game_size()
    {
        var options = CommandLineOptions.Parse(["--agenttools-frames", "7", "--agenttools-duration", "9", "--agenttools-size", "native"]);

        Assert.Equal(7, CaptureRunner.FrameCount(options, FrameClock.Fixed(166_667), null, null));
        Assert.Null(options.Size);
    }

    [Theory]
    [InlineData("all", 3, true)]
    [InlineData("none", 3, false)]
    [InlineData("1,30,60", 30, true)]
    [InlineData("1,30,60", 31, false)]
    public void Frame_selection(string spec, int frame, bool expected) =>
        Assert.Equal(expected, FrameSelection.Parse(spec).Includes(frame));

    [Theory]
    [InlineData("--agenttools-bogus")]
    [InlineData("--agenttools-fps", "0")]
    [InlineData("--agenttools-size", "1280")]
    [InlineData("--agenttools-capture")]
    [InlineData("--agenttools-save", "every:x")]
    [InlineData("--agenttools-input", "a.input")]
    [InlineData("--agenttools-timing", "real")]
    public void Rejects_invalid_options(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(args));

    [Fact]
    public void Parses_input_timing_and_record()
    {
        var options = CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-input", "a.input", "--agenttools-timing", "b.timing", "--agenttools-record", "rec"]);
        var real = CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-timing", "REAL"]);

        Assert.Equal(Path.GetFullPath("a.input"), options.InputPath);
        Assert.Equal(TimingMode.File, options.Timing);
        Assert.Equal(Path.GetFullPath("b.timing"), options.TimingPath);
        Assert.Equal(Path.GetFullPath("rec"), options.RecordPath);
        Assert.Equal(TimingMode.Real, real.Timing);
        Assert.Null(real.TimingPath);
    }
}
