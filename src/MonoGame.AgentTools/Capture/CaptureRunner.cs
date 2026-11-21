using System.Diagnostics;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.AgentTools.Diagnostics;
using MonoGame.AgentTools.Imaging;
using MonoGame.AgentTools.Input;
using MonoGame.AgentTools.Runtime;
using MonoGame.AgentTools.Timing;

namespace MonoGame.AgentTools.Capture;

/// <summary>
/// Batch runs (capture and profile): initializes the game without entering MonoGame's run loop (so the
/// window is never shown), then drives the game's real <c>Update</c>/<c>Draw</c> at a fixed step and reads
/// frames back from the backbuffer before they're presented. The same inputs produce the same frames. A
/// profile is the same loop paced to the step in real time, with no frames saved.
/// A profile measures frames as they're played, like Unreal's FPS charts and Unity Performance Testing's
/// <c>Measure.Frames</c>, over a run that repeats frame for frame.
/// Precedent for driving the real loop at a fixed rate: Unity <c>Time.captureFramerate</c>, Godot
/// <c>--fixed-fps</c>/<c>--write-movie</c>, Unreal <c>-deterministic -fps=N</c>.
/// </summary>
internal static class CaptureRunner
{
    private const int SheetTiles = 12;
    private const string FramesFolder = "frames";

    public static void RunAndExit(Game game, CommandLineOptions options, AgentToolsSetup setup)
    {
        CaptureInputs inputs;
        try
        {
            inputs = CaptureInputs.Load(options);
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A bad script or timing file is the caller's to fix: the message, not a stack trace.
            Console.Error.WriteLine($"MonoGame.AgentTools: {e.Message}");
            TryWriteError(options.OutputDirectory!, e.Message);
            Environment.Exit(AgentTools.InvalidArgumentsExitCode);
            return;
        }

        int exitCode = 0;
        try
        {
            Run(game, options, setup, inputs);
        }
        catch (Exception e)
        {
            exitCode = AgentTools.CaptureFailedExitCode;
            Console.Error.WriteLine($"MonoGame.AgentTools capture failed: {e}");
            TryWriteError(options.OutputDirectory!, e.ToString());
        }
        Console.Out.Flush();
        Environment.Exit(exitCode);
    }

