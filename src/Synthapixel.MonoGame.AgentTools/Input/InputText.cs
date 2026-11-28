using System.Text;
using System.Text.Json;

namespace Synthapixel.MonoGame.AgentTools.Input;

/// <summary>
/// The quoted string a <c>Text</c> line carries: JSON string syntax, so quotes, backslashes and control
/// characters are escaped and everything else is written as is (UTF-8).
/// </summary>
internal static class InputText
{
    /// <summary>The longest a character gets when written: <c>\uXXXX</c>.</summary>
    public const int MaxBytesPerChar = 6;

    /// <summary>Characters MonoGame raises <c>TextInput</c> for from a key press, which a script or recording carries as the key.</summary>
    public static bool IsFromKey(char c) => c is '\b' or '\t' or '\r' or '\u001b' or '\u007f';

    /// <summary>The string in <paramref name="quoted"/>, or null if it isn't one quoted string.</summary>
    public static string? Parse(string quoted)
    {
        if (quoted.Length < 2 || quoted[0] != '"')
            return null;
        try
        {
            return JsonSerializer.Deserialize<string>(quoted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Quote(string text)
    {
        var bytes = new byte[text.Length * MaxBytesPerChar + 2];
        return Encoding.UTF8.GetString(bytes, 0, Write(bytes, text));
    }

    /// <summary>Writes <paramref name="text"/> quoted, without allocating; returns the bytes written.</summary>
    public static int Write(Span<byte> destination, ReadOnlySpan<char> text)
    {
        var length = 0;
        destination[length++] = (byte)'"';
        foreach (var c in text)
        {
            if (c is '"' or '\\')
            {
                destination[length++] = (byte)'\\';
                destination[length++] = (byte)c;
            }
            else if (c < 0x20 || c == 0x7f || char.IsSurrogate(c))
            {
                // Surrogates too, so a pair split across two lines still parses.
                destination[length++] = (byte)'\\';
                destination[length++] = (byte)'u';
                ((int)c).TryFormat(destination[length..], out var written, "x4", System.Globalization.CultureInfo.InvariantCulture);
                length += written;
            }
            else
                length += Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(in c), destination[length..]);
        }
        destination[length++] = (byte)'"';
        return length;
    }

    /// <summary>Where a <c>#</c> comment starts in <paramref name="line"/>, outside any quoted string; -1 if none.</summary>
    public static int CommentStart(string line)
    {
        var quoted = false;
        for (int i = 0; i < line.Length; ++i)
        {
            var c = line[i];
            if (quoted && c == '\\')
                ++i;
            else if (c == '"')
                quoted = !quoted;
            else if (c == '#' && !quoted)
                return i;
        }
        return -1;
    }
}
