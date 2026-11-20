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
    public void Script_text_is_taken_as_is(string text)
    {
        var replay = ReplayInputs.Resolve(_workspace, text, null);

        Assert.Equal(text, replay.Script);
        Assert.Null(replay.Timing);
    }

    [Fact]
    public void A_file_brings_the_timing_beside_it()
    {
        File.WriteAllText(Path.Combine(_root, "f60.input"), "f60 Space down");
        File.WriteAllBytes(Path.Combine(_root, "f60.timing"), [1, 2, 3]);

        var replay = ReplayInputs.Resolve(_workspace, "f60.input", null);
        var overridden = ReplayInputs.Resolve(_workspace, "f60.input", "fixed");

        Assert.Equal("f60 Space down", replay.Script);
        Assert.Equal([1, 2, 3], replay.Timing);
        Assert.Null(overridden.Timing);
        Assert.Equal("fixed", overridden.TimingMode);
    }

    [Fact]
    public void A_capture_name_replays_its_input_and_timing_and_writes_them_into_the_new_capture()
    {
        var capture = Path.Combine(_workspace.EnsureCapturesDirectory(), "session");
        Directory.CreateDirectory(capture);
        File.WriteAllText(Path.Combine(capture, ReplayInputs.InputFile), "f1 A down");
        File.WriteAllBytes(Path.Combine(capture, ReplayInputs.TimingFile), [9]);
        var replay = ReplayInputs.Resolve(_workspace, "session", null);

        // The capture may replace the folder it replays, so the files are read before it's cleared.
        Directory.Delete(capture, recursive: true);
        Directory.CreateDirectory(capture);
        var arguments = replay.Write(capture).ToList();

        Assert.Equal(["--agenttools-input", Path.Combine(capture, ReplayInputs.InputFile), "--agenttools-timing", Path.Combine(capture, ReplayInputs.TimingFile)], arguments);
        Assert.Equal("f1 A down", File.ReadAllText(Path.Combine(capture, ReplayInputs.InputFile)));
    }

    [Theory]
    [InlineData("nonexistent", null, "isn't a script")]
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
