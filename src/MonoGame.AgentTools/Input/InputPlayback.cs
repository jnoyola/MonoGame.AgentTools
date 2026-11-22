using MonoGame.AgentTools.Timing;

namespace MonoGame.AgentTools.Input;

/// <summary>Receives scripted input as a capture applies it.</summary>
internal interface IInputSink
{
    void Apply(in ScriptEvent e);
}

/// <summary>
/// A script ready to play against a <see cref="FrameClock"/>. When frame times are known before the run,
/// every <c>t</c> is resolved to a frame up front (the first frame whose total time is at or after it), so
/// events apply in file order within a frame. Under real timing, frame-keyed events wait for their frame
/// and time-keyed ones for the first frame whose measured total reaches them. Applying allocates nothing.
/// </summary>
internal sealed class InputPlayback
{
    private readonly ScriptEvent[] _byFrame;   // resolved, or frame-keyed under real timing; sorted by frame
    private readonly ScriptEvent[] _byTime;    // real timing only; sorted by ticks
    private readonly int[] _appliedFrame;      // per event in _byFrame then _byTime, for the summary
    private int _nextFrame;
    private int _nextTime;

    private InputPlayback(ScriptEvent[] byFrame, ScriptEvent[] byTime, List<string> warnings)
    {
        _byFrame = byFrame;
        _byTime = byTime;
        _appliedFrame = new int[byFrame.Length + byTime.Length];
        Warnings = warnings;
    }

    public IReadOnlyList<string> Warnings { get; }

    public int Count => _byFrame.Length + _byTime.Length;

    public int Applied => _nextFrame + _nextTime;

    public static InputPlayback Create(InputScript script, FrameClock clock)
    {
        var warnings = new List<string>();
        if (!clock.IsKnown)
        {
            var byFrame = script.Events.Where(e => e.Ticks < 0).OrderBy(e => e.Frame).ToArray();
            var byTime = script.Events.Where(e => e.Ticks >= 0).OrderBy(e => e.Ticks).ToArray();
            return new InputPlayback(byFrame, byTime, warnings);
        }

        var resolved = script.Events
            .Select(e => e.Ticks >= 0 ? e with { Frame = clock.FirstFrameAtOrAfter(e.Ticks) } : e)
            .OrderBy(e => e.Frame)
            .ToArray();
        foreach (var frame in resolved.Where(e => e.Control.IsButton).GroupBy(e => (e.Frame, e.Control)))
        {
            var downs = frame.Where(e => e.Action == InputAction.Down).ToList();
            if (downs.Count > 0 && frame.Any(e => e.Action == InputAction.Up && e.Line > downs[0].Line))
                warnings.Add($"line {downs[0].Line}: {downs[0].Control} goes down and up on f{downs[0].Frame}, so GetState never shows it pressed.");
        }
        return new InputPlayback(resolved, [], warnings);
    }

    /// <summary>Applies every event due on <paramref name="frame"/>, whose total game time is <paramref name="totalTicks"/>.</summary>
    public void Apply(int frame, long totalTicks, IInputSink sink)
    {
        while (_nextFrame < _byFrame.Length && _byFrame[_nextFrame].Frame <= frame)
        {
            sink.Apply(in _byFrame[_nextFrame]);
            _appliedFrame[_nextFrame++] = frame;
        }
        while (_nextTime < _byTime.Length && _byTime[_nextTime].Ticks <= totalTicks)
        {
            sink.Apply(in _byTime[_nextTime]);
            _appliedFrame[_byFrame.Length + _nextTime++] = frame;
        }
    }

    /// <summary>The events applied so far, with the frame each applied on.</summary>
    public IEnumerable<(int Frame, ScriptEvent Event)> AppliedEvents()
    {
        for (int i = 0; i < _nextFrame; ++i)
            yield return (_appliedFrame[i], _byFrame[i]);
        for (int i = 0; i < _nextTime; ++i)
            yield return (_appliedFrame[_byFrame.Length + i], _byTime[i]);
    }
}
