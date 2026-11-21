using MonoGame.AgentTools.Mcp.Tools;

namespace MonoGame.AgentTools.Mcp.Tests;

public sealed class ReplayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agenttools-replay-").FullName;
    private readonly Workspace _workspace;

    public ReplayTests() => _workspace = new Workspace(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("f60 Space down")]
    [InlineData("t1.5 MouseLeft down 640,360")]
    [InlineData("# jump\nf60 Space down\nf75 Space up")]
    public void Script_text_is_written_into_the_run_folder(string text)
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

        var arguments = ReplayInputs.Resolve(_workspace, text, null).Arguments(output).ToList();

        Assert.Equal(["--agenttools-input", Path.Combine(output, ReplayInputs.CaptureInput)], arguments);
        Assert.Equal(text, File.ReadAllText(arguments[1]));
    }

    [Fact]
    public void A_file_is_passed_as_it_is_and_the_game_pairs_its_timing()
    {
        File.WriteAllText(Path.Combine(_root, "f60.input"), "f60 Space down");

        var replay = ReplayInputs.Resolve(_workspace, "f60.input", null);
        var overridden = ReplayInputs.Resolve(_workspace, "f60.input", "FIXED");

        Assert.Equal(["--agenttools-input", Path.Combine(_root, "f60.input")], replay.Arguments(_root));
        Assert.Equal(["--agenttools-input", Path.Combine(_root, "f60.input"), "--agenttools-timing", "fixed"], overridden.Arguments(_root));
    }

    [Fact]
    public void Captures_and_sessions_replay_by_name_and_a_capture_wins_a_shared_name()
    {
        var capture = Directory.CreateDirectory(Path.Combine(_workspace.CapturesDirectory, "jump")).FullName;
        File.WriteAllText(Path.Combine(capture, "capture.input"), "f1 A down");
        var session = Directory.CreateDirectory(Path.Combine(_workspace.SessionsDirectory, "Game-20261121-093000")).FullName;
        File.WriteAllText(Path.Combine(session, "session.input"), "f1 B down");
        File.WriteAllBytes(Path.Combine(session, "session.timing"), [9]);
        Directory.CreateDirectory(Path.Combine(_workspace.SessionsDirectory, "jump"));

        Assert.Equal(Path.Combine(capture, "capture.input"), ReplayInputs.Resolve(_workspace, "jump", null).InputPath);
        var replay = ReplayInputs.Resolve(_workspace, "Game-20261121-093000", "Game-20261121-093000");
        Assert.Equal(Path.Combine(session, "session.input"), replay.InputPath);
        Assert.Equal(Path.Combine(session, "session.timing"), replay.Timing);
    }

    [Fact]
    public void Replaying_into_the_folder_being_replaced_is_refused()
    {
        var capture = Directory.CreateDirectory(Path.Combine(_workspace.CapturesDirectory, "jump")).FullName;
        File.WriteAllText(Path.Combine(capture, "capture.input"), "f1 A down");
        var replay = ReplayInputs.Resolve(_workspace, "jump", null);

        replay.CheckNotIn(Path.Combine(_workspace.CapturesDirectory, "jump-2"), "jump-2");
        var error = Assert.Throws<ToolException>(() => replay.CheckNotIn(capture, "jump"));
        Assert.Contains("give this run another name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replaying_the_oldest_unnamed_capture_keeps_it_through_pruning()
    {
        var start = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < Workspace.KeptAutomaticCaptures; i++)
        {
            var folder = _workspace.PrepareCapture($"game-{i:00}", "capture");
            File.WriteAllText(Path.Combine(folder, "capture.input"), "f1 A down");
            Directory.SetCreationTimeUtc(folder, start.AddMinutes(i));
        }
        var replay = ReplayInputs.Resolve(_workspace, "game-00", null);

        _workspace.PrepareCapture("game-new", "capture", replay.Sources);
        _workspace.PrepareCapture("game-newer", "capture", replay.Sources);

        Assert.True(File.Exists(replay.InputPath));
        Assert.False(Directory.Exists(Path.Combine(_workspace.CapturesDirectory, "game-01")));
    }

    [Theory]
    [InlineData("nonexistent", null, "isn't a script")]
    [InlineData(null, "nonexistent", "timing must be 'fixed', 'real'")]
    [InlineData(null, "slow", "timing must be 'fixed', 'real'")]
    public void Unresolvable_input_or_timing_says_what_it_expects(string? input, string? timing, string message)
    {
        var error = Assert.Throws<ToolException>(() => ReplayInputs.Resolve(_workspace, input, timing));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sheet_times_come_from_frames_csv()
    {
        var frames = Path.Combine(_root, "c", "frames");
        Directory.CreateDirectory(frames);
        File.WriteAllText(Path.Combine(_root, "c", "frames.csv"), "frame,sim_s,wall_s\n1,0.0164,0.1\n2,0.0503,0.2\n");
        File.WriteAllBytes(Path.Combine(frames, "f0002.png"), []);

        var tiles = SheetTools.SavedFrames(frames, fps: 60);

        Assert.Equal(0.0503, Assert.Single(tiles).Seconds);
    }

    [Fact]
    public void Brief_names_the_timing_and_input()
    {
        var brief = CaptureBrief.Format("""
            { "frames": 103, "fps": 60, "simSeconds": 1.72, "wallSeconds": 2,
              "timing": { "mode": "real", "stepMs": 16.6667, "file": "capture.timing", "frames": 103 },
              "input": { "file": "capture.input", "events": 21, "applied": 19 } }
            """);

        Assert.Contains("103 frames, real timing (measured; nominal 16.6667 ms) kept in capture.timing", brief, StringComparison.Ordinal);
        Assert.Contains("Input: 19 of 21 events applied from capture.input", brief, StringComparison.Ordinal);
    }
}
