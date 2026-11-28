using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Synthapixel.MonoGame.AgentTools.Diagnostics;

/// <summary>Context for a report that isn't part of the frame data.</summary>
internal sealed record ReportInfo
{
    public required string Mode { get; init; }
    public required string Game { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double WallSeconds { get; init; }
    /// <summary>What <see cref="FrameSample.FrameMs"/> measures in this mode.</summary>
    public required string FrameTimeBasis { get; init; }
    /// <summary>Capture: frames per second of the step, rounded to 3 decimals.</summary>
    public double? Fps { get; init; }
    public IReadOnlyList<string> CustomMetricNames { get; init; } = [];
    /// <summary>Custom metric values, frame-major: frame i, metric j at [i * metrics + j].</summary>
    public double[] CustomMetricValues { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Live mode: memory over the whole session (frames only cover the last few seconds).</summary>
    public IReadOnlyList<MemoryPoint> MemoryHistory { get; init; } = [];
    /// <summary>Further top-level sections, such as the capture's timing and input.</summary>
    public IReadOnlyDictionary<string, object> Sections { get; init; } = new Dictionary<string, object>();
    public IReadOnlyDictionary<string, object> Files { get; init; } = new Dictionary<string, object>();
}

/// <summary>Writes the agent-facing outputs: a JSON summary and a per-frame CSV.</summary>
internal static class Report
{
    private const int MaxMethodsPerHitch = 40;

    /// <summary>
    /// Growth is judged over at least this long after the memory gate: shorter windows can't tell a steady
    /// rise from settling, and it's the run length the performance guide already asks for.
    /// </summary>
    private const double MinGrowthSeconds = 5;

    /// <summary>
    /// Private bytes are judged only over windows this long, and growth beyond the limit counts as native
    /// memory growing. A heuristic: the counter moves in 4 MB steps, up and down, as the GC commits and
    /// releases regions (the basic region size on 64-bit since .NET 7) and drivers commit memory, so a
    /// short capture can't tell a step from a leak; live sessions can.
    /// </summary>
    private const double PrivateGrowthSeconds = 30;
    private const long PrivateGrowthLimit = 8 * 1024 * 1024;

    public static string Version { get; } =
        typeof(Report).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static void WriteSummary(string path, ReportInfo info, FrameAnalysis analysis)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var frames = analysis.Frames;

        json.WriteStartObject();
        json.WriteString("tool", "Synthapixel.MonoGame.AgentTools");
        json.WriteString("version", Version);
        json.WriteString("mode", info.Mode);
        json.WriteString("game", info.Game);
        json.WriteStartObject("backbuffer");
        json.WriteNumber("width", info.Width);
        json.WriteNumber("height", info.Height);
        json.WriteEndObject();
        if (info.Fps is { } fps)
            json.WriteNumber("fps", fps);
        json.WriteNumber("frames", frames.Length);
        if (frames.Length > 0 && !double.IsNaN(frames[^1].SimSeconds))
            Number(json, "simSeconds", frames[^1].SimSeconds);
        Number(json, "wallSeconds", info.WallSeconds);

        json.WriteStartObject("frameTimeMs");
        json.WriteString("basis", info.FrameTimeBasis);
        json.WriteString("window", analysis.MeasuredAfterWarmup ? "after performance warm-up" : "all frames (warm-up not reached; includes first-use costs)");
        WriteDistribution(json, analysis.FrameTime);
        json.WriteEndObject();

        json.WriteStartObject("warmup");
        var active = analysis.Warmup.ActiveSignals(info.WallSeconds);
        WriteGate(json, "memory", analysis.Warmup.Memory, info.WallSeconds, active & (WarmupSignal.Allocations | WarmupSignal.GarbageCollections));
        WriteGate(json, "performance", analysis.Warmup.Performance, info.WallSeconds, active);
        json.WriteEndObject();

        json.WriteStartObject("allocations");
        json.WriteNumber("gameThreadBytes", frames.Sum(f => f.AllocatedBytes));
        json.WriteNumber("framesAllocating", frames.Count(f => f.AllocatedBytes > 0));
        var memory = analysis.Warmup.Memory;
        if (memory.State == WarmupState.Stable)
            json.WriteNumber("bytesAfterMemoryStable", frames.Where(f => f.Frame > memory.Frame).Sum(f => f.AllocatedBytes));
        json.WriteNumber("otherThreadsBytes", frames.Sum(f => f.OtherThreadsAllocatedBytes));
        if (memory.State == WarmupState.Stable)
            json.WriteNumber("otherThreadsBytesAfterMemoryStable", frames.Where(f => f.Frame > memory.Frame).Sum(f => f.OtherThreadsAllocatedBytes));
        if (frames.Any(f => f.CaptureAllocatedBytes > 0))
        {
            json.WriteNumber("captureBytes", frames.Sum(f => f.CaptureAllocatedBytes));
            if (memory.State == WarmupState.Stable)
                json.WriteNumber("captureBytesAfterMemoryStable", frames.Where(f => f.Frame > memory.Frame).Sum(f => f.CaptureAllocatedBytes));
        }
        json.WriteEndObject();

        WriteGrowth(json, info.MemoryHistory.Count > 0 ? info.MemoryHistory : frames.Select(f => new MemoryPoint(f.Frame, f.WallSeconds, f.HeapBytes, f.PrivateBytes, f.GraphicsResources)).ToList(), memory);

        json.WriteStartObject("gc");
        json.WriteNumber("gen0", frames.Sum(f => f.Gen0Collections));
        json.WriteNumber("gen1", frames.Sum(f => f.Gen1Collections));
        json.WriteNumber("gen2", frames.Sum(f => f.Gen2Collections));
        if (frames.Length > 0)
            json.WriteNumber("heapBytesAtEnd", frames[^1].HeapBytes);
        json.WriteEndObject();

        json.WriteStartObject("jit");
        json.WriteNumber("gameThreadMethods", frames.Sum(f => f.JitMethods));
        Number(json, "gameThreadMs", frames.Sum(f => f.JitMs));
        json.WriteEndObject();

        json.WriteStartObject("render");
        json.WriteString("source", "GraphicsDevice.Metrics, per frame");
        WriteMetric(json, "drawCalls", analysis.Measured, f => f.Graphics.DrawCount);
        WriteMetric(json, "primitives", analysis.Measured, f => f.Graphics.PrimitiveCount);
        WriteMetric(json, "sprites", analysis.Measured, f => f.Graphics.SpriteCount);
        WriteMetric(json, "renderTargetSwitches", analysis.Measured, f => f.Graphics.TargetCount);
        WriteMetric(json, "clears", analysis.Measured, f => f.Graphics.ClearCount);
        WriteMetric(json, "textureSwitches", analysis.Measured, f => f.Graphics.TextureCount);
        WriteMetric(json, "vertexShaderSwitches", analysis.Measured, f => f.Graphics.VertexShaderCount);
        WriteMetric(json, "pixelShaderSwitches", analysis.Measured, f => f.Graphics.PixelShaderCount);
        json.WriteEndObject();

        json.WriteStartObject("hitches");
        Number(json, "thresholdMs", analysis.HitchThresholdMs);
        json.WriteString("rule", "frame time > max(2 x median, median + 2 ms)");
        json.WriteNumber("count", analysis.HitchCount);
        if (analysis.HitchCount > analysis.Hitches.Count)
            json.WriteString("listed", $"the {analysis.Hitches.Count} largest, in frame order; frames.csv has every frame");
        json.WriteStartArray("frames");
        foreach (var hitch in analysis.Hitches)
        {
            var s = hitch.Sample;
            json.WriteStartObject();
            json.WriteNumber("frame", hitch.Frame);
            Number(json, "frameMs", hitch.FrameMs);
            Number(json, "excessMs", hitch.ExcessMs);
            if (!double.IsNaN(s.UpdateMs))
            {
                Number(json, "updateMs", s.UpdateMs);
                Number(json, "drawMs", s.DrawMs);
            }
            json.WriteNumber("jitMethods", s.JitMethods);
            Number(json, "jitMs", s.JitMs);
            json.WriteNumber("allocatedBytes", s.AllocatedBytes);
            json.WriteNumber("collections", s.Gen0Collections);
            json.WriteString("likelyCause", hitch.LikelyCause);
            if (hitch.Methods.Count > 0)
            {
                json.WriteNumber("methodCount", hitch.Methods.Count);
                json.WriteStartArray("methods");
                foreach (var method in hitch.Methods.Take(MaxMethodsPerHitch))
                    json.WriteStringValue(method);
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        if (info.Notes.Count > 0)
        {
            json.WriteStartArray("notes");
            foreach (var note in info.Notes)
                json.WriteStringValue(note);
            json.WriteEndArray();
        }

        if (info.CustomMetricNames.Count > 0)
        {
            json.WriteStartObject("customMetrics");
            for (int p = 0; p < info.CustomMetricNames.Count; ++p)
            {
                // NaN means the metric had no value that frame (an empty CSV cell); the summary skips it.
                var values = Enumerable.Range(0, frames.Length)
                    .Select(i => info.CustomMetricValues[i * info.CustomMetricNames.Count + p])
                    .Where(v => !double.IsNaN(v)).ToArray();
                json.WriteStartObject(info.CustomMetricNames[p]);
                Number(json, "first", values.Length > 0 ? values[0] : double.NaN);
                Number(json, "last", values.Length > 0 ? values[^1] : double.NaN);
                Number(json, "min", values.Length > 0 ? values.Min() : double.NaN);
                Number(json, "max", values.Length > 0 ? values.Max() : double.NaN);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }

        foreach (var (key, value) in info.Sections)
        {
            json.WritePropertyName(key);
            JsonSerializer.Serialize(json, value, value.GetType());
        }

        if (info.Files.Count > 0)
        {
            json.WriteStartObject("files");
            foreach (var (key, value) in info.Files)
            {
                json.WritePropertyName(key);
                JsonSerializer.Serialize(json, value, value.GetType());
            }
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    /// <summary>Custom metric columns are prefixed so a metric can't collide with a built-in column.</summary>
    public const string CustomMetricColumnPrefix = "custom.";

    internal static string CsvField(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    public static void WriteCsv(string path, ReportInfo info, FrameSample[] frames)
    {
        var csv = new StringBuilder();
        csv.Append("frame,sim_s,wall_s,frame_ms,update_ms,draw_ms,alloc_b,alloc_other_threads_b,alloc_capture_b,gc0,gc1,gc2,heap_b,private_b,gpu_resources,")
           .Append("jit_methods,jit_ms,jit_methods_all_threads,draws,primitives,sprites,targets,clears,textures,vertex_shaders,pixel_shaders");
        foreach (var name in info.CustomMetricNames)
            csv.Append(',').Append(CsvField(CustomMetricColumnPrefix + name));
        csv.Append('\n');

        var metrics = info.CustomMetricNames.Count;
        for (int i = 0; i < frames.Length; ++i)
        {
            var f = frames[i];
            var g = f.Graphics;
            csv.Append(CultureInfo.InvariantCulture, $"{f.Frame},{F(f.SimSeconds)},{F(f.WallSeconds)},{F(f.FrameMs)},{F(f.UpdateMs)},{F(f.DrawMs)},")
               .Append(CultureInfo.InvariantCulture, $"{f.AllocatedBytes},{f.OtherThreadsAllocatedBytes},{f.CaptureAllocatedBytes},{f.Gen0Collections},{f.Gen1Collections},{f.Gen2Collections},")
               .Append(CultureInfo.InvariantCulture, $"{f.HeapBytes},{f.PrivateBytes},{f.GraphicsResources},")
               .Append(CultureInfo.InvariantCulture, $"{f.JitMethods},{F(f.JitMs)},{f.JitMethodsAllThreads},")
               .Append(CultureInfo.InvariantCulture, $"{g.DrawCount},{g.PrimitiveCount},{g.SpriteCount},{g.TargetCount},{g.ClearCount},{g.TextureCount},{g.VertexShaderCount},{g.PixelShaderCount}");
            for (int p = 0; p < metrics; ++p)
                csv.Append(',').Append(F(info.CustomMetricValues[i * metrics + p]));
            csv.Append('\n');
        }
        File.WriteAllText(path, csv.ToString());
    }

    private static string F(double value) =>
        double.IsNaN(value) ? "" : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static void Number(Utf8JsonWriter json, string name, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            json.WriteNull(name);
        else
            json.WriteNumber(name, Math.Round(value, 4));
    }

    private static void WriteDistribution(Utf8JsonWriter json, Distribution d)
    {
        Number(json, "p50", d.P50);
        Number(json, "p95", d.P95);
        Number(json, "p99", d.P99);
        Number(json, "max", d.Max);
        Number(json, "mean", d.Mean);
        json.WriteNumber("count", d.Count);
    }

    private static void WriteMetric(Utf8JsonWriter json, string name, FrameSample[] frames, Func<FrameSample, long> metric)
    {
        json.WriteStartObject(name);
        Number(json, "mean", frames.Length > 0 ? frames.Average(f => (double)metric(f)) : double.NaN);
        json.WriteNumber("max", frames.Length > 0 ? frames.Max(metric) : 0);
        json.WriteEndObject();
    }

    /// <summary>
    /// How much retained memory and live resources changed after warm-up: steady growth here means
    /// something is created repeatedly and kept (or never disposed), even when no garbage is collected.
    /// </summary>
    private static void WriteGrowth(Utf8JsonWriter json, IReadOnlyList<MemoryPoint> points, WarmupGate memory)
    {
        if (points.Count == 0)
            return;
        var stable = memory.State == WarmupState.Stable;
        var first = stable && points.Any(p => p.Frame >= memory.Frame) ? points.First(p => p.Frame >= memory.Frame) : points[0];
        var last = points[^1];

        var seconds = last.WallSeconds - first.WallSeconds;
        var resources = last.GraphicsResources >= 0 ? last.GraphicsResources - first.GraphicsResources : 0;
        var privateBytes = last.PrivateBytes - first.PrivateBytes;
        var verdict =
            !stable ? "not judged: memory gate not reached (see allocations)"
            : seconds < MinGrowthSeconds ? string.Create(CultureInfo.InvariantCulture, $"too short to judge: {seconds:0.#} s after the memory gate, needs {MinGrowthSeconds} s")
            : resources > 0 ? $"GPU resources not released: +{resources}"
            : seconds < PrivateGrowthSeconds ? string.Create(CultureInfo.InvariantCulture, $"steady: no GPU resources leaked (private bytes are judged from {PrivateGrowthSeconds} s)")
            : privateBytes > PrivateGrowthLimit ? string.Create(CultureInfo.InvariantCulture, $"native memory growing: private bytes +{privateBytes / (1024.0 * 1024):0.#} MB")
            : "steady";

        json.WriteStartObject("growth");
        json.WriteString("window", stable ? "from the memory gate to the end" : "whole run (memory gate not reached)");
        json.WriteString("verdict", verdict);
        json.WriteString("heapNote", "heap includes garbage not yet collected, so it rises with any allocation until a GC; judge retained memory by GPU resources, private bytes and allocations after the memory gate");
        json.WriteNumber("fromFrame", first.Frame);
        Number(json, "seconds", seconds);
        WriteChange(json, "heapBytes", first.HeapBytes, last.HeapBytes);
        WriteChange(json, "privateBytes", first.PrivateBytes, last.PrivateBytes);
        if (last.GraphicsResources >= 0)
            WriteChange(json, "graphicsResources", first.GraphicsResources, last.GraphicsResources);
        json.WriteEndObject();
    }

    public static void WriteMemoryCsv(string path, IReadOnlyList<MemoryPoint> points)
    {
        var csv = new StringBuilder("frame,wall_s,heap_b,private_b,gpu_resources\n");
        foreach (var p in points)
            csv.Append(CultureInfo.InvariantCulture, $"{p.Frame},{F(p.WallSeconds)},{p.HeapBytes},{p.PrivateBytes},{p.GraphicsResources}\n");
        File.WriteAllText(path, csv.ToString());
    }

    private static void WriteChange(Utf8JsonWriter json, string name, long start, long end)
    {
        json.WriteStartObject(name);
        json.WriteNumber("start", start);
        json.WriteNumber("end", end);
        json.WriteNumber("change", end - start);
        json.WriteEndObject();
    }

    private static void WriteGate(Utf8JsonWriter json, string name, WarmupGate gate, double wallSeconds, WarmupSignal active)
    {
        json.WriteStartObject(name);
        switch (gate.State)
        {
            case WarmupState.Stable:
                json.WriteString("state", "stable");
                json.WriteNumber("frame", gate.Frame);
                Number(json, "wallSeconds", gate.WallSeconds);
                json.WriteNumber("disturbancesAfterStable", gate.DisturbancesAfterStable);
                if (gate.DisturbancesAfterStable > 0)
                    json.WriteNumber("firstDisturbanceFrame", gate.FirstDisturbanceFrame);
                break;
            case WarmupState.NeverStabilized:
                json.WriteString("state", "never stabilized");
                json.WriteString("blocking", gate.Blocking.ToString());
                Number(json, "allocatedBytesPerSecond", gate.AllocatedBytesPerSecond);
                break;
            default:
                json.WriteString("state", "not reached");
                json.WriteString("activeSignals", active.ToString());
                json.WriteString("reason", $"measurement ended at {wallSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s wall, before every signal was quiet for 1 s; run longer");
                break;
        }
        json.WriteEndObject();
    }
}
