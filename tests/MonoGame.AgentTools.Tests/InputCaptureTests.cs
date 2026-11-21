using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MonoGame.AgentTools.Tests;

/// <summary>
/// End to end: captures of the InputProbe game, which reports what it reads from Keyboard, Mouse and
/// GamePad as custom metrics. Needs a GPU and a desktop session, so it's skipped in CI.
/// </summary>
public sealed class InputCaptureTests : IDisposable
{
    private const string Script = """
        f5  Space down
        f8  Space up
        f10 F1 down
        f12 F1 up
        f15 CapsLock down
        f16 CapsLock up
        f20 Mouse 40,30
        f22 MouseLeft down 60,70
        f25 MouseLeft up
        f27 MouseWheel +2
        f30 PadA down
        f30 PadLeftStick 0.5,-1
        f31 PadRightTrigger 0.75
        f35 PadA up
        f35 PadLeftStick 0,0
        f36 Pad2B down
        f40 M down
        f41 M up
        f42 Enter down
        f43 Enter up
        f45 Text "hé \"x\""
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"agenttools-input-{Guid.NewGuid():N}");

    public InputCaptureTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_game_reads_scripted_input_and_a_recording_of_it_replays_identically()
    {
        SkipWithoutDesktop();
        var script = Path.Combine(_folder, "script.input");
        File.WriteAllText(script, Script);

        Capture("a", "--agenttools-input", script, "--agenttools-record", Path.Combine(_folder, "rec"));
        var a = Metrics("a");

        Assert.Equal(105, a.Count - 1);   // the last event (f45) plus 1 s
        Assert.Equal(1, a[5]["key.space"]);
        Assert.Equal(0, a[8]["key.space"]);
        Assert.Equal(1, a[10]["key.f1"]);
        Assert.Equal(0, a[15]["key.capslock"]);   // the real lock state never reaches a capture
        Assert.Equal(1, a[43]["events.text"]);   // Enter raises TextInput, as MonoGame does for control characters
        Assert.Equal(7, a[45]["events.text"]);   // one per typed character: h, é, space, quote, x, quote
        Assert.Equal((40.0, 30.0), (a[20]["mouse.x"], a[20]["mouse.y"]));
        Assert.Equal((60.0, 70.0, 1.0), (a[22]["mouse.x"], a[22]["mouse.y"], a[22]["mouse.left"]));
        Assert.Equal(240, a[27]["mouse.wheel"]);
        Assert.Equal((100.0, 50.0), (a[40]["mouse.x"], a[40]["mouse.y"]));   // Mouse.SetPosition moved the scripted cursor
        Assert.Equal((1.0, 0.0), (a[1]["pad.connected"], a[1]["pad.a"]));
        Assert.Equal(1, a[30]["pad.a"]);
        Assert.Equal(0.5, a[30]["pad.lx"], Tolerance);
        Assert.Equal(-1, a[30]["pad.ly"], Tolerance);
        Assert.Equal(0.75, a[31]["pad.rt"], 1e-4);
        Assert.Equal((1.0, 1.0), (a[36]["pad2.connected"], a[36]["pad2.b"]));
        Assert.Equal(16.6667, a[1]["dt.ms"], 1e-4);   // the game's own TargetElapsedTime
        Assert.Equal(0, Summary("a").GetProperty("allocations").GetProperty("gameThreadBytes").GetInt64());

        Capture("b", "--agenttools-input", Path.Combine(_folder, "rec.input"));
        Assert.Equal(a, Metrics("b"), new FrameComparer());
    }

    [Fact]
    public void Real_timing_is_kept_and_replays_exactly_for_a_variable_step_game()
    {
        SkipWithoutDesktop();
        var script = Path.Combine(_folder, "script.input");
        File.WriteAllText(script, Script);

        Capture("real", "--variable", "--agenttools-input", script, "--agenttools-timing", "real", "--agenttools-realtime",
            "--agenttools-record", Path.Combine(_folder, "vrec"));
        Assert.StartsWith("# Input recorded", File.ReadAllText(Path.Combine(_folder, "vrec.input")), StringComparison.Ordinal);
        Assert.Contains("\nstep variable\n", File.ReadAllText(Path.Combine(_folder, "vrec.input")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_folder, "vrec.timing")));
        Assert.True(File.Exists(Path.Combine(_folder, "real", "capture.timing")));

        // The recording's .timing beside its .input is picked up without asking.
        Capture("replay", "--variable", "--agenttools-input", Path.Combine(_folder, "vrec.input"));
        var real = Metrics("real");
        var replay = Metrics("replay");

        Assert.Equal("file", Summary("replay").GetProperty("timing").GetProperty("mode").GetString());
        Assert.Equal(real.Count, replay.Count);
        Assert.Equal(real.Skip(1).Select(f => f["dt.ms"]), replay.Skip(1).Select(f => f["dt.ms"]));
        Assert.Equal(real, replay, new FrameComparer());
    }

    private static readonly double Tolerance = 1e-4;

    /// <summary>
    /// Equal except KeyDown/KeyUp counts: a press and release within one frame (CapsLock in the script) fires
    /// the events, but sampling once per frame never sees it, so a recording can't contain it.
    /// </summary>
    private sealed class FrameComparer : IEqualityComparer<Dictionary<string, double>>
    {
        public bool Equals(Dictionary<string, double>? x, Dictionary<string, double>? y) =>
            x!.Where(p => !p.Key.StartsWith("events.key", StringComparison.Ordinal)).All(p => Math.Abs(p.Value - y![p.Key]) < Tolerance);

        public int GetHashCode(Dictionary<string, double> obj) => 0;
    }

    private static void SkipWithoutDesktop() =>
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") == "true" || !OperatingSystem.IsWindows(),
            "Runs a game: needs a GPU and a desktop session (checked on Windows).");

    private void Capture(string name, params string[] args)
    {
        var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "InputProbe",
            new DirectoryInfo(AppContext.BaseDirectory).Name, OperatingSystem.IsWindows() ? "InputProbe.exe" : "InputProbe"));
        var start = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--agenttools-capture", Path.Combine(_folder, name), "--agenttools-save", "none" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "The probe didn't finish.");
        Assert.True(process.ExitCode == 0, $"The probe failed: {error.Result}");
    }

    private JsonElement Summary(string name) =>
        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(_folder, name, "summary.json")));

    /// <summary>Custom metrics by frame number (index 0 unused).</summary>
    private List<Dictionary<string, double>> Metrics(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(_folder, name, "frames.csv"));
        var header = lines[0].Split(',');
        var frames = new List<Dictionary<string, double>> { new() };
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            frames.Add(header.Select((column, i) => (column, cell: cells[i]))
                .Where(c => c.column.StartsWith("custom.", StringComparison.Ordinal))
                .ToDictionary(c => c.column["custom.".Length..], c => double.Parse(c.cell, CultureInfo.InvariantCulture)));
        }
        return frames;
    }
}
