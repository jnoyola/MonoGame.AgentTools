using MonoGame.AgentTools.Capture;
using MonoGame.AgentTools.Live;
using MonoGame.AgentTools.Timing;

namespace MonoGame.AgentTools.Tests;

public class OptionsTests
{
    private static readonly FrameClock Clock60 = FrameClock.Fixed(166_667);

    [Fact]
    public void Defaults_to_live_mode_at_the_standard_size()
    {
        var options = CommandLineOptions.Parse(["--some-game-flag", "value"]);

        Assert.Equal(RunMode.Live, options.Mode);
        Assert.False(options.IsBatch);
        Assert.Equal((1280, 720), options.Size);
        Assert.Null(options.Fps);
    }

    [Fact]
    public void Parses_a_full_capture_command()
    {
        var options = CommandLineOptions.Parse([
            "--agenttools-capture", "out", "--agenttools-duration", "2.5s", "--agenttools-fps", "30",
            "--agenttools-size", "640x360", "--agenttools-save", "every:5", "--agenttools-jit-names", "--agenttools-hud"]);

        Assert.Equal(RunMode.Capture, options.Mode);
        Assert.Equal(Path.GetFullPath("out"), options.OutputDirectory);
        Assert.Equal(30, options.Fps);
        Assert.Equal(75, CaptureRunner.FrameCount(options, FrameClock.Fixed(TimeSpan.TicksPerSecond / 30), null, null));
        Assert.Equal((640, 360), options.Size);
        Assert.True(options.Save.Includes(10));
        Assert.False(options.Save.Includes(11));
        Assert.True(options.JitNames);
        Assert.True(options.Hud);
        Assert.False(CommandLineOptions.Parse(["--agenttools-capture", "out"]).Hud);
    }

    [Fact]
    public void A_profile_saves_no_frames_and_runs_8_s_by_default()
    {
        var options = CommandLineOptions.Parse(["--agenttools-profile", "out"]);

        Assert.Equal(RunMode.Profile, options.Mode);
        Assert.Equal(FrameSelection.None, options.Save);
        Assert.Equal(480, CaptureRunner.FrameCount(options, Clock60, null, null));
        Assert.Equal(60, CaptureRunner.FrameCount(CommandLineOptions.Parse(["--agenttools-capture", "out"]), Clock60, null, null));
    }

