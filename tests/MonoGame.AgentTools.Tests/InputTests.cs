using Microsoft.Xna.Framework.Input;
using MonoGame.AgentTools.Capture;
using MonoGame.AgentTools.Input;
using MonoGame.AgentTools.Timing;

namespace MonoGame.AgentTools.Tests;

public class InputTests
{
    private const long Step = 166_667;   // MonoGame's default TargetElapsedTime

    private sealed class Sink : IInputSink
    {
        public List<(int Frame, string Event)> Applied { get; } = [];
        public int Frame { get; set; }
        public void Apply(in ScriptEvent e) => Applied.Add((Frame, e.Describe()));
    }

    [Theory]
    [InlineData("Space", (int)ControlKind.Key, (int)Keys.Space, 0)]
    [InlineData("space", (int)ControlKind.Key, (int)Keys.Space, 0)]
    [InlineData("F1", (int)ControlKind.Key, (int)Keys.F1, 0)]
    [InlineData("MouseLeft", (int)ControlKind.MouseButton, (int)MouseButton.Left, 0)]
    [InlineData("Mouse", (int)ControlKind.MousePosition, 0, 0)]
    [InlineData("MouseWheelH", (int)ControlKind.MouseWheelHorizontal, 0, 0)]
    [InlineData("PadA", (int)ControlKind.PadButton, (int)Buttons.A, 0)]
    [InlineData("Pad1A", (int)ControlKind.PadButton, (int)Buttons.A, 0)]
    [InlineData("Pad3DPadUp", (int)ControlKind.PadButton, (int)Buttons.DPadUp, 2)]
    [InlineData("PadLeftStickButton", (int)ControlKind.PadButton, (int)Buttons.LeftStick, 0)]
    [InlineData("Pad2LeftStick", (int)ControlKind.PadStick, (int)PadSide.Left, 1)]
    [InlineData("PadRightTrigger", (int)ControlKind.PadTrigger, (int)PadSide.Right, 0)]
    public void Controls_use_MonoGame_names_with_the_device_built_in(string name, int kind, int code, int pad)
    {
        Assert.True(InputControl.TryParse(name, out var control));
        Assert.Equal(new InputControl((ControlKind)kind, code, pad), control);
        Assert.True(InputControl.TryParse(control.ToString(), out var again));
        Assert.Equal(control, again);
    }

    [Fact]
    public void Every_control_name_round_trips()
    {
        foreach (var key in InputControl.AllKeys)
        {
            var control = new InputControl(ControlKind.Key, (int)key);
            Assert.True(InputControl.TryParse(control.ToString(), out var parsed), control.ToString());
            Assert.Equal(control, parsed);
        }
    }

    [Fact]
    public void Parses_every_kind_of_line()
    {
        var script = InputScript.Parse("""
            # a comment
            step 16.6667
            f60 Space down
            f75 Space up          # trailing comment
            f10 F1 down
            t1.5 Mouse 640,360
            f90 t1.5000000 MouseLeft down 10,20
            f96 MouseLeft up
            f120 MouseWheel -2
            f140 PadLeftStick 0.5,-1
            f141 Pad2RightTrigger 0.25
            """, "test.input");

        Assert.Equal(166_667, script.StepTicks);
        Assert.False(script.VariableStep);
        Assert.Equal(
            ["Space down", "Space up", "F1 down", "Mouse 640,360", "MouseLeft down 10,20", "MouseLeft up", "MouseWheel -2", "PadLeftStick 0.5,-1", "Pad2RightTrigger 0.25"],
            script.Events.Select(e => e.Describe()));
        Assert.Equal(10, script.Events[2].Frame);
        Assert.Equal(15_000_000, script.Events[3].Ticks);
        Assert.Equal((90, 15_000_000L), (script.Events[4].Frame, script.Events[4].Ticks));
        Assert.Equal(0b11, script.PadMask);
    }

