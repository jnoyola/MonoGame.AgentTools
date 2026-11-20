using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.AgentTools.Diagnostics;
using MonoGame.AgentTools.Imaging;
using MonoGame.AgentTools.Runtime;

namespace MonoGame.AgentTools.Capture;

/// <summary>
/// Batch capture: initializes the game without entering MonoGame's run loop (so the window is never
/// shown), then drives the game's real <c>Update</c>/<c>Draw</c> at a fixed step and reads each frame
/// back from the backbuffer before it's presented. The same inputs produce the same frames.
/// Precedent for driving the real loop at a fixed rate: Unity <c>Time.captureFramerate</c>, Godot
/// <c>--fixed-fps</c>/<c>--write-movie</c>, Unreal <c>-deterministic -fps=N</c>.
/// </summary>
internal static class CaptureRunner
{
    private const int SheetTiles = 12;
    private const string FramesFolder = "frames";

    public static void RunAndExit(Game game, CommandLineOptions options, AgentToolsSetup setup)
    {
        int exitCode = 0;
        try
        {
            Run(game, options, setup);
        }
        catch (Exception e)
        {
            exitCode = AgentTools.CaptureFailedExitCode;
            Console.Error.WriteLine($"MonoGame.AgentTools capture failed: {e}");
            TryWriteError(options.CaptureDirectory!, e);
        }
        Console.Out.Flush();
        Environment.Exit(exitCode);
    }

    private static void Run(Game game, CommandLineOptions options, AgentToolsSetup setup)
    {
        var metrics = setup.CustomMetrics;
        var outDir = options.CaptureDirectory!;
        var framesDir = Path.Combine(outDir, FramesFolder);
        Directory.CreateDirectory(framesDir);
        foreach (var stale in Directory.EnumerateFiles(framesDir, "f????.png"))
            File.Delete(stale);
        File.Delete(Path.Combine(outDir, "error.txt"));

        Precompile.Types(typeof(WarmupDetector), typeof(WarmupGate), typeof(FrameSample), typeof(RuntimeCounters), typeof(ResourceCounters), typeof(ProcessMemory));
        ConfigureDevice(game, options);
        var hooks = GameHooks.Bind(game);
        if (!hooks.BeforeRun())
            throw new PlatformNotSupportedException("This MonoGame platform runs its loop asynchronously and can't be captured.");

        var wall = Stopwatch.StartNew();
        hooks.Initialize();
        hooks.BeginRun();

        var device = game.GraphicsDevice;
        var width = device.PresentationParameters.BackBufferWidth;
        var height = device.PresentationParameters.BackBufferHeight;
        var frameCount = options.Frames;
        var step = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / options.Fps);

        var samples = new FrameSample[frameCount];
        var metricValues = new double[frameCount * metrics.Count];
        var pixels = new Color[width * height];
        var png = new PngEncoder();
        var reader = new BackBufferReader(device, width, height);
        // Built up front so the frame loop formats no strings.
        var framePaths = new string?[frameCount + 1];
        for (int f = 1; f <= frameCount; ++f)
            if (options.Save.Includes(f))
                framePaths[f] = NativeFile.Prepare(Path.Combine(framesDir, FrameFileName(f)));
        WarmSavePath(reader, png, pixels, width, height, framesDir, saving: framePaths.Any(p => p is not null));
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
        for (int frame = 1; frame <= frameCount; ++frame)
        {
            time.TotalGameTime = step * frame;
            time.ElapsedGameTime = step;

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
            for (int p = 0; p < metrics.Count; ++p)
                metricValues[(frame - 1) * metrics.Count + p] = metrics[p].Read();
            samples[frame - 1] = sample;
            warmup.Observe(sample);

            var sheetTile = Array.IndexOf(sheetFrames, frame);
            var save = framePaths[frame] is not null;
            if (save || sheetTile >= 0)
            {
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
            }
            hooks.EndDraw();
            if (options.Realtime)
                WaitUntil(wall, loopStart + step * frame);
        }
        warmup.Finish(wall.Elapsed.TotalSeconds);

        sheet.Save(Path.Combine(outDir, "sheet.png"));
        var analysis = FrameAnalysis.Run(samples, warmup);
        if (jit is not null)
        {
            Thread.Sleep(250);   // let the runtime's event dispatcher deliver the last JIT events
            analysis.AttachMethodNames(jit.MethodsFor);
        }

        var info = new ReportInfo
        {
            Mode = "capture",
            Game = game.GetType().FullName ?? game.GetType().Name,
            Width = width,
            Height = height,
            Fps = options.Fps,
            WallSeconds = wall.Elapsed.TotalSeconds,
            FrameTimeBasis = options.Save.Kind == FrameSelectionKind.None && options.Realtime
                ? "CPU time of Update + Draw on the game thread"
                : "CPU time of Update + Draw on the game thread; saving PNGs adds wall time and background JIT, so for performance numbers use --agenttools-save none --agenttools-realtime",
            CustomMetricNames = metrics.Select(p => p.Name).ToArray(),
            CustomMetricValues = metricValues,
            Notes = reader.FallbackReason is { } fallback ? [.. setup.Notes, $"Frame readback allocates ({fallback})."] : setup.Notes,
            Files = new Dictionary<string, object>
            {
                ["frames"] = $"{FramesFolder}/f{{frame:0000}}.png",
                ["savedFrames"] = savedFrames,
                ["sheet"] = "sheet.png",
                ["sheetFrames"] = sheetFrames,
                ["sheetColumns"] = Math.Min(4, sheetFrames.Length),
                ["table"] = "frames.csv",
                ["realtime"] = options.Realtime,
            },
        };
        Report.WriteCsv(Path.Combine(outDir, "frames.csv"), info, samples);
        Report.WriteSummary(Path.Combine(outDir, "summary.json"), info, analysis);
        Console.WriteLine($"MonoGame.AgentTools: captured {frameCount} frames at {width}x{height} to {outDir}");
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

    private static string FrameFileName(int frame) => $"f{frame.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)}.png";

    private static void TryWriteError(string outDir, Exception error)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "error.txt"), error.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
