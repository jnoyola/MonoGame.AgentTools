namespace MonoGame.AgentTools.Tests;

public class OptionsTests
{
    [Fact]
    public void Defaults_to_live_mode_at_the_standard_size()
    {
        var options = CommandLineOptions.Parse(["--some-game-flag", "value"]);

        Assert.False(options.IsCapture);
        Assert.Equal((1280, 720), options.Size);
        Assert.Equal(60, options.Frames);
    }

    [Fact]
    public void Parses_a_full_capture_command()
    {
        var options = CommandLineOptions.Parse([
            "--agenttools-capture", "out", "--agenttools-duration", "2.5", "--agenttools-fps", "30",
            "--agenttools-size", "640x360", "--agenttools-save", "every:5", "--agenttools-realtime", "--agenttools-jit-names"]);

        Assert.True(options.IsCapture);
        Assert.Equal(Path.GetFullPath("out"), options.CaptureDirectory);
        Assert.Equal(75, options.Frames);
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

        Assert.Equal(7, options.Frames);
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
    public void Rejects_invalid_options(params string[] args) =>
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(args));
}
