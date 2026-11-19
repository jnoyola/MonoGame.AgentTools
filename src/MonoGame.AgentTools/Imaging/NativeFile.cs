using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MonoGame.AgentTools.Imaging;

/// <summary>
/// Writes a whole file through the OS's own create/write/close calls. Every .NET file API wraps the handle
/// in a managed object (<see cref="FileStream"/>, <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>),
/// so writing a file per frame would otherwise allocate on every frame.
/// </summary>
/// <remarks>
/// Falls back to <see cref="FileStream"/> where the C library can't be loaded (musl, for example); that
/// path allocates a few hundred bytes per file.
/// </remarks>
internal static partial class NativeFile
{
    private static bool _unavailable;

    /// <summary>
    /// The form of <paramref name="path"/> to pass to <see cref="Write"/>. Prepare paths before a per-frame
    /// loop so the loop formats no strings.
    /// </summary>
    public static string Prepare(string path)
    {
        path = Path.GetFullPath(path);
        // CreateFileW only accepts paths over MAX_PATH with the extended-length prefix.
        return OperatingSystem.IsWindows() && path.Length >= 260 && !path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\" + path
            : path;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Write(string path, ReadOnlySpan<byte> data)
    {
        if (!_unavailable)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    WriteWindows(path, data);
                else
                    WriteUnix(path, data);
                return;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
            }
        }
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0);
        file.Write(data);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void WriteWindows(string path, ReadOnlySpan<byte> data)
    {
        const uint GenericWrite = 0x40000000, ShareRead = 1, CreateAlways = 2, AttributeNormal = 0x80;
        var handle = CreateFile(path, GenericWrite, ShareRead, 0, CreateAlways, AttributeNormal, 0);
        if (handle == -1)
            throw Error(path);
        try
        {
            fixed (byte* start = data)
            {
                for (int offset = 0; offset < data.Length;)
                {
                    if (!WriteFile(handle, start + offset, (uint)(data.Length - offset), out var written, 0))
                        throw Error(path);
                    offset += (int)written;
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void WriteUnix(string path, ReadOnlySpan<byte> data)
    {
        const int Interrupted = 4;   // EINTR, the same on Linux and macOS
        const uint Mode = 0x1A4;     // 0644
        var macOS = OperatingSystem.IsMacOS();
        // creat rather than open: open is variadic, which P/Invoke can't call portably (Apple arm64).
        var fd = macOS ? CreatMac(path, Mode) : CreatLinux(path, Mode);
        if (fd < 0)
            throw Error(path);
        try
        {
            fixed (byte* start = data)
            {
                for (int offset = 0; offset < data.Length;)
                {
                    var count = (nuint)(data.Length - offset);
                    var written = macOS ? WriteMac(fd, start + offset, count) : WriteLinux(fd, start + offset, count);
                    if (written < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == Interrupted)
                            continue;
                        throw Error(path);
                    }
                    offset += (int)written;
                }
            }
        }
        finally
        {
            _ = macOS ? CloseMac(fd) : CloseLinux(fd);
        }
    }

    private static IOException Error(string path) =>
        new($"Couldn't write {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteFile(nint file, byte* buffer, uint count, out uint written, nint overlapped);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("libc.so.6", EntryPoint = "creat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreatLinux(string path, uint mode);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("libc.so.6", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint WriteLinux(int fd, byte* buffer, nuint count);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("libc.so.6", EntryPoint = "close")]
    private static partial int CloseLinux(int fd);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "creat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CreatMac(string path, uint mode);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint WriteMac(int fd, byte* buffer, nuint count);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [LibraryImport("libSystem.B.dylib", EntryPoint = "close")]
    private static partial int CloseMac(int fd);
}