    private static void Run(Game game, CommandLineOptions options, AgentToolsSetup setup, CaptureInputs inputs)
    {
        var metrics = setup.CustomMetrics;
        var outDir = options.OutputDirectory!;
        var profile = options.Mode == RunMode.Profile;
        var framesDir = Path.Combine(outDir, FramesFolder);
        Directory.CreateDirectory(framesDir);
        foreach (var stale in Directory.EnumerateFiles(framesDir, "f????.png"))
            File.Delete(stale);
        File.Delete(Path.Combine(outDir, "error.txt"));

        var (script, timingMode, timingPath, recordedTiming) = inputs;

        Precompile.Types(typeof(WarmupDetector), typeof(WarmupGate), typeof(FrameSample), typeof(RuntimeCounters), typeof(ResourceCounters), typeof(ProcessMemory),
            typeof(FrameClock), typeof(InputPlayback), typeof(InputInjector), typeof(InputText));
        ConfigureDevice(game, options);
        var hooks = GameHooks.Bind(game);
        if (!hooks.BeforeRun())
            throw new PlatformNotSupportedException("This MonoGame platform runs its loop asynchronously and can't be captured.");
        using var injector = BindInput(game, script, out var inputNote);

        var wall = Stopwatch.StartNew();
        hooks.Initialize();
        hooks.BeginRun();
        injector?.Isolate(script?.PadMask ?? 0);

        // The game's own step unless fps overrides it, read after Initialize in case the game sets it there.
        var step = options.Fps is int f ? TimeSpan.TicksPerSecond / f : script?.StepTicks ?? game.TargetElapsedTime.Ticks;
        var plannedClock = recordedTiming is not null ? FrameClock.FromFile(recordedTiming, step) : FrameClock.Fixed(step);
        var frameCount = FrameCount(options, plannedClock, script, recordedTiming);
        var clock = timingMode == TimingMode.Real ? FrameClock.Real(step, frameCount) : plannedClock;
        var playback = script is null ? null : InputPlayback.Create(script, clock);

        var notes = new List<string>(setup.Notes);
        if (inputNote is not null)
            notes.Add(inputNote);
        if (playback is not null)
            notes.AddRange(playback.Warnings);
        if (script is { VariableStep: true } && timingMode == TimingMode.Fixed)
            notes.Add("The input was recorded at a variable step and is replayed at a fixed one: inputs keep their times, but the game's state can drift from the session's.");
        if (profile && plannedClock.Total(frameCount) < TimeSpan.TicksPerSecond * MinimumProfileSeconds)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"A profile under {MinimumProfileSeconds} s can end before the performance gate opens, so its numbers may include warm-up. The default is {options.DefaultSeconds} s."));

        var device = game.GraphicsDevice;
        var width = device.PresentationParameters.BackBufferWidth;
        var height = device.PresentationParameters.BackBufferHeight;

        var samples = new FrameSample[frameCount];
        var metricValues = new double[frameCount * metrics.Count];
        var pixels = new Color[width * height];
        var png = new PngEncoder();
        var reader = new BackBufferReader(device, width, height);
        // Built up front so the frame loop formats no strings.
        var framePaths = new string?[frameCount + 1];
        for (int frame = 1; frame <= frameCount; ++frame)
            if (options.Save.Includes(frame))
                framePaths[frame] = NativeFile.Prepare(Path.Combine(framesDir, FrameFileName(frame)));
        WarmSavePath(reader, png, pixels, width, height, framesDir, saving: framePaths.Any(path => path is not null));
        var sheetFrames = ContactSheet.PickFrames(frameCount, SheetTiles);
        var sheet = new ContactSheet(width, height, sheetFrames.Length);
        var warmup = new WarmupDetector();
        using var resources = new ResourceCounters(device);
        using var jit = options.JitNames ? new JitAttribution() : null;
        var savedFrames = 0;

        // XNA and MonoGame run one Update with a zero GameTime before the first frame.
        var time = new GameTime();
        hooks.Update(time);

        var previous = RuntimeCounters.Read();
        var loopStart = wall.Elapsed;
        long previousStart = 0, captureWork = 0;
        for (int frame = 1; frame <= frameCount; ++frame)
        {
            // Real timing: the wall time since the previous frame started, less the capture's own work.
            var frameStart = Stopwatch.GetTimestamp();
            if (clock.Mode == TimingMode.Real)
                clock.SetMeasured(frame, frame == 1 ? step : Math.Max(0, Stopwatch.GetElapsedTime(previousStart, frameStart).Ticks - captureWork));
            previousStart = frameStart;
            captureWork = 0;
            time.ElapsedGameTime = TimeSpan.FromTicks(clock.Elapsed(frame));
            time.TotalGameTime = TimeSpan.FromTicks(clock.Total(frame));
            playback?.Apply(frame, time.TotalGameTime.Ticks, injector!);
            injector?.EndFrame();

            jit?.BeginFrame(frame);
            var before = RuntimeCounters.Read();
            var t0 = Stopwatch.GetTimestamp();
            FrameworkDispatcher.Update();
            hooks.Update(time);
            var t1 = Stopwatch.GetTimestamp();
            if (hooks.BeginDraw())
                hooks.Draw(time);
            var t2 = Stopwatch.GetTimestamp();
            var after = RuntimeCounters.Read();
            jit?.EndFrame();

            var sample = FrameSample.Between(before, after);
            sample.Frame = frame;
            sample.SimSeconds = time.TotalGameTime.TotalSeconds;
            sample.WallSeconds = wall.Elapsed.TotalSeconds;
            sample.UpdateMs = Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
            sample.DrawMs = Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
            sample.FrameMs = sample.UpdateMs + sample.DrawMs;
            sample.JitMethodsAllThreads = (int)(after.JitMethodsAllThreads - previous.JitMethodsAllThreads);
            sample.HeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            sample.Graphics = device.Metrics;
            // Over the whole interval since the previous frame; the game thread's own work between frames
            // (saving PNGs, reading process memory) is subtracted with its exact per-thread count.
            sample.OtherThreadsAllocatedBytes = after.OtherThreadsAllocatedSince(previous);
            sample.GraphicsResources = resources.GraphicsResources();
            resources.SamplePrivateBytes(sample.WallSeconds);
            sample.PrivateBytes = resources.PrivateBytes;
            previous = after;
            for (int m = 0; m < metrics.Count; ++m)
                metricValues[(frame - 1) * metrics.Count + m] = metrics[m].Read();
            samples[frame - 1] = sample;
            warmup.Observe(sample);

            var sheetTile = Array.IndexOf(sheetFrames, frame);
            var save = framePaths[frame] is not null;
            if (save || sheetTile >= 0)
            {
                var workStart = Stopwatch.GetTimestamp();
                var captureStart = GC.GetAllocatedBytesForCurrentThread();
                reader.Read(pixels, width, height);
                if (save)
                {
                    png.Save(framePaths[frame]!, width, height, pixels);
                    ++savedFrames;
                }
                if (sheetTile >= 0)
                    sheet.Add(sheetTile, pixels, width);
                samples[frame - 1].CaptureAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - captureStart;
                captureWork = Stopwatch.GetElapsedTime(workStart).Ticks;
            }
            hooks.EndDraw();
            // A profile paces to the step as vsync would, so frame times include the waits a player's do.
            // Real timing does too, so the times measured are the game's own frame-to-frame variation and
            // the run covers the game time it was asked for.
            if (profile || clock.Mode == TimingMode.Real)
                WaitUntil(wall, loopStart + TimeSpan.FromTicks(clock.IsKnown ? clock.Total(frame) : step * frame));
        }
        warmup.Finish(wall.Elapsed.TotalSeconds);

        sheet.Save(Path.Combine(outDir, "sheet.png"));
        var analysis = FrameAnalysis.Run(samples, warmup);
        if (jit is not null)
        {
            Thread.Sleep(250);   // let the runtime's event dispatcher deliver the last JIT events
            analysis.AttachMethodNames(jit.MethodsFor);
        }

        if (reader.FallbackReason is { } fallback)
            notes.Add($"Frame readback allocates ({fallback}).");
        var sections = new Dictionary<string, object> { ["timing"] = TimingSection(clock, outDir, timingPath) };
        if (playback is not null)
        {
            sections["input"] = InputSection(playback, script!, options.InputPath!, outDir);
            if (playback.Applied < playback.Count)
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{playback.Count - playback.Applied} input events after f{frameCount} not applied."));
        }

        var info = new ReportInfo
        {
            Mode = profile ? "profile" : "capture",
            Game = game.GetType().FullName ?? game.GetType().Name,
            Width = width,
            Height = height,
            Fps = Math.Round(TimeSpan.TicksPerSecond / (double)step, 3),
            WallSeconds = wall.Elapsed.TotalSeconds,
            FrameTimeBasis = profile
                ? "CPU time of Update + Draw on the game thread, paced to the step in real time"
                : "CPU time of Update + Draw on the game thread, run as fast as frames render; saving PNGs adds wall time and background JIT, so take performance numbers from a profile",
            CustomMetricNames = metrics.Select(m => m.Name).ToArray(),
            CustomMetricValues = metricValues,
            Notes = notes,
            Sections = sections,
            Files = new Dictionary<string, object>
            {
                ["frames"] = $"{FramesFolder}/f{{frame:0000}}.png",
                ["savedFrames"] = savedFrames,
                ["sheet"] = "sheet.png",
                ["sheetFrames"] = sheetFrames,
                ["sheetColumns"] = Math.Min(4, sheetFrames.Length),
                ["table"] = "frames.csv",
            },
        };
        Report.WriteCsv(Path.Combine(outDir, "frames.csv"), info, samples);
        Report.WriteSummary(Path.Combine(outDir, "summary.json"), info, analysis);
        Console.WriteLine($"MonoGame.AgentTools: {(profile ? "profiled" : "captured")} {frameCount} frames at {width}x{height} to {outDir}");
    }

    /// <summary>A profile shorter than this is noted: the performance gate typically opens a few seconds in.</summary>
    private const int MinimumProfileSeconds = 5;

    /// <summary>
    /// Frames to run: the duration given, in frames or the frame nearest its seconds; else as many as a
    /// replayed timing file holds (the session it came from); else the mode's default (1 s for a capture, 8 s
    /// for a profile), extended to the last input event plus 1 s, to show the reaction to it.
    /// </summary>
    internal static int FrameCount(CommandLineOptions options, FrameClock clock, InputScript? script, long[]? recordedTiming)
    {
        if (options.Duration is { Frames: int frames })
            return frames;
        if (options.Duration is { Seconds: double seconds })
            return NearestFrame(clock, (long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        if (recordedTiming is { Length: > 0 })
            return recordedTiming.Length;
        var count = NearestFrame(clock, (long)(options.DefaultSeconds * TimeSpan.TicksPerSecond));
        if (script is { Events.Count: > 0 })
        {
            var last = script.Events.Max(e => e.Ticks >= 0 ? e.Ticks : clock.Total(e.Frame));
            count = Math.Max(count, NearestFrame(clock, last + TimeSpan.TicksPerSecond));
        }
        return count;
    }

    private static int NearestFrame(FrameClock clock, long ticks)
    {
        var frame = clock.FirstFrameAtOrAfter(ticks);
        if (frame > 1 && clock.Total(frame) - ticks > ticks - clock.Total(frame - 1))
            --frame;
        return Math.Max(1, frame);
    }

    /// <summary>
    /// Hooks input for scripts and isolation. The hooks reach into MonoGame's SDL platform; where they
    /// don't fit (another platform or MonoGame version), a capture without a script still runs, with a
    /// note that the machine's own input isn't shut out, and a capture with one fails with the reason.
    /// </summary>
    private static InputInjector? BindInput(Game game, InputScript? script, out string? note)
    {
        note = null;
        InputInjector? injector = null;
        try
        {
            injector = InputInjector.Bind(game);
            injector.Install();
            return injector;
        }
        catch (Exception e) when (script is null && e is InvalidOperationException or PlatformNotSupportedException)
        {
            injector?.Dispose();
            note = $"The machine's keyboard, mouse and gamepads aren't shut out of this capture: {e.Message}";
            return null;
        }
    }

    /// <summary>The base name of the input and timing a capture keeps: <c>capture.input</c> and <c>capture.timing</c>.</summary>
    internal const string Recording = "capture";
    private const string InputFile = Recording + InputRecorder.InputExtension;
    private const string TimingFileName = Recording + TimingFile.Extension;

    /// <summary>The timing used, kept in the capture folder (when it isn't an even step) so the capture can be replayed.</summary>
    private static Dictionary<string, object> TimingSection(FrameClock clock, string outDir, string? timingPath)
    {
        var section = new Dictionary<string, object>
        {
            ["mode"] = clock.Mode.ToString().ToLowerInvariant(),
            ["stepMs"] = Math.Round(clock.StepTicks / (double)TimeSpan.TicksPerMillisecond, 4),
        };
        var kept = Path.Combine(outDir, TimingFileName);
        if (clock.Mode != TimingMode.Fixed)
        {
            // From memory: the times this capture ran with, whatever has happened to the source file since.
            TimingFile.Write(kept, clock.ElapsedTimes);
            section["file"] = TimingFileName;
            section["frames"] = clock.Length;
            if (timingPath is not null && !SamePath(timingPath, kept))
                section["source"] = timingPath;
        }
        return section;
    }

    private static Dictionary<string, object> InputSection(InputPlayback playback, InputScript script, string inputPath, string outDir)
    {
        var kept = Path.Combine(outDir, InputFile);
        var copied = !SamePath(inputPath, kept);
        File.WriteAllText(kept, script.Text);
        var section = new Dictionary<string, object>
        {
            ["file"] = InputFile,
            ["events"] = playback.Count,
            ["applied"] = playback.Applied,
            ["byFrame"] = playback.AppliedEvents()
                .GroupBy(a => a.Frame)
                .Select(g => new Dictionary<string, object> { ["frame"] = g.Key, ["events"] = g.Select(a => a.Event.Describe()).ToArray() })
                .ToArray(),
        };
        if (copied)
            section["source"] = inputPath;
        return section;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The input script and timing a capture plays, read before the game starts so a bad one fails fast.</summary>
    internal sealed record CaptureInputs(InputScript? Script, TimingMode TimingMode, string? TimingPath, long[]? RecordedTiming)
    {
        /// <summary>A <c>.timing</c> file beside the <c>.input</c> is replayed unless <c>--agenttools-timing</c> says otherwise.</summary>
        public static CaptureInputs Load(CommandLineOptions options)
        {
            var script = options.InputPath is { } inputPath ? InputScript.Load(inputPath) : null;
            var beside = options.InputPath is { } input ? Path.ChangeExtension(input, TimingFile.Extension) : null;
            var mode = options.Timing ?? (beside is not null && File.Exists(beside) ? TimingMode.File : TimingMode.Fixed);
            var timingPath = mode == TimingMode.File ? options.TimingPath ?? beside! : null;
            var recorded = timingPath is not null ? TimingFile.Read(timingPath) : null;
            if (script?.StepTicks is long step && options.Fps is int fps && Math.Abs(TimeSpan.TicksPerSecond / fps - step) > 1)
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"The input was recorded at a {step / (double)TimeSpan.TicksPerMillisecond:0.####} ms step, but --agenttools-fps {fps} asks for {1000.0 / fps:0.####} ms."));
            return new CaptureInputs(script, mode, timingPath, recorded);
        }
    }

    /// <summary>
    /// Runs the readback and save path once at the real frame size before any frame is measured: it sizes
    /// the encoder's buffers, builds its static tables, compiles everything it calls (including generic
    /// SIMD code <see cref="Precompile"/> can't reach) and binds the native calls, so no frame allocates
    /// or compiles for it. The methods on that path are also marked AggressiveOptimization: tiered
    /// compilation promoting them mid-capture allocated a few dozen bytes on a random frame.
    /// </summary>
    private static void WarmSavePath(BackBufferReader reader, PngEncoder png, Color[] pixels, int width, int height, string framesDir, bool saving)
    {
        reader.Warm(pixels, width, height);
        if (!saving)
            return;
        var path = NativeFile.Prepare(Path.Combine(framesDir, "warmup.png"));
        png.Save(path, width, height, pixels);
        File.Delete(path);
    }

    /// <summary>Sleeps until the frame's wall-clock deadline; a late frame is caught up by the next ones.</summary>
    private static void WaitUntil(Stopwatch wall, TimeSpan deadline)
    {
        TimeSpan remaining;
        while ((remaining = deadline - wall.Elapsed) > TimeSpan.Zero)
        {
            if (remaining > TimeSpan.FromMilliseconds(2))
                Thread.Sleep(1);
            else
                Thread.SpinWait(64);
        }
    }

    /// <summary>Standard capture size (same framing on every machine) and no vsync throttling.</summary>
    private static void ConfigureDevice(Game game, CommandLineOptions options)
    {
        if (game.Services.GetService<IGraphicsDeviceManager>() is not GraphicsDeviceManager manager)
            throw new InvalidOperationException("The game has no GraphicsDeviceManager. Create one in the game's constructor.");
        if (game.GraphicsDevice is not null)
            throw new InvalidOperationException("The graphics device already exists. Call AgentTools.Attach before game.Run().");
        if (options.Size is { } size)
        {
            manager.PreferredBackBufferWidth = size.Width;
            manager.PreferredBackBufferHeight = size.Height;
        }
        manager.IsFullScreen = false;
        manager.SynchronizeWithVerticalRetrace = false;
    }

    private static string FrameFileName(int frame) => $"f{frame.ToString("D4", CultureInfo.InvariantCulture)}.png";

    private static void TryWriteError(string outDir, string error)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "error.txt"), error);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