    [Theory]
    [InlineData("f60 Spcae down", "line 1: unknown control 'Spcae'; did you mean 'Space'?")]
    [InlineData("f60 Pad1Strat down", "did you mean 'PadStart'?")]
    [InlineData("Space down", "each line starts with a frame (f60) or a time (t1.5)")]
    [InlineData("f0 Space down", "frames are numbered from f1")]
    [InlineData("f60 Space", "Space takes 'down' or 'up'")]
    [InlineData("f60 Mouse 10", "expected Mouse x,y")]
    [InlineData("f60 PadLeftStick 2,0", "values are from -1 to 1")]
    [InlineData("f60 Pad2LeftStick down", "Pad2LeftStick takes x,y; the click is Pad2LeftStickButton.")]
    [InlineData("f60 PadLeftThumbstick 0,0", "did you mean 'PadLeftStick'?")]
    [InlineData("f60 PadLeftTrigger 1.5", "values are from 0 to 1")]
    [InlineData("f60 Space down\nstep 16", "line 2: the step header goes before the events")]
    [InlineData("step 0", "isn't a step in milliseconds")]
    [InlineData("f5 down", "'f5' is read as frame 5; the key is F5.")]
    public void Errors_name_the_line_and_suggest_a_fix(string text, string message)
    {
        var error = Assert.Throws<FormatException>(() => InputScript.Parse(text, "s.input"));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("s.input line ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lowercase_function_key_after_a_time_is_the_key()
    {
        var e = Assert.Single(InputScript.Parse("t1.5 f5 down", "s.input").Events);

        Assert.Equal(new InputControl(ControlKind.Key, (int)Keys.F5), e.Control);
        Assert.Equal(0, e.Frame);
    }

    [Fact]
    public void Recording_over_the_input_being_replayed_is_refused()
    {
        var folder = Directory.CreateTempSubdirectory("agenttools-collision").FullName;
        try
        {
            var input = Path.Combine(folder, "boss.input");
            File.WriteAllText(input, "f1 Space down\n");
            var options = CommandLineOptions.Parse(["--agenttools-capture", folder, "--agenttools-input", input, "--agenttools-record", Path.Combine(folder, "boss")]);

            var error = Assert.Throws<ArgumentException>(() => CaptureRunner.CaptureInputs.Load(options));

            Assert.Contains("would overwrite", error.Message, StringComparison.Ordinal);
            Assert.Equal("f1 Space down\n", File.ReadAllText(input));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Fixed_clock_counts_steps_and_maps_times_to_the_first_frame_at_or_after()
    {
        var clock = FrameClock.Fixed(Step);

        Assert.Equal(90 * Step, clock.Total(90));
        Assert.Equal(1, clock.FirstFrameAtOrAfter(0));
        Assert.Equal(1, clock.FirstFrameAtOrAfter(Step));
        Assert.Equal(2, clock.FirstFrameAtOrAfter(Step + 1));
        Assert.Equal(90, clock.FirstFrameAtOrAfter(15_000_000));   // 1.5 s
    }

    [Fact]
    public void File_clock_replays_recorded_times_then_continues_at_the_step()
    {
        var clock = FrameClock.FromFile([160_000, 180_000, 160_000], Step);

        Assert.Equal((160_000L, 160_000L), (clock.Elapsed(1), clock.Total(1)));
        Assert.Equal(500_000, clock.Total(3));
        Assert.Equal(500_000 + Step, clock.Total(4));
        Assert.Equal(Step, clock.Elapsed(4));
        Assert.Equal(3, clock.FirstFrameAtOrAfter(400_000));   // the example in the design: t0.04 lands on f3
        Assert.Equal(2, clock.FirstFrameAtOrAfter(340_000));
        Assert.Equal(4, clock.FirstFrameAtOrAfter(500_001));
    }

    [Fact]
    public void Real_clock_takes_measured_times_in_order()
    {
        var clock = FrameClock.Real(Step, 3);
        clock.SetMeasured(1, 100);
        clock.SetMeasured(2, 250);

        Assert.False(clock.IsKnown);
        Assert.Equal(350, clock.Total(2));
        Assert.Equal([100L, 250L], clock.ElapsedTimes.ToArray());
        Assert.Throws<InvalidOperationException>(() => clock.SetMeasured(4, 1));
        Assert.Throws<InvalidOperationException>(() => clock.FirstFrameAtOrAfter(10));
    }

    [Fact]
    public void Known_times_resolve_t_up_front_and_keep_file_order_within_a_frame()
    {
        var script = InputScript.Parse("f3 Space down\nt0.04 Space up\nf2 A down\nf2 B down", "s");
        var playback = InputPlayback.Create(script, FrameClock.FromFile([160_000, 180_000, 160_000], Step));
        var sink = new Sink();

        for (int frame = 1; frame <= 3; ++frame)
        {
            sink.Frame = frame;
            playback.Apply(frame, 0, sink);
        }

        Assert.Equal([(2, "A down"), (2, "B down"), (3, "Space down"), (3, "Space up")], sink.Applied);
        Assert.Equal(4, playback.Applied);
        Assert.Contains("line 1: Space goes down and up on f3", Assert.Single(playback.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Real_timing_applies_times_when_a_frame_reaches_them()
    {
        var script = InputScript.Parse("t0.02 Space down\nf2 A down", "s");
        var playback = InputPlayback.Create(script, FrameClock.Real(Step, 10));
        var sink = new Sink();

        sink.Frame = 1;
        playback.Apply(1, 150_000, sink);
        sink.Frame = 2;
        playback.Apply(2, 210_000, sink);

        Assert.Equal([(2, "A down"), (2, "Space down")], sink.Applied);
    }

    [Fact]
    public void Default_length_follows_the_input_and_a_replayed_timing_file()
    {
        var options = CommandLineOptions.Parse(["--agenttools-capture", "out"]);
        var clock = FrameClock.Fixed(Step);
        var script = InputScript.Parse("f150 Space down", "s");

        Assert.Equal(60, CaptureRunner.FrameCount(options, clock, null, null));
        Assert.Equal(210, CaptureRunner.FrameCount(options, clock, script, null));   // the last event plus 1 s
        Assert.Equal(103, CaptureRunner.FrameCount(options, FrameClock.FromFile(new long[103], Step), script, new long[103]));
        Assert.Equal(30, CaptureRunner.FrameCount(options with { DurationSeconds = 0.5 }, clock, script, null));
    }

    [Fact]
    public void Timing_files_round_trip_exactly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.timing");
        try
        {
            long[] elapsed = [166_667, 339_012, 0, 160_388];
            TimingFile.Write(path, elapsed);

            Assert.Equal(TimingFile.HeaderSize + elapsed.Length * 4, new FileInfo(path).Length);
            Assert.Equal(elapsed, TimingFile.Read(path));
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
            Assert.Throws<InvalidDataException>(() => TimingFile.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0L, "0.0000000", "0.0000")]
    [InlineData(166_667L, "0.0166667", "16.6667")]
    [InlineData(15_000_000L, "1.5000000", "1500.0000")]
    public void Times_are_written_to_the_tick(long ticks, string seconds, string milliseconds)
    {
        Span<byte> buffer = stackalloc byte[32];
        Assert.Equal(seconds, System.Text.Encoding.ASCII.GetString(buffer[..InputRecorder.Seconds(buffer, ticks)]));
        Assert.Equal(milliseconds, System.Text.Encoding.ASCII.GetString(buffer[..InputRecorder.Milliseconds(buffer, ticks)]));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(0.5f)]
    [InlineData(-0.25f)]
    [InlineData(0f)]
    public void Stick_values_invert_MonoGame_axis_conversion(float value)
    {
        int raw = InputInjector.Stick(value);
        var read = raw < 0 ? raw / 32768f : raw / 32767f;   // MonoGame's GetFromSdlAxis
        Assert.Equal(value, read, 1e-4f);
    }

    [Fact]
    public void Triggers_span_the_virtual_axis_range()
    {
        Assert.Equal(short.MinValue, InputInjector.Trigger(0));
        Assert.Equal(short.MaxValue, InputInjector.Trigger(1));
    }

    [Fact]
    public void Streamed_files_keep_order_across_buffer_swaps_and_never_allocate_to_append()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agenttools-{Guid.NewGuid():N}.bin");
        try
        {
            var file = new StreamedFile(path, bufferSize: 4096);
            var line = new byte[10];
            for (int i = 0; i < 1000; ++i)
            {
                line.AsSpan().Fill((byte)('a' + i % 26));
                file.Write(line);
            }
            file.Write(line);
            file.Flush();   // swaps and waits for the worker, so the appends below fit the free buffer
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; ++i)
            {
                line.CopyTo(file.GetSpan(line.Length));
                file.Advance(line.Length);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            file.Dispose();

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(1101 * 10, bytes.Length);
            Assert.Equal((byte)'a', bytes[0]);
            Assert.Equal((byte)('a' + 999 % 26), bytes[9990]);
            Assert.True(file.Swaps >= 3);
            Assert.Equal(0, allocated);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
