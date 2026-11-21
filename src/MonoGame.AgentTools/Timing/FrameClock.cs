namespace MonoGame.AgentTools.Timing;

internal enum TimingMode { Fixed, Real, File }

/// <summary>
/// The game time each captured frame is given. Frame 1 is the first timed <c>Update</c>. Under
/// <see cref="TimingMode.Fixed"/> and <see cref="TimingMode.File"/> every frame's time is known before the
/// run (<see cref="IsKnown"/>); under <see cref="TimingMode.Real"/> each frame's elapsed time is measured
/// as the run goes and stored with <see cref="SetMeasured"/>.
/// </summary>
internal sealed class FrameClock
{
    private readonly long[] _elapsed;   // index 0 is frame 1; recorded or measured frames
    private readonly long[] _total;     // running sums of _elapsed
    private int _measured;

    private FrameClock(TimingMode mode, long step, long[] elapsed)
    {
        Mode = mode;
        StepTicks = step;
        _elapsed = elapsed;
        _total = new long[elapsed.Length];
        if (mode == TimingMode.File)
        {
            long sum = 0;
            for (int i = 0; i < elapsed.Length; ++i)
                _total[i] = sum += elapsed[i];
            _measured = elapsed.Length;
        }
    }

    public TimingMode Mode { get; }

    /// <summary>The even step: every frame under Fixed, frames past the file under File, the nominal rate under Real.</summary>
    public long StepTicks { get; }

    public bool IsKnown => Mode != TimingMode.Real;

    /// <summary>Frames the timing file holds (File), or frames measured so far (Real).</summary>
    public int Length => _measured;

    public static FrameClock Fixed(long step) => new(TimingMode.Fixed, Positive(step), []);

    public static FrameClock FromFile(long[] elapsed, long step) => new(TimingMode.File, Positive(step), elapsed);

    /// <summary>Real timing for up to <paramref name="frames"/> frames.</summary>
    public static FrameClock Real(long step, int frames) => new(TimingMode.Real, Positive(step), new long[frames]);

    public long Elapsed(int frame) => frame <= _measured ? _elapsed[frame - 1] : StepTicks;

    public long Total(int frame) => frame <= _measured
        ? _total[frame - 1]
        : (_measured == 0 ? 0 : _total[_measured - 1]) + (long)(frame - _measured) * StepTicks;

    /// <summary>Real timing: the elapsed time measured for the next frame, which must be frame <see cref="Length"/> + 1.</summary>
    public void SetMeasured(int frame, long elapsed)
    {
        if (Mode != TimingMode.Real || frame != _measured + 1)
            throw new InvalidOperationException("Measured times are set once per frame, in order, under real timing.");
        _elapsed[frame - 1] = elapsed;
        _total[frame - 1] = (frame == 1 ? 0 : _total[frame - 2]) + elapsed;
        _measured = frame;
    }

    /// <summary>The first frame whose total time is at or after <paramref name="ticks"/>; frame 1 for times at or before it.</summary>
    public int FirstFrameAtOrAfter(long ticks)
    {
        if (!IsKnown)
            throw new InvalidOperationException("Frame times under real timing aren't known before the run.");
        if (_measured > 0 && ticks <= _total[_measured - 1])
        {
            var index = Array.BinarySearch(_total, 0, _measured, ticks);
            if (index < 0)
                index = ~index;
            else
                while (index > 0 && _total[index - 1] == ticks)
                    --index;
            return index + 1;
        }
        var end = _measured == 0 ? 0 : _total[_measured - 1];
        var beyond = (ticks - end + StepTicks - 1) / StepTicks;
        return Math.Max(1, _measured + (int)Math.Min(beyond, int.MaxValue - _measured));
    }

    /// <summary>The elapsed times so far, for writing a <c>.timing</c> file.</summary>
    public ReadOnlySpan<long> ElapsedTimes => _elapsed.AsSpan(0, _measured);

    private static long Positive(long step) =>
        step > 0 ? step : throw new ArgumentOutOfRangeException(nameof(step), "The time step must be positive.");
}
