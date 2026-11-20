using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.AgentTools.Diagnostics;

namespace MonoGame.AgentTools.Hud;

/// <summary>Frame statistics over the most recent window of wall time.</summary>
internal struct FrameWindow
{
    public double Seconds;
    public int Frames;
    public double P50Ms;
    public double P95Ms;
    public double MaxMs;
    public long AllocatedBytes;
    public long OtherThreadsAllocatedBytes;
    public int GraphicsResourcesAdded;
    public int Gen0;
    public int Gen1;
    public int Gen2;
    public int JitMethods;
    public int JitMethodsAllThreads;

    public readonly double Fps => Seconds > 0 ? Frames / Seconds : 0;
}

/// <summary>Everything the HUD shows: the same measurements the agent reads from the stats files.</summary>
internal readonly ref struct HudSnapshot
{
    public required int Frame { get; init; }
    public required double WallSeconds { get; init; }
    public required FrameWindow Window { get; init; }
    public required long HeapBytes { get; init; }
    public required long PrivateBytes { get; init; }
    public required int GraphicsResources { get; init; }
    public required RuntimeCounters SinceStart { get; init; }
    public required WarmupGate Memory { get; init; }
    public required WarmupGate Performance { get; init; }
    public required WarmupSignal ActiveSignals { get; init; }
    public required GraphicsMetrics Graphics { get; init; }
    public required IReadOnlyList<CustomMetric> CustomMetrics { get; init; }
    public required ReadOnlySpan<double> CustomMetricValues { get; init; }
}

internal sealed class HudLine
{
    public StringBuilder Text { get; } = new(96);
    public Color Color { get; set; }
}

/// <summary>
/// Formats a <see cref="HudSnapshot"/> into lines. Allocation-free once the lines exist. Numbers that change
/// every refresh have fixed-width fields, as in Unreal's stat tables and RivaTuner's on-screen display, so
/// lines keep their length while values change.
/// </summary>
internal static class HudText
{
    public const int FixedLines = 10;

    public static readonly Color Title = new(150, 160, 175);
    public static readonly Color Normal = Color.White;
    public static readonly Color Warning = new(255, 200, 80);
    public static readonly Color Bad = new(255, 100, 90);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Creates the measurement lines, followed by a blank line and the notes, which never change.</summary>
    public static HudLine[] CreateLines(int metricCount, IReadOnlyList<string> notes)
    {
        var noteLines = notes.SelectMany(n => n.ReplaceLineEndings("\n").Split('\n')).ToArray();
        var lines = new HudLine[FixedLines + metricCount + (noteLines.Length > 0 ? 1 + noteLines.Length : 0)];
        for (var i = 0; i < lines.Length; i++)
            lines[i] = new HudLine { Color = Normal };
        for (var i = 0; i < noteLines.Length; i++)
            lines[^(noteLines.Length - i)].Text.Append(noteLines[i]);
        return lines;
    }

    public static void Format(in HudSnapshot s, HudLine[] lines)
    {
        var w = s.Window;
        var line = 0;

        var text = Start(lines[line++], Title).Append("AgentTools  frame ");
        Number(text, s.Frame).Append(" | ");
        Number(text, s.WallSeconds, "F1").Append(" s");

        var hitch = w.MaxMs > Math.Max(2 * w.P50Ms, w.P50Ms + 2);
        text = Start(lines[line++], hitch ? Warning : Normal).Append("frame   ");
        Number(text, w.Fps, "F1", 7).Append(" fps | p50 ");
        Number(text, w.P50Ms, "F2", 6).Append(" | p95 ");
        Number(text, w.P95Ms, "F2", 6).Append(" | max ");
        Number(text, w.MaxMs, "F2", 6).Append(" ms");

        text = Start(lines[line++], w.AllocatedBytes + w.OtherThreadsAllocatedBytes > 0 ? Warning : Normal).Append("memory  alloc ");
        Bytes(text, w.AllocatedBytes).Append("/s | other threads ");
        Bytes(text, w.OtherThreadsAllocatedBytes).Append("/s | heap ");
        Bytes(text, s.HeapBytes);

        text = Start(lines[line++], w.Gen0 + w.Gen1 + w.Gen2 > 0 ? Warning : Normal).Append(Indent).Append("gc gen0/1/2 ");
        Number(text, w.Gen0).Append('/');
        Number(text, w.Gen1).Append('/');
        Number(text, w.Gen2).Append(" per s | ");
        Number(text, s.SinceStart.Gen0).Append('/');
        Number(text, s.SinceStart.Gen1).Append('/');
        Number(text, s.SinceStart.Gen2).Append(" total");

        text = Start(lines[line++], w.GraphicsResourcesAdded > 0 ? Warning : Normal).Append(Indent).Append("process ");
        Bytes(text, s.PrivateBytes).Append(" | gpu resources ");
        Number(text, s.GraphicsResources, width: 5);
        if (w.GraphicsResourcesAdded > 0)
            Number(text.Append(" (+"), w.GraphicsResourcesAdded).Append("/s)");

        AppendGate(lines[line++], "warm-up memory", s.Memory, s.ActiveSignals & (WarmupSignal.Allocations | WarmupSignal.GarbageCollections));
        AppendGate(lines[line++], Indent + "perf  ", s.Performance, s.ActiveSignals);
        text = Start(lines[line++], w.JitMethods > 0 ? Warning : Normal).Append(Indent).Append("jit    compiling ");
        Number(text, w.JitMethodsAllThreads).Append("/s | blocking frames ");
        Number(text, w.JitMethods).Append("/s | ");
        Number(text, s.SinceStart.JitMethodsAllThreads).Append(" total");

        var g = s.Graphics;
        text = Start(lines[line++], Normal).Append("render  draws ");
        Number(text, g.DrawCount, "N0", 5).Append(" | primitives ");
        Number(text, g.PrimitiveCount, "N0", 9).Append(" | sprites ");
        Number(text, g.SpriteCount, "N0", 7);
        text = Start(lines[line++], Normal).Append(Indent).Append("textures ");
        Number(text, g.TextureCount, "N0", 4).Append(" | targets ");
        Number(text, g.TargetCount, "N0", 3).Append(" | shaders ");
        Number(text, g.VertexShaderCount).Append('/');
        Number(text, g.PixelShaderCount).Append(" | clears ");
        Number(text, g.ClearCount, "N0", 3);

        var nameWidth = 0;
        for (var m = 0; m < s.CustomMetrics.Count; m++)
            nameWidth = Math.Max(nameWidth, s.CustomMetrics[m].Name.Length);
        for (var m = 0; m < s.CustomMetrics.Count; m++)
        {
            var name = s.CustomMetrics[m].Name;
            text = Start(lines[line++], Normal).Append(m == 0 ? "custom  " : Indent).Append(name).Append(' ', nameWidth - name.Length + 1);
            if (double.IsNaN(s.CustomMetricValues[m]))
                text.Append(' ', 11).Append('-');
            else
                Number(text, s.CustomMetricValues[m], "#,0.##", 12);
        }
    }

