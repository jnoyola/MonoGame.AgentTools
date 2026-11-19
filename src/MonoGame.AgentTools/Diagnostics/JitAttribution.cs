using System.Diagnostics.Tracing;

namespace MonoGame.AgentTools.Diagnostics;

/// <summary>
/// Names the methods compiled during each frame, from the runtime's own JIT events, so a hitch can be
/// traced to the code that ran for the first time. Opt-in: every event allocates (on the event
/// dispatch thread, not the game thread).
/// </summary>
internal sealed class JitAttribution : EventListener
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords JitKeyword = (EventKeywords)0x10;

    // MethodFlags bits 7-9 hold the optimization tier. Tiers 1-3 (MinOpts, Optimized, QuickJitted) are
    // compiled synchronously on the calling thread; higher tiers are background re-compilation.
    private const int TierShift = 7;
    private const int TierMask = 0x7;
    private const int LastBlockingTier = 3;

    private readonly List<(DateTime Time, string Name)> _methods = [];
    private readonly List<(int Frame, DateTime Start, DateTime End)> _frames = [];

    public void BeginFrame(int frame) => _frames.Add((frame, DateTime.UtcNow, DateTime.MaxValue));

    public void EndFrame()
    {
        var last = _frames[^1];
        _frames[^1] = last with { End = DateTime.UtcNow };
    }

    /// <summary>Methods compiled on a blocking tier during <paramref name="frame"/>.</summary>
    public IReadOnlyList<string> MethodsFor(int frame)
    {
        var index = _frames.FindIndex(f => f.Frame == frame);
        if (index < 0)
            return [];
        var (_, start, end) = _frames[index];
        lock (_methods)
            return _methods.Where(m => m.Time >= start && m.Time <= end).Select(m => m.Name).ToArray();
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == RuntimeProvider)
            EnableEvents(eventSource, EventLevel.Verbose, JitKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not ("MethodLoadVerbose_V1" or "MethodLoadVerbose_V2") || e.Payload is null || e.PayloadNames is null)
            return;
        var flags = Convert.ToInt32(e.Payload[e.PayloadNames.IndexOf("MethodFlags")], System.Globalization.CultureInfo.InvariantCulture);
        var tier = (flags >> TierShift) & TierMask;
        if (tier is 0 or > LastBlockingTier)
            return;
        var ns = e.Payload[e.PayloadNames.IndexOf("MethodNamespace")] as string;
        var name = e.Payload[e.PayloadNames.IndexOf("MethodName")] as string;
        lock (_methods)
            _methods.Add((e.TimeStamp.ToUniversalTime(), $"{ns}::{name}"));
    }
}
