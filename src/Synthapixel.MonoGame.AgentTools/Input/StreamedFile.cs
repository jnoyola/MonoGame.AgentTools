using System.Diagnostics;

namespace Synthapixel.MonoGame.AgentTools.Input;

/// <summary>
/// A file written as it goes from the game thread without allocating or blocking on disk: bytes go into
/// a fixed buffer, and a full buffer (or one that has waited <see cref="FlushInterval"/>) is handed to a
/// worker thread that appends it to the file while the next one fills, the way Unreal Insights and ETW
/// stream trace data. If the worker falls behind, another buffer is allocated (counted in
/// <see cref="ExtraBuffers"/>), so nothing is ever dropped. Each <see cref="Advance"/> commits one whole
/// record; <see cref="FlushFinal"/> writes every committed record from any thread, such as on an unhandled
/// exception. A failed write stops the file, keeps what was written, and is reported in <see cref="Error"/>
/// rather than thrown on the game thread or blocking a flush.
/// </summary>
internal sealed class StreamedFile : IDisposable
{
    public const int DefaultBufferSize = 64 * 1024;

    /// <summary>
    /// The longest bytes wait before going to the worker, so a process killed outright (a debugger
    /// stopping it, say), which runs no exit handlers, loses at most this much.
    /// </summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly Stream _file;
    private readonly int _bufferSize;
    private readonly object _gate = new();
    private readonly Queue<(byte[] Buffer, int Length)> _pending = new(4);
    private readonly Stack<byte[]> _free = new(4);
    private readonly SemaphoreSlim _ready = new(0);
    private readonly Thread _worker;
    private byte[] _active;
    private int _length;
    private int _inFlight;
    private long _lastSwap = Stopwatch.GetTimestamp();
    private bool _stopping;
    private bool _closed;
    private bool _disposed;

    public StreamedFile(string path, int bufferSize = DefaultBufferSize)
        : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1), bufferSize)
    {
    }

    /// <summary>Writes to <paramref name="file"/>, which it owns; for tests.</summary>
    internal StreamedFile(Stream file, int bufferSize = DefaultBufferSize)
    {
        _bufferSize = bufferSize;
        _file = file;
        _active = new byte[bufferSize];
        _free.Push(new byte[bufferSize]);
        _worker = new Thread(Work) { IsBackground = true, Name = "Synthapixel.MonoGame.AgentTools file writer" };
        _worker.Start();
    }

    /// <summary>Full buffers handed to the worker.</summary>
    public int Swaps { get; private set; }

    /// <summary>Buffers allocated beyond the two because the worker fell behind.</summary>
    public int ExtraBuffers { get; private set; }

    public long BytesWritten { get; private set; }

    /// <summary>The write that stopped the file (disk full, say), or null.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Hands the buffer to the worker if it has waited longer than <see cref="FlushInterval"/>. Call once per frame.</summary>
    public void Tick()
    {
        if (_length > 0 && Stopwatch.GetElapsedTime(_lastSwap) > FlushInterval)
            Swap();
    }

    /// <summary>Room for at least <paramref name="size"/> bytes (at most the buffer size); follow with <see cref="Advance"/>.</summary>
    public Span<byte> GetSpan(int size)
    {
        if (_length + size > _active.Length)
            Swap();
        return _active.AsSpan(_length);
    }

    /// <summary>Commits <paramref name="count"/> bytes written to the last <see cref="GetSpan"/>: one whole record.</summary>
    public void Advance(int count)
    {
        // Release, so a FlushFinal on another thread that reads the new length also sees the bytes.
        Volatile.Write(ref _length, _length + count);
        BytesWritten += count;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var chunk = data[..Math.Min(data.Length, _bufferSize)];
            chunk.CopyTo(GetSpan(chunk.Length));
            Advance(chunk.Length);
            data = data[chunk.Length..];
        }
    }

    private void Swap()
    {
        lock (_gate)
        {
            _lastSwap = Stopwatch.GetTimestamp();
            if (_length > 0 && !_closed)
            {
                _pending.Enqueue((_active, _length));
                ++_inFlight;
                ++Swaps;
                _ready.Release();
            }
            if (_free.Count > 0)
                _active = _free.Pop();
            else
            {
                _active = new byte[_bufferSize];
                ++ExtraBuffers;
            }
            _length = 0;
        }
    }

    private void Work()
    {
        while (true)
        {
            _ready.Wait();
            (byte[] Buffer, int Length) next;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out next))
                {
                    if (_stopping)
                        return;
                    continue;
                }
            }
            if (Error is null)
            {
                try
                {
                    _file.Write(next.Buffer, 0, next.Length);
                }
                catch (Exception e)
                {
                    Error = e;
                }
            }
            lock (_gate)
            {
                _free.Push(next.Buffer);
                --_inFlight;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Hands the buffer to the worker and waits until everything so far is written. Game thread only.</summary>
    public void Flush()
    {
        Swap();
        lock (_gate)
        {
            while (_inFlight > 0)
                Monitor.Wait(_gate);
        }
    }

    /// <summary>
    /// Writes every committed record and closes the file to further writes, from any thread, for an
    /// unhandled exception (after which the process ends). It never touches the buffer the game thread is
    /// filling: the record being written when it runs is left out, and later ones are dropped.
    /// </summary>
    public void FlushFinal()
    {
        lock (_gate)
        {
            if (_disposed || _closed)
                return;
            while (_inFlight > 0)
                Monitor.Wait(_gate);
            _closed = true;
            var committed = Volatile.Read(ref _length);
            if (Error is null && committed > 0)
            {
                try
                {
                    _file.Write(_active, 0, committed);
                    _file.Flush();
                }
                catch (Exception e)
                {
                    Error = e;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Flush();
        lock (_gate)
        {
            _disposed = true;
            _stopping = true;
        }
        _ready.Release();
        _worker.Join();
        _file.Dispose();
        _ready.Dispose();
    }
}