    [Theory]
    [InlineData("150f", 150)]
    [InlineData("2s", 120)]
    [InlineData("0.5S", 30)]
    [InlineData("1F", 1)]
    public void Duration_takes_seconds_or_frames(string duration, int frames) =>
        Assert.Equal(frames, CaptureRunner.FrameCount(CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-duration", duration]), Clock60, null, null));

    [Theory]
    [InlineData("3")]
    [InlineData("0s")]
    [InlineData("1.5f")]
    [InlineData("-2s")]
    [InlineData("s")]
    [InlineData("3ms")]
    public void Duration_needs_a_positive_length_and_its_unit(string duration)
    {
        var e = Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-duration", duration]));
        Assert.Contains("such as 3s or 150f", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1000001f", null)]
    [InlineData("2147483647f", null)]
    [InlineData("99999999999999s", null)]
    [InlineData("3601s", null)]
    [InlineData("3600s", "300")]
    public void Duration_has_an_upper_limit(string duration, string? fps)
    {
        string[] args = ["--agenttools-capture", "out", "--agenttools-duration", duration, .. fps is null ? Array.Empty<string>() : ["--agenttools-fps", fps]];
        var e = Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(args));
        Assert.Contains("1000000", e.Message, StringComparison.Ordinal);
        Assert.Equal(RunLength.MaxFrames, CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-duration", "1000000f"]).Duration!.Value.Frames);
    }

    [Fact]
    public void Native_keeps_the_game_size() =>
        Assert.Null(CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-size", "native"]).Size);

    [Theory]
    [InlineData("all", 3, true)]
    [InlineData("none", 3, false)]
    [InlineData("1,30,60", 30, true)]
    [InlineData("1,30,60", 31, false)]
    public void Frame_selection(string spec, int frame, bool expected) =>
        Assert.Equal(expected, FrameSelection.Parse(spec).Includes(frame));

    [Theory]
    [InlineData("Unknown option", "--agenttools-bogus")]
    [InlineData("Unknown option", "--agenttools-capture", "out", "--agenttools-realtime")]
    [InlineData("positive integer", "--agenttools-capture", "out", "--agenttools-fps", "0")]
    [InlineData("WxH", "--agenttools-capture", "out", "--agenttools-size", "1280")]
    [InlineData("expects a value", "--agenttools-capture")]
    [InlineData("every:N", "--agenttools-capture", "out", "--agenttools-save", "every:x")]
    [InlineData("needs an entry flag", "--agenttools-input", "a.input")]
    [InlineData("needs an entry flag", "--agenttools-duration", "3s")]
    [InlineData("not both", "--agenttools-capture", "a", "--agenttools-profile", "b")]
    [InlineData("capture-only", "--agenttools-profile", "out", "--agenttools-save", "all")]
    public void Rejects_invalid_options(string message, params string[] args)
    {
        var e = Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(args));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_input_and_timing()
    {
        var options = CommandLineOptions.Parse(["--agenttools-profile", "out", "--agenttools-input", "a.input", "--agenttools-timing", "b.timing"]);
        var real = CommandLineOptions.Parse(["--agenttools-capture", "out", "--agenttools-timing", "REAL"]);

        Assert.Equal(Path.GetFullPath("a.input"), options.InputPath);
        Assert.Equal(TimingMode.File, options.Timing);
        Assert.Equal(Path.GetFullPath("b.timing"), options.TimingPath);
        Assert.Equal(TimingMode.Real, real.Timing);
        Assert.Null(real.TimingPath);
    }

    [Theory]
    [InlineData("MyGame-20261121-093000", "MyGame", true)]
    [InlineData("My-Game-20261121-093000-2", "My-Game", true)]
    [InlineData("My-Game-20261121-093000", "My", false)]
    [InlineData("OtherGame-20261121-093000", "MyGame", false)]
    [InlineData("boss-fight", "MyGame", false)]
    [InlineData("MyGame-20261121", "MyGame", false)]
    [InlineData("MyGame-", "MyGame", false)]
    public void Only_this_games_sessions_named_automatically_are_pruned(string name, string game, bool automatic) =>
        Assert.Equal(automatic, Sessions.IsAutomaticName(name, game));

    [Fact]
    public void Each_game_keeps_its_own_sessions()
    {
        var repository = Directory.CreateTempSubdirectory("agenttools-sessions-").FullName;
        try
        {
            var start = new DateTime(2026, 11, 21, 9, 0, 0);
            var mine = Sessions.Create(repository, "GameA", start)!;
            for (var i = 1; i <= Sessions.Kept + 2; i++)
                Assert.NotNull(Sessions.Create(repository, "GameB", start.AddMinutes(i)));

            Assert.True(Directory.Exists(mine));
            var names = Directory.GetDirectories(Path.GetDirectoryName(mine)!).Select(Path.GetFileName).ToList();
            Assert.Equal(Sessions.Kept, names.Count(n => n!.StartsWith("GameB-", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public void Sessions_started_in_the_same_second_get_a_folder_each()
    {
        var repository = Directory.CreateTempSubdirectory("agenttools-sessions-").FullName;
        try
        {
            var now = new DateTime(2026, 11, 21, 9, 30, 0);
            var folders = Enumerable.Range(0, 8).AsParallel().Select(_ => Sessions.Create(repository, "MyGame", now)).ToArray();

            Assert.All(folders, Assert.NotNull);
            Assert.Equal(folders.Length, folders.Distinct().Count());
            Assert.Contains(folders, f => Path.GetFileName(f) == "MyGame-20261121-093000");
            Assert.Contains(folders, f => Path.GetFileName(f) == "MyGame-20261121-093000-8");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }
}