    private const string Indent = "        ";

    private static StringBuilder Start(HudLine line, Color color)
    {
        line.Color = color;
        return line.Text.Clear();
    }

    private static void AppendGate(HudLine line, string name, WarmupGate gate, WarmupSignal active)
    {
        switch (gate.State)
        {
            case WarmupState.Stable:
                var text = Start(line, Normal).Append(name).Append(" stable at ");
                Number(text, gate.WallSeconds, "F1").Append(" s (frame ");
                Number(text, gate.Frame).Append(')');
                if (gate.DisturbancesAfterStable > 0)
                    Number(text.Append(" | disturbed "), gate.DisturbancesAfterStable).Append('x');
                break;
            case WarmupState.NeverStabilized:
                AppendSignals(Start(line, Bad).Append(name).Append(" never stabilized:"), gate.Blocking);
                break;
            default:
                AppendSignals(Start(line, Warning).Append(name).Append(" warming up:"), active);
                break;
        }
    }

    private static void AppendSignals(StringBuilder text, WarmupSignal signals)
    {
        if (signals == WarmupSignal.None)
            text.Append(" settling");
        if ((signals & WarmupSignal.Allocations) != 0)
            text.Append(" alloc");
        if ((signals & WarmupSignal.GarbageCollections) != 0)
            text.Append(" gc");
        if ((signals & WarmupSignal.JitCompilation) != 0)
            text.Append(" jit");
    }

    /// <summary>
    /// Appends a byte count in 8 characters: <c>  1023 B</c>, <c> 12.0 KB</c>, <c>999.9 MB</c>. Moves to the
    /// next unit at 1000 rather than 1024, so the number never needs a fourth digit.
    /// </summary>
    private static StringBuilder Bytes(StringBuilder text, long bytes)
    {
        const double kilo = 1024;
        if (bytes < kilo)
            return Number(text, bytes, width: 6).Append(" B");
        if (bytes < 999.95 * kilo)
            return Number(text, bytes / kilo, "F1", 5).Append(" KB");
        if (bytes < 999.95 * kilo * kilo)
            return Number(text, bytes / (kilo * kilo), "F1", 5).Append(" MB");
        return Number(text, bytes / (kilo * kilo * kilo), "F1", 5).Append(" GB");
    }

    // Numbers go through the non-generic TryFormat overloads into a stack buffer. Interpolated appends use
    // the generic AppendFormatted<T>, whose unoptimized first-tier code boxes the value: the HUD then
    // allocated for its first few seconds, until tiered compilation recompiled it.
    private static StringBuilder Number(StringBuilder text, double value, string format, int width = 0)
    {
        Span<char> buffer = stackalloc char[32];
        if (!value.TryFormat(buffer, out var written, format, Invariant))
            return text.Append('#', Math.Max(1, width));
        return text.Append(' ', Math.Max(0, width - written)).Append(buffer[..written]);
    }

    private static StringBuilder Number(StringBuilder text, long value, string format = "D", int width = 0)
    {
        Span<char> buffer = stackalloc char[32];
        if (!value.TryFormat(buffer, out var written, format, Invariant))
            return text.Append('#', Math.Max(1, width));
        return text.Append(' ', Math.Max(0, width - written)).Append(buffer[..written]);
    }
}
