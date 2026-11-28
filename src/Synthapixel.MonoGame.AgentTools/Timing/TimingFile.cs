using System.Buffers.Binary;

namespace Synthapixel.MonoGame.AgentTools.Timing;

/// <summary>
/// A <c>.timing</c> file: each frame's <c>ElapsedGameTime</c> as a little-endian 32-bit tick count (100 ns,
/// <c>GameTime</c>'s own resolution, so replayed times are exact) after a 16-byte header: <c>MGAT</c>, format
/// version, the first frame's number, and a reserved word. Binary because nobody reads it by hand, and as
/// text it would bury an <c>.input</c> file's events in numbers. A frame longer than
/// <see cref="int.MaxValue"/> ticks (214 s; MonoGame caps a step at <c>MaxElapsedTime</c>, 0.5 s by default)
/// is stored as that maximum.
/// </summary>
internal static class TimingFile
{
    public const string Extension = ".timing";
    public const int HeaderSize = 16;
    private const int Version = 1;
    private static ReadOnlySpan<byte> Magic => "MGAT"u8;

    public static void WriteHeader(Span<byte> header)
    {
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], 0);
    }

    public static void WriteFrame(Span<byte> destination, long elapsedTicks) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, (int)Math.Clamp(elapsedTicks, 0, int.MaxValue));

    public static void Write(string path, ReadOnlySpan<long> elapsed)
    {
        var bytes = new byte[HeaderSize + elapsed.Length * 4];
        WriteHeader(bytes);
        for (int i = 0; i < elapsed.Length; ++i)
            WriteFrame(bytes.AsSpan(HeaderSize + i * 4), elapsed[i]);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Reads every frame's elapsed ticks. A partial last frame (a recording cut short) is dropped.</summary>
    public static long[] Read(string path)
    {
        // Shared, as for input: the recording may still be open in the game writing it.
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }
        if (bytes.Length < HeaderSize || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException($"{path} isn't a .timing file.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (version != Version)
            throw new InvalidDataException($"{path} is .timing format version {version}; this version of Synthapixel.MonoGame.AgentTools reads version {Version}.");
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != 1)
            throw new InvalidDataException($"{path} doesn't start at frame 1.");
        var frames = (bytes.Length - HeaderSize) / 4;
        var elapsed = new long[frames];
        for (int i = 0; i < frames; ++i)
            elapsed[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HeaderSize + i * 4));
        return elapsed;
    }
}
