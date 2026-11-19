namespace MonoGame.AgentTools.Diagnostics;

internal readonly record struct MemoryPoint(int Frame, double WallSeconds, long HeapBytes, long PrivateBytes, int GraphicsResources);

/// <summary>
/// Retained memory and live resources over a whole session, for growth that takes minutes to show. Points
/// are recorded every second; when the buffer fills, every other point is dropped and the interval doubles
/// (round-robin database style), so the start of the session is always kept. Allocation-free after
/// construction.
/// </summary>
internal sealed class MemoryHistory(int capacity = 3600)
{
    private readonly MemoryPoint[] _points = new MemoryPoint[capacity];
    private double _intervalSeconds = 1.0;
    private double _lastWall = double.NegativeInfinity;

    public int Count { get; private set; }
    public ReadOnlySpan<MemoryPoint> Points => _points.AsSpan(0, Count);

    public void Record(in MemoryPoint point)
    {
        if (point.WallSeconds - _lastWall < _intervalSeconds)
            return;
        if (Count == _points.Length)
        {
            for (int i = 0; i < Count / 2; ++i)
                _points[i] = _points[i * 2];
            Count /= 2;
            _intervalSeconds *= 2;
        }
        _points[Count++] = point;
        _lastWall = point.WallSeconds;
    }
}
