using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Synthapixel.MonoGame.AgentTools.Mcp.Tools;

/// <summary>
/// A short plain-text reading of summary.json for the capture tool's default result: one labeled line
/// per check, with units and the verdict stated. Clean checks stay one short line; problems get detail.
/// </summary>
internal static class CaptureBrief
{
    private const int MaxHitchesNamed = 3;
    private static readonly string[] Generations = ["gen0", "gen1", "gen2"];

    public static string Format(string summaryJson)
    {
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;
        var text = new StringBuilder();
        void Line(FormattableString line) => text.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

        var size = root.TryGetProperty("backbuffer", out var b) ? $", {b.GetProperty("width").GetInt32()}x{b.GetProperty("height").GetInt32()}" : "";
        var paced = root.TryGetProperty("mode", out var mode) && mode.GetString() == "profile" ? "paced in real time, " : "";
        Line($"{Int(root, "frames")} frames, {paced}{Timing(root)}: {Num(root, "simSeconds")} s game time, {Num(root, "wallSeconds")} s wall{size}");
        if (root.TryGetProperty("input", out var input))
        {
            var events = input.GetProperty("events").GetInt32();
            var applied = input.GetProperty("applied").GetInt32();
            Line($"Input: {(applied == events ? $"{events}" : $"{applied} of {events}")} event{(events == 1 ? "" : "s")} applied from {input.GetProperty("file").GetString()}");
        }
        if (root.TryGetProperty("notes", out var notes))
            Line($"Notes: {string.Join("; ", notes.EnumerateArray().SelectMany(n => n.GetString()!.Split('\n')))}");

        int? memoryFrame = null;
        if (root.TryGetProperty("warmup", out var warmup))
        {
            memoryFrame = warmup.GetProperty("memory") is { } m && m.GetProperty("state").GetString() == "stable" ? m.GetProperty("frame").GetInt32() : null;
            var gates = $"memory {Gate(warmup.GetProperty("memory"))}; performance {Gate(warmup.GetProperty("performance"))}";
            Line($"Warm-up: {gates}{(gates.Contains("not reached", StringComparison.Ordinal) ? "; run longer to reach it" : "")}");
        }

        if (root.TryGetProperty("frameTimeMs", out var time))
        {
            var window = time.GetProperty("window").GetString()!.StartsWith("after", StringComparison.Ordinal) ? "after warm-up" : "all frames, includes first-use costs";
            Line($"Frame CPU ms ({window}): p50 {Num(time, "p50")}, p95 {Num(time, "p95")}, p99 {Num(time, "p99")}, max {Num(time, "max")}");
        }

        if (root.TryGetProperty("allocations", out var alloc))
        {
            var stable = alloc.TryGetProperty("bytesAfterMemoryStable", out var after);
            var capture = alloc.TryGetProperty(stable ? "captureBytesAfterMemoryStable" : "captureBytes", out var c) && c.GetInt64() > 0
                ? $"; the capture's own frame readback and saving {Bytes(c.GetInt64())} (not the game's)"
                : "";
            if (stable)
                Line($"Allocations after memory stable: game thread {Bytes(after.GetInt64())}, other threads {Bytes(alloc.GetProperty("otherThreadsBytesAfterMemoryStable").GetInt64())}{capture}");
            else
            {
                var frames = alloc.GetProperty("framesAllocating").GetInt32();
                Line($"Allocations over the whole run (no memory gate): game thread {Bytes(alloc.GetProperty("gameThreadBytes").GetInt64())} in {frames} frame{(frames == 1 ? "" : "s")}, other threads {Bytes(alloc.GetProperty("otherThreadsBytes").GetInt64())}{capture}");
            }
        }

        if (root.TryGetProperty("growth", out var growth))
        {
            var parts = new List<string> { $"heap {Signed(growth.GetProperty("heapBytes"), Bytes)}", $"private {Signed(growth.GetProperty("privateBytes"), Bytes)}" };
            if (growth.TryGetProperty("graphicsResources", out var gpu))
                parts.Add($"GPU resources {Signed(gpu, n => n.ToString(CultureInfo.InvariantCulture))}");
            // The verdict states the window when it matters ("too short to judge: 1.4 s ..."); otherwise give it here.
            var verdict = growth.TryGetProperty("verdict", out var v) ? v.GetString()! : "";
            var window = verdict.StartsWith("too short", StringComparison.Ordinal) ? "" : $", {Num(growth, "seconds", "0.#")} s";
            Line($"Growth {(memoryFrame is not null ? "after memory stable" : "over the run")}{window}: {(verdict.Length > 0 ? verdict + "; " : "")}{string.Join(", ", parts)} (heap counts uncollected garbage)");
        }

        var health = new List<string>();
        if (root.TryGetProperty("gc", out var gc))
        {
            var collections = Generations.Select(g => (g, n: gc.GetProperty(g).GetInt32())).Where(c => c.n > 0).ToArray();
            health.Add(collections.Length == 0 ? "GC: none" : "GC: " + string.Join(", ", collections.Select(c => $"{c.g} {c.n}")));
        }
        if (root.TryGetProperty("jit", out var jit))
            health.Add(string.Create(CultureInfo.InvariantCulture, $"JIT on game thread: {Int(jit, "gameThreadMethods")} methods, {Num(jit, "gameThreadMs")} ms"));
        if (health.Count > 0)
            text.Append(string.Join(" · ", health)).Append('\n');

        if (root.TryGetProperty("render", out var render))
        {
            string Metric(string name, string label) =>
                render.TryGetProperty(name, out var m) ? $"{label} {Num(m, "mean", "0")}/{Int(m, "max")}" : "";
            var parts = new[] { Metric("drawCalls", "draws"), Metric("primitives", "primitives"), Metric("renderTargetSwitches", "target switches") };
            var sprites = render.TryGetProperty("sprites", out var s) && s.GetProperty("max").GetInt64() > 0 ? ", " + Metric("sprites", "sprites") : "";
            Line($"Render per frame (mean/max): {string.Join(", ", parts.Where(p => p.Length > 0))}{sprites}");
        }

        if (root.TryGetProperty("hitches", out var hitches))
        {
            var listed = hitches.GetProperty("frames").EnumerateArray().ToArray();
            var count = hitches.TryGetProperty("count", out var c) ? c.GetInt32() : listed.Length;
            if (count == 0)
                Line($"Hitches (> {Num(hitches, "thresholdMs")} ms): none");
            else
            {
                var named = listed.OrderByDescending(h => h.GetProperty("frameMs").GetDouble()).Take(MaxHitchesNamed)
                    .Select(h => string.Create(CultureInfo.InvariantCulture, $"f{h.GetProperty("frame").GetInt32()} {Num(h, "frameMs")} ms: {h.GetProperty("likelyCause").GetString()}"));
                var more = count > MaxHitchesNamed ? $"; and {count - MaxHitchesNamed} more" : "";
                Line($"Hitches (> {Num(hitches, "thresholdMs")} ms): {count}, largest {string.Join("; ", named)}{more}");
            }
        }

        if (root.TryGetProperty("customMetrics", out var custom))
        {
            text.Append("Custom metrics (first → last, min to max):\n");
            foreach (var metric in custom.EnumerateObject())
            {
                var v = metric.Value;
                text.Append("  ").Append(metric.Name).Append(": ").Append(v.GetProperty("first").ValueKind == JsonValueKind.Null
                    ? "no value"
                    : $"{Num(v, "first")} → {Num(v, "last")}, {Num(v, "min")} to {Num(v, "max")}").Append('\n');
            }
        }
        return text.ToString();
    }

