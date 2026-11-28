using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Synthapixel.MonoGame.AgentTools.Diagnostics;

/// <summary>
/// Process private memory, read without allocating where the OS allows it, so sampling it doesn't show up
/// as heap growth itself. Windows: <c>GetProcessMemoryInfo</c> PrivateUsage (Performance Monitor's Private
/// Bytes). Linux: <c>RssAnon</c> from <c>/proc/self/status</c>. Elsewhere: <see cref="Process"/>, which
/// allocates about 3 KB per read.
/// </summary>
internal sealed partial class ProcessMemory : IDisposable
{
    private readonly SafeFileHandle? _status;
    private readonly Process? _process;

    public ProcessMemory()
    {
        if (OperatingSystem.IsWindows())
            return;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/status"))
            _status = File.OpenHandle("/proc/self/status");
        else
            _process = Process.GetCurrentProcess();
    }

    /// <summary>Whether <see cref="Read"/> allocates, so callers can exclude it from their measurements.</summary>
    public bool Allocates => _process is not null;

    public long Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var counters = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
            return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size) ? (long)counters.PrivateUsage : 0;
        }
        if (_status is not null)
            return ReadRssAnon(_status);
        _process!.Refresh();
        return _process.PrivateMemorySize64;
    }

    private static long ReadRssAnon(SafeFileHandle status)
    {
        Span<byte> buffer = stackalloc byte[4096];
        var length = RandomAccess.Read(status, buffer, 0);
        var text = buffer[..length];
        var key = "RssAnon:"u8;
        var at = text.IndexOf(key);
        if (at < 0)
            return 0;
        long kilobytes = 0;
        foreach (var b in text[(at + key.Length)..])
        {
            if (b is >= (byte)'0' and <= (byte)'9')
                kilobytes = kilobytes * 10 + (b - '0');
            else if (b == '\n')
                break;
        }
        return kilobytes * 1024;
    }

    public void Dispose()
    {
        _status?.Dispose();
        _process?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx counters, uint size);
}
