using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Synthapixel.MonoGame.AgentTools.Capture;

/// <summary>
/// Reads the backbuffer without allocating. MonoGame's <see cref="GraphicsDevice.GetBackBufferData{T}(T[])"/>
/// allocates a row buffer on every call (to flip OpenGL's bottom-up rows); this calls glReadPixels, an
/// OpenGL 1.1 entry point every desktop GL library exports, and flips with a reused row.
/// </summary>
/// <remarks>
/// The first read is checked against MonoGame's; on any difference, or where the GL library can't be
/// loaded, reads fall back to MonoGame's and <see cref="FallbackReason"/> says why.
/// </remarks>
internal sealed unsafe class BackBufferReader
{
    private const uint Rgba = 0x1908, UnsignedByte = 0x1401;

    private readonly GraphicsDevice _device;
    private readonly Color[] _row;
    private delegate* unmanaged<int, int, int, int, uint, uint, void*, void> _readPixels;
    private Color[]? _expected;   // for checking the first read; allocated up front, dropped after

    public BackBufferReader(GraphicsDevice device, int width, int height)
    {
        _device = device;
        _row = new Color[width];
        var library = OperatingSystem.IsWindows() ? "opengl32.dll"
            : OperatingSystem.IsMacOS() ? "/System/Library/Frameworks/OpenGL.framework/OpenGL"
            : "libGL.so.1";
        if (NativeLibrary.TryLoad(library, out var handle) && NativeLibrary.TryGetExport(handle, "glReadPixels", out var function))
        {
            _readPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)function;
            _expected = new Color[width * height];
        }
        else
            FallbackReason = $"glReadPixels not found in {library}";
    }

    /// <summary>Why reads use MonoGame's allocating readback, or null if they don't.</summary>
    public string? FallbackReason { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Read(Color[] pixels, int width, int height)
    {
        if (_readPixels is null)
        {
            _device.GetBackBufferData(pixels);
            return;
        }

        ReadNative(pixels, width, height);
        if (_expected is { } expected)
        {
            _expected = null;
            _device.GetBackBufferData(expected);
            if (!MemoryMarshal.AsBytes(expected.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(pixels.AsSpan())))
            {
                _readPixels = null;
                FallbackReason = "glReadPixels didn't match GetBackBufferData on the first frame";
                expected.CopyTo(pixels, 0);
            }
        }
    }

    /// <summary>Reads once without checking, so the first real read has nothing left to compile or bind.</summary>
    public void Warm(Color[] pixels, int width, int height)
    {
        if (_readPixels is not null)
            ReadNative(pixels, width, height);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ReadNative(Color[] pixels, int width, int height)
    {
        fixed (Color* p = pixels)
            _readPixels(0, 0, width, height, Rgba, UnsignedByte, p);
        var rows = pixels.AsSpan(0, width * height);
        for (int top = 0, bottom = height - 1; top < bottom; ++top, --bottom)
        {
            var a = rows.Slice(top * width, width);
            var b = rows.Slice(bottom * width, width);
            a.CopyTo(_row);
            b.CopyTo(a);
            _row.CopyTo(b);
        }
    }
}