    private static string Timing(JsonElement root)
    {
        var fps = Num(root, "fps", "0.###");
        if (!root.TryGetProperty("timing", out var timing))
            return $"{fps} fps";
        var step = Num(timing, "stepMs", "0.####");
        return timing.GetProperty("mode").GetString() switch
        {
            "real" => $"real timing (measured; nominal {step} ms) kept in {timing.GetProperty("file").GetString()}",
            "file" => $"timing replayed ({Int(timing, "frames")} recorded frames, then {step} ms steps)",
            _ => $"fixed {step} ms step ({fps} fps)",
        };
    }

    private static string Gate(JsonElement gate)
    {
        var state = gate.GetProperty("state").GetString();
        return state switch
        {
            "stable" => string.Create(CultureInfo.InvariantCulture, $"stable at f{gate.GetProperty("frame").GetInt32()}")
                + (gate.GetProperty("disturbancesAfterStable").GetInt32() is var d and > 0
                    ? string.Create(CultureInfo.InvariantCulture, $", then disturbed {d} times from f{gate.GetProperty("firstDisturbanceFrame").GetInt32()}")
                    : ""),
            "never stabilized" => $"never stabilized (blocked by {gate.GetProperty("blocking").GetString()}, {Bytes((long)gate.GetProperty("allocatedBytesPerSecond").GetDouble())}/s)",
            _ => $"not reached (still active: {gate.GetProperty("activeSignals").GetString()})",
        };
    }

    private static string Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64().ToString(CultureInfo.InvariantCulture) : "?";

    private static string Num(JsonElement element, string name, string format = "0.##") =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString(format, CultureInfo.InvariantCulture) : "?";

    private static string Signed(JsonElement change, Func<long, string> format)
    {
        var n = change.GetProperty("change").GetInt64();
        return n == 0 ? "0" : (n > 0 ? "+" : "-") + format(Math.Abs(n));
    }

    internal static string Bytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };
}
